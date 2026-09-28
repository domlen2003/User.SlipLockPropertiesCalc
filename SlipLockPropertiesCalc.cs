using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Balance.Recording;
using User.SlipLockPropertiesCalc.Balance.Sources;
using User.SlipLockPropertiesCalc.Integration;
using User.SlipLockPropertiesCalc.Profiles;
using User.SlipLockPropertiesCalc.Settings;
using User.SlipLockPropertiesCalc.SlipLock;
using User.SlipLockPropertiesCalc.Telemetry;
using User.SlipLockPropertiesCalc.UI;

namespace User.SlipLockPropertiesCalc;

/// <summary>
/// SimHub plugin shell: wires SimHub to the SimHub-independent modules and owns all runtime state.
/// <para>
/// Threading: <see cref="DataUpdate"/> runs on SimHub's data thread and owns every processing object, the current
/// car profile and the save scheduler. The settings UI (UI thread) talks to the plugin only through
/// <see cref="ISlipLockHost"/>: it reads a <see cref="LiveSnapshot"/> copied under a lock at up to 20 Hz, writes
/// bool/enum settings directly and queues everything else (numeric settings, per-car edits) on
/// <see cref="commands"/>, which the data thread drains at the start of each frame.
/// </para>
/// <para>
/// Fault isolation: <see cref="DataUpdate"/> runs its stages (frame context and car handling, slip/lock, balance,
/// persistence, UI snapshot) in separate guarded blocks. A stage that throws has its outputs zeroed, so a
/// persistent error can never freeze haptic effects at their last value, and the later stages still run (the error
/// reaches the UI and saves still happen).
/// </para>
/// <para>
/// Hot path: <see cref="DataUpdate"/> is allocation-free in steady state; allocations happen only on game/car/
/// session changes, in the rate-limited error path, and in the optional 1 Hz debug file log.
/// </para>
/// COMPATIBILITY: class name, namespace, attributes and the settings key are part of existing user setups.
/// </summary>
[PluginDescription("Per-wheel slip/lock channels with corner load estimation and understeer/oversteer detection for haptic devices")]
[PluginAuthor("Dominik Lenz")]
[PluginName("Slip Lock Properties Calc")]
public sealed class SlipLockPropertiesCalc : IPlugin, IDataPlugin, IWPFSettingsV2, ISlipLockHost
{
    /// <summary>SimHub common-settings key (v1; must not change).</summary>
    public const string SettingsKey = "GeneralSettings";

    private const string PluginDataFolder = "SlipLockPropertiesCalc";
    private const string RecordingsFolder = "Recordings";
    private const string NotAvailable = "N/A";
    private const double KmhPerMs = 3.6;
    private const double PercentScale = 100.0;

    /// <summary>The UI snapshot is refreshed at most this often (20 Hz).</summary>
    private const double SnapshotIntervalSeconds = 0.05;

    /// <summary>The balance resolution text allocates, so it is refreshed at 1 Hz and only while the debug view is shown.</summary>
    private const double ResolutionIntervalSeconds = 1.0;

    /// <summary>LMU's native model name can be empty for the first frames: retry resolution this often ...</summary>
    private const double IdentityRetryIntervalSeconds = 1.0;

    /// <summary>... for at most this long after the car appeared.</summary>
    private const double IdentityRetryWindowSeconds = 10.0;

    /// <summary>After the first error, further errors are logged at most this often.</summary>
    private const double ErrorLogIntervalSeconds = 10.0;

    /// <summary>Smoothing of the displayed DataUpdate duration.</summary>
    private const double TimingSmoothing = 0.05;

    /// <summary>How long a UI request waits for the data thread before giving up.</summary>
    private const int DataThreadTimeoutMs = 3000;

    /// <summary>End(): how long to wait for queued asynchronous profile writes.</summary>
    private static readonly TimeSpan ShutdownWriteTimeout = TimeSpan.FromSeconds(5);

    private static readonly BalanceOverrides NoOverrides = new BalanceOverrides();

    /// <summary>Diagnostics text per <see cref="CarKeySource"/> (indexed by the enum value).</summary>
    private static readonly string[] KeySourceTexts = { string.Empty, "native model", "car model", "car id" };

    // ---- Infrastructure ----
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
    private readonly object snapshotLock = new object();
    private readonly LiveSnapshot snapshot = new LiveSnapshot();
    private readonly FrameContext ctx = new FrameContext();
    private SimHubLog log;
    private PluginManagerTelemetryReader reader;
    private PropertyExporter exporter;
    private DebugFileLog debugLog;
    private SettingsWriter settingsWriter;
    private string pluginDataRoot;
    private int settingsChangedFlag;
    private int dataThreadId = -1;

    // ---- Slip/lock pipeline ----
    private readonly MaxGTracker maxG = new MaxGTracker();
    private readonly SlipLockProcessor processor = new SlipLockProcessor();
    private readonly SlipLockInputs inputs = new SlipLockInputs();
    private readonly SlipLockTuning tuning = new SlipLockTuning();
    private readonly SlipLockOutputs outputs = new SlipLockOutputs();
    private SlipSourceResolver slipResolver;
    private CapabilityTracker capabilities;
    private WheelSpeedModeDetector detector;
    private GamePreset preset = GamePresets.Default;
    private string presetName = GamePresets.DefaultName;

