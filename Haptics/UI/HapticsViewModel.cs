using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.Diagnostics;
using DivebombLogistics.Haptics.Settings;
using DivebombLogistics.Haptics.SlipLock;
using DivebombLogistics.Haptics.Telemetry;
using DivebombLogistics.Haptics.UI.ViewModels;
using DivebombLogistics.UI;
using DivebombLogistics.UI.ViewModels;
using Microsoft.Win32;

namespace DivebombLogistics.Haptics.UI;

/// <summary>
/// View model of the plugin settings page.
/// <para>
/// Simple view: status line, four per-car sensitivity sliders with live meters, profile setup buttons and the
/// "Show debug view" switch. Debug view (tabs Slip / Lock, Balance, Diagnostics): every v1 pipeline display, the
/// global tuning sliders, the balance estimator internals, per-car overrides and diagnostics.
/// </para>
/// <para>
/// Refresh model: while the page is visible (<see cref="Start"/>/<see cref="Stop"/>) a 100 ms
/// <see cref="DispatcherTimer"/> copies the host's <see cref="HapticsSnapshot"/> into a private instance and pushes
/// the values into observable properties/items, which raise change notifications only for values that changed.
/// Debug-only values are updated only for the visible debug tab (<see cref="DebugTab"/>). Per-car values (sensitivities, overrides, class
/// preset, learning lock) are re-read only when <see cref="HapticsSnapshot.CarProfileVersion"/> changes, so a slider
/// being dragged is never pulled back by a stale snapshot.
/// </para>
/// Threading: UI thread only. Numeric global settings are edited through <see cref="IHapticsHost.EditSettings"/>
/// (applied on the data thread); bool/enum settings are written to <see cref="IHapticsHost.Settings"/> followed by
/// <see cref="IHapticsHost.NotifySettingsChanged"/>; per-car state goes through the host methods (queued there).
/// </summary>
public sealed class HapticsViewModel : ObservableObject
{

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// After the user flips a host-applied toggle (learning lock, recording), the snapshot may still show the old
    /// state until the data thread processed the command; ignore the snapshot for this long to avoid flicker.
    /// </summary>
    private const int ToggleSyncHoldMs = 1000;

    /// <summary>Minimum interval between two SimHub log entries for UI refresh errors.</summary>
    private const int ErrorLogIntervalMs = 10000;

    /// <summary>
    /// Suffix of the "Testing (n/60)" detection status: the per-wheel speed detector settles on "Mono" after
    /// 60 dynamic frames without a wheel-speed difference (v1 behavior, implemented in WheelSpeedModeDetector).
    /// </summary>
    private const string DetectionFramesSuffix = "/60)";

    private const double FractionToPercent = 100.0;

    private const string ProfileFileFilter = "Car profile (*.json)|*.json|All files (*.*)|*.*";
    private const string ProfileFileExtension = ".json";

    // ---- Status texts (constants: no allocation per refresh) ----
    private const string NoGameText = "No game running";
    private const string NoCarText = "no car";
    private const string LockSynthText = "Lock: synth";
    private const string LockSynthShakeItText = "Lock: synth + ShakeIT";
    private const string LockFromSlipText = "Lock: from slip";
    private const string LockFromSlipShakeItText = "Lock: from slip + ShakeIT";
    private const string SourceLockSynthText = "synth";
    private const string SourceLockSynthShakeItText = "synth + ShakeIT";
    private const string SourceLockFromSlipText = "from slip";
    private const string SourceLockFromSlipShakeItText = "from slip + ShakeIT";
    private const string BalanceUnsupportedText = "Balance: not available for this sim";
    private const string HintNoGame = "Start a game to see live values.";
    private const string HintNoSlipData =
        "No slip data found: create the ShakeIT data export profile below, import it in ShakeIT Bass Shakers and restart SimHub.";
    private const string SensitivityNoCarText = "Load a car to adjust. Values are remembered per car.";
    private const string SensitivityIdentifyingText = "Identifying car...";

    // Situational notes of the sensitivity rows (shown instead of the help line).
    private const string LockAbsNote =
        "ABS is on: the brake-pedal effect (LockABS) follows ABS; this slider changes Lock / LockBlend only. The meter shows Lock.";
    private const string SlipTcNote =
        "TC is on: SlipTC follows TC; this slider changes Slip / SlipBlend only. The meter shows Slip.";
    private const string BalanceUnavailableNote = "Understeer/oversteer detection is not available for this sim.";

    private const string SteeringRatioNote =
        "Sets G manually: G = 1 / (steering ratio × wheelbase), with the wheelbase below or the class default.";
    private const string WheelbaseNote =
        "Alone only a starting value until G is learned; tick the steering ratio too (or override G) to force G.";
    private const string NoErrorText = "none";
    private const string NoResolutionText = "not resolved yet (no game running or no data)";

    private static readonly HapticsSettings SettingsDefaults = new HapticsSettings();
    private static readonly BalanceTuning TuningDefaults = new BalanceTuning();

    private readonly IHapticsHost _host;
    private readonly HapticsSnapshot _snapshot = new HapticsSnapshot();
    private DispatcherTimer _timer;

    // ---- Simple view state ----
    private bool _showSourceStatus;
    private bool _hasCar;
    private bool _canEditCar;
    private string _gameText = NoGameText;
    private string _carText = NoCarText;
    private string _slipStatusText = string.Empty;
    private string _lockStatusText = string.Empty;
    private string _balanceStatusText = string.Empty;
    private string _statusHint = HintNoGame;
    private string _sensitivityCarText = SensitivityNoCarText;
    private string _setupStatus = string.Empty;
    private bool _showDebugView;
    private DebugTab _selectedDebugTab = DebugTab.SlipLock;

    // Keys of the inputs the composed texts were last built from (rebuild only on change).
    private string _carTextName;
    private string _carTextClass;
    private bool _carTextHasCar;
    private bool _carTextProvisional;
    private int _slipStatusKey = -1;
    private bool _balanceStatusBuilt;
    private bool _balanceStatusUnsupported;
    private bool _balanceStatusActive;
    private BalanceGate _balanceStatusGate;
    private BalancePath _balanceStatusPath;
    private int _balanceStatusConfidence;

    // ---- Per-car sync ----
    private bool _carSynced;
    private int _carProfileVersion;
    private bool _learningLocked;
    private int _learningLockedHoldUntil;

    // ---- Slip / Lock tab ----
    private bool _isDetecting;
    private string _baseSlipModeText = string.Empty;
    private double _baseSlipMinimum;
    private string _slipTcTitle = string.Empty;
    private string _lockAbsTitle = string.Empty;
    private GamePreset _shownPreset;
    private string _shownPresetName;

    // ---- Balance tab ----
    private OptionItem<BalanceMode> _selectedBalanceMode;
    private OptionItem<BalanceClassPreset?> _selectedClassPreset;
    private BalanceClassPreset? _autoPresetShown;
    private int _tagMask = -1;
    private string _carProfileStatus = string.Empty;

    // ---- Diagnostics tab ----
    private bool _isRecording;
    private int _recordingHoldUntil;
    private string _recordingPath = string.Empty;
    private string _balanceResolution = NoResolutionText;
    private string _lastError = NoErrorText;
    private string _diagnosticsStatus = string.Empty;
    private int _uiErrorCount;
    private int _lastUiErrorLogTick;
    private bool _uiErrorLogged;

    // ---- Items (fixed instances; values updated in place) ----
    private readonly SensitivityItem _slipSensitivity;
    private readonly SensitivityItem _lockSensitivity;
    private readonly SensitivityItem _understeerSensitivity;
    private readonly SensitivityItem _oversteerSensitivity;

    private readonly InfoItem _srcGame = new InfoItem("Game");
    private readonly InfoItem _srcCarId = new InfoItem("Car id", "Car id as reported by the sim (can be livery specific).");
    private readonly InfoItem _srcPreset = new InfoItem("Preset", "Code-defined game preset in use (Haptics/SlipLock/GamePresets.cs).");
    private readonly InfoItem _srcSlipSource = new InfoItem("Slip source");
    private readonly InfoItem _srcPerWheel = new InfoItem("Per-wheel", "Whether the sim provides per-wheel wheel speeds.");
    private readonly InfoItem _srcShakeIt = new InfoItem("ShakeIT slip", "ShakeIT WheelSlip export (needs the data export profile).");
    private readonly InfoItem _srcShakeItLock = new InfoItem("ShakeIT lock", "ShakeIT WheelLock export (merged into Lock when enabled).");
    private readonly InfoItem _srcLockSource = new InfoItem("Lock source");
    private readonly InfoItem _srcGameAbs = new InfoItem("Game ABS", "The sim exports an ABS level.");
    private readonly InfoItem _srcCarAbs = new InfoItem("Car ABS", "ABS was seen active in this car.");
    private readonly InfoItem _srcAbsOn = new InfoItem("ABS on");
    private readonly InfoItem _srcGameTc = new InfoItem("Game TC", "The sim exports a TC level.");
    private readonly InfoItem _srcCarTc = new InfoItem("Car TC", "TC was seen active in this car.");
    private readonly InfoItem _srcTcOn = new InfoItem("TC on");
    private readonly InfoItem _srcAggSlip = new InfoItem("Aggregate slip", "What SlipTC outputs: TC when the car's TC is on, else Slip.");
    private readonly InfoItem _srcAggLock = new InfoItem("Aggregate lock", "What LockABS outputs: ABS when the car's ABS is on, else Lock.");
    private readonly InfoItem _srcMaxSway = new InfoItem("Max sway", "Largest lateral acceleration seen with this car (normalizes corner loads).");
    private readonly InfoItem _srcMaxSurge = new InfoItem("Max surge", "Largest longitudinal acceleration seen with this car.");
    private readonly InfoItem _srcMaxDecel = new InfoItem("Max decel", "Largest deceleration seen with this car.");

    private readonly InfoItem _detSpeed = new InfoItem("Speed", "Vehicle speed in m/s; needs > 5 m/s (18 km/h).");
    private readonly InfoItem _detLateral = new InfoItem("Lateral", "Lateral acceleration; needs > 0.3 G (cornering), or braking.");
    private readonly InfoItem _detBrake = new InfoItem("Brake", "Needs > 10 %, or cornering.");
    private readonly InfoItem _detMaxDelta = new InfoItem("Max wheel delta");
    private readonly InfoItem _detStatus = new InfoItem("Status");

    private readonly LevelItem _baseSlipMono = new LevelItem("Base slip", 100, "0.0", "Raw slip before any processing (first wheel).");
    private readonly LevelItem _baseLockMono = new LevelItem("Base lock", 100, "0.0", "Raw slip used as lock (synthesized while braking).");
    private readonly LevelItem _baseAbsMono = new LevelItem("ABS active", 1, "0");
    private readonly LevelItem _baseTcMono = new LevelItem("TC active", 1, "0");

