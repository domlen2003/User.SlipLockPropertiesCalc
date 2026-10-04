using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.SpeedDial.Persistence;
using DivebombLogistics.SpeedDial.Telemetry;
using DivebombLogistics.SpeedDial.UI;

namespace DivebombLogistics.SpeedDial;

/// <summary>
/// The Speed Dial module: per-car named setup presets (TC, TC2, TC3, ABS, brake bias) dialed in through Control
/// Mapper role presses, closed-loop on the game's telemetry (<see cref="Dialer"/>). Users bind buttons to the
/// <c>DLP.SpeedDial.*</c> actions (Dial 1..8 slots, next/previous/apply-selected, set/reset pairs, cancel); the
/// <c>DLP.SpeedDial.*</c> properties report values and progress for dashboards. Global settings live in
/// <c>PluginsData\DLP\SpeedDial\Settings.json</c>, per-car data in <c>...\SpeedDial\Cars\&lt;Sim&gt;\&lt;CarKey&gt;_&lt;hash&gt;.json</c>
/// (<see cref="SpeedDialStore"/>).
/// <para>
/// Threading: every <see cref="IDlpModule"/> member and every action callback runs on SimHub's data thread (the
/// framework posts action callbacks to the module's dispatcher), which owns the settings, the car data, the dialer
/// and the save scheduler. The Speed Dial tab (UI thread) talks to the module only through
/// <see cref="ISpeedDialHost"/>: it reads a <see cref="SpeedDialSnapshot"/> copied under a lock (filled at up to
/// 20 Hz) and posts every mutation to the dispatcher; export and import wait for the data thread (up to 3 s).
/// </para>
/// <para>
/// Hot path: <see cref="Update"/> and <see cref="Tick"/> are allocation-free in steady state. Allocations happen on
/// game/car changes, in button actions and UI edits (status texts, summaries for the UI), and in the rate-limited
/// error path. Exported strings are assigned only when they change; dial status texts are the dialer's cached strings.
/// </para>
/// <para>
/// Persistence: settings are saved 2 s after the last edit, car data 2 s after the last change (presets, slots,
/// selection, stored pair values, learned channel behavior), on a car change and at shutdown; never under a
/// provisional (LMU placeholder) car key, whose data is carried over to the native key when that has no file yet.
/// </para>
/// </summary>
internal sealed class SpeedDialModule : IDlpModule, ISpeedDialHost
{
    /// <summary>The UI snapshot is refreshed at most this often (20 Hz).</summary>
    private const double SnapshotIntervalSeconds = 0.05;

    /// <summary>How long export/import wait for the data thread.</summary>
    private const int DataThreadTimeoutMs = 3000;

    /// <summary>Stem of the default preset names ("Preset 3").</summary>
    private const string DefaultPresetNameStem = "Preset ";

    // ---- Status texts (cached; the dialer's own messages are its cached strings) ----
    private const string NoCarText = "No car loaded";
    private const string NoGameText = "No game running";
    private const string NoPresetsText = "No presets for this car";
    private const string NoSelectionText = "No preset selected";
    private const string PresetNotFoundText = "Preset not found";
    private const string NoTelemetryValuesText = "No valid telemetry values";
    private const string BusyText = "Busy dialing";
    private const string CancelledByUserText = "Cancelled";
    private const string GameChangedText = "Cancelled: game changed";
    private const string CarChangedText = "Cancelled: car changed";
    private const string GameStoppedText = "Cancelled: game stopped";
    private const string ErrorText = "Cancelled: error";
    private const string ShutdownText = "Cancelled: SimHub is closing";

    // ---- LastResult texts (DLP.SpeedDial.LastResult) ----
    private const string CompletedResult = "Completed";
    private const string PartialResult = "Partial";
    private const string FailedResult = "Failed";
    private const string CancelledResult = "Cancelled";

    /// <summary>End(): how long to wait for queued asynchronous writes.</summary>
    private static readonly TimeSpan ShutdownWriteTimeout = TimeSpan.FromSeconds(5);

    private static readonly string TooManyPresetsText = SpeedDialNames.ErrorPrefix + "at most "
        + SpeedDialCarData.MaxPresets.ToString(CultureInfo.InvariantCulture) + " presets per car";

    private static readonly string[] SlotEmptyTexts = BuildIndexedTexts("Slot ", " is empty", SpeedDialSettings.MaxSlotCount);
    private static readonly string[] SlotUnusedTexts = BuildIndexedTexts("Slot ", " is not in use", SpeedDialSettings.MaxSlotCount);
    private static readonly string[] PairUndefinedTexts = BuildIndexedTexts("Pair ", " is not defined", SpeedDialSettings.MaxPairCount);

    // ---- Infrastructure (from the shell) ----
    private readonly object snapshotLock = new object();
    private readonly SpeedDialSnapshot snapshot = new SpeedDialSnapshot();
    private ILog log = NullLog.Instance;
    private ITelemetryReader reader;
    private FrameContext ctx;
    private ICarIdentityState carState;
    private IDataThreadDispatcher dispatcher;
    private ErrorReporter errors;
    private ShellDiagnostics diagnostics;
    private Func<double> clock;
    private IRoleOutput roles;
    private SpeedDialStore store;
    private SaveScheduler scheduler;
    private SpeedDialSettings settings;
    private bool settingsEdited;

    // ---- Dialing (data thread) ----
    private readonly DialRequest request = new DialRequest();
    private readonly double[] values = CreateNaNArray();
    private readonly double[] maxValues = CreateNaNArray();
    private readonly bool[] supported = new bool[DialChannels.Count];
    private Dialer dialer;
    private IDialTelemetry telemetry;
    private int statusVersion;

    // ---- Car (data thread; identity also read by the UI thread) ----
    private volatile CarIdentity identity = CarIdentity.None;
    private double identityProvisionalUntil = double.NegativeInfinity;
    private string identityCarId = string.Empty;
    private string identityCarModel = string.Empty;
    private SpeedDialCarData carData;
    private bool carDataEdited;
    private string carFilePath = string.Empty;
    private int carDataVersion;

    // ---- Immutable UI views, replaced on change ----
    private IReadOnlyList<PresetSummary> presetSummaries = PresetSummary.BuildList(null);
    private int presetsVersion;
    private IReadOnlyList<PairSummary> pairSummaries = PairSummary.BuildList(null, null);
    private int pairsVersion;
    private SpeedDialSettings settingsCopy;
    private int settingsVersion;