    // ---- Balance ----
    private readonly VehicleState vehicleState = new VehicleState();
    private IVehicleStateSource balanceSource = new NullStateSource();
    private BalanceEstimator estimator;
    private BalanceRecorder recorder;
    private string balanceResolution = string.Empty;
    private double nextResolutionTime = double.NegativeInfinity;

    // ---- Persistence / car profile (data thread) ----
    private CarProfileStore store;
    private SaveScheduler scheduler;
    private CarProfile profile;
    private volatile CarIdentity identity = CarIdentity.None;
    private string carProfilePath = string.Empty;
    private string carKeySourceText = string.Empty;
    private int carProfileVersion;

    // ---- Per-frame bookkeeping (data thread) ----
    private bool wasRunning;
    private bool gameKnown;
    private string lastGame = string.Empty;
    private string lastMaxGCarId = NotAvailable;
    private bool identityDirty = true;
    private string seenCarId;
    private string seenCarModel;
    private double identityRetryUntil = double.NegativeInfinity;
    private double nextIdentityRetry = double.PositiveInfinity;
    private bool sessionKnown;
    private Guid lastSessionId;
    private double lastSnapshotTime = double.NegativeInfinity;
    private long frameCount;
    private double dataUpdateMs;

    // ---- Error reporting (data thread) ----
    private string lastError = string.Empty;
    private bool errorLogged;
    private double lastErrorLogTime = double.NegativeInfinity;
    private int suppressedErrors;

    /// <summary>Global settings (live object, see <see cref="ISlipLockHost.Settings"/>).</summary>
    public PluginSettings Settings { get; private set; }

    /// <summary>Set by SimHub before <see cref="Init"/>.</summary>
    public PluginManager PluginManager { get; set; }

    /// <summary>Left menu icon (24x24).</summary>
    public ImageSource PictureIcon => this.ToIcon(Properties.Resources.sdkmenuicon);

    /// <summary>Short title for SimHub's left menu.</summary>
    public string LeftMenuTitle => "Slip Lock Calc";

    // =====================================================================================================
    // SimHub lifecycle
    // =====================================================================================================

    /// <summary>Called once after plugin start-up: loads settings, builds all modules and registers properties.</summary>
    public void Init(PluginManager pluginManager)
    {
        log = new SimHubLog();
        log.Info("Starting plugin");
        PluginManager = pluginManager;

        Settings = this.ReadCommonSettings(SettingsKey, () => new PluginSettings()) ?? new PluginSettings();
        Settings.Normalize();

        string simHubDirectory = Path.GetDirectoryName(typeof(PluginManager).Assembly.Location) ?? ".";
        pluginDataRoot = Path.Combine(simHubDirectory, "PluginsData", PluginDataFolder);

        reader = new PluginManagerTelemetryReader(pluginManager);
        slipResolver = new SlipSourceResolver();
        capabilities = new CapabilityTracker(Settings);
        detector = new WheelSpeedModeDetector(Settings, slipResolver, capabilities);
        estimator = new BalanceEstimator(Settings.Balance, log);
        recorder = new BalanceRecorder(log);
        store = new CarProfileStore(pluginDataRoot, log);
        settingsWriter = new SettingsWriter(copy => this.SaveCommonSettings(SettingsKey, copy), log);
        scheduler = new SaveScheduler(SaveSettingsAsync, SaveCurrentProfile, log);
        debugLog = new DebugFileLog(Path.Combine(simHubDirectory, "Logs", DebugFileLog.FileName), log);

        exporter = new PropertyExporter(pluginManager, GetType());
        exporter.RegisterSlipLock(outputs, maxG);
        exporter.RegisterBalance(estimator.Outputs);
        VerifyPropertyRegistration(pluginManager);

        log.Info("Plugin initialized: " + exporter.Names.Count.ToString(CultureInfo.InvariantCulture)
            + " properties, data folder " + pluginDataRoot);
    }

    /// <summary>
    /// Start-up self-check: every ShakeIT/dash formula and generated profile references the properties as
    /// <c>SlipLockPropertiesCalc.*</c>. A registration under another prefix would silently turn all effects off.
    /// </summary>
    private void VerifyPropertyRegistration(PluginManager pluginManager)
    {
        try
        {
            string probe = PropertyExporter.FullName("SlipLock.MaxSway");
            if (!exporter.PrefixMatchesPluginType)
            {
                log.Error("Plugin class " + GetType().Name + " does not match the property prefix " + PropertyExporter.SimHubPrefix
                    + ": existing ShakeIT profiles and dashboards will not find the properties.");
            }
            else if (pluginManager.GetPropertyValue(probe) == null)
            {
                log.Error("Self-check failed: property " + probe + " is not readable after registration.");
            }
        }
        catch (Exception ex)
        {
            log.Warn("Property self-check could not run: " + ex.Message);
        }
    }

