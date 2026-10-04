using System;
using System.Globalization;
using System.IO;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.Balance.Recording;
using DivebombLogistics.Haptics.Balance.Sources;
using DivebombLogistics.Haptics.Diagnostics;
using DivebombLogistics.Haptics.Profiles;
using DivebombLogistics.Haptics.Settings;
using DivebombLogistics.Haptics.SlipLock;
using DivebombLogistics.Haptics.Telemetry;
using DivebombLogistics.Haptics.UI;

namespace DivebombLogistics.Haptics;

/// <summary>
/// The haptics module (v1/v2 "Slip Lock Properties Calc"): the slip/lock pipeline (<c>DLP.SlipLock.*</c>) and the
/// understeer/oversteer estimator (<c>DLP.Balance.*</c>), with per-car profiles and global settings under
/// <c>PluginsData\DLP\Haptics\</c>. Everything here was extracted unchanged from the v2 plugin shell; the shell now
/// only provides frame context, car identity, sessions and fault isolation (<see cref="IDlpModule"/>).
/// <para>
/// Threading: the <see cref="IDlpModule"/> members run on SimHub's data thread, which owns every processing object,
/// the current car profile and the save scheduler. The Haptics tab (UI thread) talks to the module only through
/// <see cref="IHapticsHost"/>: it reads a <see cref="HapticsSnapshot"/> copied under a lock at up to 20 Hz, writes
/// bool/enum settings directly and posts everything else (numeric settings, per-car edits) to the module's
/// <see cref="IDataThreadDispatcher"/>, which the shell drains at the start of each frame.
/// </para>
/// <para>
/// Fault isolation: <see cref="Update"/> runs slip/lock, balance and the debug log in separate guarded stages; a
/// stage that throws has its outputs zeroed, so a persistent error can never freeze haptic effects at their last
/// value, and the later stages still run. Anything else that throws reaches the shell, which calls
/// <see cref="OnFault"/>.
/// </para>
/// <para>
/// Hot path: <see cref="Update"/> and <see cref="Tick"/> are allocation-free in steady state; allocations happen only
/// on game/car/session changes, in the rate-limited error path, and in the optional 1 Hz debug file log.
/// </para>
/// </summary>
internal sealed class HapticsModule : IDlpModule, IHapticsHost
{
    /// <summary>Module id: data folder <c>PluginsData\DLP\Haptics</c>.</summary>
    public const string ModuleId = "Haptics";

    private const string RecordingsFolder = "Recordings";
    private const string NotAvailable = "N/A";
    private const double KmhPerMs = 3.6;
    private const double PercentScale = 100.0;

    /// <summary>The UI snapshot is refreshed at most this often (20 Hz).</summary>
    private const double SnapshotIntervalSeconds = 0.05;

    /// <summary>The balance resolution text allocates, so it is refreshed at 1 Hz and only while the debug view is shown.</summary>
    private const double ResolutionIntervalSeconds = 1.0;

    /// <summary>How long a UI request waits for the data thread before giving up.</summary>
    private const int DataThreadTimeoutMs = 3000;

    /// <summary>End(): how long to wait for queued asynchronous writes.</summary>
    private static readonly TimeSpan ShutdownWriteTimeout = TimeSpan.FromSeconds(5);

    private static readonly BalanceOverrides NoOverrides = new BalanceOverrides();

    /// <summary>Diagnostics text per <see cref="CarKeySource"/> (indexed by the enum value).</summary>
    private static readonly string[] KeySourceTexts = { string.Empty, "native model", "car model", "car id" };

    // ---- Infrastructure (from the shell) ----
    private readonly object snapshotLock = new object();
    private readonly HapticsSnapshot snapshot = new HapticsSnapshot();
    private ModuleContext context;
    private ILog log;
    private ITelemetryReader reader;
    private FrameContext ctx;
    private ICarIdentityState carState;
    private IDataThreadDispatcher dispatcher;
    private ErrorReporter errors;
    private ShellDiagnostics diagnostics;
    private Func<double> clock;
    private DebugFileLog debugLog;
    private AsyncJsonWriter<HapticsSettings> settingsWriter;
    private string dataDirectory;
    private int settingsChangedFlag;

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

    /// <summary>End of the retry window that started with <see cref="identity"/> (captured with it; data thread only).</summary>
    private double identityProvisionalUntil = double.NegativeInfinity;