    // ---- Per-pair texts, rebuilt when a pair's name changes ----
    private readonly string[] pairTextNames = new string[SpeedDialSettings.MaxPairCount];
    private readonly string[] pairResetLabels = new string[SpeedDialSettings.MaxPairCount];
    private readonly string[] pairStoredTexts = new string[SpeedDialSettings.MaxPairCount];
    private readonly string[] pairNothingValidTexts = new string[SpeedDialSettings.MaxPairCount];
    private readonly string[] pairNothingStoredTexts = new string[SpeedDialSettings.MaxPairCount];
    private readonly string[] pairAlreadySetTexts = new string[SpeedDialSettings.MaxPairCount];

    // ---- Exported property values (read by SimHub on demand) ----
    private bool busy;
    private string status = string.Empty;
    private string activePreset = string.Empty;
    private string selectedPreset = string.Empty;
    private int selectedIndex;
    private int presetCount;
    private string lastResult = string.Empty;

    // ---- Bookkeeping (data thread) ----
    private string lastGame = string.Empty;
    private string telemetryName = string.Empty;
    private string telemetryDescription = string.Empty;
    private double lastSnapshotTime = double.NegativeInfinity;

    /// <summary>Creates the module; everything else comes from <see cref="ModuleContext"/> in <see cref="Init"/>.</summary>
    public SpeedDialModule()
    {
    }

    /// <inheritdoc />
    public string Id => SpeedDialNames.ModuleId;

    /// <inheritdoc />
    public string DisplayName => SpeedDialNames.ModuleDisplayName;

    /// <inheritdoc />
    public bool ControlMapperAvailable => roles != null && roles.IsAvailable;

    /// <summary>Number of registered properties (start-up log, tests).</summary>
    internal int PropertyCount { get; private set; }

    /// <summary>Number of registered actions (start-up log, tests).</summary>
    internal int ActionCount { get; private set; }

    /// <summary>Live global settings (data thread only; tests).</summary>
    internal SpeedDialSettings Settings => settings;

    /// <summary>Live data of the current car, null without a car (data thread only; tests).</summary>
    internal SpeedDialCarData CarData => carData;

    /// <summary>The dialer's live status (data thread only; tests).</summary>
    internal DialStatus DialerStatus => dialer?.Status;

    /// <summary>The module's file store (tests).</summary>
    internal SpeedDialStore Store => store;

    // =====================================================================================================
    // IDlpModule lifecycle
    // =====================================================================================================

    /// <inheritdoc />
    public void Init(ModuleContext moduleContext)
    {
        if (moduleContext == null)
        {
            throw new ArgumentNullException(nameof(moduleContext));
        }

        log = moduleContext.Log ?? NullLog.Instance;
        reader = moduleContext.Reader ?? throw new ArgumentException("A telemetry reader is required.", nameof(moduleContext));
        ctx = moduleContext.Frame ?? throw new ArgumentException("A frame context is required.", nameof(moduleContext));
        carState = moduleContext.Car ?? throw new ArgumentException("A car identity state is required.", nameof(moduleContext));
        dispatcher = moduleContext.Dispatcher ?? throw new ArgumentException("A dispatcher is required.", nameof(moduleContext));
        roles = moduleContext.Roles ?? throw new ArgumentException("A role output is required.", nameof(moduleContext));
        clock = moduleContext.Clock ?? throw new ArgumentException("A clock is required.", nameof(moduleContext));
        IPropertyRegistry properties = moduleContext.Properties ?? throw new ArgumentException("A property registry is required.", nameof(moduleContext));
        IActionRegistry actions = moduleContext.Actions ?? throw new ArgumentException("An action registry is required.", nameof(moduleContext));
        errors = moduleContext.Errors ?? new ErrorReporter(log);
        diagnostics = moduleContext.Diagnostics ?? new ShellDiagnostics();

        store = new SpeedDialStore(moduleContext.DataDirectory, log);
        settings = store.LoadSettings(out JsonReadStatus settingsStatus);
        dialer = new Dialer(roles, log);
        statusVersion = dialer.Status.Version;
        scheduler = new SaveScheduler(SaveSettingsAsync, SaveCurrentCarData, log);
        settingsCopy = settings.DeepCopy();
        BuildPairTexts();
        pairSummaries = PairSummary.BuildList(settings, null);

        int propertiesBefore = properties.Names.Count;
        RegisterProperties(properties);
        PropertyCount = properties.Names.Count - propertiesBefore;
        int actionsBefore = actions.Names.Count;
        RegisterActions(actions);
        ActionCount = actions.Names.Count - actionsBefore;

        lock (snapshotLock)
        {
            FillSnapshot(snapshot, clock());
        }

        log.Info("Initialized: " + PropertyCount.ToString(CultureInfo.InvariantCulture) + " properties, "
            + ActionCount.ToString(CultureInfo.InvariantCulture) + " actions, settings " + settingsStatus
            + ", data folder " + store.DataDirectory);
    }

    /// <inheritdoc />
    public void OnGameChanged(FrameContext frame)
    {
        lastGame = frame?.GameName ?? string.Empty;
        dialer.Cancel(GameChangedText);
        RefreshDialStatus();

        telemetry = DialTelemetryFactory.Create(lastGame, reader);
        telemetryName = telemetry.Name ?? string.Empty;
        telemetryDescription = telemetry.Describe() ?? string.Empty;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            supported[i] = telemetry.IsSupported((DialChannel)i);
        }