    private readonly InfoItem _presetName = new InfoItem("Preset");
    private readonly InfoItem _presetSlipLoad = new InfoItem("Slip load lat / long (%)", "Corner-load influence on the slip channel.");
    private readonly InfoItem _presetLockLoad = new InfoItem("Lock load lat / long (%)");
    private readonly InfoItem _presetAbsLoad = new InfoItem("ABS load lat / long (%)");
    private readonly InfoItem _presetTcLoad = new InfoItem("TC load lat / long (%)");
    private readonly InfoItem _presetSynthLock = new InfoItem("Synth lock from slip", "Unsigned sources: lock is derived from slip while braking.");
    private readonly InfoItem _presetSpeedFade = new InfoItem("Speed fade below (km/h)", "Slip/lock fade to zero below this speed (0 = off).");
    private readonly InfoItem _presetInverseLoad = new InfoItem("Inverse slip load", "Divide slip by corner load instead of multiplying.");
    private readonly InfoItem _presetPreGain = new InfoItem("Pre gain (%)");
    private readonly InfoItem _presetPreCut = new InfoItem("Pre cut (%)", "Noise floor removed before scaling.");

    private readonly LevelItem _balUndersteer = new LevelItem("Understeer output (0-1)", 1, "0.00",
        "Balance.Understeer after shaping, 0..1 like the exported property (the simple view shows it as 0-100).");
    private readonly LevelItem _balOversteer = new LevelItem("Oversteer output (0-1)", 1, "0.00",
        "Balance.Oversteer after shaping, 0..1 like the exported property (the simple view shows it as 0-100).");
    private readonly LevelItem _balConfidence = new LevelItem("Confidence", 1, "0.00", "Confidence in the vehicle model / direct path in use.");
    private readonly InfoItem _balGate = new InfoItem("State", "Why the outputs are (not) active.");
    private readonly InfoItem _balPath = new InfoItem("Path", "model = yaw rate vs. steering; direct = tyre slip angles.");
    private readonly InfoItem _balActive = new InfoItem("Active");
    private readonly InfoItem _balTags = new InfoItem("Tags", "Context tags exported as Balance.* booleans.");
    private readonly InfoItem _balClassPreset = new InfoItem("Class preset in use");

    private readonly LevelItem _detUsModel = new LevelItem("US model", 1, "0.00", "Understeer from yaw ratio (1 - r / r_ref).");
    private readonly LevelItem _detUsDirect = new LevelItem("US direct", 1, "0.00", "Understeer from front vs. rear slip angle.");
    private readonly LevelItem _detOsModel = new LevelItem("OS model", 1, "0.00", "Max of yaw excess, countersteer and body slip.");
    private readonly LevelItem _detOsYaw = new LevelItem("OS yaw excess", 1, "0.00", "Yaw rate above the reference (r / r_ref - 1).");
    private readonly LevelItem _detOsCountersteer = new LevelItem("OS countersteer", 1, "0.00", "Steering against the yaw direction.");
    private readonly LevelItem _detOsBodySlip = new LevelItem("OS body slip", 1, "0.00", "Body slip angle beyond the learned envelope.");
    private readonly LevelItem _detOsDirect = new LevelItem("OS direct", 1, "0.00", "Oversteer from rear vs. front slip angle.");
    private readonly LevelItem _detSpeedRamp = new LevelItem("Speed ramp", 1, "0.00", "Output weight from vehicle speed (0 below V min, 1 above V full).");

    private readonly InfoItem _sigYawRate = new InfoItem("Yaw rate r (rad/s)");
    private readonly InfoItem _sigYawRef = new InfoItem("Reference r_ref (rad/s)", "Yaw rate the vehicle model expects for the current steering and speed.");
    private readonly InfoItem _sigYawRatio = new InfoItem("Yaw ratio ρ", "r / r_ref: below 1 = understeer, above 1 = oversteer.");
    private readonly InfoItem _sigBodySlip = new InfoItem("Body slip β (°)");
    private readonly InfoItem _sigSteering = new InfoItem("Steering (°)", "Steering-wheel angle.");
    private readonly InfoItem _sigSteeringEff = new InfoItem("Steering − offset (°)", "Steering-wheel angle minus the learned offset θ0.");
    private readonly InfoItem _sigAlphaFront = new InfoItem("Slip angle front", "Degrees when the sim reports radians, else the sim's unit.");
    private readonly InfoItem _sigAlphaRear = new InfoItem("Slip angle rear");

    private readonly ParameterItem _paramG = new ParameterItem("G (1/m)", "0.0000", "Yaw gain: r = G·v·θ / (1 + K·v²).");
    private readonly ParameterItem _paramK = new ParameterItem("K (s²/m²)", "0.00000", "Understeer factor of the vehicle model.");
    private readonly ParameterItem _paramTheta0 = new ParameterItem("θ0 (°)", "0.00", "Steering offset (steering-wheel degrees).");
    private readonly ParameterItem _paramTau = new ParameterItem("τ yaw (s)", "0.000", "Lag between steering input and yaw response.");
    private readonly ParameterItem _paramAyMax = new ParameterItem("ay max (m/s²)", "0.0", "Learned lateral grip limit (98th percentile).");
    private readonly ParameterItem _paramAlphaPeak = new ParameterItem("α peak", "0.000", "Learned peak slip angle (direct path).");

    private readonly InfoItem _learnActive = new InfoItem("Learning now", "This frame fed the learner.");
    private readonly InfoItem _learnLocked = new InfoItem("Baseline locked");
    private readonly InfoItem _learnSession = new InfoItem("Session override", "This session's behaviour differs from the stored baseline (e.g. setup change).");
    private readonly InfoItem _learnSteeringSign = new InfoItem("Steering sign",
        "Runtime-verified steering direction correction (per sim). Re-verified after Reset learned model or Retest.");
    private readonly InfoItem _learnForwardSign = new InfoItem("Forward sign", "Runtime-verified longitudinal velocity correction.");
    private readonly InfoItem _learnGravity = new InfoItem("Gravity in vertical accel.", "Whether the sim's vertical acceleration includes gravity.");
    private readonly InfoItem _learnAutoPreset = new InfoItem("Auto class preset", "Class preset detected from the car class.");

    private readonly InfoItem _diagSimKey = new InfoItem("Sim key");
    private readonly InfoItem _diagCarKey = new InfoItem("Car key", "Key the per-car profile is stored under.");
    private readonly InfoItem _diagCarKeySource = new InfoItem("Car key source",
        "native model = livery independent; car model / car id = fallback (livery specific on LMU).");
    private readonly InfoItem _diagCarId = new InfoItem("Car id (sim)");
    private readonly InfoItem _diagCarName = new InfoItem("Car name");
    private readonly InfoItem _diagCarClass = new InfoItem("Car class");
    private readonly InfoItem _diagProfilePath = new InfoItem("Profile file");
    private readonly InfoItem[] _diagSlipPaths = new InfoItem[Wheels.Count];
    private readonly InfoItem[] _diagLockPaths = new InfoItem[Wheels.Count];
    private readonly InfoItem _diagBalanceSource = new InfoItem("Balance source");
    private readonly InfoItem _diagBalanceSupported = new InfoItem("Balance supported");
    private readonly InfoItem _diagUpdateMs = new InfoItem("DataUpdate (ms)", "Average processing time per frame.");
    private readonly InfoItem _diagFrames = new InfoItem("Frames");
    private readonly InfoItem _diagUiErrors = new InfoItem("UI refresh errors");

    private readonly OptionItem<BalanceClassPreset?> _autoPresetOption;
    private readonly OverrideItem[] _overrides;

    // ---- Commands that depend on having a car ----
    private readonly RelayCommand _resetLearningCommand;
    private readonly RelayCommand _exportCarProfileCommand;
    private readonly RelayCommand _importCarProfileCommand;

    /// <summary>Design-time constructor (sample data, no plugin).</summary>
    public HapticsViewModel()
        : this(new DesignTimeHost())
    {
    }

