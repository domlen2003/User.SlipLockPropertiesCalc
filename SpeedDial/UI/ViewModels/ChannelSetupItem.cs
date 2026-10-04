using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DivebombLogistics.Core;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// One channel row of the "Setup" expander: enabled switch, Increase/Decrease Control Mapper roles (editable combo
/// boxes), the Test −/+ buttons, the channel's current value and maximum, and the result of the row's last test press
/// (so a test can be judged without scrolling up to the page status).
/// <para>
/// The role choices are append-only collections: Control Mapper roles fetched when the expander opens plus every role
/// value the row has stored. An editable WPF ComboBox clears its text when its selected item disappears from the list,
/// which would silently erase a binding; adding entries never does that. The boxes commit a typed role only on focus
/// loss or Enter and a picked role at once (see the view), so a half-typed name is never stored and never becomes a
/// choice. <see cref="Sync"/> shows the stored binding without calling back.
/// </para>
/// </summary>
public sealed class ChannelSetupItem : ObservableObject
{
    private const string MaxPrefix = "max ";
    private const string NowPrefix = "now ";
    private const string InfoSeparator = "  ·  ";
    private const string UnsupportedText = "not reported by this sim";
    private const string LastTestPrefix = "Last test: ";

    private readonly Action<DialChannel, bool> _setEnabled;
    private readonly Action<DialChannel, bool, string> _setRole;
    private readonly Func<bool> _canTest;
    private bool _enabled = true;
    private string _increaseRole = string.Empty;
    private string _decreaseRole = string.Empty;
    private bool _isSupported = true;
    private string _infoText = string.Empty;
    private double _shownMax = double.NaN;
    private double _shownCurrent = double.NaN;
    private bool _infoBuilt;
    private string _lastTestText = string.Empty;

    /// <param name="channel">The channel of the row.</param>
    /// <param name="setEnabled">Stores the enabled flag.</param>
    /// <param name="setRole">Stores a role (channel, increase, role).</param>
    /// <param name="test">Sends one test press (channel, increase).</param>
    /// <param name="canTest">Test presses are possible now.</param>
    internal ChannelSetupItem(
        DialChannel channel,
        Action<DialChannel, bool> setEnabled,
        Action<DialChannel, bool, string> setRole,
        Action<DialChannel, bool> test,
        Func<bool> canTest)
    {
        if (test == null)
        {
            throw new ArgumentNullException(nameof(test));
        }

        Channel = channel;
        Name = DialChannels.DisplayName(channel);
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
        _setRole = setRole ?? throw new ArgumentNullException(nameof(setRole));
        _canTest = canTest ?? throw new ArgumentNullException(nameof(canTest));
        TestIncreaseCommand = new RelayCommand(() => test(Channel, true), () => _canTest() && _enabled && !string.IsNullOrWhiteSpace(_increaseRole));
        TestDecreaseCommand = new RelayCommand(() => test(Channel, false), () => _canTest() && _enabled && !string.IsNullOrWhiteSpace(_decreaseRole));
    }

    /// <summary>The channel of the row.</summary>
    public DialChannel Channel { get; }

    /// <summary>Channel display name.</summary>
    public string Name { get; }