    /// <summary>
    /// Called once per SimHub data frame (60+ Hz) on the data thread. Allocation-free in steady state; never throws.
    /// </summary>
    public void DataUpdate(PluginManager pluginManager, ref GameData data)
    {
        long startTicks = clock.ElapsedTicks;
        double now = clock.Elapsed.TotalSeconds;
        dataThreadId = Thread.CurrentThread.ManagedThreadId;

        // Each command is guarded individually inside.
        DrainCommands(now);

        bool running = false;
        try
        {
            FrameContextBuilder.Fill(ctx, data, now);
            if (ctx.GameRunning)
            {
                PrepareFrame(now);
                running = true;
            }
            else
            {
                HandleGameNotRunning();
            }
        }
        catch (Exception ex)
        {
            ReportError(ex, now);
            ClearAllOutputs();
        }

        if (running)
        {
            try
            {
                ProcessSlipLock(now);
            }
            catch (Exception ex)
            {
                ReportError(ex, now);
                outputs.Clear();
                processor.Reset();
            }

            try
            {
                ProcessBalance(now);
            }
            catch (Exception ex)
            {
                ReportError(ex, now);
                ResetBalanceAfterError(now);
            }

            try
            {
                WriteDebugLog(now);
            }
            catch (Exception ex)
            {
                ReportError(ex, now);
            }
        }

        try
        {
            scheduler.Tick(now);
        }
        catch (Exception ex)
        {
            ReportError(ex, now);
        }

        try
        {
            UpdateSnapshot(now);
        }
        catch (Exception ex)
        {
            ReportError(ex, now);
        }

        frameCount++;
        double elapsedMs = (clock.ElapsedTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
        dataUpdateMs += (elapsedMs - dataUpdateMs) * TimingSmoothing;
    }

    /// <summary>Called at plugin manager stop: saves everything synchronously and stops background work.</summary>
    public void End(PluginManager pluginManager)
    {
        try
        {
            // SimHub no longer calls DataUpdate: apply what the UI queued last (an import, a slider edit).
            DrainCommands(clock.Elapsed.TotalSeconds);
            recorder?.Dispose();
            if (profile != null && !IsIdentityProvisional(clock.Elapsed.TotalSeconds))
            {
                estimator.SaveTo(profile);
                store.Save(profile); // synchronous; supersedes anything still queued for this car
            }

            scheduler?.DiscardProfileChanges();
            settingsWriter?.Save(Settings); // synchronous; supersedes a queued asynchronous save
            store?.WaitForPendingWrites(ShutdownWriteTimeout);
            settingsWriter?.WaitForPendingWrites(ShutdownWriteTimeout);
            log?.Info("Plugin stopped");
        }
        catch (Exception ex)
        {
            log?.Error("End failed: " + ex);
        }
    }

    /// <summary>Returns the settings page.</summary>
    public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager) =>
        new SettingsControl(new SettingsViewModel(this));

    // =====================================================================================================
    // Per-frame processing (data thread)
    // =====================================================================================================

    /// <summary>Game, car and session changes (rare paths; allocations are fine there).</summary>
    private void PrepareFrame(double now)
    {
        wasRunning = true;
        if (!gameKnown || !string.Equals(ctx.GameName, lastGame, StringComparison.Ordinal))
        {
            OnGameChanged();
        }

        UpdateCar(now);
        UpdateSession();
    }

    /// <summary>The v1 slip/lock pipeline (order: max G, capabilities, slip source, base slip, processor).</summary>
    private void ProcessSlipLock(double now)
    {
        maxG.Update(ctx.Sway, ctx.Surge);
        capabilities.Update(reader, ctx.AbsActive, ctx.TcActive);
        if (slipResolver.Probe(reader, now))
        {
            LogSlipResolution();
        }

        detector.ComputeBaseSlip(reader, ctx.SpeedKmh / KmhPerMs, ctx.Sway, ctx.Brake, now, inputs.BaseSlip);
        inputs.BaseSlipIsSigned = detector.BaseSlipIsSigned;
        inputs.HasShakeItLock = slipResolver.ReadWheelLock(reader, inputs.ShakeItLock);
        if (detector.SettingsChanged || capabilities.SettingsChanged)
        {
            scheduler.MarkSettingsDirty(now);
            detector.ClearSettingsChanged();
            capabilities.ClearSettingsChanged();
        }

        inputs.WallTime = now;
        inputs.Throttle = ctx.Throttle;
        inputs.Brake = ctx.Brake;
        inputs.Sway = ctx.Sway;
        inputs.Surge = ctx.Surge;
        inputs.SpeedKmh = ctx.SpeedKmh;
        inputs.AbsActive = ctx.AbsActive;
        inputs.TcActive = ctx.TcActive;
        inputs.GameExportsAbs = capabilities.GameExportsAbs;
        inputs.GameExportsTc = capabilities.GameExportsTc;
        inputs.AbsLevel = capabilities.AbsLevel;
        inputs.TcLevel = capabilities.TcLevel;

        Settings.CopyTo(tuning);
        CarProfile car = profile;
        tuning.SlipSensitivity = car != null ? car.SlipSensitivity / PercentScale : 1.0;
        tuning.LockSensitivity = car != null ? car.LockSensitivity / PercentScale : 1.0;
        processor.Process(inputs, preset, tuning, maxG, outputs);
    }