    /// <summary>Creates the view model for the running plugin.</summary>
    public HapticsViewModel(IHapticsHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        _slipSensitivity = Sensitivity(SensitivityKind.Slip, "Slip sensitivity (%)",
            "Raise if wheelspin feels too weak, lower if it buzzes all the time.",
            "Live SlipTC output (mono).");
        _lockSensitivity = Sensitivity(SensitivityKind.Lock, "Lock sensitivity (%)",
            "Raise for cars with weak lock feedback, e.g. LMP3.",
            "Live LockABS output (mono).");
        _understeerSensitivity = Sensitivity(SensitivityKind.Understeer, "Understeer sensitivity (%)",
            "Raise to feel the front washing out earlier.",
            "Live Balance.Understeer output.");
        _oversteerSensitivity = Sensitivity(SensitivityKind.Oversteer, "Oversteer sensitivity (%)",
            "Raise to feel the rear stepping out earlier.",
            "Live Balance.Oversteer output.");
        Sensitivities = new[] { _slipSensitivity, _lockSensitivity, _understeerSensitivity, _oversteerSensitivity };

        GenerateDataExportProfileCommand = new RelayCommand(() => SetupStatus = RunHostAction(_host.GenerateShakeItDataExportProfile));
        GenerateHapticPedalProfileCommand = new RelayCommand(() => SetupStatus = RunHostAction(_host.GenerateHapticPedalProfile));
        GenerateBalanceProfileCommand = new RelayCommand(() => SetupStatus = RunHostAction(_host.GenerateBalanceProfile));
        RetestCommand = new RelayCommand(_host.RequestRetest);
        ResetSlipLockTuningCommand = new RelayCommand(ResetSlipLockTuning);
        ResetBalanceTuningCommand = new RelayCommand(ResetBalanceTuning);
        _resetLearningCommand = new RelayCommand(ResetLearning, () => _hasCar);
        _exportCarProfileCommand = new RelayCommand(ExportCarProfile, () => _hasCar);
        _importCarProfileCommand = new RelayCommand(ImportCarProfile, () => _hasCar);
        DumpPropertyNamesCommand = new RelayCommand(() => DiagnosticsStatus = RunHostAction(_host.DumpPropertyNames));

        // One list per group, so a group never shares a row with the next one whatever the window width.
        SourceIdentityInfo = new[] { _srcGame, _srcCarId, _srcPreset };
        SourceSlipInfo = new[] { _srcSlipSource, _srcPerWheel, _srcShakeIt, _srcShakeItLock, _srcLockSource };
        SourceAbsInfo = new[] { _srcGameAbs, _srcCarAbs, _srcAbsOn };
        SourceTcInfo = new[] { _srcGameTc, _srcCarTc, _srcTcOn };
        SourceAggregateInfo = new[] { _srcAggSlip, _srcAggLock };
        SourceMaxGInfo = new[] { _srcMaxSway, _srcMaxSurge, _srcMaxDecel };
        DetectionConditions = new[] { _detSpeed, _detLateral, _detBrake };
        DetectionResult = new[] { _detMaxDelta, _detStatus };
        BaseMonoLevels = new[] { _baseSlipMono, _baseLockMono, _baseAbsMono, _baseTcMono };
        PresetInfo = new[]
        {
            _presetName, _presetSlipLoad, _presetLockLoad, _presetAbsLoad, _presetTcLoad, _presetSynthLock,
            _presetSpeedFade, _presetInverseLoad, _presetPreGain, _presetPreCut,
        };
        EnvelopeSliders = CreateEnvelopeSliders();
        BlendSliders = CreateBlendSliders();
        ThresholdSliders = CreateThresholdSliders();

        BalanceOutputLevels = new[] { _balUndersteer, _balOversteer, _balConfidence };
        BalanceStateInfo = new[] { _balGate, _balPath, _balActive, _balTags, _balClassPreset };
        DetectorLevels = new[] { _detUsModel, _detUsDirect, _detOsModel, _detOsYaw, _detOsCountersteer, _detOsBodySlip, _detOsDirect, _detSpeedRamp };
        SignalInfo = new[] { _sigYawRate, _sigYawRef, _sigYawRatio, _sigBodySlip, _sigSteering, _sigSteeringEff, _sigAlphaFront, _sigAlphaRear };
        Parameters = new[] { _paramG, _paramK, _paramTheta0, _paramTau, _paramAyMax, _paramAlphaPeak };
        LearningInfo = new[] { _learnActive, _learnLocked, _learnSession, _learnSteeringSign, _learnForwardSign, _learnGravity, _learnAutoPreset };
        BalanceTuningGroups = CreateBalanceTuningGroups();

        BalanceModes = new[]
        {
            new OptionItem<BalanceMode>(BalanceMode.Auto, DisplayText.Mode(BalanceMode.Auto)),
            new OptionItem<BalanceMode>(BalanceMode.ModelOnly, DisplayText.Mode(BalanceMode.ModelOnly)),
            new OptionItem<BalanceMode>(BalanceMode.DirectOnly, DisplayText.Mode(BalanceMode.DirectOnly)),
        };
        _selectedBalanceMode = FindMode(_host.Settings.Balance.Mode);

        _autoPresetOption = new OptionItem<BalanceClassPreset?>(null, AutoPresetText(BalanceClassPreset.None));
        ClassPresets = new[]
        {
            _autoPresetOption,
            PresetOption(BalanceClassPreset.None),
            PresetOption(BalanceClassPreset.FormulaPrototype),
            PresetOption(BalanceClassPreset.GT),
            PresetOption(BalanceClassPreset.RoadTouring),
            PresetOption(BalanceClassPreset.RallyLoose),
            PresetOption(BalanceClassPreset.Oval),
        };
        _selectedClassPreset = _autoPresetOption;

        _overrides = CreateOverrides();
        Overrides = _overrides;

        for (int i = 0; i < Wheels.Count; i++)
        {
            _diagSlipPaths[i] = new InfoItem("Slip path " + Wheels.ShortNames[i]);
            _diagLockPaths[i] = new InfoItem("Lock path " + Wheels.ShortNames[i]);
        }

        var diagnostics = new List<InfoItem>
        {
            _diagSimKey, _diagCarKey, _diagCarKeySource, _diagCarId, _diagCarName, _diagCarClass, _diagProfilePath,
        };
        diagnostics.AddRange(_diagSlipPaths);
        diagnostics.AddRange(_diagLockPaths);
        diagnostics.Add(_diagBalanceSource);
        diagnostics.Add(_diagBalanceSupported);
        diagnostics.Add(_diagUpdateMs);
        diagnostics.Add(_diagFrames);
        diagnostics.Add(_diagUiErrors);
        DiagnosticsInfo = diagnostics;
        _diagUiErrors.SetNumber(0, "0");

        _showDebugView = _host.Settings.ShowDebugView;
    }

    // =====================================================================================================
    // Lifecycle
    // =====================================================================================================

    /// <summary>Starts the 10 Hz refresh (call when the page becomes visible). Idempotent.</summary>
    public void Start()
    {
        if (_timer == null)
        {
            // Background priority: live values never delay input handling or rendering.
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
            _timer.Tick += OnTimerTick;
        }

        if (!_timer.IsEnabled)
        {
            Refresh();
            _timer.Start();
        }
    }

    /// <summary>Stops the refresh (call when the page is hidden/unloaded) so no timer keeps running. Idempotent.</summary>
    public void Stop() => _timer?.Stop();

    // =====================================================================================================
    // Simple view
    // =====================================================================================================

    /// <summary>A game is running: the slip / lock / balance status line is shown.</summary>
    public bool ShowSourceStatus
    {
        get => _showSourceStatus;
        private set => SetProperty(ref _showSourceStatus, value);
    }

    /// <summary>
    /// The per-car sensitivities can be edited: a car is loaded and its key is final (while LMU still identifies
    /// the car the profile may switch, and edits would land in the placeholder profile).
    /// </summary>
    public bool CanEditCar
    {
        get => _canEditCar;
        private set => SetProperty(ref _canEditCar, value);
    }