    /// <summary>The dialer may press this channel's roles (two-way).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _setEnabled(Channel, value);
                RefreshCommands();
            }
        }
    }

    /// <summary>Control Mapper role that raises the value (two-way; typed or picked).</summary>
    public string IncreaseRole
    {
        get => _increaseRole;
        set => SetRole(true, value);
    }

    /// <summary>Control Mapper role that lowers the value (two-way; typed or picked).</summary>
    public string DecreaseRole
    {
        get => _decreaseRole;
        set => SetRole(false, value);
    }

    /// <summary>Choices of the Increase combo box (append-only).</summary>
    public ObservableCollection<string> IncreaseRoleOptions { get; } = new ObservableCollection<string>();

    /// <summary>Choices of the Decrease combo box (append-only).</summary>
    public ObservableCollection<string> DecreaseRoleOptions { get; } = new ObservableCollection<string>();

    /// <summary>"now 4  ·  max 12", "not reported by this sim" or empty.</summary>
    public string InfoText
    {
        get => _infoText;
        private set => SetProperty(ref _infoText, value);
    }

    /// <summary>"Last test: Pressed TractionControl+ (TC +)" or the error of the row's last test press; empty before one.</summary>
    public string LastTestText
    {
        get => _lastTestText;
        private set => SetProperty(ref _lastTestText, value);
    }

    /// <summary>Sends one press of the Increase role.</summary>
    public RelayCommand TestIncreaseCommand { get; }

    /// <summary>Sends one press of the Decrease role.</summary>
    public RelayCommand TestDecreaseCommand { get; }

    /// <summary>Shows the stored binding (no host call).</summary>
    internal void Sync(ChannelBinding binding)
    {
        if (binding == null)
        {
            return;
        }

        string increase = binding.IncreaseRole ?? string.Empty;
        string decrease = binding.DecreaseRole ?? string.Empty;
        AddOption(IncreaseRoleOptions, increase);
        AddOption(DecreaseRoleOptions, decrease);
        bool changed = SetProperty(ref _enabled, binding.Enabled, nameof(Enabled));
        changed |= SetProperty(ref _increaseRole, increase, nameof(IncreaseRole));
        changed |= SetProperty(ref _decreaseRole, decrease, nameof(DecreaseRole));
        if (changed)
        {
            RefreshCommands();
        }
    }

    /// <summary>Adds the Control Mapper's button roles to both choice lists.</summary>
    internal void AddRoles(IReadOnlyList<string> roles)
    {
        if (roles == null)
        {
            return;
        }

        for (int i = 0; i < roles.Count; i++)
        {
            AddOption(IncreaseRoleOptions, roles[i]);
            AddOption(DecreaseRoleOptions, roles[i]);
        }
    }

    /// <summary>Shows telemetry support, the current value and the car's maximum (NaN = unknown).</summary>
    internal void ShowTelemetry(bool supported, double max, double current)
    {
        if (_infoBuilt && supported == _isSupported && max.Equals(_shownMax) && current.Equals(_shownCurrent))
        {
            return;
        }

        _infoBuilt = true;
        _shownMax = max;
        _shownCurrent = current;
        _isSupported = supported;
        if (!supported)
        {
            InfoText = UnsupportedText;
            return;
        }

        string now = MathUtil.IsFinite(current) ? NowPrefix + DialChannels.FormatValue(Channel, current) : string.Empty;
        string limit = MathUtil.IsFinite(max) ? MaxPrefix + DialChannels.FormatValue(Channel, max) : string.Empty;
        InfoText = now.Length > 0 && limit.Length > 0 ? now + InfoSeparator + limit : now + limit;
    }

    /// <summary>Shows the page status that answered this row's last test press.</summary>
    internal void ShowTestResult(string status) =>
        LastTestText = string.IsNullOrEmpty(status) ? string.Empty : LastTestPrefix + status;

    /// <summary>Test availability changed (busy, Control Mapper): re-query the buttons.</summary>
    internal void RefreshCommands()
    {
        TestIncreaseCommand.RaiseCanExecuteChanged();
        TestDecreaseCommand.RaiseCanExecuteChanged();
    }

    private static void AddOption(ObservableCollection<string> options, string role)
    {
        if (!string.IsNullOrEmpty(role) && !options.Contains(role))
        {
            options.Add(role);
        }
    }

    private void SetRole(bool increase, string value)
    {
        // Called on commit (focus loss, Enter or a picked item). The box keeps the raw text; the host gets it trimmed,
        // as ChannelBinding.Normalize would store it anyway.
        string text = value ?? string.Empty;
        string previous = increase ? _increaseRole : _decreaseRole;
        if (text == previous)
        {
            return;
        }

        if (increase)
        {
            _increaseRole = text;
        }
        else
        {
            _decreaseRole = text;
        }

        OnPropertyChanged(increase ? nameof(IncreaseRole) : nameof(DecreaseRole));
        string role = text.Trim();
        if (role != previous.Trim())
        {
            _setRole(Channel, increase, role);
        }

        RefreshCommands();
    }
}