    /// <summary>Understeer/oversteer estimation, recording and learner persistence marks.</summary>
    private void ProcessBalance(double now)
    {
        balanceSource.Read(ctx, reader, vehicleState);
        estimator.Update(vehicleState);
        recorder.Record(vehicleState, estimator.Outputs); // no-op while not recording
        if (estimator.LearnerDirty)
        {
            scheduler.MarkProfileDirty(now);
        }

        if (estimator.CalibrationChanged)
        {
            scheduler.MarkSettingsDirty(now);
            estimator.ClearCalibrationChanged();
        }
    }

    /// <summary>Optional 1 Hz debug file log (the rate check comes before any formatting).</summary>
    private void WriteDebugLog(double now)
    {
        if (debugLog.IsDue(Settings.DebugFileLog, now))
        {
            debugLog.WriteScanIfPending(reader, ctx.GameName, presetName, balanceSource.Name);
            debugLog.WriteFrame(ctx, inputs, outputs, detector.EffectiveSource, estimator.Outputs);
        }
    }

    /// <summary>A stage failed before the slip/lock and balance stages could run: zero every export.</summary>
    private void ClearAllOutputs()
    {
        try
        {
            outputs.Clear();
            processor.Reset();
            estimator.Reset();
        }
        catch
        {
            // Error path: must not throw out of DataUpdate.
        }
    }

    /// <summary>The balance stage threw: zero its outputs (estimator reset); a failing reset still leaves the outputs cleared.</summary>
    private void ResetBalanceAfterError(double now)
    {
        try
        {
            estimator.Reset();
        }
        catch (Exception ex)
        {
            estimator.Outputs.Clear();
            ReportError(ex, now);
        }
    }

    /// <summary>
    /// First frame without a running game after it ran: zero every export (v1 left stale values) and reset the
    /// envelopes/filters so the next session starts clean.
    /// </summary>
    private void HandleGameNotRunning()
    {
        if (!wasRunning)
        {
            return;
        }

        wasRunning = false;
        outputs.Clear();
        processor.Reset();
        estimator.Reset();
    }

    /// <summary>Per-game reset (v1 semantics) and creation of the game's balance adapter.</summary>
    private void OnGameChanged()
    {
        gameKnown = true;
        lastGame = ctx.GameName;

        slipResolver.Reset();
        capabilities.Reset(lastGame);
        detector.Reset(lastGame);
        preset = GamePresets.Get(lastGame, out presetName);

        balanceSource = VehicleStateSourceFactory.Create(lastGame, log);
        SwitchCar(CarIdentity.None); // until the new sim's car resolves
        identityDirty = true;
        sessionKnown = false;
        debugLog.RequestScan();
        log.Info("Game: " + lastGame + ", preset " + presetName + ", balance source " + balanceSource.Name
            + (balanceSource.IsSupported ? string.Empty : " (understeer/oversteer not supported)"));
    }

    private void UpdateCar(double now)
    {
        // v1 max-G reset rule, unchanged.
        string carId = ctx.CarId;
        if (!string.Equals(carId, lastMaxGCarId, StringComparison.Ordinal) && !string.IsNullOrEmpty(carId) && carId != NotAvailable)
        {
            maxG.Reset();
            lastMaxGCarId = carId;
        }

        bool changed = identityDirty
            || !string.Equals(ctx.CarId, seenCarId, StringComparison.Ordinal)
            || !string.Equals(ctx.CarModel, seenCarModel, StringComparison.Ordinal);
        bool retry = !changed && identity.ShouldRetry && now >= nextIdentityRetry && now <= identityRetryUntil;
        if (!changed && !retry)
        {
            return;
        }

        // Rare path (car change or bounded retry): allocations are fine here.
        identityDirty = false;
        seenCarId = ctx.CarId;
        seenCarModel = ctx.CarModel;
        CarIdentity resolved = CarIdentityResolver.Resolve(ctx, reader);
        if (changed)
        {
            identityRetryUntil = now + IdentityRetryWindowSeconds;
        }

        nextIdentityRetry = resolved.ShouldRetry ? now + IdentityRetryIntervalSeconds : double.PositiveInfinity;
        if (resolved.HasCar != identity.HasCar || !resolved.SameCarAs(identity))
        {
            SwitchCar(resolved);
        }
    }

