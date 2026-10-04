using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// One current-value chip of the status line ("TC 3", "BB 54.2 %"). The text is re-formatted only when the value or
/// the support flag changed, so a steady value costs nothing per refresh.
/// </summary>
public sealed class ValueChipItem : ObservableObject
{
    private const string UnsupportedToolTipSuffix = ": not reported by this sim";

    private string _valueText = SpeedDialText.Missing;
    private bool _isSupported = true;
    private string _toolTip;
    private bool _hasValue;
    private double _lastValue;
    private string _note = string.Empty;

    /// <param name="channel">The channel shown.</param>
    public ValueChipItem(DialChannel channel)
    {
        Channel = channel;
        Label = DialChannels.ShortName(channel);
        _toolTip = DialChannels.DisplayName(channel);
    }

    /// <summary>The channel shown.</summary>
    public DialChannel Channel { get; }

    /// <summary>Short channel name ("TC", "BB").</summary>
    public string Label { get; }

    /// <summary>Formatted current value ("-" when unknown).</summary>
    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    /// <summary>False when the sim does not report the channel (the chip is dimmed).</summary>
    public bool IsSupported
    {
        get => _isSupported;
        private set => SetProperty(ref _isSupported, value);
    }

    /// <summary>Full channel name, plus a note when unsupported.</summary>
    public string ToolTip
    {
        get => _toolTip;
        private set => SetProperty(ref _toolTip, value);
    }

    /// <summary>Shows <paramref name="value"/> (NaN = unknown).</summary>
    public void Update(double value, bool supported)
    {
        if (supported != _isSupported)
        {
            IsSupported = supported;
            ToolTip = BuildToolTip();
        }

        if (_hasValue && value.Equals(_lastValue))
        {
            return;
        }

        _hasValue = true;
        _lastValue = value;
        ValueText = DialChannels.FormatValue(Channel, value);
    }

    /// <summary>Adds a sim-specific note to the tooltip (e.g. the raw brake bias of ACC); empty removes it.</summary>
    public void SetNote(string note)
    {
        note ??= string.Empty;
        if (!string.Equals(note, _note, System.StringComparison.Ordinal))
        {
            _note = note;
            ToolTip = BuildToolTip();
        }
    }

    private string BuildToolTip()
    {
        string text = _isSupported ? DialChannels.DisplayName(Channel) : DialChannels.DisplayName(Channel) + UnsupportedToolTipSuffix;
        return _note.Length == 0 ? text : text + "\n" + _note;
    }
}