    /// <summary>SimHub's CarId when <see cref="identity"/> was taken (data thread only): tells a placeholder of the same vehicle from another car.</summary>
    private string identityCarId = string.Empty;
    private string carProfilePath = string.Empty;
    private string carKeySourceText = string.Empty;
    private int carProfileVersion;

    // ---- Per-frame bookkeeping (data thread) ----
    private string lastGame = string.Empty;
    private string lastMaxGCarId = NotAvailable;
    private double lastSnapshotTime = double.NegativeInfinity;

    /// <inheritdoc />
    public string Id => ModuleId;

    /// <inheritdoc />
    public string DisplayName => "Haptics";

    /// <summary>Global settings (live object, see <see cref="IHapticsHost.Settings"/>).</summary>
    public HapticsSettings Settings { get; private set; }

    /// <summary>Registered property names (relative), for the shell's start-up log and tests.</summary>
    internal int PropertyCount { get; private set; }

    // =====================================================================================================
    // IDlpModule lifecycle
    // =====================================================================================================

    /// <inheritdoc />
    public void Init(ModuleContext moduleContext)
    {
        context = moduleContext ?? throw new ArgumentNullException(nameof(moduleContext));
        log = context.Log ?? NullLog.Instance;
        reader = context.Reader ?? throw new ArgumentException("A telemetry reader is required.", nameof(moduleContext));
        ctx = context.Frame ?? throw new ArgumentException("A frame context is required.", nameof(moduleContext));
        carState = context.Car ?? throw new ArgumentException("A car identity state is required.", nameof(moduleContext));
        dispatcher = context.Dispatcher ?? throw new ArgumentException("A dispatcher is required.", nameof(moduleContext));
        errors = context.Errors ?? new ErrorReporter(log);
        diagnostics = context.Diagnostics ?? new ShellDiagnostics();
        clock = context.Clock ?? throw new ArgumentException("A clock is required.", nameof(moduleContext));
        dataDirectory = context.DataDirectory;

        HapticsSettingsStore.LoadResult loaded = HapticsSettingsStore.Load(dataDirectory, log);
        Settings = loaded.Settings;

        slipResolver = new SlipSourceResolver();
        capabilities = new CapabilityTracker(Settings);
        detector = new WheelSpeedModeDetector(Settings, slipResolver, capabilities);
        estimator = new BalanceEstimator(Settings.Balance, log);
        recorder = new BalanceRecorder(log);
        store = new CarProfileStore(dataDirectory, log);
        settingsWriter = new AsyncJsonWriter<HapticsSettings>(loaded.SavePath, log);
        scheduler = new SaveScheduler(SaveSettingsAsync, SaveCurrentProfile, log);
        debugLog = new DebugFileLog(Path.Combine(context.LogDirectory ?? string.Empty, DebugFileLog.FileName), log);

        var exporter = new HapticsPropertyExporter(context.Properties ?? throw new ArgumentException("A property registry is required.", nameof(moduleContext)));
        int before = context.Properties.Names.Count;
        exporter.RegisterSlipLock(outputs, maxG);
        exporter.RegisterBalance(estimator.Outputs);
        PropertyCount = context.Properties.Names.Count - before;

        log.Info("Initialized: " + PropertyCount.ToString(CultureInfo.InvariantCulture) + " properties, settings "
            + loaded.Status + ", data folder " + dataDirectory);
    }

    /// <inheritdoc />
    public void OnGameChanged(FrameContext frame)
    {
        // Per-game reset (v1 semantics) and creation of the game's balance adapter.
        lastGame = frame.GameName;
        slipResolver.Reset();
        capabilities.Reset(lastGame);
        detector.Reset(lastGame);
        preset = GamePresets.Get(lastGame, out presetName);

        balanceSource = VehicleStateSourceFactory.Create(lastGame, log);
        debugLog.RequestScan();
        log.Info("Game: " + lastGame + ", preset " + presetName + ", balance source " + balanceSource.Name
            + (balanceSource.IsSupported ? string.Empty : " (understeer/oversteer not supported)"));
    }

    /// <inheritdoc />
    public void OnCarChanged(CarIdentity car) => SwitchCar(car ?? CarIdentity.None);

    /// <inheritdoc />
    public void OnSessionChanged(FrameContext frame)
    {
        // A new SimHub session restarts the balance filters and session adaptation layer.
        estimator.Reset();
    }