    /// <summary>Saves the current car's profile and loads (or creates) the profile of <paramref name="next"/>.</summary>
    private void SwitchCar(CarIdentity next)
    {
        CarIdentity previous = identity;
        CarProfile provisionalProfile = null;
        if (profile != null)
        {
            bool provisional = previous.ShouldRetry && next.KeySource == CarKeySource.NativeModel
                && string.Equals(previous.SimKey, next.SimKey, StringComparison.OrdinalIgnoreCase);
            if (provisional)
            {
                // The livery-specific fallback key was only a placeholder until LMU reported the model name:
                // do not leave a profile file behind for it; its state is carried over below instead.
                scheduler.DiscardProfileChanges();
                estimator.SaveTo(profile);
                provisionalProfile = profile;
            }
            else
            {
                scheduler.FlushProfile();
            }
        }

        scheduler.DiscardProfileChanges();
        identity = next;
        carKeySourceText = KeySourceTexts[Math.Max(0, Math.Min(KeySourceTexts.Length - 1, (int)next.KeySource))];
        if (next.HasCar)
        {
            bool stored = store.ProfileExists(next.SimKey, next.CarKey);
            profile = store.Load(next.SimKey, next.CarKey, next.DisplayName, next.CarClass);
            if (provisionalProfile != null && !stored)
            {
                // First time this car is seen under its native key: keep what was set/learned under the placeholder.
                CarryOver(provisionalProfile, profile);
                scheduler.MarkProfileEdited(clock.Elapsed.TotalSeconds);
            }

            estimator.LoadCar(profile, Settings.GetCalibration(next.SimKey), next.CarClass);
            carProfilePath = store.GetProfilePath(next.SimKey, next.CarKey);
            log.Info("Car: " + next + ", profile " + carProfilePath);
        }
        else
        {
            if (profile != null)
            {
                log.Info("Car unloaded (" + previous + ")");
            }

            profile = null;
            estimator.Unload();
            carProfilePath = string.Empty;
        }

        carProfileVersion++;
        nextResolutionTime = double.NegativeInfinity;
    }

    /// <summary>Copies the per-car settings and learned state of the placeholder profile into the resolved one.</summary>
    private static void CarryOver(CarProfile from, CarProfile to)
    {
        to.SlipSensitivity = from.SlipSensitivity;
        to.LockSensitivity = from.LockSensitivity;
        to.UndersteerSensitivity = from.UndersteerSensitivity;
        to.OversteerSensitivity = from.OversteerSensitivity;
        from.Overrides.CopyTo(to.Overrides);
        to.LearningLocked = from.LearningLocked;
        to.Learned = from.Learned ?? new BalanceLearnedState();
    }

    /// <summary>
    /// True while the car key is a livery-specific placeholder that may still be replaced by the native model name
    /// (bounded retry window): the profile is not saved then, so no file is left behind for the placeholder.
    /// </summary>
    private bool IsIdentityProvisional(double now) => identity.ShouldRetry && now <= identityRetryUntil;

    /// <summary>A new SimHub session (SessionId) restarts the balance filters and session adaptation layer.</summary>
    private void UpdateSession()
    {
        Guid sessionId = ctx.SessionId;
        if (!sessionKnown)
        {
            sessionKnown = true;
            lastSessionId = sessionId;
            return;
        }

        if (sessionId != lastSessionId)
        {
            lastSessionId = sessionId;
            estimator.Reset();
        }
    }