    /// <summary>A car profile is loaded (per-car controls are enabled).</summary>
    public bool HasCar
    {
        get => _hasCar;
        private set
        {
            if (SetProperty(ref _hasCar, value))
            {
                _resetLearningCommand.RaiseCanExecuteChanged();
                _exportCarProfileCommand.RaiseCanExecuteChanged();
                _importCarProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Game name or "No game running".</summary>
    public string GameText
    {
        get => _gameText;
        private set => SetProperty(ref _gameText, value);
    }

    /// <summary>"Car name (class)" or "no car".</summary>
    public string CarText
    {
        get => _carText;
        private set => SetProperty(ref _carText, value);
    }

    /// <summary>"Slip: &lt;source&gt;".</summary>
    public string SlipStatusText
    {
        get => _slipStatusText;
        private set => SetProperty(ref _slipStatusText, value);
    }

    /// <summary>"Lock: synth", "Lock: from slip", each optionally "+ ShakeIT".</summary>
    public string LockStatusText
    {
        get => _lockStatusText;
        private set => SetProperty(ref _lockStatusText, value);
    }

    /// <summary>"Balance: active (model, confidence 45 %)" or "Balance: &lt;reason&gt;".</summary>
    public string BalanceStatusText
    {
        get => _balanceStatusText;
        private set => SetProperty(ref _balanceStatusText, value);
    }

    /// <summary>What the user should do next (empty when everything is fine).</summary>
    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    /// <summary>"Saved for &lt;car&gt;" or a hint to load a car.</summary>
    public string SensitivityCarText
    {
        get => _sensitivityCarText;
        private set => SetProperty(ref _sensitivityCarText, value);
    }

    /// <summary>Slip, Lock, Understeer, Oversteer (per car).</summary>
    public IReadOnlyList<SensitivityItem> Sensitivities { get; }

    public ICommand GenerateDataExportProfileCommand { get; }

    public ICommand GenerateHapticPedalProfileCommand { get; }

    public ICommand GenerateBalanceProfileCommand { get; }

    /// <summary>Result of the last profile generation.</summary>
    public string SetupStatus
    {
        get => _setupStatus;
        private set => SetProperty(ref _setupStatus, value);
    }

    /// <summary>Shows the debug view (persisted in <see cref="HapticsSettings.ShowDebugView"/>).</summary>
    public bool ShowDebugView
    {
        get => _showDebugView;
        set
        {
            if (!SetProperty(ref _showDebugView, value))
            {
                return;
            }

            _host.Settings.ShowDebugView = value;
            _host.NotifySettingsChanged();
            if (value)
            {
                // Fill the debug tab right away instead of showing stale values until the next tick.
                Refresh();
            }
        }
    }

    /// <summary>Selected debug tab (only this tab is refreshed).</summary>
    public DebugTab SelectedDebugTab
    {
        get => _selectedDebugTab;
        set
        {
            if (SetProperty(ref _selectedDebugTab, value))
            {
                Refresh();
            }
        }
    }

    // =====================================================================================================
    // Debug: Slip / Lock
    // =====================================================================================================

    // Data source and capability grid (v1 "DATA SOURCE + CAPABILITIES"), one list per group.

    /// <summary>Game, car id, preset (full-width rows: car ids are long).</summary>
    public IReadOnlyList<InfoItem> SourceIdentityInfo { get; }

    public IReadOnlyList<InfoItem> SourceSlipInfo { get; }

    public IReadOnlyList<InfoItem> SourceAbsInfo { get; }

    public IReadOnlyList<InfoItem> SourceTcInfo { get; }

    public IReadOnlyList<InfoItem> SourceAggregateInfo { get; }

    public IReadOnlyList<InfoItem> SourceMaxGInfo { get; }

    /// <summary>Per-wheel speed detection is running (shows <see cref="DetectionInfo"/>).</summary>
    public bool IsDetecting
    {
        get => _isDetecting;
        private set => SetProperty(ref _isDetecting, value);
    }

    /// <summary>Live detection conditions (v1 "Spd / Lat / Brk" line).</summary>
    public IReadOnlyList<InfoItem> DetectionConditions { get; }

    /// <summary>Largest wheel-speed difference and detection status.</summary>
    public IReadOnlyList<InfoItem> DetectionResult { get; }

    public ICommand RetestCommand { get; }

    /// <summary>Merge ShakeIT WheelLock into the lock channel (global setting).</summary>
    public bool UseShakeItWheelLock
    {
        get => _host.Settings.UseShakeItWheelLock;
        set => UpdateSetting(UseShakeItWheelLock, value, v => _host.Settings.UseShakeItWheelLock = v);
    }

    /// <summary>Zero Slip/TC while the throttle is released (global setting).</summary>
    public bool GateSlipOnThrottle
    {
        get => _host.Settings.GateSlipOnThrottle;
        set => UpdateSetting(GateSlipOnThrottle, value, v => _host.Settings.GateSlipOnThrottle = v);
    }

    /// <summary>Zero Lock/ABS while the brake is released (global setting).</summary>
    public bool GateLockOnBrake
    {
        get => _host.Settings.GateLockOnBrake;
        set => UpdateSetting(GateLockOnBrake, value, v => _host.Settings.GateLockOnBrake = v);
    }

    public string SlipTcTitle
    {
        get => _slipTcTitle;
        private set => SetProperty(ref _slipTcTitle, value);
    }

    public string LockAbsTitle
    {
        get => _lockAbsTitle;
        private set => SetProperty(ref _lockAbsTitle, value);
    }

    public WheelValues SlipTc { get; } = new WheelValues();

    public WheelValues LockAbs { get; } = new WheelValues();

    public WheelValues SlipBlend { get; } = new WheelValues();

    public WheelValues LockBlend { get; } = new WheelValues();

    public WheelValues Slip { get; } = new WheelValues();

    public WheelValues Lock { get; } = new WheelValues();

    public WheelValues Abs { get; } = new WheelValues();

    public WheelValues Tc { get; } = new WheelValues();

    /// <summary>Raw base slip per wheel (−100..100 when signed, else 0..100).</summary>
    public WheelValues BaseSlip { get; } = new WheelValues();

    /// <summary>Bar minimum of <see cref="BaseSlip"/>: −100 for signed sources (+ spin, − lock), 0 for unsigned ones.</summary>
    public double BaseSlipMinimum
    {
        get => _baseSlipMinimum;
        private set => SetProperty(ref _baseSlipMinimum, value);
    }

    /// <summary>Corner loads (proxyL, 0..50, neutral 25).</summary>
    public WheelValues Loads { get; } = new WheelValues();

    /// <summary>Base slip / lock / ABS / TC mono values (v1 "MONO" bars).</summary>
    public IReadOnlyList<LevelItem> BaseMonoLevels { get; }

    /// <summary>Explains what the raw base values are for the current source.</summary>
    public string BaseSlipModeText
    {
        get => _baseSlipModeText;
        private set => SetProperty(ref _baseSlipModeText, value);
    }

    public SliderGroup EnvelopeSliders { get; }

    public SliderGroup BlendSliders { get; }

    public SliderGroup ThresholdSliders { get; }

    /// <summary>Read-only values of the game preset in use.</summary>
    public IReadOnlyList<InfoItem> PresetInfo { get; }

    public ICommand ResetSlipLockTuningCommand { get; }

    // =====================================================================================================
    // Debug: Balance
    // =====================================================================================================

    public IReadOnlyList<LevelItem> BalanceOutputLevels { get; }

    public IReadOnlyList<InfoItem> BalanceStateInfo { get; }

    public IReadOnlyList<LevelItem> DetectorLevels { get; }

    public IReadOnlyList<InfoItem> SignalInfo { get; }

    public IReadOnlyList<ParameterItem> Parameters { get; }

    public IReadOnlyList<InfoItem> LearningInfo { get; }

    public IReadOnlyList<OptionItem<BalanceMode>> BalanceModes { get; }

    /// <summary>Estimation path selection (global setting).</summary>
    public OptionItem<BalanceMode> SelectedBalanceMode
    {
        get => _selectedBalanceMode;
        set
        {
            if (value == null || !SetProperty(ref _selectedBalanceMode, value))
            {
                return;
            }

            _host.Settings.Balance.Mode = value.Value;
            _host.NotifySettingsChanged();
        }
    }

    /// <summary>"Auto (&lt;detected&gt;)" followed by every class preset.</summary>
    public IReadOnlyList<OptionItem<BalanceClassPreset?>> ClassPresets { get; }

    /// <summary>Per-car class preset override (Auto = null).</summary>
    public OptionItem<BalanceClassPreset?> SelectedClassPreset
    {
        get => _selectedClassPreset;
        set
        {
            if (value != null && SetProperty(ref _selectedClassPreset, value))
            {
                _host.SetClassPresetOverride(value.Value);
            }
        }
    }

    /// <summary>Per-car manual vehicle-model overrides.</summary>
    public IReadOnlyList<OverrideItem> Overrides { get; }

    /// <summary>Freeze the learned baseline of the current car.</summary>
    public bool LearningLocked
    {
        get => _learningLocked;
        set
        {
            if (SetProperty(ref _learningLocked, value))
            {
                _learningLockedHoldUntil = unchecked(Environment.TickCount + ToggleSyncHoldMs);
                _host.SetLearningLocked(value);
            }
        }
    }

    public ICommand ResetLearningCommand => _resetLearningCommand;

    public ICommand ExportCarProfileCommand => _exportCarProfileCommand;

    public ICommand ImportCarProfileCommand => _importCarProfileCommand;

    /// <summary>Result of the last reset/export/import.</summary>
    public string CarProfileStatus
    {
        get => _carProfileStatus;
        private set => SetProperty(ref _carProfileStatus, value);
    }

    /// <summary>Global detector tuning (<see cref="HapticsSettings.Balance"/>).</summary>
    public IReadOnlyList<SliderGroup> BalanceTuningGroups { get; }

    public ICommand ResetBalanceTuningCommand { get; }

    // =====================================================================================================
    // Debug: Diagnostics
    // =====================================================================================================

    public IReadOnlyList<InfoItem> DiagnosticsInfo { get; }

    /// <summary>Resolved balance property paths (multi-line).</summary>
    public string BalanceResolution
    {
        get => _balanceResolution;
        private set => SetProperty(ref _balanceResolution, string.IsNullOrEmpty(value) ? NoResolutionText : value);
    }

    /// <summary>Last DataUpdate error reported by the plugin ("none" when there was none).</summary>
    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, string.IsNullOrEmpty(value) ? NoErrorText : value);
    }

    /// <summary>Write the 1 Hz diagnostic log (global setting).</summary>
    public bool DebugFileLog
    {
        get => _host.Settings.DebugFileLog;
        set => UpdateSetting(DebugFileLog, value, v => _host.Settings.DebugFileLog = v);
    }

    /// <summary>Record balance telemetry to CSV.</summary>
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                _recordingHoldUntil = unchecked(Environment.TickCount + ToggleSyncHoldMs);
                _host.SetRecording(value);
            }
        }
    }

    public string RecordingPath
    {
        get => _recordingPath;
        private set => SetProperty(ref _recordingPath, value ?? string.Empty);
    }

    public ICommand DumpPropertyNamesCommand { get; }

    /// <summary>Result of the last property dump.</summary>
    public string DiagnosticsStatus
    {
        get => _diagnosticsStatus;
        private set => SetProperty(ref _diagnosticsStatus, value);
    }

    // =====================================================================================================
    // Refresh
    // =====================================================================================================

    private void OnTimerTick(object sender, EventArgs e) => Refresh();

    /// <summary>Copies the latest snapshot and updates everything visible.</summary>
    internal void Refresh()
    {
        try
        {
            _host.CopySnapshot(_snapshot);
            HapticsSnapshot s = _snapshot;
            SyncCarProfile(s);
            UpdateStatus(s);
            UpdateSensitivityLevels(s);
            if (!_showDebugView)
            {
                return;
            }

            switch (_selectedDebugTab)
            {
                case DebugTab.SlipLock:
                    UpdateSlipLockTab(s);
                    break;
                case DebugTab.Balance:
                    UpdateBalanceTab(s);
                    break;
                case DebugTab.Diagnostics:
                    UpdateDiagnosticsTab(s);
                    break;
            }
        }
        catch (Exception ex)
        {
            // A display problem must never take down SimHub's UI thread; count it and log it rate-limited.
            ReportRefreshError(ex);
        }
    }

    /// <summary>Re-reads per-car values when the car or its profile instance changed.</summary>
    private void SyncCarProfile(HapticsSnapshot s)
    {
        bool carChanged = s.HasCar != _hasCar;
        HasCar = s.HasCar;
        CanEditCar = s.HasCar && !s.CarKeyProvisional;
        if (_carSynced && !carChanged && s.CarProfileVersion == _carProfileVersion)
        {
            return;
        }

        _carSynced = true;
        _carProfileVersion = s.CarProfileVersion;

        _slipSensitivity.Load(s.SlipSensitivity);
        _lockSensitivity.Load(s.LockSensitivity);
        _understeerSensitivity.Load(s.UndersteerSensitivity);
        _oversteerSensitivity.Load(s.OversteerSensitivity);

        for (int i = 0; i < _overrides.Length; i++)
        {
            _overrides[i].Load(s.Overrides.Get(_overrides[i].Kind));
        }

        // Assign the backing fields directly: values loaded from the profile must not be sent back to the host.
        SetProperty(ref _selectedClassPreset, FindClassPreset(s.ClassPresetOverride), nameof(SelectedClassPreset));
        SetProperty(ref _learningLocked, s.LearningLocked, nameof(LearningLocked));
    }

    private void UpdateStatus(HapticsSnapshot s)
    {
        ShowSourceStatus = s.GameRunning;
        GameText = s.GameRunning && !string.IsNullOrEmpty(s.GameName) ? s.GameName : NoGameText;

        // Snapshot strings are reassigned only when they change, so a reference check detects changes cheaply.
        if (s.HasCar != _carTextHasCar || s.CarKeyProvisional != _carTextProvisional
            || !ReferenceEquals(s.CarDisplayName, _carTextName) || !ReferenceEquals(s.CarClass, _carTextClass))
        {
            _carTextHasCar = s.HasCar;
            _carTextProvisional = s.CarKeyProvisional;
            _carTextName = s.CarDisplayName;
            _carTextClass = s.CarClass;
            string name = string.IsNullOrEmpty(s.CarDisplayName) ? s.CarKey : s.CarDisplayName;
            CarText = !s.HasCar ? NoCarText :
                string.IsNullOrEmpty(s.CarClass) ? name : string.Concat(name, " (", s.CarClass, ")");
            SensitivityCarText = !s.HasCar ? SensitivityNoCarText
                : s.CarKeyProvisional ? SensitivityIdentifyingText
                : "Saved for " + name;
        }

        bool detecting = s.Detection == DetectionState.Detecting;
        int slipKey = ((int)s.SlipSource * 2) + (detecting ? 1 : 0);
        if (slipKey != _slipStatusKey)
        {
            _slipStatusKey = slipKey;
            SlipStatusText = string.Concat("Slip: ", DisplayText.SlipSource(s.SlipSource), detecting ? " (detecting)" : string.Empty);
        }

        LockStatusText = s.LockSynthesized
            ? (s.LockUsesShakeIt ? LockSynthShakeItText : LockSynthText)
            : (s.LockUsesShakeIt ? LockFromSlipShakeItText : LockFromSlipText);
        UpdateBalanceStatus(s);

        if (!s.GameRunning)
        {
            StatusHint = HintNoGame;
        }
        else
        {
            // Any working slip source is fine (ShakeIT, ACC native, rF2/LMU wheel rotation, per-wheel speeds).
            StatusHint = s.SlipDataAvailable ? string.Empty : HintNoSlipData;
        }
    }

    private void UpdateBalanceStatus(HapticsSnapshot s)
    {
        BalanceOutputs b = s.Balance;
        bool unsupported = s.GameRunning && !s.BalanceSupported;
        int confidencePercent = (int)Math.Round(MathUtil.Clamp01(MathUtil.FiniteOr(b.Confidence, 0.0)) * FractionToPercent);

        // Rebuild (allocate) the text only when one of its inputs changed.
        if (_balanceStatusBuilt && unsupported == _balanceStatusUnsupported && b.Active == _balanceStatusActive &&
            b.Gate == _balanceStatusGate && b.Path == _balanceStatusPath && confidencePercent == _balanceStatusConfidence)
        {
            return;
        }

        _balanceStatusBuilt = true;
        _balanceStatusUnsupported = unsupported;
        _balanceStatusActive = b.Active;
        _balanceStatusGate = b.Gate;
        _balanceStatusPath = b.Path;
        _balanceStatusConfidence = confidencePercent;
        if (unsupported)
        {
            BalanceStatusText = BalanceUnsupportedText;
        }
        else if (b.Active)
        {
            BalanceStatusText = string.Concat(
                "Balance: active (", DisplayText.Path(b.Path), ", confidence ",
                confidencePercent.ToString(DisplayText.Culture), " %)");
        }
        else
        {
            BalanceStatusText = "Balance: " + DisplayText.Gate(b.Gate);
        }
    }

    private void UpdateSensitivityLevels(HapticsSnapshot s)
    {
        // While the car's TC/ABS is on, SlipTC/LockABS are the TC/ABS channels, which the sensitivities do not scale:
        // meter the channel the slider does change and say so.
        bool tcOn = s.GameRunning && s.AggregateUsesTc;
        bool absOn = s.GameRunning && s.AggregateUsesAbs;
        _slipSensitivity.SetLevel(tcOn ? s.SlipMono : s.SlipTcMono);
        _slipSensitivity.SetState(true, tcOn ? SlipTcNote : string.Empty);
        _lockSensitivity.SetLevel(absOn ? s.LockMono : s.LockAbsMono);
        _lockSensitivity.SetState(true, absOn ? LockAbsNote : string.Empty);

        bool balanceAvailable = !s.GameRunning || s.BalanceSupported;
        string balanceNote = balanceAvailable ? string.Empty : BalanceUnavailableNote;
        _understeerSensitivity.SetLevel(s.Balance.Understeer * FractionToPercent);
        _understeerSensitivity.SetState(balanceAvailable, balanceNote);
        _oversteerSensitivity.SetLevel(s.Balance.Oversteer * FractionToPercent);
        _oversteerSensitivity.SetState(balanceAvailable, balanceNote);
    }

    private void UpdateSlipLockTab(HapticsSnapshot s)
    {
        // ---- Data source & capabilities (v1 texts) ----
        _srcGame.SetText(s.GameRunning ? s.GameName : "No game");
        _srcCarId.SetText(s.CarId);
        _srcPreset.SetText(s.PresetName);
        _srcSlipSource.SetText(LegacySlipSourceText(s));
        _srcPerWheel.SetText(PerWheelText(s));
        _srcShakeIt.SetText(ShakeItSlipText(s));
        _srcShakeItLock.SetText(s.ShakeItLockAvailable ? "Available" : "Not found");
        _srcLockSource.SetText(s.LockSynthesized
            ? (s.LockUsesShakeIt ? SourceLockSynthShakeItText : SourceLockSynthText)
            : (s.LockUsesShakeIt ? SourceLockFromSlipShakeItText : SourceLockFromSlipText));
        _srcGameAbs.SetText(s.GameExportsAbs ? "Yes" : "No");
        _srcCarAbs.SetText(DisplayText.CarCapability(s.CarHasAbs));
        SetAssistState(_srcAbsOn, s.GameExportsAbs, s.AbsLevel);
        _srcGameTc.SetText(s.GameExportsTc ? "Yes" : "No");
        _srcCarTc.SetText(DisplayText.CarCapability(s.CarHasTc));
        SetAssistState(_srcTcOn, s.GameExportsTc, s.TcLevel);
        if (s.AggregateUsesTc)
        {
            _srcAggSlip.SetFormatted("TC (lvl ", s.TcLevel, "0", ")");
        }
        else
        {
            _srcAggSlip.SetText(s.GameExportsTc ? "Slip (TC off)" : "Slip (no TC)");
        }

        if (s.AggregateUsesAbs)
        {
            _srcAggLock.SetFormatted("ABS (lvl ", s.AbsLevel, "0", ")");
        }
        else
        {
            _srcAggLock.SetText(s.GameExportsAbs ? "Lock (ABS off)" : "Lock (no ABS)");
        }

        _srcMaxSway.SetNumber(s.MaxSway, "0.0");
        _srcMaxSurge.SetNumber(s.MaxSurge, "0.0");
        _srcMaxDecel.SetNumber(s.MaxDecel, "0.0");

        // ---- Per-wheel speed detection (only meaningful while detecting) ----
        IsDetecting = s.Detection == DetectionState.Detecting;
        if (_isDetecting)
        {
            UpdateDetection(s);
        }

        // ---- Pipeline values ----
        SlipTcTitle = s.AggregateUsesTc ? "Slip / TC — using TC" : "Slip / TC — using Slip";
        LockAbsTitle = s.AggregateUsesAbs ? "Lock / ABS — using ABS" : "Lock / ABS — using Lock";
        SlipTc.Update(s.SlipTc);
        LockAbs.Update(s.LockAbs);
        SlipBlend.Update(s.SlipBlend);
        LockBlend.Update(s.LockBlend);
        Slip.Update(s.Slip);
        Lock.Update(s.Lock);
        Abs.Update(s.Abs);
        Tc.Update(s.Tc);
        BaseSlip.Update(s.BaseSlip);
        Loads.Update(s.Loads);
        _baseSlipMono.Set(s.BaseSlipMono);
        _baseLockMono.Set(s.BaseLockMono);
        _baseAbsMono.Set(s.BaseAbsMono);
        _baseTcMono.Set(s.BaseTcMono);
        BaseSlipModeText = BaseModeText(s);
        BaseSlipMinimum = IsBaseSlipSigned(s) ? -100.0 : 0.0;

        if (!ReferenceEquals(s.Preset, _shownPreset) || !ReferenceEquals(s.PresetName, _shownPresetName))
        {
            _shownPreset = s.Preset;
            _shownPresetName = s.PresetName;
            ShowPreset(s.PresetName, s.Preset);
        }
    }

    private void UpdateDetection(HapticsSnapshot s)
    {
        _detSpeed.SetFormatted(s.DetectSpeedOk ? "OK " : "-- ", s.DetectSpeed, "0.0", " m/s");
        _detLateral.SetFormatted(s.DetectCornerOk ? "OK " : "-- ", s.DetectLat, "0.00", " G");
        _detBrake.SetFormatted(s.DetectBrakeOk ? "OK " : "-- ", s.DetectBrake, "0", " %");
        _detMaxDelta.SetNumber(s.DetectMaxDelta, "0.000");
        if (s.DetectSpeedOk && (s.DetectCornerOk || s.DetectBrakeOk))
        {
            _detStatus.SetFormatted("Testing (", s.DetectFrames, "0", DetectionFramesSuffix);
        }
        else
        {
            _detStatus.SetText("Drive + corner/brake...");
        }
    }

    private void ShowPreset(string name, GamePreset preset)
    {
        _presetName.SetText(name);
        if (preset == null)
        {
            for (int i = 1; i < PresetInfo.Count; i++)
            {
                PresetInfo[i].SetText(null);
            }

            return;
        }

        _presetSlipLoad.SetText(LatLongText(preset.SlipLat, preset.SlipLong));
        _presetLockLoad.SetText(LatLongText(preset.LockLat, preset.LockLong));
        _presetAbsLoad.SetText(LatLongText(preset.AbsLat, preset.AbsLong));
        _presetTcLoad.SetText(LatLongText(preset.TcLat, preset.TcLong));
        _presetSynthLock.SetFlag(preset.SynthLockFromSlip);
        if (preset.SpeedFadeKmh > 0)
        {
            _presetSpeedFade.SetNumber(preset.SpeedFadeKmh, "0");
        }
        else
        {
            _presetSpeedFade.SetText("off");
        }

        _presetInverseLoad.SetFlag(preset.InverseSlipLoad);
        _presetPreGain.SetNumber(preset.PreGain, "0");
        _presetPreCut.SetNumber(preset.PreCut, "0");
    }

    private void UpdateBalanceTab(HapticsSnapshot s)
    {
        BalanceOutputs b = s.Balance;

        _balUndersteer.Set(b.Understeer);
        _balOversteer.Set(b.Oversteer);
        _balConfidence.Set(b.Confidence);
        _balGate.SetText(DisplayText.Gate(b.Gate));
        _balPath.SetText(DisplayText.Path(b.Path));
        _balActive.SetFlag(b.Active);
        UpdateTags(b);
        _balClassPreset.SetText(DisplayText.ClassPreset(b.ClassPreset));

        _detUsModel.Set(b.UsModel);
        _detUsDirect.Set(b.UsDirect);
        _detOsModel.Set(b.OsModel);
        _detOsYaw.Set(b.OsYaw);
        _detOsCountersteer.Set(b.OsCountersteer);
        _detOsBodySlip.Set(b.OsBodySlip);
        _detOsDirect.Set(b.OsDirect);
        _detSpeedRamp.Set(b.SpeedRamp);

        _sigYawRate.SetNumber(b.YawRate, "0.000");
        _sigYawRef.SetNumber(b.YawRef, "0.000");
        _sigYawRatio.SetNumber(b.YawRatio, "0.00");
        _sigBodySlip.SetNumber(b.BodySlipDeg, "0.0");
        _sigSteering.SetNumber(b.SteeringDeg, "0.0");
        _sigSteeringEff.SetNumber(b.SteeringEffDeg, "0.0");
        _sigAlphaFront.SetNumber(b.AlphaFront, "0.00");
        _sigAlphaRear.SetNumber(b.AlphaRear, "0.00");

        // Only G and K report a confidence in BalanceOutputs; the others show "-".
        _paramG.Update(b.G, DisplayText.Source(b.GSource), b.ConfG, b.SamplesG);
        _paramK.Update(b.K, DisplayText.Source(b.KSource), b.ConfK, b.SamplesK);
        _paramTheta0.Update(b.Theta0Deg, DisplayText.Source(b.Theta0Source), double.NaN, b.SamplesTheta0);
        _paramTau.Update(b.TauYaw, DisplayText.Source(b.TauSource), double.NaN, b.SamplesTau);
        _paramAyMax.Update(b.AyMax, LearnedText(b.AyMax), double.NaN, b.SamplesAy);
        _paramAlphaPeak.Update(b.AlphaPeak, LearnedText(b.AlphaPeak), double.NaN, b.SamplesAlpha);

        _learnActive.SetFlag(b.LearningActive);
        _learnLocked.SetFlag(b.LearningLocked);
        _learnSession.SetFlag(b.SessionOverrideActive);
        _learnSteeringSign.SetText(b.SteeringSignVerified ? DisplayText.Sign(b.SteeringSign) : DisplayText.SignVerifying(b.SteeringSign));
        _learnForwardSign.SetText(DisplayText.Sign(b.ForwardSign));
        _learnGravity.SetText(b.GravityIncluded.HasValue ? DisplayText.YesNo(b.GravityIncluded.Value) : "detecting");
        _learnAutoPreset.SetText(DisplayText.ClassPreset(s.ClassPresetAuto));

        if (_autoPresetShown != s.ClassPresetAuto)
        {
            _autoPresetShown = s.ClassPresetAuto;
            _autoPresetOption.Text = AutoPresetText(s.ClassPresetAuto);
        }

        UpdateOverrideHints(b);

        if (IsSyncAllowed(_learningLockedHoldUntil))
        {
            SetProperty(ref _learningLocked, s.LearningLocked, nameof(LearningLocked));
        }
    }

    private void UpdateTags(BalanceOutputs b)
    {
        int mask = (b.PowerOversteer ? 1 : 0) | (b.LiftOrBrakeOversteer ? 2 : 0) | (b.EntryUndersteer ? 4 : 0) |
                   (b.ExitUndersteer ? 8 : 0) | (b.Countersteer ? 16 : 0) | (b.Spin ? 32 : 0);
        if (mask == _tagMask)
        {
            return;
        }

        _tagMask = mask;
        if (mask == 0)
        {
            _balTags.SetText("none");
            return;
        }

        var text = new StringBuilder();
        AppendTag(text, b.PowerOversteer, "power oversteer");
        AppendTag(text, b.LiftOrBrakeOversteer, "lift/brake oversteer");
        AppendTag(text, b.EntryUndersteer, "entry understeer");
        AppendTag(text, b.ExitUndersteer, "exit understeer");
        AppendTag(text, b.Countersteer, "countersteer");
        AppendTag(text, b.Spin, "spin");
        _balTags.SetText(text.ToString());
    }

    private void UpdateOverrideHints(BalanceOutputs b)
    {
        BalanceTuning tuning = _host.Settings.Balance;
        for (int i = 0; i < _overrides.Length; i++)
        {
            OverrideItem item = _overrides[i];
            switch (item.Kind)
            {
                case BalanceOverrideKind.SteeringRatio:
                    item.Suggest(tuning.SteeringRatioDefault);
                    item.SetInUseNote(SteeringRatioNote);
                    break;
                case BalanceOverrideKind.WheelbaseM:
                    item.Suggest(tuning.WheelbaseDefaultM);
                    item.SetInUseNote(WheelbaseNote);
                    break;
                case BalanceOverrideKind.G:
                    SuggestEffective(item, b.G, b.GSource);
                    break;
                case BalanceOverrideKind.K:
                    SuggestEffective(item, b.K, b.KSource);
                    break;
                case BalanceOverrideKind.Theta0Deg:
                    SuggestEffective(item, b.Theta0Deg, b.Theta0Source);
                    break;
                case BalanceOverrideKind.TauYawS:
                    SuggestEffective(item, b.TauYaw, b.TauSource);
                    break;
            }
        }
    }

    private void UpdateDiagnosticsTab(HapticsSnapshot s)
    {
        _diagSimKey.SetText(s.GameName);
        _diagCarKey.SetText(s.CarKey);
        _diagCarKeySource.SetText(s.CarKeyProvisional ? string.Concat(s.CarKeySource, " (provisional, identifying car)") : s.CarKeySource);
        _diagCarId.SetText(s.CarId);
        _diagCarName.SetText(s.CarDisplayName);
        _diagCarClass.SetText(s.CarClass);
        _diagProfilePath.SetText(s.CarProfilePath);
        for (int i = 0; i < Wheels.Count; i++)
        {
            _diagSlipPaths[i].SetText(s.SlipPaths[i]);
            _diagLockPaths[i].SetText(s.LockPaths[i]);
        }

        _diagBalanceSource.SetText(s.BalanceSourceName);
        _diagBalanceSupported.SetFlag(s.BalanceSupported);
        _diagUpdateMs.SetNumber(s.DataUpdateMs, "0.000");
        _diagFrames.SetNumber(s.FrameCount, "0");
        BalanceResolution = s.BalanceResolution;
        LastError = s.LastError;
        RecordingPath = s.RecordingPath;
        if (IsSyncAllowed(_recordingHoldUntil))
        {
            SetProperty(ref _isRecording, s.RecordingActive, nameof(IsRecording));
        }
    }

    private void ReportRefreshError(Exception ex)
    {
        _uiErrorCount++;
        _diagUiErrors.SetFormatted(null, _uiErrorCount, "0", " (last: " + ex.Message + ")");
        int now = Environment.TickCount;
        if (!_uiErrorLogged || unchecked(now - _lastUiErrorLogTick) >= ErrorLogIntervalMs)
        {
            _uiErrorLogged = true;
            _lastUiErrorLogTick = now;
            SimHub.Logging.Current.Error("DLP [Haptics]: settings UI refresh failed (" + _uiErrorCount + " so far)", ex);
        }
    }

    // =====================================================================================================
    // Commands
    // =====================================================================================================

    private void ResetSlipLockTuning()
    {
        // Booleans may be written here directly (atomic); the numeric values are reset on the data thread.
        HapticsSettings live = _host.Settings;
        live.GateSlipOnThrottle = SettingsDefaults.GateSlipOnThrottle;
        live.GateLockOnBrake = SettingsDefaults.GateLockOnBrake;
        live.UseShakeItWheelLock = SettingsDefaults.UseShakeItWheelLock;
        _host.EditSettings(settings => settings.ResetSlipLockTuningToDefaults());
        EnvelopeSliders.ShowDefaults();
        BlendSliders.ShowDefaults();
        ThresholdSliders.ShowDefaults();
        OnPropertyChanged(nameof(GateSlipOnThrottle));
        OnPropertyChanged(nameof(GateLockOnBrake));
        OnPropertyChanged(nameof(UseShakeItWheelLock));
    }

    private void ResetBalanceTuning()
    {
        _host.EditSettings(settings => settings.Balance.ResetToDefaults());
        for (int i = 0; i < BalanceTuningGroups.Count; i++)
        {
            BalanceTuningGroups[i].ShowDefaults();
        }

        SetProperty(ref _selectedBalanceMode, FindMode(TuningDefaults.Mode), nameof(SelectedBalanceMode));
    }

    private void ResetLearning()
    {
        string car = CarNameForDialogs();
        MessageBoxResult answer = MessageBox.Show(
            "Forget everything the plugin has learned about \"" + car + "\" (steering gain, understeer factor, offsets, grip)?"
            + " The steering direction of this sim is verified again in the next corners.",
            "Reset learned vehicle model",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        CarProfileStatus = RunHostAction(() =>
        {
            _host.ResetLearning();
            return "Learned model cleared for " + car + ".";
        });
    }

    private void ExportCarProfile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export car profile",
            Filter = ProfileFileFilter,
            DefaultExt = ProfileFileExtension,
            AddExtension = true,
            FileName = SafeFileName(CarNameForDialogs()) + ProfileFileExtension,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(DialogOwner()) == true)
        {
            CarProfileStatus = RunHostAction(() => _host.ExportCarProfile(dialog.FileName));
        }
    }

    private void ImportCarProfile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import car profile into " + CarNameForDialogs(),
            Filter = ProfileFileFilter,
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(DialogOwner()) == true)
        {
            CarProfileStatus = RunHostAction(() => _host.ImportCarProfile(dialog.FileName));
        }
    }

    // =====================================================================================================
    // Item factories
    // =====================================================================================================

    private SensitivityItem Sensitivity(SensitivityKind kind, string title, string help, string levelToolTip) =>
        new SensitivityItem(kind, title, help, levelToolTip, (k, percent) => _host.SetSensitivity(k, percent));

    private SliderGroup CreateEnvelopeSliders()
    {
        const double AttackMax = 200;
        const double ReleaseMax = 500;
        const double MinMs = 1;
        const string Attack = "Rise time of the channel. Lower = snappier.";
        const string Release = "Decay time of the channel. Higher = smoother, longer tail.";
        HapticsSettings d = SettingsDefaults;
        return new SliderGroup("Envelope (attack / release)", new[]
        {
            Setting("Slip attack (ms)", MinMs, AttackMax, 1, "0", d.SlipAttackMs, Attack, s => s.SlipAttackMs, (s, v) => s.SlipAttackMs = v),
            Setting("Slip release (ms)", MinMs, ReleaseMax, 1, "0", d.SlipReleaseMs, Release, s => s.SlipReleaseMs, (s, v) => s.SlipReleaseMs = v),
            Setting("Lock attack (ms)", MinMs, AttackMax, 1, "0", d.LockAttackMs, Attack, s => s.LockAttackMs, (s, v) => s.LockAttackMs = v),
            Setting("Lock release (ms)", MinMs, ReleaseMax, 1, "0", d.LockReleaseMs, Release, s => s.LockReleaseMs, (s, v) => s.LockReleaseMs = v),
            Setting("ABS attack (ms)", MinMs, AttackMax, 1, "0", d.ABSAttackMs, Attack, s => s.ABSAttackMs, (s, v) => s.ABSAttackMs = v),
            Setting("ABS release (ms)", MinMs, ReleaseMax, 1, "0", d.ABSReleaseMs, Release, s => s.ABSReleaseMs, (s, v) => s.ABSReleaseMs = v),
            Setting("TC attack (ms)", MinMs, AttackMax, 1, "0", d.TCAttackMs, Attack, s => s.TCAttackMs, (s, v) => s.TCAttackMs = v),
            Setting("TC release (ms)", MinMs, ReleaseMax, 1, "0", d.TCReleaseMs, Release, s => s.TCReleaseMs, (s, v) => s.TCReleaseMs = v),
        });
    }

    private SliderGroup CreateBlendSliders()
    {
        const double Max = 100;
        const string Throttle = "Share of the effect scaled by throttle (0 = raw effect, 100 = fully throttle dependent).";
        const string Brake = "Share of the effect scaled by brake pressure (0 = raw effect, 100 = fully brake dependent).";
        HapticsSettings d = SettingsDefaults;
        return new SliderGroup("Pedal blend", new[]
        {
            Setting("Slip × throttle (%)", 0, Max, 1, "0", d.SlipThrottleBlend, Throttle, s => s.SlipThrottleBlend, (s, v) => s.SlipThrottleBlend = v),
            Setting("Lock × brake (%)", 0, Max, 1, "0", d.LockBrakeBlend, Brake, s => s.LockBrakeBlend, (s, v) => s.LockBrakeBlend = v),
            Setting("TC × throttle (%)", 0, Max, 1, "0", d.TCThrottleBlend, Throttle, s => s.TCThrottleBlend, (s, v) => s.TCThrottleBlend = v),
            Setting("ABS × brake (%)", 0, Max, 1, "0", d.ABSBrakeBlend, Brake, s => s.ABSBrakeBlend, (s, v) => s.ABSBrakeBlend = v),
        });
    }

    private SliderGroup CreateThresholdSliders()
    {
        const double Max = 50;
        const string Help = "Values at or below this are cut; the rest is rescaled to 0..100 (removes the noise floor).";
        HapticsSettings d = SettingsDefaults;
        return new SliderGroup("Thresholds", new[]
        {
            Setting("Slip threshold", 0, Max, 1, "0", d.SlipThreshold, Help, s => s.SlipThreshold, (s, v) => s.SlipThreshold = v),
            Setting("Lock threshold", 0, Max, 1, "0", d.LockThreshold, Help, s => s.LockThreshold, (s, v) => s.LockThreshold = v),
            Setting("ABS threshold", 0, Max, 1, "0", d.ABSThreshold, Help, s => s.ABSThreshold, (s, v) => s.ABSThreshold = v),
            Setting("TC threshold", 0, Max, 1, "0", d.TCThreshold, Help, s => s.TCThreshold, (s, v) => s.TCThreshold = v),
        });
    }

    private IReadOnlyList<SliderGroup> CreateBalanceTuningGroups()
    {
        BalanceTuning d = TuningDefaults;
        return new[]
        {
            new SliderGroup("Speed and inputs", new[]
            {
                Tuning("V min (m/s)", 0, 30, 0.5, "0.0", d.VMin, "Below this speed the outputs are 0.", t => t.VMin, (t, v) => t.VMin = v),
                Tuning("V full (m/s)", 1, 50, 0.5, "0.0", d.VFull, "From this speed on the outputs are at full weight.", t => t.VFull, (t, v) => t.VFull = v),
                Tuning("Steering deadband (°)", 0, 10, 0.1, "0.0", d.ThetaDeadbandDeg, "Minimum steering (minus offset) for understeer evaluation.", t => t.ThetaDeadbandDeg, (t, v) => t.ThetaDeadbandDeg = v),
                Tuning("Yaw floor (rad/s)", 0.01, 0.5, 0.01, "0.00", d.RFloor, "Below this reference yaw rate the yaw ratio is undefined.", t => t.RFloor, (t, v) => t.RFloor = v),
                Tuning("Input smoothing (s)", 0, 0.2, 0.005, "0.000", d.TauInput, "Smoothing time constant for steering, yaw rate and speed.", t => t.TauInput, (t, v) => t.TauInput = v),
                Tuning("Default yaw lag (s)", 0.02, 0.5, 0.01, "0.00", d.TauYawDefault, "Steering-to-yaw lag used until it is learned.", t => t.TauYawDefault, (t, v) => t.TauYawDefault = v),
            }),
            new SliderGroup("Understeer", new[]
            {
                Tuning("US onset", 0, 0.5, 0.01, "0.00", d.UsOnset, "Yaw deficit (1 − ρ) where understeer starts.", t => t.UsOnset, (t, v) => t.UsOnset = v),
                Tuning("US full", 0.05, 1.5, 0.01, "0.00", d.UsFull, "Yaw deficit (1 − ρ) giving full understeer.", t => t.UsFull, (t, v) => t.UsFull = v),
            }),
            new SliderGroup("Oversteer", new[]
            {
                Tuning("OS yaw onset", 0, 0.5, 0.01, "0.00", d.OsYawOnset, "Yaw excess (ρ − 1) where oversteer starts.", t => t.OsYawOnset, (t, v) => t.OsYawOnset = v),
                Tuning("OS yaw full", 0.05, 2, 0.01, "0.00", d.OsYawFull, "Yaw excess (ρ − 1) giving full oversteer.", t => t.OsYawFull, (t, v) => t.OsYawFull = v),
                Tuning("Countersteer min (°)", 0, 20, 0.5, "0.0", d.CsThetaMinDeg, "Minimum opposite steering to count as countersteer.", t => t.CsThetaMinDeg, (t, v) => t.CsThetaMinDeg = v),
                Tuning("Countersteer yaw onset (rad/s)", 0, 1, 0.01, "0.00", d.CsROnset, "Yaw rate where countersteer oversteer starts.", t => t.CsROnset, (t, v) => t.CsROnset = v),
                Tuning("Countersteer yaw full (rad/s)", 0.05, 2, 0.01, "0.00", d.CsRFull, "Yaw rate giving full countersteer oversteer.", t => t.CsRFull, (t, v) => t.CsRFull = v),
                Tuning("Countersteer base", 0, 1, 0.05, "0.00", d.CsBase, "Minimum oversteer while countersteering.", t => t.CsBase, (t, v) => t.CsBase = v),
                Tuning("Body slip onset margin (°)", 0, 10, 0.1, "0.0", d.BetaOnsetMarginDeg, "Body slip beyond the learned envelope where oversteer starts.", t => t.BetaOnsetMarginDeg, (t, v) => t.BetaOnsetMarginDeg = v),
                Tuning("Body slip full margin (°)", 0.5, 30, 0.1, "0.0", d.BetaFullMarginDeg, "Body slip beyond the envelope giving full oversteer.", t => t.BetaFullMarginDeg, (t, v) => t.BetaFullMarginDeg = v),
                Tuning("Loose surface multiplier", 1, 5, 0.1, "0.0", d.LooseSurfaceMultiplier, "Widens oversteer thresholds on gravel/grass/dirt.", t => t.LooseSurfaceMultiplier, (t, v) => t.LooseSurfaceMultiplier = v),
                Tuning("Spin body slip (°)", 10, 90, 1, "0", d.BetaSpinDeg, "Body slip treated as a spin (oversteer 1).", t => t.BetaSpinDeg, (t, v) => t.BetaSpinDeg = v),
                Tuning("Spin timeout (s)", 0, 5, 0.1, "0.0", d.SpinTimeout, "After this long in a spin the outputs fade out.", t => t.SpinTimeout, (t, v) => t.SpinTimeout = v),
            }),
            new SliderGroup("Output shaping", new[]
            {
                Tuning("Attack (s)", 0, 0.5, 0.005, "0.000", d.Attack, "Envelope rise time constant.", t => t.Attack, (t, v) => t.Attack = v),
                Tuning("Release (s)", 0, 1, 0.01, "0.00", d.Release, "Envelope decay time constant.", t => t.Release, (t, v) => t.Release = v),
                Tuning("Hysteresis", 0, 0.2, 0.005, "0.000", d.Hysteresis, "Raw intensity needed to switch an output on.", t => t.Hysteresis, (t, v) => t.Hysteresis = v),
                Tuning("Gamma", 0.3, 3, 0.05, "0.00", d.Gamma, "Curve after the onset-to-full mapping (1 = linear, > 1 = softer start).", t => t.Gamma, (t, v) => t.Gamma = v),
                Tuning("Mutual exclusion", 0, 1, 0.05, "0.00", d.MutualExclusionOs, "Oversteer above this forces understeer to 0.", t => t.MutualExclusionOs, (t, v) => t.MutualExclusionOs = v),
                Tuning("Tag threshold", 0, 1, 0.05, "0.00", d.TagThreshold, "Output level that sets the context tags.", t => t.TagThreshold, (t, v) => t.TagThreshold = v),
            }),
            new SliderGroup("Edge cases", new[]
            {
                Tuning("Contact spike (g)", 1, 10, 0.1, "0.0", d.ContactBlankG, "Horizontal acceleration spike treated as contact.", t => t.ContactBlankG, (t, v) => t.ContactBlankG = v),
                Tuning("Contact blank (s)", 0, 2, 0.05, "0.00", d.ContactBlankTime, "Outputs muted after contact.", t => t.ContactBlankTime, (t, v) => t.ContactBlankTime = v),
                Tuning("Airborne margin (g)", 0.1, 1, 0.05, "0.00", d.AirborneMarginG, "Airborne when vertical load drops below (1 − margin) g.", t => t.AirborneMarginG, (t, v) => t.AirborneMarginG = v),
                Tuning("Airborne hold (s)", 0, 1, 0.05, "0.00", d.AirborneHold, "Outputs muted after being airborne.", t => t.AirborneHold, (t, v) => t.AirborneHold = v),
                Tuning("Reset blank (s)", 0, 10, 0.5, "0.0", d.ResetBlankTime, "Outputs muted after a reset, teleport or rejoin.", t => t.ResetBlankTime, (t, v) => t.ResetBlankTime = v),
            }),
            new SliderGroup("Direct slip-angle path (fractions of the learned peak)", new[]
            {
                Tuning("Direct US onset", 0, 1, 0.01, "0.00", d.DirectUsOnset, "Front-minus-rear slip angle where understeer starts.", t => t.DirectUsOnset, (t, v) => t.DirectUsOnset = v),
                Tuning("Direct US full", 0.05, 2, 0.01, "0.00", d.DirectUsFull, "Front-minus-rear slip angle giving full understeer.", t => t.DirectUsFull, (t, v) => t.DirectUsFull = v),
                Tuning("Direct OS onset", 0, 1, 0.01, "0.00", d.DirectOsOnset, "Rear-minus-front slip angle where oversteer starts.", t => t.DirectOsOnset, (t, v) => t.DirectOsOnset = v),
                Tuning("Direct OS full", 0.05, 2, 0.01, "0.00", d.DirectOsFull, "Rear-minus-front slip angle giving full oversteer.", t => t.DirectOsFull, (t, v) => t.DirectOsFull = v),
                Tuning("Front peak gate", 0, 2, 0.05, "0.00", d.DirectPeakGate, "Understeer needs the front slip angle at this fraction of the peak.", t => t.DirectPeakGate, (t, v) => t.DirectPeakGate = v),
            }),
            new SliderGroup("Learning", new[]
            {
                Tuning("Confidence to use learned", 0, 1, 0.05, "0.00", d.LearnedConfidenceThreshold, "Learned values are used from this confidence on.", t => t.LearnedConfidenceThreshold, (t, v) => t.LearnedConfidenceThreshold = v),
            }),
        };
    }

    private OverrideItem[] CreateOverrides()
    {
        // Ranges match the learner's plausibility limits (G 0.005..0.1, K 0..0.004, |θ0| ≤ 10°, τ 0.05..0.30 s).
        BalanceTuning d = TuningDefaults;
        return new[]
        {
            Override(BalanceOverrideKind.SteeringRatio, "Steering ratio", 5, 30, 0.5, "0.0", d.SteeringRatioDefault,
                "Steering-wheel angle per road-wheel angle. Together with the wheelbase it defines G."),
            Override(BalanceOverrideKind.WheelbaseM, "Wheelbase (m)", 1.5, 4, 0.01, "0.00", d.WheelbaseDefaultM,
                "Distance between the axles. Together with the steering ratio it defines G."),
            Override(BalanceOverrideKind.G, "Yaw gain G (1/m)", 0.005, 0.1, 0.0005, "0.0000", d.GDefault,
                "Overrides G directly (takes precedence over steering ratio + wheelbase)."),
            Override(BalanceOverrideKind.K, "Understeer factor K (s²/m²)", 0, 0.004, 0.00001, "0.00000", d.KDefault,
                "Speed-dependent understeer of the vehicle model."),
            Override(BalanceOverrideKind.Theta0Deg, "Steering offset θ0 (°)", -10, 10, 0.1, "0.0", 0,
                "Steering-wheel angle when driving straight."),
            Override(BalanceOverrideKind.TauYawS, "Yaw lag τ (s)", 0.05, 0.3, 0.01, "0.00", d.TauYawDefault,
                "Delay between steering input and yaw response."),
        };
    }

    private SliderItem Setting(
        string title, double min, double max, double step, string format, double defaultValue, string help,
        Func<HapticsSettings, double> get, Action<HapticsSettings, double> set) =>
        new SliderItem(title, min, max, step, format, defaultValue, help,
            () => get(_host.Settings), v => _host.EditSettings(settings => set(settings, v)), null);

    private SliderItem Tuning(
        string title, double min, double max, double step, string format, double defaultValue, string help,
        Func<BalanceTuning, double> get, Action<BalanceTuning, double> set) =>
        new SliderItem(title, min, max, step, format, defaultValue, help,
            () => get(_host.Settings.Balance), v => _host.EditSettings(settings => set(settings.Balance, v)), null);

    private OverrideItem Override(
        BalanceOverrideKind kind, string title, double min, double max, double step, string format, double initial, string help) =>
        new OverrideItem(kind, title, min, max, step, format, initial, help, item => _host.SetOverride(item.Kind, item.OverrideValue));

    private static OptionItem<BalanceClassPreset?> PresetOption(BalanceClassPreset preset) =>
        new OptionItem<BalanceClassPreset?>(preset, DisplayText.ClassPreset(preset));

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    /// <summary>Writes a boolean global setting, schedules the save and notifies the binding.</summary>
    private void UpdateSetting(bool current, bool value, Action<bool> write, [CallerMemberName] string propertyName = null)
    {
        if (current == value)
        {
            return;
        }

        write(value);
        _host.NotifySettingsChanged();
        OnPropertyChanged(propertyName);
    }

    private OptionItem<BalanceMode> FindMode(BalanceMode mode)
    {
        for (int i = 0; i < BalanceModes.Count; i++)
        {
            if (BalanceModes[i].Value == mode)
            {
                return BalanceModes[i];
            }
        }

        return BalanceModes[0];
    }

    private OptionItem<BalanceClassPreset?> FindClassPreset(BalanceClassPreset? preset)
    {
        for (int i = 0; i < ClassPresets.Count; i++)
        {
            if (ClassPresets[i].Value == preset)
            {
                return ClassPresets[i];
            }
        }

        return _autoPresetOption;
    }

    private static string AutoPresetText(BalanceClassPreset detected) =>
        string.Concat("Auto (", DisplayText.ClassPreset(detected), ")");

    private static void SuggestEffective(OverrideItem item, double value, ParamSource source)
    {
        item.Suggest(value);
        item.SetInUse(value, DisplayText.Source(source));
    }

    private static string LearnedText(double value) => MathUtil.IsFinite(value) ? "learned" : "not learned";

    private static void SetAssistState(InfoItem item, bool exported, double level)
    {
        if (!exported)
        {
            item.SetText("N/A");
        }
        else if (level > 0)
        {
            item.SetFormatted("Yes (lvl ", level, "0", ")");
        }
        else
        {
            item.SetText("Off");
        }
    }

    /// <summary>v1 "Slip Src" wording.</summary>
    private static string LegacySlipSourceText(HapticsSnapshot s)
    {
        if (!s.GameRunning)
        {
            return "No game";
        }

        bool hasSlip = s.SlipSource != SlipSourceKind.None;
        return s.Detection switch
        {
            DetectionState.Detecting => hasSlip ? DetectingText(s.SlipSource) : "Detecting...",
            DetectionState.PerWheel => "Per-Wheel",
            DetectionState.Mono => hasSlip ? DisplayText.SlipSource(s.SlipSource) : "No data",
            _ => "...",
        };
    }

    private static string DetectingText(SlipSourceKind kind) => kind switch
    {
        SlipSourceKind.ShakeIt => "ShakeIT (detecting)",
        SlipSourceKind.AccNative => "ACC native (detecting)",
        SlipSourceKind.RFactorRotation => "wheel rotation (detecting)",
        SlipSourceKind.PerWheelSpeed => "per-wheel speed (detecting)",
        _ => "Detecting...",
    };

    /// <summary>v1 "Per-Whl" wording.</summary>
    private static string PerWheelText(HapticsSnapshot s) => s.Detection switch
    {
        DetectionState.Detecting => "Detecting...",
        DetectionState.PerWheel => "Per-Wheel",
        DetectionState.Mono => s.SlipSource == SlipSourceKind.RFactorRotation ? "Per-Wheel" : "Mono",
        _ => "...",
    };

    /// <summary>v1 "ShakeIT" column; other native sources are named instead of "Not found".</summary>
    private static string ShakeItSlipText(HapticsSnapshot s)
    {
        if (s.ShakeItSlipAvailable)
        {
            return "Available";
        }

        return s.ResolvedSlipSource switch
        {
            SlipSourceKind.AccNative => "N/A (ACC native slip)",
            SlipSourceKind.RFactorRotation => "N/A (LMU native)",
            _ => "Not found",
        };
    }

    private static bool IsBaseSlipSigned(HapticsSnapshot s) =>
        s.Detection == DetectionState.PerWheel || s.SlipSource == SlipSourceKind.PerWheelSpeed
        || s.SlipSource == SlipSourceKind.RFactorRotation;

    private static string BaseModeText(HapticsSnapshot s)
    {
        if (s.Detection == DetectionState.PerWheel || s.SlipSource == SlipSourceKind.PerWheelSpeed)
        {
            return "Per-wheel speed slip, signed (+ spin, − lock).";
        }

        if (s.SlipSource == SlipSourceKind.RFactorRotation)
        {
            return "Per-wheel rotation slip, signed (+ spin, − lock).";
        }

        return "Slip source values, unsigned (lock is synthesized while braking).";
    }

    private static string LatLongText(double lateral, double longitudinal) =>
        string.Concat(DisplayText.Number(lateral, "0"), " / ", DisplayText.Number(longitudinal, "0"));

    private static void AppendTag(StringBuilder text, bool active, string name)
    {
        if (!active)
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append(", ");
        }

        text.Append(name);
    }

    /// <summary>True when no user toggle is waiting for the host to confirm it.</summary>
    private static bool IsSyncAllowed(int holdUntilTick) => unchecked(Environment.TickCount - holdUntilTick) >= 0;

    private static string RunHostAction(Func<string> action)
    {
        try
        {
            return action() ?? string.Empty;
        }
        catch (Exception ex)
        {
            // Host actions do file IO; report failures in the page instead of crashing the UI thread.
            return "Error: " + ex.Message;
        }
    }

    private string CarNameForDialogs()
    {
        HapticsSnapshot s = _snapshot;
        if (!string.IsNullOrEmpty(s.CarDisplayName))
        {
            return s.CarDisplayName;
        }

        return string.IsNullOrEmpty(s.CarKey) ? "car" : s.CarKey;
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var result = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            result.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return result.ToString().Trim();
    }

    private static Window DialogOwner()
    {
        Application app = Application.Current;
        return app?.MainWindow;
    }

    /// <summary>Sample data for the XAML designer (never used at runtime).</summary>
    private sealed class DesignTimeHost : IHapticsHost
    {
        private readonly HapticsSettings _settings = new HapticsSettings();

        public HapticsSettings Settings => _settings;

        public void CopySnapshot(HapticsSnapshot target)
        {
            target.GameRunning = true;
            target.GameName = "LMU";
            target.HasCar = true;
            target.CarDisplayName = "Ligier JS P320";
            target.CarKey = "Ligier JS P320";
            target.CarClass = "LMP3";
            target.SlipSource = SlipSourceKind.ShakeIt;
            target.ShakeItSlipAvailable = true;
            target.ShakeItLockAvailable = true;
            target.LockUsesShakeIt = true;
            target.LockSynthesized = true;
            target.SlipDataAvailable = true;
            target.ResolvedSlipSource = SlipSourceKind.ShakeIt;
            target.CarKeySource = "native model";
            target.Detection = DetectionState.Mono;
            target.PresetName = "LMU";
            target.LockSensitivity = 180;
            target.SlipTcMono = 35;
            target.LockAbsMono = 60;
            target.Balance.Understeer = 0.4;
            target.Balance.Gate = BalanceGate.Active;
            target.Balance.Active = true;
            target.Balance.Path = BalancePath.Model;
            target.Balance.Confidence = 0.45;
            target.BalanceSupported = true;
        }

        public void NotifySettingsChanged()
        {
        }

        public void EditSettings(Action<HapticsSettings> edit) => edit?.Invoke(_settings);

        public void SetSensitivity(SensitivityKind kind, double percent)
        {
        }

        public void SetOverride(BalanceOverrideKind kind, double? value)
        {
        }

        public void SetClassPresetOverride(BalanceClassPreset? preset)
        {
        }

        public void SetLearningLocked(bool locked)
        {
        }

        public void ResetLearning()
        {
        }

        public void SetRecording(bool enabled)
        {
        }

        public void RequestRetest()
        {
        }

        public string GenerateShakeItDataExportProfile() => string.Empty;

        public string GenerateHapticPedalProfile() => string.Empty;

        public string GenerateBalanceProfile() => string.Empty;

        public string ExportCarProfile(string filePath) => string.Empty;

        public string ImportCarProfile(string filePath) => string.Empty;

        public string DumpPropertyNames() => string.Empty;
    }
}