    /// <inheritdoc />
    public void Update(FrameContext frame)
    {
        double now = frame.WallTime;
        UpdateMaxGCar(frame.CarId);

        try
        {
            ProcessSlipLock(now);
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
            outputs.Clear();
            processor.Reset();
        }

        try
        {
            ProcessBalance(now);
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
            ResetBalanceAfterError(now);
        }

        try
        {
            WriteDebugLog(now);
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
        }
    }

    /// <summary>
    /// First frame without a running game after it ran: zero every export (v1 left stale values) and reset the
    /// envelopes/filters so the next session starts clean.
    /// </summary>
    public void OnGameStopped()
    {
        outputs.Clear();
        processor.Reset();
        estimator.Reset();
    }

    /// <inheritdoc />
    public void Tick(double now, bool gameRunning)
    {
        if (Interlocked.Exchange(ref settingsChangedFlag, 0) != 0)
        {
            scheduler.MarkSettingsDirty(now);
        }

        try
        {
            scheduler.Tick(now);
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
        }

        try
        {
            UpdateSnapshot(now);
        }
        catch (Exception ex)
        {
            errors.Report(ex, now);
        }
    }

    /// <summary>Zeroes every export and resets the envelopes and the estimator (never throws).</summary>
    public void OnFault(Exception error)
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

    /// <summary>Saves the current car profile and the settings synchronously and stops the recorder.</summary>
    public void End()
    {
        recorder?.Dispose();
        if (profile != null && !IsIdentityProvisional(clock()))
        {
            estimator.SaveTo(profile);
            store.Save(profile); // synchronous; supersedes anything still queued for this car
        }

        scheduler?.DiscardProfileChanges();
        settingsWriter?.Save(Settings); // synchronous; supersedes a queued asynchronous save
        store?.WaitForPendingWrites(ShutdownWriteTimeout);
        settingsWriter?.WaitForPendingWrites(ShutdownWriteTimeout);
    }

    // =====================================================================================================
    // Per-frame processing (data thread)
    // =====================================================================================================