    private void DrainCommands(double now)
    {
        if (Interlocked.Exchange(ref settingsChangedFlag, 0) != 0)
        {
            scheduler.MarkSettingsDirty(now);
        }

        while (commands.TryDequeue(out Action command))
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                ReportError(ex, now);
            }
        }
    }

    private void UpdateSnapshot(double now)
    {
        if (now - lastSnapshotTime < SnapshotIntervalSeconds)
        {
            return;
        }

        lastSnapshotTime = now;
        if (Settings.ShowDebugView && now >= nextResolutionTime)
        {
            nextResolutionTime = now + ResolutionIntervalSeconds;
            string text = balanceSource.DescribeResolution() ?? string.Empty;
            if (!string.Equals(text, balanceResolution, StringComparison.Ordinal))
            {
                balanceResolution = text;
            }
        }

        lock (snapshotLock)
        {
            FillSnapshot(snapshot);
        }
    }

    /// <summary>Copies the current state into the shared snapshot (under <see cref="snapshotLock"/>; allocation-free).</summary>
    private void FillSnapshot(LiveSnapshot s)
    {
        CarIdentity car = identity;
        CarProfile carProfile = profile;

        s.GameRunning = ctx.GameRunning;
        s.GameName = lastGame;
        s.HasCar = carProfile != null;
        s.CarId = ctx.CarId;
        s.CarKey = car.CarKey;
        s.CarDisplayName = car.DisplayName;
        s.CarClass = car.CarClass;
        s.CarProfileVersion = carProfileVersion;
        s.CarProfilePath = carProfilePath;
        s.CarKeySource = carKeySourceText;
        s.CarKeyProvisional = IsIdentityProvisional(clock.Elapsed.TotalSeconds);

        s.SlipSensitivity = carProfile?.SlipSensitivity ?? CarProfile.DefaultSensitivity;
        s.LockSensitivity = carProfile?.LockSensitivity ?? CarProfile.DefaultSensitivity;
        s.UndersteerSensitivity = carProfile?.UndersteerSensitivity ?? CarProfile.DefaultSensitivity;
        s.OversteerSensitivity = carProfile?.OversteerSensitivity ?? CarProfile.DefaultSensitivity;

        // In per-wheel mode the slip source is not read every frame: its resolution tells whether it exists.
        bool perWheel = detector.State == DetectionState.PerWheel;
        SlipSourceKind kind = slipResolver.Kind;
        s.SlipSource = detector.EffectiveSource;
        s.ResolvedSlipSource = kind;
        s.SlipDataAvailable = perWheel ? detector.WheelSpeedAvailable : detector.SlipSourceAvailable;
        s.ShakeItSlipAvailable = kind == SlipSourceKind.ShakeIt && (perWheel || detector.SlipSourceAvailable);
        s.ShakeItLockAvailable = slipResolver.HasWheelLock;
        s.LockUsesShakeIt = processor.LockMergedShakeIt;
        s.LockSynthesized = processor.LockSynthesizedFromSlip;
        s.Detection = detector.State;
        s.DetectSpeed = detector.DetectSpeed;
        s.DetectLat = detector.DetectLat;
        s.DetectBrake = detector.DetectBrake;
        s.DetectSpeedOk = detector.DetectSpeedOk;
        s.DetectCornerOk = detector.DetectCornerOk;
        s.DetectBrakeOk = detector.DetectBrakeOk;
        s.DetectFrames = detector.DetectFrames;
        s.DetectMaxDelta = detector.DetectMaxDelta;
        for (int i = 0; i < Core.Wheels.Count; i++)
        {
            s.SlipPaths[i] = slipResolver.ResolvedPaths[i];
            s.LockPaths[i] = slipResolver.WheelLockPaths[i];
            s.BaseSlip[i] = outputs.BaseSlip[i];
            s.Slip[i] = outputs.Slip[i];
            s.Lock[i] = outputs.Lock[i];
            s.Abs[i] = outputs.Abs[i];
            s.Tc[i] = outputs.Tc[i];
            s.SlipBlend[i] = outputs.SlipBlend[i];
            s.LockBlend[i] = outputs.LockBlend[i];
            s.SlipTc[i] = outputs.SlipTc[i];
            s.LockAbs[i] = outputs.LockAbs[i];
            s.Loads[i] = outputs.Loads[i];
        }

        s.PresetName = presetName;
        s.Preset = preset;

        s.GameExportsAbs = capabilities.GameExportsAbs;
        s.GameExportsTc = capabilities.GameExportsTc;
        s.CarHasAbs = capabilities.CarHasAbs;
        s.CarHasTc = capabilities.CarHasTc;
        s.AbsLevel = capabilities.AbsLevel;
        s.TcLevel = capabilities.TcLevel;
        s.AggregateUsesTc = outputs.AggregateUsesTc;
        s.AggregateUsesAbs = outputs.AggregateUsesAbs;
        s.MaxSway = maxG.MaxSway;
        s.MaxSurge = maxG.MaxSurge;
        s.MaxDecel = maxG.MaxDecel;

        s.BaseSlipMono = outputs.BaseSlipMono;
        s.BaseLockMono = outputs.BaseLockMono;
        s.BaseAbsMono = outputs.BaseAbsMono;
        s.BaseTcMono = outputs.BaseTcMono;
        s.SlipMono = outputs.SlipMono;
        s.LockMono = outputs.LockMono;
        s.SlipTcMono = outputs.SlipTcMono;
        s.LockAbsMono = outputs.LockAbsMono;

        s.BalanceSourceName = balanceSource.Name;
        s.BalanceSupported = balanceSource.IsSupported;
        s.BalanceResolution = balanceResolution;
        estimator.Outputs.CopyTo(s.Balance);
        s.ClassPresetAuto = estimator.AutoClassPreset;
        s.ClassPresetOverride = carProfile?.Overrides.ClassPreset;
        (carProfile?.Overrides ?? NoOverrides).CopyTo(s.Overrides);
        s.LearningLocked = carProfile != null && carProfile.LearningLocked;

        s.RecordingActive = recorder.IsRecording;
        s.RecordingPath = recorder.FilePath;
        s.LastError = lastError;
        s.FrameCount = frameCount;
        s.DataUpdateMs = dataUpdateMs;
    }

    // =====================================================================================================
    // Persistence callbacks (data thread, via SaveScheduler)
    // =====================================================================================================

    /// <summary>Copies the settings on the data thread and writes them on the thread pool (no file IO in DataUpdate).</summary>
    private void SaveSettingsAsync() => settingsWriter.SaveAsync(Settings);

    /// <summary>
    /// Writes the learned state into the profile and queues the file write. The JSON serialization runs here on the
    /// data thread (so the thread pool never touches the live profile); it is a deliberate exception to DESIGN 2,
    /// bounded to at most once per minute while learning and 2 s after a user edit.
    /// </summary>
    private void SaveCurrentProfile()
    {
        CarProfile car = profile;
        if (car == null)
        {
            return;
        }

        double now = clock.Elapsed.TotalSeconds;
        if (IsIdentityProvisional(now))
        {
            // Placeholder key: save only once the retry window has passed and the key is final.
            scheduler.MarkProfileEdited(identityRetryUntil);
            return;
        }

        estimator.SaveTo(car);
        store.SaveAsync(car);
    }

    // =====================================================================================================
    // Diagnostics
    // =====================================================================================================

    /// <summary>Error path: first error logged immediately, then at most every 10 s with a suppressed count.</summary>
    private void ReportError(Exception ex, double now)
    {
        try
        {
            lastError = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + ex.GetType().Name + ": " + ex.Message;
            lock (snapshotLock)
            {
                // Directly, so the UI shows the error even when the snapshot update itself is what fails.
                snapshot.LastError = lastError;
            }

            if (errorLogged && now - lastErrorLogTime < ErrorLogIntervalSeconds)
            {
                suppressedErrors++;
                return;
            }

            string suppressed = suppressedErrors > 0
                ? " (" + suppressedErrors.ToString(CultureInfo.InvariantCulture) + " further errors suppressed)"
                : string.Empty;
            errorLogged = true;
            lastErrorLogTime = now;
            suppressedErrors = 0;
            log.Error("DataUpdate error" + suppressed + ": " + ex);
        }
        catch
        {
            // Reporting must never throw out of DataUpdate.
        }
    }

    private void LogSlipResolution()
    {
        string text = "Slip source: " + slipResolver.Kind + " [" + string.Join(", ", slipResolver.ResolvedPaths) + "]";
        if (slipResolver.Kind == SlipSourceKind.RFactorRotation)
        {
            text += FormattableString.Invariant(
                $" radii=[{slipResolver.GetTireRadius(0):F3},{slipResolver.GetTireRadius(1):F3},{slipResolver.GetTireRadius(2):F3},{slipResolver.GetTireRadius(3):F3}]");
        }

        text += slipResolver.HasWheelLock ? ", ShakeIT WheelLock available" : ", no ShakeIT WheelLock";
        log.Info(text);
    }

    // =====================================================================================================
    // ISlipLockHost (UI thread)
    // =====================================================================================================

    /// <inheritdoc />
    public void CopySnapshot(LiveSnapshot target)
    {
        if (target == null)
        {
            return;
        }

        lock (snapshotLock)
        {
            snapshot.CopyTo(target);
        }
    }

    /// <inheritdoc />
    public void NotifySettingsChanged() => Interlocked.Exchange(ref settingsChangedFlag, 1);

    /// <inheritdoc />
    public void EditSettings(Action<PluginSettings> edit)
    {
        if (edit == null)
        {
            return;
        }

        commands.Enqueue(() =>
        {
            edit(Settings);
            scheduler.MarkSettingsDirty(clock.Elapsed.TotalSeconds);
        });
    }

    /// <inheritdoc />
    public void SetSensitivity(SensitivityKind kind, double percent) =>
        EnqueueProfileEdit(car => car.SetSensitivity(kind, percent));

    /// <inheritdoc />
    public void SetOverride(BalanceOverrideKind kind, double? value) =>
        EnqueueProfileEdit(car => car.Overrides.Set(kind, value));

    /// <inheritdoc />
    public void SetClassPresetOverride(BalanceClassPreset? classPreset) =>
        EnqueueProfileEdit(car => car.Overrides.ClassPreset = classPreset);

    /// <inheritdoc />
    public void SetLearningLocked(bool locked) =>
        EnqueueProfileEdit(car => car.LearningLocked = locked);

    /// <inheritdoc />
    public void ResetLearning() =>
        EnqueueProfileEdit(car =>
        {
            estimator.ResetLearning();

            // A wrongly verified steering sign would keep the relearned model broken: verify it again too.
            estimator.ResetCalibration();
            log.Info("Learned vehicle model cleared for " + car.SimKey + "/" + car.CarKey);
        });

    /// <inheritdoc />
    public void SetRecording(bool enabled)
    {
        // The recorder is thread-safe; Start does no file IO on the caller's thread.
        if (enabled)
        {
            CarIdentity car = identity;
            string sim = car.HasCar ? car.SimKey : lastGame;
            recorder.Start(Path.Combine(pluginDataRoot, RecordingsFolder), sim, car.CarKey);
        }
        else
        {
            recorder.Stop();
        }
    }

    /// <inheritdoc />
    public void RequestRetest() =>
        commands.Enqueue(() =>
        {
            detector.RequestRetest();
            slipResolver.Reset(); // lets a ShakeIT profile activated later replace the rF2 rotation source
            balanceSource.Reset();
            estimator.Reset();
            estimator.ResetCalibration(); // the sim's sign conventions are re-verified as well
            log.Info("Retest requested for " + lastGame);
        });

    /// <inheritdoc />
    public string GenerateShakeItDataExportProfile() =>
        Report(ShakeItProfileGenerator.WriteDataExportProfile(ShakeItProfileGenerator.DefaultDirectory));

    /// <inheritdoc />
    public string GenerateHapticPedalProfile() =>
        Report(ShakeItProfileGenerator.WriteHapticPedalProfile(ShakeItProfileGenerator.DefaultDirectory));

    /// <inheritdoc />
    public string GenerateBalanceProfile() =>
        Report(ShakeItProfileGenerator.WriteBalanceProfile(ShakeItProfileGenerator.DefaultDirectory));

    /// <inheritdoc />
    public string ExportCarProfile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return ShakeItProfileGenerator.ErrorMessagePrefix + "no file selected.";
        }

        // Snapshot the profile (with the current learned state) on the data thread, write it on this thread.
        CarProfile copy = null;
        string failure = RunOnDataThread(() =>
        {
            CarProfile car = profile;
            if (car == null)
            {
                return "no car loaded.";
            }

            estimator.SaveTo(car);
            copy = JsonFile.Deserialize<CarProfile>(JsonFile.Serialize(car), out _);
            return null;
        });
        if (failure != null)
        {
            return ShakeItProfileGenerator.ErrorMessagePrefix + failure;
        }

        return store.Export(copy, filePath, out string error)
            ? "Exported to " + filePath
            : ShakeItProfileGenerator.ErrorMessagePrefix + error;
    }

    /// <inheritdoc />
    public string ImportCarProfile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return ShakeItProfileGenerator.ErrorMessagePrefix + "no file selected.";
        }

        CarIdentity car = identity;
        if (!car.HasCar)
        {
            return ShakeItProfileGenerator.ErrorMessagePrefix + "no car loaded.";
        }

        // Read and validate the file on this thread; apply on the data thread and report what really happened.
        CarProfile imported = store.Import(filePath, out string error);
        if (imported == null)
        {
            return ShakeItProfileGenerator.ErrorMessagePrefix + error;
        }

        string failure = RunOnDataThread(() => ApplyImportedProfile(imported, car));
        return failure != null
            ? ShakeItProfileGenerator.ErrorMessagePrefix + failure
            : "Imported " + Path.GetFileName(filePath) + " into " + car.DisplayName + ".";
    }

    /// <inheritdoc />
    public string DumpPropertyNames()
    {
        string header;
        lock (snapshotLock)
        {
            header = "Game: " + snapshot.GameName + "\r\nCar: " + snapshot.CarKey + " (id " + snapshot.CarId + ")"
                + "\r\nSlip source: " + snapshot.SlipSource + "\r\nBalance source: " + snapshot.BalanceSourceName;
        }

        string path = PropertyDump.Write(PluginManager, reader, pluginDataRoot, header);
        log.Info("Property dump written: " + path);
        return "Property names written to " + path;
    }

    /// <summary>Queues an edit of the current car profile (ignored without a car) and schedules its save.</summary>
    private void EnqueueProfileEdit(Action<CarProfile> edit)
    {
        commands.Enqueue(() =>
        {
            CarProfile car = profile;
            if (car == null)
            {
                return;
            }

            edit(car);
            scheduler.MarkProfileEdited(clock.Elapsed.TotalSeconds);
        });
    }

    /// <summary>
    /// Replaces the current car's profile with an imported one, keeping the current car's identity. Returns null on
    /// success, else why nothing was applied (the car changed or unloaded since the user chose the file).
    /// </summary>
    private string ApplyImportedProfile(CarProfile imported, CarIdentity target)
    {
        CarProfile current = profile;
        if (current == null)
        {
            return "no car loaded.";
        }

        if (!string.Equals(current.SimKey, target.SimKey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.CarKey, target.CarKey, StringComparison.OrdinalIgnoreCase))
        {
            return "the car changed before the import could be applied; nothing was imported.";
        }

        imported.SimKey = current.SimKey;
        imported.CarKey = current.CarKey;
        imported.DisplayName = current.DisplayName;
        imported.CarClass = current.CarClass;
        scheduler.DiscardProfileChanges();
        profile = imported;
        estimator.LoadCar(imported, Settings.GetCalibration(imported.SimKey), identity.CarClass);
        if (IsIdentityProvisional(clock.Elapsed.TotalSeconds))
        {
            scheduler.MarkProfileEdited(identityRetryUntil);
        }
        else
        {
            store.SaveAsync(imported);
        }

        carProfileVersion++;
        log.Info("Car profile imported for " + imported.SimKey + "/" + imported.CarKey);
        return null;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the data thread and waits for it (UI requests that need data-thread-owned
    /// state). Returns the action's result, or an error text when the data thread does not respond in time.
    /// </summary>
    private string RunOnDataThread(Func<string> action)
    {
        if (Thread.CurrentThread.ManagedThreadId == dataThreadId)
        {
            return action();
        }

        var done = new ManualResetEventSlim(false);
        string result = null;
        commands.Enqueue(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                result = ex.Message;
            }
            finally
            {
                done.Set();
            }
        });

        if (!done.Wait(DataThreadTimeoutMs))
        {
            // Not disposed: the queued command may still run later and signal it.
            return "SimHub is not processing data right now, try again.";
        }

        done.Dispose();
        return result;
    }

    private string Report(ProfileWriteResult result)
    {
        if (result.Success)
        {
            log.Info("Profile written: " + result.Path);
        }
        else
        {
            log.Warn("Profile generation failed: " + result.Message);
        }

        return result.Message;
    }
}
