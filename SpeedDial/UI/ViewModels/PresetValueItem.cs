using System;
using DivebombLogistics.Core;
using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// One channel cell of a preset row: include checkbox plus value box.
/// <para>
/// Ticking the box includes the channel at the value last shown in the cell, else at the current telemetry value;
/// when neither is known the cell stays ticked locally ("pending") until a value is typed. Typing a valid value
/// includes the channel; clearing the box excludes it; invalid text is reverted. Values are sanitized like the host
/// does (<see cref="DialChannels.Sanitize"/>) so the box shows what is stored. Snapshot updates
/// (<see cref="Update"/>) never call back into the host.
/// </para>
/// </summary>
public sealed class PresetValueItem : ObservableObject
{
    private const string IncludedToolTip = "Included: this preset dials the channel to the value on the right.";
    private const string ExcludedToolTip = "Not included: this preset leaves the channel unchanged.";
    private const string UnsupportedToolTipSuffix = " (not reported by this sim: dialing it fails)";

    private readonly Action<DialChannel, double?> _commit;
    private readonly Func<DialChannel, double> _currentValue;
    private bool _isIncluded;
    private bool _pendingInclude;
    private double _value = double.NaN;
    private string _valueText = string.Empty;
    private bool _isSupported = true;

    /// <param name="channel">The channel of the cell.</param>
    /// <param name="commit">Stores a value (or null = exclude) in the preset.</param>
    /// <param name="currentValue">Current telemetry value of a channel (NaN = unknown).</param>
    internal PresetValueItem(DialChannel channel, Action<DialChannel, double?> commit, Func<DialChannel, double> currentValue)
    {
        Channel = channel;
        Header = DialChannels.ShortName(channel);
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _currentValue = currentValue ?? throw new ArgumentNullException(nameof(currentValue));
    }

    /// <summary>The channel of the cell.</summary>
    public DialChannel Channel { get; }

    /// <summary>Short channel name shown next to the checkbox.</summary>
    public string Header { get; }

    /// <summary>The sim reports the channel (unsupported cells are dimmed).</summary>
    public bool IsSupported
    {
        get => _isSupported;
        private set
        {
            if (SetProperty(ref _isSupported, value))
            {
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    /// <summary>Explains the cell.</summary>
    public string ToolTip => (_isIncluded ? IncludedToolTip : ExcludedToolTip) + (_isSupported ? string.Empty : UnsupportedToolTipSuffix);

    /// <summary>The preset sets this channel (two-way, user edits go to the host).</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (value == _isIncluded)
            {
                return;
            }

            if (!value)
            {
                _pendingInclude = false;
                SetIncluded(false);
                _commit(Channel, null);
                return;
            }

            double candidate = MathUtil.IsFinite(_value) ? _value : _currentValue(Channel);
            SetIncluded(true);
            if (MathUtil.IsFinite(candidate))
            {
                _pendingInclude = false;
                ShowValue(DialChannels.Sanitize(Channel, candidate));
                _commit(Channel, _value);
            }
            else
            {
                // Nothing to store yet: keep the tick until the user types a value.
                _pendingInclude = true;
            }
        }
    }

    /// <summary>The value as text (two-way, committed on focus loss or Enter).</summary>
    public string ValueText
    {
        get => _valueText;
        set
        {
            string text = value ?? string.Empty;
            if (text == _valueText)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                // Clearing the box excludes the channel; the box keeps the last value for an easy re-include.
                _valueText = string.Empty;
                if (_isIncluded)
                {
                    _pendingInclude = false;
                    SetIncluded(false);
                    _commit(Channel, null);
                }

                ShowValue(_value);
                return;
            }

            if (!SpeedDialText.TryParseValue(text, out double parsed))
            {
                // Revert invalid input (WPF re-reads the property after the setter).
                _valueText = text;
                ShowValue(_value);
                return;
            }

            double sanitized = DialChannels.Sanitize(Channel, parsed);
            _valueText = text;
            ShowValue(sanitized);
            _pendingInclude = false;
            SetIncluded(true);
            _commit(Channel, sanitized);
        }
    }

    /// <summary>Shows the preset's stored value (NaN = excluded) without calling the host.</summary>
    internal void Update(double value, bool supported)
    {
        IsSupported = supported;
        if (MathUtil.IsFinite(value))
        {
            _pendingInclude = false;
            SetIncluded(true);
            ShowValue(value);
        }
        else if (!_pendingInclude)
        {
            SetIncluded(false);
        }
    }

    private void SetIncluded(bool included)
    {
        if (SetProperty(ref _isIncluded, included, nameof(IsIncluded)))
        {
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    private void ShowValue(double value)
    {
        _value = value;
        SetProperty(ref _valueText, SpeedDialText.EditText(Channel, value), nameof(ValueText));
    }
}
