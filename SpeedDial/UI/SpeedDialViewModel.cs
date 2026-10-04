using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DivebombLogistics.Framework;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.SpeedDial.Telemetry;
using DivebombLogistics.SpeedDial.UI.ViewModels;
using DivebombLogistics.UI;
using DivebombLogistics.UI.ViewModels;
using Microsoft.Win32;

namespace DivebombLogistics.SpeedDial.UI;

/// <summary>
/// View model of the "Speed Dial" tab.
/// <para>
/// Layout (simple by default): status (car, current-value chips, dial progress with Cancel), the presets of the
/// current car, the set/reset pairs, and two expanders: "Button bindings" (SimHub <c>ControlsEditor</c> per action)
/// and "Setup" (Control Mapper roles, slot count, pair definitions, timing, learning, diagnostics).
/// </para>
/// <para>
/// Refresh model: while the tab is visible (<see cref="Start"/>/<see cref="Stop"/>) a 100 ms
/// <see cref="DispatcherTimer"/> copies the host's <see cref="SpeedDialSnapshot"/> into a private instance and pushes
/// it into observable properties and items, which raise change notifications only for values that changed. Row
/// lists are reconciled only when <see cref="SpeedDialSnapshot.PresetsVersion"/>, <see cref="SpeedDialSnapshot.PairsVersion"/>,
/// <see cref="SpeedDialSnapshot.SettingsVersion"/> or <see cref="SpeedDialSnapshot.CarDataVersion"/> changed, and
/// existing rows are updated in place (matched by id), so an edit box never loses focus to a refresh. Global settings
/// are not re-read for <see cref="SettingsSyncHoldMs"/> after a local edit, so a queued edit never makes a control jump
/// back to the old value.
/// </para>
/// Threading: UI thread only. Every change goes through <see cref="ISpeedDialHost"/> (posted to the data thread);
/// global settings through <see cref="ISpeedDialHost.EditSettings"/> with captured values (the snapshot's settings
/// copy is never mutated).
/// </summary>
public sealed class SpeedDialViewModel : ObservableObject, IPresetRowOwner
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>After a local settings edit, ignore snapshot settings for this long (the edit is still queued).</summary>
    private const int SettingsSyncHoldMs = 1500;

    /// <summary>Minimum interval between two SimHub log entries for UI refresh errors.</summary>
    private const int ErrorLogIntervalMs = 10000;

    private const int MillisecondsPerSecond = 1000;

    private const string PresetFileFilter = "SpeedDial presets (*.json)|*.json|All files (*.*)|*.*";
    private const string PresetFileExtension = ".json";
    private const string PresetFilePrefix = "SpeedDial_";

    // ---- Status texts (constants: no allocation per refresh) ----
    private const string NoGameText = "No game running";
    private const string GameRunningText = "Game running";
    private const string NoCarText = "no car";
    private const string IdentifyingSuffix = " (identifying...)";
    private const string FallbackCarName = "car";
    private const string HintNoGame = "Start a game to see the current values and dial presets.";
    private const string HintNoControlMapper =
        "SimHub's Control Mapper is not available. SpeedDial presses Control Mapper output roles: enable the Control Mapper plugin and map its roles to the game's buttons.";
    private const string HintWaitingForCar = "Waiting for the car...";
    private const string HintProvisional = "Identifying the car: edits are kept and saved once the car is known.";
    private const string PresetsNoCarHint = "Load a car to create and edit its presets (presets are saved per car).";
    private const string PresetsEmptyHint = "No presets for this car yet: type a name below and create one from the current values.";
    private const string PresetsListHint =
        "Tick the channels a preset sets. Assign presets to Dial slots for the Dial buttons; the marked preset is the one Next / Previous / Apply selected use.";
    private const string ControlMapperAvailableText = "available";
    private const string ControlMapperMissingText = "not available (enable the Control Mapper plugin)";
    private const string NoErrorText = "none";
    private const string NoRolesText = "No Control Mapper button roles found: type the role names (as defined in the Control Mapper) instead.";
    private const string RolesFoundSuffix = " Control Mapper button roles found.";
    private const string ProgressChannelsInfix = " of ";
    private const string ProgressChannelsSuffix = " channels";
    private const string ProgressPressesPrefix = "presses: ";
    private const string UiErrorPrefix = "Speed Dial UI error: ";
    private const string ErrorPrefix = SpeedDialNames.ErrorPrefix;

    // ---- Binding captions ----
    private const string DialCaptionPrefix = "Dial ";
    private const string TestSentText = "sent...";

    /// <summary>
    /// ACC, AC EVO and AC Rally report the brake bias without the car-specific offset their display adds, and that raw
    /// value is what SpeedDial stores and dials.
    /// </summary>
    private const string RawBrakeBiasHint =
        "Brake bias here is the raw value of this sim: the game shows it plus a car-specific offset. Capture brake bias from the car instead of typing the in-game number.";

    /// <summary>Refreshes (100 ms each) after which an unchanged status is taken as the answer to a test press.</summary>
    private const int TestAnswerRefreshes = 10;

    /// <summary>Number-box format of the sliders (every SpeedDial setting is a whole number).</summary>
    private const string WholeNumberFormat = "0";
    private const string NextCaption = "Next preset";
    private const string PreviousCaption = "Previous preset";
    private const string ApplySelectedCaption = "Apply selected preset";
    private const string SetCaptionPrefix = "Set: ";
    private const string ResetCaptionPrefix = "Reset: ";
    private const string CancelCaption = "Cancel dialing";

    private static readonly IReadOnlyList<PairItem> NoPairs = new PairItem[0];
    private static readonly IReadOnlyList<PairSummary> NoPairSummaries = new PairSummary[0];
    private static readonly IReadOnlyList<PresetSummary> NoPresetSummaries = new PresetSummary[0];

    private readonly ISpeedDialHost _host;
    private readonly SpeedDialSnapshot _snapshot = new SpeedDialSnapshot();
    private DispatcherTimer _timer;

    // ---- Fixed per-channel items ----
    private readonly ValueChipItem[] _chips = new ValueChipItem[DialChannels.Count];
    private readonly DialChannelItem[] _dialChannels = new DialChannelItem[DialChannels.Count];
    private readonly ChannelSetupItem[] _channelSetup = new ChannelSetupItem[DialChannels.Count];
    private readonly LearningItem[] _learning = new LearningItem[DialChannels.Count];
    private readonly SliderItem[] _timing;
    private readonly bool[] _support = new bool[DialChannels.Count];

    // ---- Slot choices: one instance per slot for the page's lifetime (combo boxes keep their selection) ----
    private readonly SlotOption[] _allSlotOptions = CreateSlotOptions();
    private SlotOption[] _slotOptions;
    private readonly string[] _slotIds = new string[SpeedDialSettings.MaxSlotCount];

    // ---- Status ----
    private bool _hasCar;
    private bool _controlMapperAvailable = true;
    private string _gameText = NoGameText;
    private string _carText = NoCarText;
    private string _statusHint = HintNoGame;
    private string _statusText = string.Empty;
    private string _carTextName;
    private bool _carTextHasCar;
    private bool _carTextProvisional;
    private bool _carTextBuilt;

    // ---- Dial ----
    private bool _dialSynced;
    private int _dialVersion;
    private bool _isBusy;
    private string _brakeBiasHint = string.Empty;

    // ---- Last test press, until the module's status answers it ----
    private ChannelSetupItem _pendingTestRow;
    private string _pendingTestStatus = string.Empty;
    private int _pendingTestRefreshes;
    private bool _showDial;
    private string _dialStateText = SpeedDialText.State(DialState.Idle);
    private string _dialLabelText = string.Empty;
    private string _dialMessage = string.Empty;
    private double _dialProgress;
    private string _dialProgressText = string.Empty;

    // ---- Presets ----
    private bool _presetsSynced;
    private int _presetsVersion;
    private int _carDataVersion;
    private int _slotCount = -1;
    private string _selectedPresetId;
    private string _newPresetName = string.Empty;
    private string _presetsHint = PresetsNoCarHint;
    private string _presetsStatus = string.Empty;

    // ---- Pairs ----
    private bool _pairsSynced;
    private int _pairsVersion;
    private int _pairsCarDataVersion;
    private IReadOnlyList<PairItem> _pairs = NoPairs;

    // ---- Bindings ----
    private bool _bindingsSynced;
    private int _bindingsSlotCount;
    private int _bindingsPairsVersion;

    // ---- Settings / setup ----
    private bool _settingsSynced;
    private int _settingsVersion;
    private int _settingsHoldUntil;
    private bool _isSetupExpanded;
    private string _rolesText = string.Empty;
    private string _controlMapperText = ControlMapperAvailableText;
    private string _telemetryText = SpeedDialText.Missing;
    private string _telemetryDescription = string.Empty;
    private string _carFilePath = string.Empty;
    private string _lastError = NoErrorText;
    private int _pairCount;

    // ---- UI errors ----
    private int _uiErrorCount;
    private int _lastUiErrorLogTick;
    private bool _uiErrorLogged;

    /// <summary>Designer constructor: sample data (never used at runtime).</summary>
    public SpeedDialViewModel()
        : this(new DesignTimeSpeedDialHost())
    {
        Refresh();
    }

    /// <summary>Creates the view model for the running module.</summary>
    public SpeedDialViewModel(ISpeedDialHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settingsHoldUntil = Environment.TickCount;
        _slotOptions = BuildSlotOptions(SpeedDialSettings.DefaultSlotCount);
        for (int i = 0; i < DialChannels.Count; i++)
        {
            var channel = (DialChannel)i;
            _chips[i] = new ValueChipItem(channel);
            _dialChannels[i] = new DialChannelItem(channel);
            _channelSetup[i] = new ChannelSetupItem(channel, SetChannelEnabled, SetChannelRole, TestRole, CanTestRoles);
            _learning[i] = new LearningItem(channel, ResetChannelLearning, () => _hasCar);
            _support[i] = true;
        }

        Presets = new ObservableCollection<PresetItem>();
        Bindings = new ObservableCollection<BindingItem>();
        PairDefinitions = new ObservableCollection<PairDefinitionItem>();

        SlotCountSlider = WholeNumberSlider(
            "Dial slots",
            SpeedDialSettings.MinSlotCount,
            SpeedDialSettings.MaxSlotCount,
            1,
            SpeedDialSettings.DefaultSlotCount,
            "Number of Dial buttons (Dial 1..n). Every car assigns its own presets to the slots.",
            v => EditSettings(s => s.SlotCount = (int)v));
        _timing = CreateTimingSliders();

        CancelDialCommand = new RelayCommand(() => _host.CancelDial(), () => _isBusy);
        NewPresetFromCurrentCommand = new RelayCommand(() => CreatePreset(true), CanCreatePreset);
        NewEmptyPresetCommand = new RelayCommand(() => CreatePreset(false), CanCreatePreset);
        ExportPresetsCommand = new RelayCommand(ExportPresets, () => _hasCar);
        ImportPresetsCommand = new RelayCommand(ImportPresets, () => _hasCar);
        AddPairCommand = new RelayCommand(AddPair, () => _pairCount < SpeedDialSettings.MaxPairCount);
        RemoveLastPairCommand = new RelayCommand(RemoveLastPair, () => _pairCount > SpeedDialSettings.MinPairCount);
        ResetTimingCommand = new RelayCommand(ResetTiming);
        ResetAllLearningCommand = new RelayCommand(ResetAllLearning, () => _hasCar);
        RefreshRolesCommand = new RelayCommand(LoadRoles);

        Confirm = ConfirmWithMessageBox;
        PickExportPath = PickExportPathWithDialog;
        PickImportPath = PickImportPathWithDialog;
    }

    // =====================================================================================================
    // Bindable state
    // =====================================================================================================

    /// <summary>A car is loaded (per-car edits possible).</summary>
    public bool HasCar
    {
        get => _hasCar;
        private set => SetProperty(ref _hasCar, value);
    }

    /// <summary>Game name or "No game running".</summary>
    public string GameText
    {
        get => _gameText;
        private set => SetProperty(ref _gameText, value);
    }

    /// <summary>Car name (with "(identifying...)" while the key is provisional) or "no car".</summary>
    public string CarText
    {
        get => _carText;
        private set => SetProperty(ref _carText, value);
    }

    /// <summary>Call-to-action banner; empty when nothing needs attention.</summary>
    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    /// <summary>Latest action/dial status of the module (<c>DLP.SpeedDial.Status</c>).</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Current-value chips, one per channel.</summary>
    public IReadOnlyList<ValueChipItem> Chips => _chips;

    /// <summary>The dial panel is shown (a job ran since the module started).</summary>
    public bool ShowDial
    {
        get => _showDial;
        private set => SetProperty(ref _showDial, value);
    }

    /// <summary>"Dialing", "Completed", ...</summary>
    public string DialStateText
    {
        get => _dialStateText;
        private set => SetProperty(ref _dialStateText, value);
    }

    /// <summary>Label of the job (preset name or "Reset Pair 1").</summary>
    public string DialLabelText
    {
        get => _dialLabelText;
        private set => SetProperty(ref _dialLabelText, value);
    }

    /// <summary>Message of the dialer (why it stopped, ...).</summary>
    public string DialMessage
    {
        get => _dialMessage;
        private set => SetProperty(ref _dialMessage, value);
    }

    /// <summary>Finished share of the targeted channels (0..1).</summary>
    public double DialProgress
    {
        get => _dialProgress;
        private set => SetProperty(ref _dialProgress, value);
    }

    /// <summary>"2 of 5 channels · presses: 7".</summary>
    public string DialProgressText
    {
        get => _dialProgressText;
        private set => SetProperty(ref _dialProgressText, value);
    }

    /// <summary>Per-channel progress rows (rows of untargeted channels are hidden).</summary>
    public IReadOnlyList<DialChannelItem> DialChannelRows => _dialChannels;

    /// <summary>Presets of the current car in list order.</summary>
    public ObservableCollection<PresetItem> Presets { get; }

    /// <summary>Note for sims whose brake bias is a raw value that differs from the in-game display; empty elsewhere.</summary>
    public string BrakeBiasHint
    {
        get => _brakeBiasHint;
        private set => SetProperty(ref _brakeBiasHint, value);
    }

    /// <summary>Explains the preset list (or why it is empty).</summary>
    public string PresetsHint
    {
        get => _presetsHint;
        private set => SetProperty(ref _presetsHint, value);
    }

    /// <summary>Name for the next new preset (empty = "Preset n").</summary>
    public string NewPresetName
    {
        get => _newPresetName;
        set => SetProperty(ref _newPresetName, value ?? string.Empty);
    }

    /// <summary>Result of the last export/import.</summary>
    public string PresetsStatus
    {
        get => _presetsStatus;
        private set => SetProperty(ref _presetsStatus, value ?? string.Empty);
    }

    /// <summary>Set/reset pairs with this car's stored values.</summary>
    public IReadOnlyList<PairItem> Pairs
    {
        get => _pairs;
        private set => SetProperty(ref _pairs, value);
    }

    /// <summary>SimHub actions for the "Button bindings" expander.</summary>
    public ObservableCollection<BindingItem> Bindings { get; }

    /// <summary>
    /// The Setup expander is open. Opening it fetches the Control Mapper roles for the role pickers (never per tick)
    /// and starts the live learning/diagnostics updates.
    /// </summary>
    public bool IsSetupExpanded
    {
        get => _isSetupExpanded;
        set
        {
            if (SetProperty(ref _isSetupExpanded, value) && value)
            {
                LoadRoles();
                UpdateSetupLive(_snapshot);
            }
        }
    }

    /// <summary>Per-channel role setup rows.</summary>
    public IReadOnlyList<ChannelSetupItem> ChannelSetup => _channelSetup;

    /// <summary>How many Control Mapper roles were found (or how to proceed without).</summary>
    public string RolesText
    {
        get => _rolesText;
        private set => SetProperty(ref _rolesText, value);
    }

    /// <summary>Number of Dial slots.</summary>
    public SliderItem SlotCountSlider { get; }

    /// <summary>Pair definitions (name + channels).</summary>
    public ObservableCollection<PairDefinitionItem> PairDefinitions { get; }

    /// <summary>Dial timing sliders.</summary>
    public IReadOnlyList<SliderItem> TimingSliders => _timing;

    /// <summary>What was learned per channel for the current car.</summary>
    public IReadOnlyList<LearningItem> Learning => _learning;

    /// <summary>Control Mapper availability text.</summary>
    public string ControlMapperText
    {
        get => _controlMapperText;
        private set => SetProperty(ref _controlMapperText, value);
    }

    /// <summary>Name of the dial telemetry source.</summary>
    public string TelemetryText
    {
        get => _telemetryText;
        private set => SetProperty(ref _telemetryText, value);
    }

    /// <summary>Which properties the telemetry source reads (diagnostics).</summary>
    public string TelemetryDescription
    {
        get => _telemetryDescription;
        private set => SetProperty(ref _telemetryDescription, value);
    }

    /// <summary>The car's SpeedDial file.</summary>
    public string CarFilePath
    {
        get => _carFilePath;
        private set => SetProperty(ref _carFilePath, value);
    }

    /// <summary>Newest module error, or "none".</summary>
    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    // ---- Commands ----

    /// <summary>Cancels the running dial job.</summary>
    public RelayCommand CancelDialCommand { get; }

    /// <summary>Creates a preset from the current values.</summary>
    public RelayCommand NewPresetFromCurrentCommand { get; }

    /// <summary>Creates an empty preset.</summary>
    public RelayCommand NewEmptyPresetCommand { get; }

    /// <summary>Saves this car's presets to a file.</summary>
    public RelayCommand ExportPresetsCommand { get; }

    /// <summary>Adds the presets of a file to this car.</summary>
    public RelayCommand ImportPresetsCommand { get; }

    /// <summary>Adds a set/reset pair (up to four).</summary>
    public RelayCommand AddPairCommand { get; }

    /// <summary>Removes the last set/reset pair (at least one stays).</summary>
    public RelayCommand RemoveLastPairCommand { get; }

    /// <summary>Restores the default timing.</summary>
    public RelayCommand ResetTimingCommand { get; }

    /// <summary>Forgets every channel's learning for this car.</summary>
    public RelayCommand ResetAllLearningCommand { get; }

    /// <summary>Fetches the Control Mapper roles again.</summary>
    public RelayCommand RefreshRolesCommand { get; }

    // ---- Dialog seams (replaceable, e.g. by a UI harness) ----

    /// <summary>Asks a yes/no question (title, message) → yes. Defaults to a message box.</summary>
    internal Func<string, string, bool> Confirm { get; set; }

    /// <summary>Asks for an export path (suggested file name) → path or null. Defaults to a save dialog.</summary>
    internal Func<string, string> PickExportPath { get; set; }

    /// <summary>Asks for an import path → path or null. Defaults to an open dialog.</summary>
    internal Func<string> PickImportPath { get; set; }

    // =====================================================================================================
    // Lifecycle
    // =====================================================================================================

    /// <summary>Starts the 10 Hz refresh (call when the tab becomes visible). Idempotent.</summary>
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

    /// <summary>Stops the refresh (call when the tab is hidden or unloaded). Idempotent.</summary>
    public void Stop() => _timer?.Stop();

    private void OnTimerTick(object sender, EventArgs e) => Refresh();

    /// <summary>Copies the latest snapshot and updates everything that changed.</summary>
    internal void Refresh()
    {
        try
        {
            _host.CopySnapshot(_snapshot);
            SpeedDialSnapshot s = _snapshot;
            UpdateStatus(s);
            UpdatePendingTest(s);
            UpdateDial(s);
            SyncSettings(s);
            SyncPresets(s);
            SyncPairs(s);
            SyncBindings(s);
            if (_isSetupExpanded)
            {
                UpdateSetupLive(s);
            }
        }
        catch (Exception ex)
        {
            // A display problem must never take down SimHub's UI thread; count it and log it rate-limited.
            ReportRefreshError(ex);
        }
    }

    // =====================================================================================================
    // Refresh stages
    // =====================================================================================================

    private void UpdateStatus(SpeedDialSnapshot s)
    {
        GameText = !s.GameRunning ? NoGameText : string.IsNullOrEmpty(s.GameName) ? GameRunningText : s.GameName;
        string brakeBiasHint = HasRawBrakeBias(s.GameName) ? RawBrakeBiasHint : string.Empty;
        if (!string.Equals(brakeBiasHint, _brakeBiasHint, StringComparison.Ordinal))
        {
            BrakeBiasHint = brakeBiasHint;
            _chips[(int)DialChannel.BrakeBias].SetNote(brakeBiasHint);
        }

        if (s.HasCar != _hasCar)
        {
            HasCar = s.HasCar;
            RefreshCommandStates();
        }

        if (s.ControlMapperAvailable != _controlMapperAvailable)
        {
            _controlMapperAvailable = s.ControlMapperAvailable;
            ControlMapperText = s.ControlMapperAvailable ? ControlMapperAvailableText : ControlMapperMissingText;
            RefreshTestCommands();
        }

        string name = string.IsNullOrEmpty(s.CarDisplayName) ? s.CarKey : s.CarDisplayName;
        if (!_carTextBuilt || s.HasCar != _carTextHasCar || s.CarKeyProvisional != _carTextProvisional || !string.Equals(name, _carTextName, StringComparison.Ordinal))
        {
            _carTextBuilt = true;
            _carTextHasCar = s.HasCar;
            _carTextProvisional = s.CarKeyProvisional;
            _carTextName = name;
            string carName = string.IsNullOrEmpty(name) ? FallbackCarName : name;
            CarText = !s.HasCar ? NoCarText : s.CarKeyProvisional ? carName + IdentifyingSuffix : carName;
        }

        StatusHint = !s.ControlMapperAvailable ? HintNoControlMapper
            : !s.GameRunning ? HintNoGame
            : !s.HasCar ? HintWaitingForCar
            : s.CarKeyProvisional ? HintProvisional
            : string.Empty;
        // The module mirrors the dial message into its status; the dial panel already shows it, so do not repeat it.
        bool repeatsDialMessage = s.Dial.State != DialState.Idle && string.Equals(s.Status, s.Dial.Message, StringComparison.Ordinal);
        StatusText = repeatsDialMessage ? string.Empty : s.Status ?? string.Empty;

        for (int i = 0; i < _chips.Length; i++)
        {
            _support[i] = !s.GameRunning || s.ChannelSupported[i];
            _chips[i].Update(s.CurrentValues[i], _support[i]);
        }
    }

    private void UpdateDial(SpeedDialSnapshot s)
    {
        DialStatus dial = s.Dial;
        if (dial.IsBusy != _isBusy)
        {
            _isBusy = dial.IsBusy;
            CancelDialCommand.RaiseCanExecuteChanged();
            RefreshTestCommands();
        }

        if (_dialSynced && dial.Version == _dialVersion)
        {
            return;
        }

        _dialSynced = true;
        _dialVersion = dial.Version;
        ShowDial = dial.State != DialState.Idle;
        DialStateText = SpeedDialText.State(dial.State);
        DialLabelText = dial.Label ?? string.Empty;
        DialMessage = dial.Message ?? string.Empty;
        int total = 0;
        int done = 0;
        int presses = 0;
        for (int i = 0; i < _dialChannels.Length; i++)
        {
            ChannelProgress progress = dial.Channels[i];
            bool current = dial.CurrentChannel.HasValue && (int)dial.CurrentChannel.Value == i;
            _dialChannels[i].Update(progress, current);
            if (DialChannelItem.IsTargetedProgress(progress))
            {
                total++;
                presses += progress.Presses;
                if (progress.Result != ChannelResult.Pending)
                {
                    done++;
                }
            }
        }

        DialProgress = total > 0 ? (double)done / total : dial.IsFinished ? 1.0 : 0.0;
        DialProgressText = done.ToString(CultureInfo.InvariantCulture) + ProgressChannelsInfix + total.ToString(CultureInfo.InvariantCulture)
            + ProgressChannelsSuffix + SpeedDialText.Separator + ProgressPressesPrefix + presses.ToString(CultureInfo.InvariantCulture);
    }

    private void SyncSettings(SpeedDialSnapshot s)
    {
        if (_settingsSynced && s.SettingsVersion == _settingsVersion)
        {
            return;
        }

        if (_settingsSynced && !IsSyncAllowed(_settingsHoldUntil))
        {
            // A local edit is still on its way to the data thread; the snapshot may predate it. Sync later.
            return;
        }

        SpeedDialSettings settings = s.Settings;
        if (settings == null)
        {
            return;
        }

        _settingsSynced = true;
        _settingsVersion = s.SettingsVersion;
        for (int i = 0; i < _channelSetup.Length; i++)
        {
            _channelSetup[i].Sync(settings.GetBinding((DialChannel)i));
        }

        SlotCountSlider.ShowValue(settings.SlotCount);
        DialTiming timing = settings.Timing ?? new DialTiming();
        _timing[0].ShowValue(timing.PressMs);
        _timing[1].ShowValue(timing.GapMs);
        _timing[2].ShowValue(timing.ConfirmTimeoutMs);
        _timing[3].ShowValue(timing.MaxStallPresses);
        _timing[4].ShowValue(timing.MaxPressesPerChannel);
        _timing[5].ShowValue((double)timing.GatePauseTimeoutMs / MillisecondsPerSecond);

        int count = settings.Pairs?.Count ?? 0;
        if (PairDefinitions.Count != count)
        {
            while (PairDefinitions.Count > count)
            {
                PairDefinitions.RemoveAt(PairDefinitions.Count - 1);
            }

            while (PairDefinitions.Count < count)
            {
                PairDefinitions.Add(new PairDefinitionItem(PairDefinitions.Count, RenamePair, SetPairChannel));
            }
        }

        for (int i = 0; i < count; i++)
        {
            PairDefinitions[i].Sync(settings.Pairs[i]);
        }

        if (count != _pairCount)
        {
            _pairCount = count;
            AddPairCommand.RaiseCanExecuteChanged();
            RemoveLastPairCommand.RaiseCanExecuteChanged();
        }
    }

    private void SyncPresets(SpeedDialSnapshot s)
    {
        bool slotsChanged = s.SlotCount != _slotCount;
        if (slotsChanged)
        {
            _slotCount = s.SlotCount;
            _slotOptions = BuildSlotOptions(_slotCount);
        }

        bool listChanged = !_presetsSynced || s.PresetsVersion != _presetsVersion || s.CarDataVersion != _carDataVersion;
        if (listChanged || SupportChanged())
        {
            _presetsSynced = true;
            _presetsVersion = s.PresetsVersion;
            _carDataVersion = s.CarDataVersion;
            ReconcilePresets(s.Presets);
            NewPresetFromCurrentCommand.RaiseCanExecuteChanged();
            NewEmptyPresetCommand.RaiseCanExecuteChanged();
        }

        bool markersChanged = listChanged || slotsChanged || !string.Equals(s.SelectedPresetId, _selectedPresetId, StringComparison.Ordinal);
        for (int i = 0; i < _slotIds.Length; i++)
        {
            if (!string.Equals(s.SlotPresetIds[i], _slotIds[i], StringComparison.Ordinal))
            {
                _slotIds[i] = s.SlotPresetIds[i];
                markersChanged = true;
            }
        }

        if (markersChanged)
        {
            _selectedPresetId = s.SelectedPresetId;
            for (int i = 0; i < Presets.Count; i++)
            {
                PresetItem item = Presets[i];
                item.ShowSelected(string.Equals(item.Id, _selectedPresetId, StringComparison.Ordinal));
                item.ShowSlot(_slotOptions, FindSlot(item.Id));
            }
        }

        PresetsHint = !_hasCar ? PresetsNoCarHint : Presets.Count == 0 ? PresetsEmptyHint : PresetsListHint;
    }

    private void SyncPairs(SpeedDialSnapshot s)
    {
        if (_pairsSynced && s.PairsVersion == _pairsVersion && s.CarDataVersion == _pairsCarDataVersion)
        {
            return;
        }

        _pairsSynced = true;
        _pairsVersion = s.PairsVersion;
        _pairsCarDataVersion = s.CarDataVersion;
        IReadOnlyList<PairSummary> summaries = s.Pairs ?? NoPairSummaries;
        var items = new PairItem[summaries.Count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new PairItem(summaries[i], index => _host.SetPair(index), index => _host.ResetPair(index), () => _hasCar);
        }

        Pairs = items;
    }

    private void SyncBindings(SpeedDialSnapshot s)
    {
        if (_bindingsSynced && s.SlotCount == _bindingsSlotCount && s.PairsVersion == _bindingsPairsVersion)
        {
            return;
        }

        _bindingsSynced = true;
        _bindingsSlotCount = s.SlotCount;
        _bindingsPairsVersion = s.PairsVersion;
        var wanted = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < s.SlotCount && i < SpeedDialSettings.MaxSlotCount; i++)
        {
            wanted.Add(Binding(SpeedDialNames.DialAction(i), DialCaptionPrefix + (i + 1).ToString(CultureInfo.InvariantCulture)));
        }

        wanted.Add(Binding(SpeedDialNames.NextPresetAction, NextCaption));
        wanted.Add(Binding(SpeedDialNames.PreviousPresetAction, PreviousCaption));
        wanted.Add(Binding(SpeedDialNames.ApplySelectedPresetAction, ApplySelectedCaption));
        IReadOnlyList<PairSummary> pairs = s.Pairs ?? NoPairSummaries;
        for (int i = 0; i < pairs.Count; i++)
        {
            string set = SpeedDialNames.SetAction(pairs[i].Index);
            string reset = SpeedDialNames.ResetAction(pairs[i].Index);
            if (set != null && reset != null)
            {
                wanted.Add(Binding(set, SetCaptionPrefix + pairs[i].Name));
                wanted.Add(Binding(reset, ResetCaptionPrefix + pairs[i].Name));
            }
        }

        wanted.Add(Binding(SpeedDialNames.CancelAction, CancelCaption));
        ReconcileBindings(wanted);
    }

    private void UpdateSetupLive(SpeedDialSnapshot s)
    {
        for (int i = 0; i < DialChannels.Count; i++)
        {
            _learning[i].Update(s.LearnedDirection[i], s.DirectionConfirmed[i], s.LearnedStep[i], s.ObservedMin[i], s.ObservedMax[i]);
            _channelSetup[i].ShowTelemetry(_support[i], s.MaxValues[i], s.CurrentValues[i]);
        }

        TelemetryText = string.IsNullOrEmpty(s.TelemetryName) ? SpeedDialText.Missing : s.TelemetryName;
        TelemetryDescription = s.TelemetryDescription ?? string.Empty;
        CarFilePath = s.CarFilePath ?? string.Empty;
        LastError = string.IsNullOrEmpty(s.LastError) ? NoErrorText : s.LastError;
    }

    // =====================================================================================================
    // Reconciliation helpers
    // =====================================================================================================

    /// <summary>Keeps existing rows (matched by id) and only touches the collection when membership or order changed.</summary>
    private void ReconcilePresets(IReadOnlyList<PresetSummary> summaries)
    {
        summaries ??= NoPresetSummaries;
        var existing = new Dictionary<string, PresetItem>(StringComparer.Ordinal);
        for (int i = 0; i < Presets.Count; i++)
        {
            if (!existing.ContainsKey(Presets[i].Id))
            {
                existing[Presets[i].Id] = Presets[i];
            }
        }

        var next = new List<PresetItem>(summaries.Count);
        for (int i = 0; i < summaries.Count; i++)
        {
            PresetSummary summary = summaries[i];
            if (summary == null)
            {
                continue;
            }

            if (existing.TryGetValue(summary.Id, out PresetItem item))
            {
                existing.Remove(summary.Id);
                item.Update(summary, _support);
            }
            else
            {
                item = new PresetItem(this, summary, _slotOptions, _support);
            }

            next.Add(item);
        }

        bool same = next.Count == Presets.Count;
        for (int i = 0; same && i < next.Count; i++)
        {
            same = ReferenceEquals(next[i], Presets[i]);
        }

        if (!same)
        {
            Presets.Clear();
            for (int i = 0; i < next.Count; i++)
            {
                Presets.Add(next[i]);
            }
        }
    }

    /// <summary>Moves/inserts/removes binding rows so existing <c>ControlsEditor</c>s survive slot or pair changes.</summary>
    private void ReconcileBindings(List<KeyValuePair<string, string>> wanted)
    {
        for (int i = 0; i < wanted.Count; i++)
        {
            int found = -1;
            for (int j = i; j < Bindings.Count; j++)
            {
                if (Bindings[j].ActionName == wanted[i].Key)
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
            {
                Bindings.Insert(i, new BindingItem(wanted[i].Key, wanted[i].Value));
                continue;
            }

            if (found != i)
            {
                Bindings.Move(found, i);
            }

            Bindings[i].FriendlyName = wanted[i].Value;
        }

        while (Bindings.Count > wanted.Count)
        {
            Bindings.RemoveAt(Bindings.Count - 1);
        }
    }

    private static KeyValuePair<string, string> Binding(string relativeAction, string caption) =>
        new KeyValuePair<string, string>(DlpNames.FullName(relativeAction), caption);

    private bool SupportChanged()
    {
        // _support is refreshed in UpdateStatus; compare against what the rows last showed. Every row shows the same
        // support flags, so the first row is enough.
        if (Presets.Count == 0)
        {
            return false;
        }

        IReadOnlyList<PresetValueItem> values = Presets[0].Values;
        for (int c = 0; c < values.Count && c < _support.Length; c++)
        {
            if (values[c].IsSupported != _support[c])
            {
                return true;
            }
        }

        return false;
    }

    private SlotOption FindSlot(string presetId)
    {
        for (int slot = 0; slot < _slotCount && slot < _slotIds.Length; slot++)
        {
            if (string.Equals(_slotIds[slot], presetId, StringComparison.Ordinal))
            {
                return _allSlotOptions[slot + 1];
            }
        }

        return _allSlotOptions[0];
    }

    private SlotOption[] BuildSlotOptions(int slotCount)
    {
        int count = Math.Max(SpeedDialSettings.MinSlotCount, Math.Min(SpeedDialSettings.MaxSlotCount, slotCount));
        var options = new SlotOption[count + 1];
        Array.Copy(_allSlotOptions, options, options.Length);
        return options;
    }

    private static SlotOption[] CreateSlotOptions()
    {
        var options = new SlotOption[SpeedDialSettings.MaxSlotCount + 1];
        options[0] = SlotOption.CreateNone();
        for (int i = 0; i < SpeedDialSettings.MaxSlotCount; i++)
        {
            options[i + 1] = SlotOption.CreateSlot(i);
        }

        return options;
    }

    // =====================================================================================================
    // Preset rows (IPresetRowOwner)
    // =====================================================================================================

    bool IPresetRowOwner.CanEditPresets => _hasCar;

    double IPresetRowOwner.CurrentValue(DialChannel channel) =>
        DialChannels.IsValid(channel) ? _snapshot.CurrentValues[(int)channel] : double.NaN;

    void IPresetRowOwner.Apply(PresetItem preset) => _host.ApplyPreset(preset.Id);

    void IPresetRowOwner.Capture(PresetItem preset)
    {
        // Overwrites the preset's values with no undo, and the button sits next to Apply: ask first, like Delete.
        if (Confirm("Capture current values", "Overwrite the values of \"" + preset.Name + "\" with the car's current values?"))
        {
            _host.CapturePresetFromCurrent(preset.Id);
        }
    }

    void IPresetRowOwner.Rename(PresetItem preset, string name) => _host.RenamePreset(preset.Id, name);

    void IPresetRowOwner.Delete(PresetItem preset)
    {
        if (Confirm("Delete preset", "Delete the preset \"" + preset.Name + "\" of " + CarNameForDialogs() + "?"))
        {
            _host.DeletePreset(preset.Id);
        }
    }

    void IPresetRowOwner.Select(PresetItem preset) => _host.SelectPreset(preset.Id);

    void IPresetRowOwner.AssignSlot(PresetItem preset, SlotOption previous, SlotOption next)
    {
        if (next == null)
        {
            return;
        }

        if (next.IsNone)
        {
            // "No slot": free every slot that dials this preset (also hidden ones beyond the slot count), plus the
            // slot this row picked last, which the refreshed copy may not show yet.
            for (int slot = 0; slot < _slotIds.Length; slot++)
            {
                if (string.Equals(_slotIds[slot], preset.Id, StringComparison.Ordinal)
                    || (previous != null && !previous.IsNone && previous.SlotIndex == slot))
                {
                    _host.AssignSlot(slot, null);
                }
            }

            return;
        }

        // Moving the preset: the module frees every other slot that holds it (on the data thread, with current data),
        // so one post is enough even when several changes come before the next refresh.
        _host.AssignSlot(next.SlotIndex, preset.Id);
    }

    void IPresetRowOwner.SetValue(PresetItem preset, DialChannel channel, double? value) =>
        _host.SetPresetValue(preset.Id, channel, value);

    // =====================================================================================================
    // Commands
    // =====================================================================================================

    private bool CanCreatePreset() => _hasCar && Presets.Count < SpeedDialCarData.MaxPresets;

    private void CreatePreset(bool fromCurrentValues)
    {
        _host.CreatePreset((_newPresetName ?? string.Empty).Trim(), fromCurrentValues);
        NewPresetName = string.Empty;
    }

    private void ExportPresets()
    {
        string path = PickExportPath(PresetFilePrefix + SafeFileName(CarNameForDialogs()) + PresetFileExtension);
        if (!string.IsNullOrEmpty(path))
        {
            PresetsStatus = RunHostAction(() => _host.ExportPresets(path));
        }
    }

    private void ImportPresets()
    {
        string path = PickImportPath();
        if (!string.IsNullOrEmpty(path))
        {
            PresetsStatus = RunHostAction(() => _host.ImportPresets(path));
        }
    }

    private void AddPair()
    {
        EditSettings(s =>
        {
            if (s.Pairs != null && s.Pairs.Count < SpeedDialSettings.MaxPairCount)
            {
                s.Pairs.Add(SetResetPairDefinition.Create(SetResetPairDefinition.DefaultName(s.Pairs.Count)));
            }
        });

        // Shown at once; the settings sync (after the hold) confirms it.
        PairDefinitions.Add(new PairDefinitionItem(PairDefinitions.Count, RenamePair, SetPairChannel));
        PairDefinitions[PairDefinitions.Count - 1].Sync(SetResetPairDefinition.Create(SetResetPairDefinition.DefaultName(PairDefinitions.Count - 1)));
        _pairCount = PairDefinitions.Count;
        AddPairCommand.RaiseCanExecuteChanged();
        RemoveLastPairCommand.RaiseCanExecuteChanged();
    }

    private void RemoveLastPair()
    {
        // Only the last pair can go: per-car stored values are kept by pair index, so removing one in the middle
        // would attach them to the wrong pair.
        EditSettings(s =>
        {
            if (s.Pairs != null && s.Pairs.Count > SpeedDialSettings.MinPairCount)
            {
                s.Pairs.RemoveAt(s.Pairs.Count - 1);
            }
        });

        if (PairDefinitions.Count > SpeedDialSettings.MinPairCount)
        {
            PairDefinitions.RemoveAt(PairDefinitions.Count - 1);
        }

        _pairCount = PairDefinitions.Count;
        AddPairCommand.RaiseCanExecuteChanged();
        RemoveLastPairCommand.RaiseCanExecuteChanged();
    }

    private void ResetTiming()
    {
        EditSettings(s => s.Timing = new DialTiming());
        for (int i = 0; i < _timing.Length; i++)
        {
            _timing[i].ShowValue(_timing[i].DefaultValue);
        }
    }

    private void ResetAllLearning()
    {
        if (Confirm("Reset learned values", "Forget the learned direction, step and range of every channel of " + CarNameForDialogs() + "?"))
        {
            _host.ResetLearning(null);
        }
    }

    private void ResetChannelLearning(DialChannel channel) => _host.ResetLearning(channel);

    private void LoadRoles()
    {
        IReadOnlyList<string> roles;
        try
        {
            roles = _host.GetButtonRoles();
        }
        catch (Exception ex)
        {
            RolesText = ErrorPrefix + ex.Message;
            return;
        }

        int count = roles?.Count ?? 0;
        for (int i = 0; i < _channelSetup.Length; i++)
        {
            _channelSetup[i].AddRoles(roles);
        }

        RolesText = count == 0 ? NoRolesText : count.ToString(CultureInfo.InvariantCulture) + RolesFoundSuffix;
    }

    // =====================================================================================================
    // Settings edits (queued to the data thread with captured values)
    // =====================================================================================================

    private void EditSettings(Action<SpeedDialSettings> edit)
    {
        _settingsHoldUntil = unchecked(Environment.TickCount + SettingsSyncHoldMs);
        _host.EditSettings(edit);
    }

    private void SetChannelEnabled(DialChannel channel, bool enabled) =>
        EditSettings(s =>
        {
            ChannelBinding binding = s.GetBinding(channel);
            if (binding != null)
            {
                binding.Enabled = enabled;
            }
        });

    private void SetChannelRole(DialChannel channel, bool increase, string role) =>
        EditSettings(s =>
        {
            ChannelBinding binding = s.GetBinding(channel);
            if (binding == null)
            {
                return;
            }

            if (increase)
            {
                binding.IncreaseRole = role;
            }
            else
            {
                binding.DecreaseRole = role;
            }
        });

    private void RenamePair(int index, string name) =>
        EditSettings(s =>
        {
            SetResetPairDefinition pair = s.GetPair(index);
            if (pair != null)
            {
                pair.Name = name;
            }
        });

    private void SetPairChannel(int index, DialChannel channel, bool included) =>
        EditSettings(s => s.GetPair(index)?.SetIncluded(channel, included));

    private void TestRole(DialChannel channel, bool increase)
    {
        // The module answers in its status line (pressed, no role, Control Mapper missing): copy that answer into the
        // tested row, which sits far below the status line.
        _pendingTestRow = DialChannels.IsValid(channel) ? _channelSetup[(int)channel] : null;
        _pendingTestStatus = _snapshot.Status ?? string.Empty;
        _pendingTestRefreshes = 0;
        _pendingTestRow?.ShowTestResult(TestSentText);
        _host.TestRole(channel, increase);
    }

    /// <summary>True for the sims whose dial telemetry reports the brake bias without the display offset (AccDialTelemetry).</summary>
    private static bool HasRawBrakeBias(string gameName) =>
        string.Equals(gameName, DialGameNames.Acc, StringComparison.OrdinalIgnoreCase)
        || string.Equals(gameName, DialGameNames.AcEvo, StringComparison.OrdinalIgnoreCase)
        || string.Equals(gameName, DialGameNames.AcRally, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies the status that answered the last test press into its row (called on every refresh).</summary>
    private void UpdatePendingTest(SpeedDialSnapshot s)
    {
        if (_pendingTestRow == null)
        {
            return;
        }

        string status = s.Status ?? string.Empty;
        _pendingTestRefreshes++;
        bool answered = !string.Equals(status, _pendingTestStatus, StringComparison.Ordinal);

        // The same answer as before (a repeated test) leaves the status unchanged: take it after a short wait.
        if (answered || _pendingTestRefreshes >= TestAnswerRefreshes)
        {
            _pendingTestRow.ShowTestResult(status);
            _pendingTestRow = null;
        }
    }

    private bool CanTestRoles() => _controlMapperAvailable && !_isBusy;

    /// <summary>A slider for a whole-number setting (every SpeedDial setting is one), starting at its default until the stored value is shown.</summary>
    private static SliderItem WholeNumberSlider(string title, double minimum, double maximum, double step, double defaultValue, string help, Action<double> setter) =>
        new SliderItem(title, minimum, maximum, step, WholeNumberFormat, defaultValue, help, null, setter, null, 0);

    private SliderItem[] CreateTimingSliders()
    {
        const double MsStep = 10;
        return new[]
        {
            WholeNumberSlider("Press duration (ms)", DialTiming.MinPressMs, DialTiming.MaxPressMs, MsStep, DialTiming.DefaultPressMs,
                "How long each role press is held. Raise it when the game misses presses.",
                v => EditSettings(s => { if (s.Timing != null) { s.Timing.PressMs = (int)v; } })),
            WholeNumberSlider("Gap between presses (ms)", DialTiming.MinGapMs, DialTiming.MaxGapMs, MsStep, DialTiming.DefaultGapMs,
                "Pause after the game showed the new value, before the next press.",
                v => EditSettings(s => { if (s.Timing != null) { s.Timing.GapMs = (int)v; } })),
            WholeNumberSlider("Confirm timeout (ms)", DialTiming.MinConfirmTimeoutMs, DialTiming.MaxConfirmTimeoutMs, MsStep, DialTiming.DefaultConfirmTimeoutMs,
                "How long to wait for the value to change after a press before the press counts as unanswered.",
                v => EditSettings(s => { if (s.Timing != null) { s.Timing.ConfirmTimeoutMs = (int)v; } })),
            WholeNumberSlider("Unanswered presses before giving up", DialTiming.MinMaxStallPresses, DialTiming.MaxMaxStallPresses, 1, DialTiming.DefaultMaxStallPresses,
                "After this many presses without a change the channel stops (no response, or at its limit).",
                v => EditSettings(s => { if (s.Timing != null) { s.Timing.MaxStallPresses = (int)v; } })),
            WholeNumberSlider("Max presses per channel", DialTiming.MinMaxPressesPerChannel, DialTiming.MaxMaxPressesPerChannel, 1, DialTiming.DefaultMaxPressesPerChannel,
                "Safety stop for one channel.",
                v => EditSettings(s => { if (s.Timing != null) { s.Timing.MaxPressesPerChannel = (int)v; } })),
            WholeNumberSlider("Pause timeout (s)", (double)DialTiming.MinGatePauseTimeoutMs / MillisecondsPerSecond, (double)DialTiming.MaxGatePauseTimeoutMs / MillisecondsPerSecond, 1,
                (double)DialTiming.DefaultGatePauseTimeoutMs / MillisecondsPerSecond,
                "A job paused by the game (pause, menu, replay) is cancelled after this time.",
                v => EditSettings(s => { if (s.Timing != null) { s.Timing.GatePauseTimeoutMs = (int)(v * MillisecondsPerSecond); } })),
        };
    }

    // =====================================================================================================
    // Command state
    // =====================================================================================================

    private void RefreshCommandStates()
    {
        NewPresetFromCurrentCommand.RaiseCanExecuteChanged();
        NewEmptyPresetCommand.RaiseCanExecuteChanged();
        ExportPresetsCommand.RaiseCanExecuteChanged();
        ImportPresetsCommand.RaiseCanExecuteChanged();
        ResetAllLearningCommand.RaiseCanExecuteChanged();
        for (int i = 0; i < Presets.Count; i++)
        {
            Presets[i].RefreshCommands();
        }

        for (int i = 0; i < _pairs.Count; i++)
        {
            _pairs[i].RefreshCommands();
        }

        for (int i = 0; i < _learning.Length; i++)
        {
            _learning[i].ResetCommand.RaiseCanExecuteChanged();
        }
    }

    private void RefreshTestCommands()
    {
        for (int i = 0; i < _channelSetup.Length; i++)
        {
            _channelSetup[i].RefreshCommands();
        }
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    private void ReportRefreshError(Exception ex)
    {
        _uiErrorCount++;
        StatusText = UiErrorPrefix + ex.Message;
        int now = Environment.TickCount;
        if (!_uiErrorLogged || unchecked(now - _lastUiErrorLogTick) >= ErrorLogIntervalMs)
        {
            _uiErrorLogged = true;
            _lastUiErrorLogTick = now;
            SimHub.Logging.Current.Error("DLP [SpeedDial]: settings UI refresh failed (" + _uiErrorCount + " so far)", ex);
        }
    }

    /// <summary>True when no local settings edit is waiting for the data thread.</summary>
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
            return ErrorPrefix + ex.Message;
        }
    }

    private string CarNameForDialogs()
    {
        SpeedDialSnapshot s = _snapshot;
        if (!string.IsNullOrEmpty(s.CarDisplayName))
        {
            return s.CarDisplayName;
        }

        return string.IsNullOrEmpty(s.CarKey) ? FallbackCarName : s.CarKey;
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

    private static bool ConfirmWithMessageBox(string title, string message)
    {
        Window owner = DialogOwner();
        MessageBoxResult answer = owner != null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    private string PickExportPathWithDialog(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export SpeedDial presets of " + CarNameForDialogs(),
            Filter = PresetFileFilter,
            DefaultExt = PresetFileExtension,
            AddExtension = true,
            FileName = suggestedFileName,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(DialogOwner()) == true ? dialog.FileName : null;
    }

    private string PickImportPathWithDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import SpeedDial presets into " + CarNameForDialogs(),
            Filter = PresetFileFilter,
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(DialogOwner()) == true ? dialog.FileName : null;
    }

    private static Window DialogOwner() => Application.Current?.MainWindow;
}