    /// <summary>v1 max-G reset rule, unchanged: the tracked maxima restart when SimHub's CarId changes.</summary>
    private void UpdateMaxGCar(string carId)
    {
        if (!string.Equals(carId, lastMaxGCarId, StringComparison.Ordinal) && !string.IsNullOrEmpty(carId) && carId != NotAvailable)
        {
            maxG.Reset();
            lastMaxGCarId = carId;
        }
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
            errors.Report(ex, now);
        }
    }

    /// <summary>Saves the current car's profile and loads (or creates) the profile of <paramref name="next"/>.</summary>
    private void SwitchCar(CarIdentity next)
    {
        CarIdentity previous = identity;
        CarProfile provisionalProfile = null;
        if (profile != null)
        {
            // Carry over only while the previous key is still a placeholder for this same vehicle (its own retry
            // window, judged before identity is replaced; SimHub's CarId unchanged). Any other change is flushed.
            bool provisional = IsIdentityProvisional(clock()) && next.KeySource == CarKeySource.NativeModel
                && string.Equals(previous.SimKey, next.SimKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ctx.CarId ?? string.Empty, identityCarId, StringComparison.Ordinal);
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
        identityProvisionalUntil = carState.ProvisionalUntil;
        identityCarId = ctx.CarId ?? string.Empty;
        carKeySourceText = KeySourceTexts[Math.Max(0, Math.Min(KeySourceTexts.Length - 1, (int)next.KeySource))];
        if (next.HasCar)
        {
            bool stored = store.ProfileExists(next.SimKey, next.CarKey);
            profile = store.Load(next.SimKey, next.CarKey, next.DisplayName, next.CarClass);
            if (provisionalProfile != null && !stored)
            {
                // First time this car is seen under its native key: keep what was set/learned under the placeholder.
                CarryOver(provisionalProfile, profile);
                scheduler.MarkProfileEdited(clock());
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
    /// Uses this module's identity and the retry window captured with it, so the previous car is judged by its own
    /// window while <see cref="SwitchCar"/> runs (the shell has already started the next car's window by then).
    /// </summary>
    private bool IsIdentityProvisional(double now) => identity.ShouldRetry && now <= identityProvisionalUntil;

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
    private void FillSnapshot(HapticsSnapshot s)
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
        s.CarKeyProvisional = IsIdentityProvisional(clock());

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
        for (int i = 0; i < Wheels.Count; i++)
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
        s.LastError = errors.LastError;
        s.FrameCount = diagnostics.FrameCount;
        s.DataUpdateMs = diagnostics.DataUpdateMs;
    }

    // =====================================================================================================
    // Persistence callbacks (data thread, via SaveScheduler)
    // =====================================================================================================

    /// <summary>Copies the settings on the data thread and writes them on the thread pool (no file IO in DataUpdate).</summary>
    private void SaveSettingsAsync() => settingsWriter.SaveAsync(Settings);

    /// <summary>
    /// Writes the learned state into the profile and queues the file write. The JSON serialization runs here on the
    /// data thread (so the thread pool never touches the live profile); a deliberate, rate-limited exception to the
    /// no-IO rule (at most once per minute while learning and 2 s after a user edit).
    /// </summary>
    private void SaveCurrentProfile()
    {
        CarProfile car = profile;
        if (car == null)
        {
            return;
        }

        if (IsIdentityProvisional(clock()))
        {
            // Placeholder key: save only once the retry window has passed and the key is final.
            scheduler.MarkProfileEdited(identityProvisionalUntil);
            return;
        }

        estimator.SaveTo(car);
        store.SaveAsync(car);
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
    // IHapticsHost (UI thread)
    // =====================================================================================================

    /// <inheritdoc />
    public void CopySnapshot(HapticsSnapshot target)
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
        target.LastError = errors.LastError;
    }

    /// <inheritdoc />
    public void NotifySettingsChanged() => Interlocked.Exchange(ref settingsChangedFlag, 1);

    /// <inheritdoc />
    public void EditSettings(Action<HapticsSettings> edit)
    {
        if (edit == null)
        {
            return;
        }

        dispatcher.Post(() =>
        {
            edit(Settings);
            scheduler.MarkSettingsDirty(clock());
        });
    }

    /// <inheritdoc />
    public void SetSensitivity(SensitivityKind kind, double percent) =>
        PostProfileEdit(car => car.SetSensitivity(kind, percent));

    /// <inheritdoc />
    public void SetOverride(BalanceOverrideKind kind, double? value) =>
        PostProfileEdit(car => car.Overrides.Set(kind, value));

    /// <inheritdoc />
    public void SetClassPresetOverride(BalanceClassPreset? classPreset) =>
        PostProfileEdit(car => car.Overrides.ClassPreset = classPreset);

    /// <inheritdoc />
    public void SetLearningLocked(bool locked) =>
        PostProfileEdit(car => car.LearningLocked = locked);

    /// <inheritdoc />
    public void ResetLearning() =>
        PostProfileEdit(car =>
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
            recorder.Start(Path.Combine(dataDirectory, RecordingsFolder), sim, car.CarKey);
        }
        else
        {
            recorder.Stop();
        }
    }

    /// <inheritdoc />
    public void RequestRetest() =>
        dispatcher.Post(() =>
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
        string failure = dispatcher.Run(
            () =>
            {
                CarProfile car = profile;
                if (car == null)
                {
                    return "no car loaded.";
                }

                estimator.SaveTo(car);
                copy = JsonFile.Deserialize<CarProfile>(JsonFile.Serialize(car), out _);
                return null;
            },
            DataThreadTimeoutMs);
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

        string failure = dispatcher.Run(() => ApplyImportedProfile(imported, car), DataThreadTimeoutMs);
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

        string path = PropertyDump.Write(context.ListPropertyNames?.Invoke(), reader, dataDirectory, header);
        log.Info("Property dump written: " + path);
        return "Property names written to " + path;
    }

    /// <summary>Posts an edit of the current car profile (ignored without a car) and schedules its save.</summary>
    private void PostProfileEdit(Action<CarProfile> edit)
    {
        dispatcher.Post(() =>
        {
            CarProfile car = profile;
            if (car == null)
            {
                return;
            }

            edit(car);
            scheduler.MarkProfileEdited(clock());
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
        if (IsIdentityProvisional(clock()))
        {
            scheduler.MarkProfileEdited(identityProvisionalUntil);
        }
        else
        {
            store.SaveAsync(imported);
        }

        carProfileVersion++;
        log.Info("Car profile imported for " + imported.SimKey + "/" + imported.CarKey);
        return null;
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