        ClearValues();
        log.Info("Game: " + lastGame + ", dial telemetry " + telemetryName);
    }

    /// <inheritdoc />
    public void OnCarChanged(CarIdentity car) => SwitchCar(car ?? CarIdentity.None);

    /// <inheritdoc />
    public void OnSessionChanged(FrameContext frame)
    {
        // Nothing to reset: a running job is closed-loop (the gate pauses it in menus and the garage).
    }

    /// <inheritdoc />
    public void Update(FrameContext frame)
    {
        double now = frame.WallTime;
        try
        {
            ReadChannels();
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
            ClearValues();
        }

        try
        {
            // Presses only while the driver is in control of a live car (DESIGN-DLP 4.5): never in menus, pause or replays.
            bool gateOpen = frame.GameRunning && !frame.GamePaused && !frame.GameInMenu && !frame.IsReplay && !frame.Spectating;
            dialer.Update(now, gateOpen, telemetry, settings, carData);
            if (dialer.LearningChanged)
            {
                dialer.ClearLearningChanged();
                if (carData != null)
                {
                    MarkCarDataEdited(now);
                }
            }

            RefreshDialStatus();
        }
        catch (Exception ex)
        {
            // Role output or dialer failure: never leave a job pressing buttons after an error.
            errors.Report(ex, now);
            CancelAfterError(now);
        }
    }

    /// <summary>First frame without a running game: cancel the job and zero the exported values.</summary>
    public void OnGameStopped()
    {
        dialer.Cancel(GameStoppedText);
        RefreshDialStatus();
        ClearValues();
    }

    /// <inheritdoc />
    public void Tick(double now, bool gameRunning)
    {
        try
        {
            RefreshDialStatus();
            scheduler.Tick(now);
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
        }

        try
        {
            UpdateSnapshot(now, gameRunning);
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
        }
    }

    /// <summary>Cancels the job and zeroes the exported values (never throws).</summary>
    public void OnFault(Exception error)
    {
        try
        {
            dialer?.Cancel(ErrorText);
            if (dialer != null)
            {
                RefreshDialStatus();
            }
        }
        catch
        {
            // Error path: must not throw out of DataUpdate.
            busy = false;
        }

        ClearValues();
    }

    /// <summary>Cancels the job and saves edited car data and settings synchronously.</summary>
    public void End()
    {
        if (store == null)
        {
            return;
        }

        try
        {
            dialer?.Cancel(ShutdownText);
        }
        catch (Exception ex)
        {
            log.Warn("Cancelling the dial at shutdown failed: " + ex.Message);
        }

        if (carData != null && carDataEdited && !IsIdentityProvisional(clock()))
        {
            store.SaveCarData(carData); // synchronous; supersedes anything still queued for this car
        }

        scheduler?.DiscardProfileChanges();
        if (settingsEdited)
        {
            store.SaveSettings(settings); // synchronous; supersedes a queued asynchronous save
        }

        store.WaitForPendingWrites(ShutdownWriteTimeout);
    }

    // =====================================================================================================
    // Registration (Init)
    // =====================================================================================================

    /// <summary>Registers the properties in <see cref="SpeedDialNames.AllProperties"/> order (delegates created once).</summary>
    private void RegisterProperties(IPropertyRegistry properties)
    {
        properties.Attach(SpeedDialNames.BusyProperty, () => busy);
        properties.Attach(SpeedDialNames.StatusProperty, () => status);
        properties.Attach(SpeedDialNames.ActivePresetProperty, () => activePreset);
        properties.Attach(SpeedDialNames.SelectedPresetProperty, () => selectedPreset);
        properties.Attach(SpeedDialNames.SelectedIndexProperty, () => selectedIndex);
        properties.Attach(SpeedDialNames.PresetCountProperty, () => presetCount);
        for (int i = 0; i < DialChannels.Count; i++)
        {
            int channel = i; // captured once per channel (closures are created here, never per frame)
            properties.Attach(SpeedDialNames.ValueProperty((DialChannel)i), () => ExportValue(channel));
        }

        properties.Attach(SpeedDialNames.LastResultProperty, () => lastResult);
    }

    /// <summary>
    /// Registers every action in <see cref="SpeedDialNames.AllActions"/> order, always the maximum counts (8 slots,
    /// 4 pairs) so button bindings survive changes of the slot and pair counts. Delegates are created once here.
    /// </summary>
    private void RegisterActions(IActionRegistry actions)
    {
        for (int i = 0; i < SpeedDialSettings.MaxSlotCount; i++)
        {
            int slot = i;
            actions.Add(SpeedDialNames.DialAction(i), () => DialSlot(slot));
        }

        actions.Add(SpeedDialNames.NextPresetAction, SelectNextPreset);
        actions.Add(SpeedDialNames.PreviousPresetAction, SelectPreviousPreset);
        actions.Add(SpeedDialNames.ApplySelectedPresetAction, ApplySelectedPreset);
        for (int i = 0; i < SpeedDialSettings.MaxPairCount; i++)
        {
            int pair = i;
            actions.Add(SpeedDialNames.SetAction(i), () => StorePair(pair));
        }

        for (int i = 0; i < SpeedDialSettings.MaxPairCount; i++)
        {
            int pair = i;
            actions.Add(SpeedDialNames.ResetAction(i), () => ResetPairValues(pair));
        }

        actions.Add(SpeedDialNames.CancelAction, CancelByUser);
    }

    // =====================================================================================================
    // Actions (data thread: posted by the framework, run at the start of a frame)
    // =====================================================================================================

    /// <summary><c>SpeedDial.Dial{slot+1}</c>: dials the preset assigned to the slot.</summary>
    private void DialSlot(int slotIndex)
    {
        if (slotIndex >= settings.SlotCount)
        {
            SetStatus(SlotUnusedTexts[slotIndex]);
            return;
        }

        if (!CanDial())
        {
            return;
        }

        DialPreset preset = carData.GetSlotPreset(slotIndex);
        if (preset == null)
        {
            SetStatus(SlotEmptyTexts[slotIndex]);
            return;
        }

        StartPreset(preset);
    }

    private void SelectNextPreset() => CycleSelection(1);

    private void SelectPreviousPreset() => CycleSelection(-1);

    /// <summary>Next/Previous with wrap-around; without a selection Next picks the first preset, Previous the last.</summary>
    private void CycleSelection(int step)
    {
        SpeedDialCarData data = carData;
        if (data == null)
        {
            SetStatus(NoCarText);
            return;
        }

        int count = data.Presets.Count;
        if (count == 0)
        {
            SetStatus(NoPresetsText);
            return;
        }

        int index = data.IndexOfPreset(data.SelectedPresetId);
        int next = index < 0 ? (step > 0 ? 0 : count - 1) : (index + step + count) % count;
        DialPreset preset = data.Presets[next];
        data.SelectedPresetId = preset.Id;
        UpdateSelectionExports();
        SetStatus(preset.Name);
        MarkCarDataEdited(clock());
    }

    /// <summary><c>SpeedDial.ApplySelectedPreset</c>: dials the selected preset.</summary>
    private void ApplySelectedPreset()
    {
        if (!CanDial())
        {
            return;
        }

        DialPreset preset = carData.FindPreset(carData.SelectedPresetId);
        if (preset == null)
        {
            SetStatus(carData.Presets.Count == 0 ? NoPresetsText : NoSelectionText);
            return;
        }

        StartPreset(preset);
    }

    /// <summary>
    /// <c>SpeedDial.Set{pair+1}</c>: stores the current values of the pair's channels that have valid telemetry (the
    /// old values are kept when none has).
    /// </summary>
    private void StorePair(int pairIndex)
    {
        SpeedDialCarData data = carData;
        if (data == null)
        {
            SetStatus(NoCarText);
            return;
        }

        SetResetPairDefinition pair = settings.GetPair(pairIndex);
        if (pair == null)
        {
            SetStatus(PairUndefinedTexts[pairIndex]);
            return;
        }

        int valid = 0;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            if (pair.Includes((DialChannel)i) && MathUtil.IsFinite(values[i]))
            {
                valid++;
            }
        }

        if (valid == 0)
        {
            SetStatus(pairNothingValidTexts[pairIndex]);
            return;
        }

        DialSnapshot stored = data.GetPairSnapshot(pairIndex);
        stored.Clear();
        for (int i = 0; i < DialChannels.Count; i++)
        {
            var channel = (DialChannel)i;
            if (pair.Includes(channel) && MathUtil.IsFinite(values[i]))
            {
                stored.SetValue(channel, values[i]);
            }
        }

        stored.CapturedUtc = DateTime.UtcNow;
        PairsChanged();
        MarkCarDataEdited(clock());
        SetStatus(pairStoredTexts[pairIndex]);
    }

    /// <summary>
    /// <c>SpeedDial.Reset{pair+1}</c>: dials back to the stored values, only the channels whose current value differs
    /// ("already set" when none does).
    /// </summary>
    private void ResetPairValues(int pairIndex)
    {
        if (!CanDial())
        {
            return;
        }

        SetResetPairDefinition pair = settings.GetPair(pairIndex);
        if (pair == null)
        {
            SetStatus(PairUndefinedTexts[pairIndex]);
            return;
        }

        request.LoadPairSnapshot(pairResetLabels[pairIndex], pair, carData.GetPairSnapshot(pairIndex));
        if (request.TargetCount == 0)
        {
            SetStatus(pairNothingStoredTexts[pairIndex]);
            return;
        }

        for (int i = 0; i < DialChannels.Count; i++)
        {
            var channel = (DialChannel)i;
            if (request.HasTarget(channel) && DialChannels.Matches(channel, values[i], request.GetTarget(channel), LearnedStep(channel)))
            {
                request.SetTarget(channel, double.NaN);
            }
        }

        if (request.TargetCount == 0)
        {
            SetStatus(pairAlreadySetTexts[pairIndex]);
            return;
        }

        dialer.Start(request, clock());
        RefreshDialStatus();
    }

    /// <summary><c>SpeedDial.Cancel</c>.</summary>
    private void CancelByUser()
    {
        dialer.Cancel(CancelledByUserText);
        RefreshDialStatus();
    }

    /// <summary>A job needs a car and a running game (otherwise it would wait unseen until the game runs again).</summary>
    private bool CanDial()
    {
        if (carData == null)
        {
            SetStatus(NoCarText);
            return false;
        }

        if (!ctx.GameRunning)
        {
            SetStatus(NoGameText);
            return false;
        }

        return true;
    }

    private void StartPreset(DialPreset preset)
    {
        request.LoadPreset(preset);
        dialer.Start(request, clock());
        RefreshDialStatus();
    }

    // =====================================================================================================
    // Per-frame processing (data thread)
    // =====================================================================================================

    /// <summary>Current value of every channel, exported every frame (allocation-free; NaN = unknown).</summary>
    private void ReadChannels()
    {
        IDialTelemetry source = telemetry;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            values[i] = source != null && supported[i] && source.TryRead((DialChannel)i, out double value) ? value : double.NaN;
        }
    }

    /// <summary>
    /// The car's known maximum per channel. Only the UI shows it (the dialer reads it itself), so it is refreshed at
    /// snapshot rate instead of every frame: fewer SimHub property lookups on the hot path. Allocation-free.
    /// </summary>
    private void ReadMaxima()
    {
        IDialTelemetry source = telemetry;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            maxValues[i] = source != null && supported[i] && source.TryGetMax((DialChannel)i, out double max) ? max : double.NaN;
        }
    }

    /// <summary>Mirrors the dialer's status into the exported fields when its version changed (string references only).</summary>
    private void RefreshDialStatus()
    {
        DialStatus dial = dialer.Status;
        if (dial.Version == statusVersion)
        {
            return;
        }

        statusVersion = dial.Version;
        busy = dial.IsBusy;
        activePreset = dial.Label ?? string.Empty;
        if (!string.IsNullOrEmpty(dial.Message))
        {
            status = dial.Message;
        }

        string result = ResultText(dial.State);
        if (result != null)
        {
            lastResult = result;
        }
    }

    private void CancelAfterError(double now)
    {
        try
        {
            dialer.Cancel(ErrorText);
            RefreshDialStatus();
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
            busy = false;
        }
    }

    private void ClearValues()
    {
        for (int i = 0; i < DialChannels.Count; i++)
        {
            values[i] = double.NaN;
            maxValues[i] = double.NaN;
        }
    }

    private double ExportValue(int channel)
    {
        double value = values[channel];
        return MathUtil.IsFinite(value) ? value : 0.0;
    }

    private double LearnedStep(DialChannel channel) => carData?.GetLearning(channel)?.Step ?? 0.0;

    private void SetStatus(string text) => status = text ?? string.Empty;

    // =====================================================================================================
    // Car switching and persistence (data thread)
    // =====================================================================================================

    /// <summary>Saves the current car's data (if edited) and loads or creates the data of <paramref name="next"/>.</summary>
    private void SwitchCar(CarIdentity next)
    {
        double now = clock();
        CarIdentity previous = identity;

        // The previous key was only a placeholder for this same vehicle: still inside its own retry window (judged
        // before identity is replaced), now resolved to the native model name, and SimHub's CarId unchanged. Only then
        // is its data carried over; a different car (or a key that became final after the window) is flushed normally,
        // so another car's presets and learning never move onto the new car.
        bool provisional = carData != null && IsIdentityProvisional(now) && next.KeySource == CarKeySource.NativeModel
            && string.Equals(previous.SimKey, next.SimKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ctx.CarId ?? string.Empty, identityCarId, StringComparison.Ordinal);

        // LMU's placeholder key replaced by the native model name of the same vehicle (the shell's retry; SimHub's
        // CarId/CarModel unchanged): same physical car, so a running job continues. Any other change cancels it.
        bool sameVehicle = provisional
            && string.Equals(ctx.CarModel ?? string.Empty, identityCarModel, StringComparison.Ordinal);
        if (!sameVehicle)
        {
            dialer.Cancel(CarChangedText);
            RefreshDialStatus();
        }

        SpeedDialCarData provisionalData = null;
        if (carData != null)
        {
            if (provisional)
            {
                // The livery-specific key was only a placeholder: no file is left behind for it; its data is
                // carried over below instead.
                scheduler.DiscardProfileChanges();
                provisionalData = carData;
            }
            else
            {
                scheduler.FlushProfile(); // saves if dirty (never under a provisional key, see SaveCurrentCarData)
            }
        }

        scheduler.DiscardProfileChanges();
        identity = next;
        identityProvisionalUntil = carState.ProvisionalUntil;
        identityCarId = ctx.CarId ?? string.Empty;
        identityCarModel = ctx.CarModel ?? string.Empty;
        carDataEdited = false;
        if (next.HasCar)
        {
            bool stored = store.CarDataExists(next.SimKey, next.CarKey);
            carData = store.LoadCarData(next.SimKey, next.CarKey, next.DisplayName);
            if (provisionalData != null && !stored)
            {
                // First time this car is seen under its native key: keep what was set/learned under the placeholder.
                carData = CarryOver(provisionalData, next);
                MarkCarDataEdited(now);
            }

            carFilePath = store.GetCarFilePath(next.SimKey, next.CarKey);
            log.Info("Car: " + next + ", data " + carFilePath);
        }
        else
        {
            if (carData != null)
            {
                log.Info("Car unloaded (" + previous + ")");
            }

            carData = null;
            carFilePath = string.Empty;
        }

        carDataVersion++;
        PresetsChanged();
        PairsChanged();
    }

    /// <summary>The placeholder key's data rebound to the native key (presets, slots, selection, pair values, learning).</summary>
    private static SpeedDialCarData CarryOver(SpeedDialCarData from, CarIdentity to)
    {
        SpeedDialCarData data = from.DeepCopy();
        data.SimKey = to.SimKey;
        data.CarKey = to.CarKey;
        data.DisplayName = to.DisplayName;
        data.Normalize();
        return data;
    }

    /// <summary>
    /// True while the car key is a livery-specific placeholder that may still be replaced by the native model name: car
    /// data is not saved then. Uses this module's identity and the retry window that started with it, so the previous
    /// car is judged by its own window while <see cref="SwitchCar"/> runs.
    /// </summary>
    private bool IsIdentityProvisional(double now) => identity.ShouldRetry && now <= identityProvisionalUntil;

    /// <summary>The car data changed (user edit, action, learning): saved once the changes pause for 2 s.</summary>
    private void MarkCarDataEdited(double now)
    {
        carDataEdited = true;
        scheduler.MarkProfileEdited(now);
    }

    /// <summary>Copies the settings on the data thread and writes them on the thread pool (SaveScheduler callback).</summary>
    private void SaveSettingsAsync() => store.SaveSettingsAsync(settings);

    /// <summary>
    /// SaveScheduler callback: serializes the car data here on the data thread and writes it on the thread pool (a
    /// deliberate, debounced exception to the no-IO rule). Under a provisional key the save is postponed until the retry
    /// window has passed and the key is final.
    /// </summary>
    private void SaveCurrentCarData()
    {
        SpeedDialCarData data = carData;
        if (data == null)
        {
            return;
        }

        if (IsIdentityProvisional(clock()))
        {
            scheduler.MarkProfileEdited(identityProvisionalUntil);
            return;
        }

        store.SaveCarDataAsync(data);
    }

    // =====================================================================================================
    // UI views (data thread)
    // =====================================================================================================

    /// <summary>The car's preset list changed: new immutable summaries, version bump, selection exports.</summary>
    private void PresetsChanged()
    {
        presetSummaries = PresetSummary.BuildList(carData);
        presetsVersion++;
        UpdateSelectionExports();
    }

    /// <summary>Pair definitions or the car's stored pair values changed.</summary>
    private void PairsChanged()
    {
        pairSummaries = PairSummary.BuildList(settings, carData);
        pairsVersion++;
    }

    private void UpdateSelectionExports()
    {
        SpeedDialCarData data = carData;
        int index = data == null ? -1 : data.IndexOfPreset(data.SelectedPresetId);
        presetCount = data?.Presets.Count ?? 0;
        selectedIndex = index + 1;
        selectedPreset = index >= 0 ? data.Presets[index].Name : string.Empty;
    }

    /// <summary>After an edit of the live settings: repair them, publish a copy for the UI and schedule the save.</summary>
    private void ApplySettingsChange(double now)
    {
        settings.Normalize();
        settingsCopy = settings.DeepCopy();
        settingsVersion++;
        BuildPairTexts();

        // Most settings edits (a timing slider, a role, the slot count) cannot change the pairs: only a real change
        // bumps pairsVersion, so the UI does not rebuild the Set/Reset rows (and drop their keyboard focus) on every edit.
        IReadOnlyList<PairSummary> rebuilt = PairSummary.BuildList(settings, carData);
        if (!PairSummary.ListsEqual(rebuilt, pairSummaries))
        {
            pairSummaries = rebuilt;
            pairsVersion++;
        }

        settingsEdited = true;
        scheduler.MarkSettingsDirty(now);
    }

    /// <summary>Status texts per pair (cached; rebuilt only for pairs whose name changed).</summary>
    private void BuildPairTexts()
    {
        for (int i = 0; i < SpeedDialSettings.MaxPairCount; i++)
        {
            string name = settings.GetPair(i)?.Name;
            if (string.IsNullOrEmpty(name))
            {
                name = SetResetPairDefinition.DefaultName(i);
            }

            if (string.Equals(name, pairTextNames[i], StringComparison.Ordinal))
            {
                continue;
            }

            pairTextNames[i] = name;
            pairResetLabels[i] = "Reset " + name;
            pairStoredTexts[i] = name + " stored";
            pairNothingValidTexts[i] = name + ": no values to store";
            pairNothingStoredTexts[i] = name + ": nothing stored";
            pairAlreadySetTexts[i] = name + " already set";
        }
    }

    private void UpdateSnapshot(double now, bool gameRunning)
    {
        if (now - lastSnapshotTime < SnapshotIntervalSeconds)
        {
            return;
        }

        lastSnapshotTime = now;
        if (gameRunning && telemetry != null)
        {
            ReadMaxima();

            // Cached by the source: the same instance until a channel's resolution changes.
            telemetryDescription = telemetry.Describe() ?? string.Empty;
        }

        lock (snapshotLock)
        {
            FillSnapshot(snapshot, now);
        }
    }

    /// <summary>Copies the current state into the shared snapshot (under <see cref="snapshotLock"/>; allocation-free).</summary>
    private void FillSnapshot(SpeedDialSnapshot s, double now)
    {
        CarIdentity car = identity;
        SpeedDialCarData data = carData;

        s.GameRunning = ctx.GameRunning;
        s.GameName = lastGame;
        s.HasCar = data != null;
        s.CarKey = car.CarKey;
        s.CarDisplayName = data != null ? data.DisplayName : car.DisplayName;
        s.CarKeyProvisional = data != null && IsIdentityProvisional(now);
        s.CarFilePath = carFilePath;
        s.CarDataVersion = carDataVersion;

        s.TelemetryName = telemetryName;
        s.TelemetryDescription = telemetryDescription;
        s.ControlMapperAvailable = roles.IsAvailable;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            s.ChannelSupported[i] = supported[i];
            s.CurrentValues[i] = values[i];
            s.MaxValues[i] = maxValues[i];
        }

        dialer.Status.CopyTo(s.Dial);
        s.Status = status;
        s.LastResult = lastResult;

        s.Presets = presetSummaries;
        s.PresetsVersion = presetsVersion;
        s.SlotCount = settings.SlotCount;
        string[] slots = data?.SlotPresetIds;
        for (int i = 0; i < s.SlotPresetIds.Length; i++)
        {
            s.SlotPresetIds[i] = slots != null && i < slots.Length ? slots[i] : null;
        }

        s.SelectedPresetId = data?.SelectedPresetId;
        s.Pairs = pairSummaries;
        s.PairsVersion = pairsVersion;
        s.Settings = settingsCopy;
        s.SettingsVersion = settingsVersion;
        s.SetLearning(data);

        s.LastError = errors.LastError;
        s.FrameCount = diagnostics.FrameCount;
    }

    // =====================================================================================================
    // ISpeedDialHost (UI thread; every mutation is posted to the data thread)
    // =====================================================================================================

    /// <inheritdoc />
    public void CopySnapshot(SpeedDialSnapshot target)
    {
        if (target == null)
        {
            return;
        }

        lock (snapshotLock)
        {
            snapshot.CopyTo(target);
        }

        // Directly from the reporter, so the UI shows an error even when the snapshot update itself is what fails.
        target.LastError = errors?.LastError ?? string.Empty;
    }

    /// <inheritdoc />
    public void EditSettings(Action<SpeedDialSettings> edit)
    {
        if (edit == null)
        {
            return;
        }

        Post(() =>
        {
            try
            {
                edit(settings);
            }
            finally
            {
                // Also after a failing edit: whatever it changed is repaired, published and saved.
                ApplySettingsChange(clock());
            }
        });
    }

    /// <inheritdoc />
    public void CreatePreset(string name, bool fromCurrentValues) => Post(() => CreatePresetNow(name, fromCurrentValues));

    /// <inheritdoc />
    public void RenamePreset(string presetId, string name) => Post(() => RenamePresetNow(presetId, name));

    /// <inheritdoc />
    public void DeletePreset(string presetId) => Post(() => DeletePresetNow(presetId));

    /// <inheritdoc />
    public void SetPresetValue(string presetId, DialChannel channel, double? value) => Post(() => SetPresetValueNow(presetId, channel, value));

    /// <inheritdoc />
    public void CapturePresetFromCurrent(string presetId) => Post(() => CapturePresetNow(presetId));

    /// <inheritdoc />
    public void AssignSlot(int slotIndex, string presetId) => Post(() => AssignSlotNow(slotIndex, presetId));

    /// <inheritdoc />
    public void SelectPreset(string presetId) => Post(() => SelectPresetNow(presetId));

    /// <inheritdoc />
    public void ApplyPreset(string presetId) => Post(() => ApplyPresetNow(presetId));

    /// <inheritdoc />
    public void SetPair(int pairIndex)
    {
        if (pairIndex >= 0 && pairIndex < SpeedDialSettings.MaxPairCount)
        {
            Post(() => StorePair(pairIndex));
        }
    }

    /// <inheritdoc />
    public void ResetPair(int pairIndex)
    {
        if (pairIndex >= 0 && pairIndex < SpeedDialSettings.MaxPairCount)
        {
            Post(() => ResetPairValues(pairIndex));
        }
    }

    /// <inheritdoc />
    public void CancelDial() => Post(CancelByUser);

    /// <inheritdoc />
    public void TestRole(DialChannel channel, bool increase) => Post(() => TestRoleNow(channel, increase));

    /// <inheritdoc />
    public IReadOnlyList<string> GetButtonRoles()
    {
        try
        {
            return roles?.GetButtonRoles() ?? new string[0];
        }
        catch (Exception ex)
        {
            log.Warn("Could not list the Control Mapper roles: " + ex.Message);
            return new string[0];
        }
    }

    /// <inheritdoc />
    public void ResetLearning(DialChannel? channel) => Post(() => ResetLearningNow(channel));

    /// <inheritdoc />
    public string ExportPresets(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return SpeedDialNames.ErrorPrefix + "no file selected.";
        }

        if (dispatcher == null || store == null)
        {
            return SpeedDialNames.ErrorPrefix + "Speed Dial is not running.";
        }

        // Copy the data on the data thread, write the copy on this thread.
        SpeedDialCarData copy = null;
        string failure = dispatcher.Run(
            () =>
            {
                SpeedDialCarData data = carData;
                if (data == null)
                {
                    return "no car loaded.";
                }

                copy = data.DeepCopy();
                return null;
            },
            DataThreadTimeoutMs);
        if (failure != null)
        {
            return SpeedDialNames.ErrorPrefix + failure;
        }

        return store.Export(copy, filePath, out string error)
            ? "Exported " + copy.Presets.Count.ToString(CultureInfo.InvariantCulture) + " preset(s) of " + copy.DisplayName + " to " + filePath
            : SpeedDialNames.ErrorPrefix + error;
    }

    /// <inheritdoc />
    public string ImportPresets(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return SpeedDialNames.ErrorPrefix + "no file selected.";
        }

        if (dispatcher == null || store == null)
        {
            return SpeedDialNames.ErrorPrefix + "Speed Dial is not running.";
        }

        CarIdentity car = identity;
        if (!car.HasCar)
        {
            return SpeedDialNames.ErrorPrefix + "no car loaded.";
        }

        // Read and validate the file on this thread; apply on the data thread and report what really happened.
        SpeedDialCarData imported = store.Import(filePath, out string error);
        if (imported == null)
        {
            return SpeedDialNames.ErrorPrefix + error;
        }

        if (imported.Presets.Count == 0)
        {
            return SpeedDialNames.ErrorPrefix + "the file contains no presets.";
        }

        // state: 0 = queued, 1 = started on the data thread, 2 = abandoned here after a timeout. A late run after the
        // timeout must not import behind the user's back (they were told to try again).
        int state = 0;
        string applied = null;
        string failure = dispatcher.Run(
            () =>
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) == 0)
                {
                    applied = ApplyImport(imported, car);
                }

                return null;
            },
            DataThreadTimeoutMs);
        if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
        {
            return SpeedDialNames.ErrorPrefix + DataThreadDispatcher.TimeoutMessage;
        }

        if (applied == null)
        {
            return SpeedDialNames.ErrorPrefix + (failure ?? "the import failed.");
        }

        return applied;
    }

    private void Post(Action action) => dispatcher?.Post(action);

    // ---- Data-thread bodies of the host operations ----

    private void CreatePresetNow(string name, bool fromCurrentValues)
    {
        SpeedDialCarData data = carData;
        if (data == null)
        {
            SetStatus(NoCarText);
            return;
        }

        if (data.Presets.Count >= SpeedDialCarData.MaxPresets)
        {
            SetStatus(TooManyPresetsText);
            return;
        }

        DialPreset preset = DialPreset.Create(DialPreset.CleanName(name, NextDefaultPresetName(data)));
        if (fromCurrentValues)
        {
            for (int i = 0; i < DialChannels.Count; i++)
            {
                if (MathUtil.IsFinite(values[i]))
                {
                    preset.SetValue((DialChannel)i, values[i]);
                }
            }
        }

        data.Presets.Add(preset);
        if (data.FindPreset(data.SelectedPresetId) == null)
        {
            data.SelectedPresetId = preset.Id;
        }

        PresetsChanged();
        MarkCarDataEdited(clock());
        SetStatus("Created " + preset.Name);
    }

    private void RenamePresetNow(string presetId, string name)
    {
        DialPreset preset = FindPresetOrReport(presetId);
        if (preset == null)
        {
            return;
        }

        string cleaned = DialPreset.CleanName(name, preset.Name);
        if (string.Equals(cleaned, preset.Name, StringComparison.Ordinal))
        {
            return;
        }

        preset.Name = cleaned;
        PresetsChanged();
        MarkCarDataEdited(clock());
    }

    private void DeletePresetNow(string presetId)
    {
        DialPreset preset = FindPresetOrReport(presetId);
        if (preset == null)
        {
            return;
        }

        carData.RemovePreset(preset.Id);
        PresetsChanged();
        MarkCarDataEdited(clock());
        SetStatus("Deleted " + preset.Name);
    }

    private void SetPresetValueNow(string presetId, DialChannel channel, double? value)
    {
        if (!DialChannels.IsValid(channel))
        {
            return;
        }

        DialPreset preset = FindPresetOrReport(presetId);
        if (preset == null)
        {
            return;
        }

        bool hadValue = preset.TryGetValue(channel, out double before);
        preset.SetValue(channel, value);
        bool hasValue = preset.TryGetValue(channel, out double after);
        if (hadValue == hasValue && (!hasValue || before == after))
        {
            return; // no change: no version bump (the UI keeps its rows)
        }

        PresetsChanged();
        MarkCarDataEdited(clock());
    }

    /// <summary>Overwrites the preset's included channels with the current values (every valid channel when it includes none).</summary>
    private void CapturePresetNow(string presetId)
    {
        DialPreset preset = FindPresetOrReport(presetId);
        if (preset == null)
        {
            return;
        }

        bool includesAny = preset.IncludedCount() > 0;
        int captured = 0;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            var channel = (DialChannel)i;
            if ((includesAny && !preset.Includes(channel)) || !MathUtil.IsFinite(values[i]))
            {
                continue;
            }

            preset.SetValue(channel, values[i]);
            captured++;
        }

        if (captured == 0)
        {
            SetStatus(NoTelemetryValuesText);
            return;
        }

        PresetsChanged();
        MarkCarDataEdited(clock());
        SetStatus("Captured current values into " + preset.Name);
    }

    private void AssignSlotNow(int slotIndex, string presetId)
    {
        if (slotIndex < 0 || slotIndex >= SpeedDialSettings.MaxSlotCount)
        {
            return;
        }

        SpeedDialCarData data = carData;
        if (data == null)
        {
            SetStatus(NoCarText);
            return;
        }

        string id = null;
        if (!string.IsNullOrEmpty(presetId))
        {
            DialPreset preset = data.FindPreset(presetId);
            if (preset == null)
            {
                SetStatus(PresetNotFoundText);
                return;
            }

            id = preset.Id;
        }

        // A preset sits on at most one slot: assigning it moves it (also off hidden slots beyond the slot count), so
        // two quick changes in the UI can never leave it on two Dial buttons.
        bool changed = false;
        if (id != null)
        {
            for (int j = 0; j < data.SlotPresetIds.Length; j++)
            {
                if (j != slotIndex && string.Equals(data.SlotPresetIds[j], id, StringComparison.OrdinalIgnoreCase))
                {
                    data.SlotPresetIds[j] = null;
                    changed = true;
                }
            }
        }

        if (!string.Equals(data.SlotPresetIds[slotIndex], id, StringComparison.OrdinalIgnoreCase))
        {
            data.SlotPresetIds[slotIndex] = id;
            changed = true;
        }

        if (changed)
        {
            MarkCarDataEdited(clock());
        }
    }

    private void SelectPresetNow(string presetId)
    {
        SpeedDialCarData data = carData;
        if (data == null)
        {
            SetStatus(NoCarText);
            return;
        }

        DialPreset preset = null;
        if (!string.IsNullOrEmpty(presetId))
        {
            preset = data.FindPreset(presetId);
            if (preset == null)
            {
                SetStatus(PresetNotFoundText);
                return;
            }
        }

        string id = preset?.Id;
        if (string.Equals(data.SelectedPresetId, id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        data.SelectedPresetId = id;
        UpdateSelectionExports();
        MarkCarDataEdited(clock());
        SetStatus(preset != null ? preset.Name : NoSelectionText);
    }

    private void ApplyPresetNow(string presetId)
    {
        if (!CanDial())
        {
            return;
        }

        DialPreset preset = carData.FindPreset(presetId);
        if (preset == null)
        {
            SetStatus(PresetNotFoundText);
            return;
        }

        StartPreset(preset);
    }

    /// <summary>One press of a channel's role, to test the binding (ignored while a job runs).</summary>
    private void TestRoleNow(DialChannel channel, bool increase)
    {
        if (!DialChannels.IsValid(channel))
        {
            return;
        }

        if (dialer.IsBusy)
        {
            SetStatus(BusyText);
            return;
        }

        string role = settings.GetBinding(channel)?.GetRole(increase) ?? string.Empty;
        string what = DialChannels.DisplayName(channel) + (increase ? " +" : " -");
        if (role.Length == 0)
        {
            SetStatus(SpeedDialNames.ErrorPrefix + "no role bound for " + what);
            return;
        }

        if (!roles.Press(role, settings.Timing.PressMs))
        {
            SetStatus(SpeedDialNames.ErrorPrefix + "could not press " + role
                + (roles.IsAvailable ? " (not a Control Mapper role)" : " (Control Mapper not available)"));
            return;
        }

        SetStatus("Pressed " + role + " (" + what + ")");
    }

    private void ResetLearningNow(DialChannel? channel)
    {
        SpeedDialCarData data = carData;
        if (data == null)
        {
            SetStatus(NoCarText);
            return;
        }

        if (channel.HasValue)
        {
            if (!DialChannels.IsValid(channel.Value))
            {
                return;
            }

            data.GetLearning(channel.Value)?.Reset();
            SetStatus("Learning reset for " + DialChannels.DisplayName(channel.Value));
        }
        else
        {
            for (int i = 0; i < DialChannels.Count; i++)
            {
                data.GetLearning((DialChannel)i)?.Reset();
            }

            SetStatus("Learning reset for every channel");
        }

        MarkCarDataEdited(clock());
    }

    /// <summary>Appends the imported presets to the current car if it is still the car the user imported into.</summary>
    private string ApplyImport(SpeedDialCarData imported, CarIdentity target)
    {
        SpeedDialCarData data = carData;
        if (data == null)
        {
            return SpeedDialNames.ErrorPrefix + "no car loaded.";
        }

        if (!identity.SameCarAs(target))
        {
            return SpeedDialNames.ErrorPrefix + "the car changed before the import could be applied; nothing was imported.";
        }

        int added = SpeedDialStore.AppendPresets(data, imported, out int skipped);
        if (added == 0)
        {
            return TooManyPresetsText + "; nothing was imported.";
        }

        carDataVersion++;
        PresetsChanged();
        MarkCarDataEdited(clock());
        string message = "Imported " + added.ToString(CultureInfo.InvariantCulture) + " preset(s) into " + data.DisplayName
            + (skipped > 0
                ? " (" + skipped.ToString(CultureInfo.InvariantCulture) + " skipped: at most "
                    + SpeedDialCarData.MaxPresets.ToString(CultureInfo.InvariantCulture) + " per car)."
                : ".");
        SetStatus(message);
        log.Info(message);
        return message;
    }

    /// <summary>The preset of the current car, or null with a status text (no car, unknown id).</summary>
    private DialPreset FindPresetOrReport(string presetId)
    {
        SpeedDialCarData data = carData;
        if (data == null)
        {
            SetStatus(NoCarText);
            return null;
        }

        DialPreset preset = data.FindPreset(presetId);
        if (preset == null)
        {
            SetStatus(PresetNotFoundText);
        }

        return preset;
    }

    /// <summary>"Preset n" with the smallest n ≥ count + 1 that no preset of the car uses yet.</summary>
    private static string NextDefaultPresetName(SpeedDialCarData data)
    {
        for (int n = data.Presets.Count + 1; ; n++)
        {
            string candidate = DefaultPresetNameStem + n.ToString(CultureInfo.InvariantCulture);
            bool used = false;
            foreach (DialPreset preset in data.Presets)
            {
                if (string.Equals(preset.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    used = true;
                    break;
                }
            }

            if (!used)
            {
                return candidate;
            }
        }
    }

    // =====================================================================================================
    // Static helpers
    // =====================================================================================================

    private static string ResultText(DialState state)
    {
        switch (state)
        {
            case DialState.Completed:
                return CompletedResult;
            case DialState.Partial:
                return PartialResult;
            case DialState.Failed:
                return FailedResult;
            case DialState.Cancelled:
                return CancelledResult;
            default:
                return null;
        }
    }

    private static string[] BuildIndexedTexts(string prefix, string suffix, int count)
    {
        var texts = new string[count];
        for (int i = 0; i < count; i++)
        {
            texts[i] = prefix + (i + 1).ToString(CultureInfo.InvariantCulture) + suffix;
        }

        return texts;
    }

    private static double[] CreateNaNArray()
    {
        var array = new double[DialChannels.Count];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = double.NaN;
        }

        return array;
    }
}
