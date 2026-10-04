using DivebombLogistics.UI;

namespace DivebombLogistics.Haptics.UI.ViewModels;

/// <summary>
/// One "label: value" line of a read-only info grid (debug view). The view model pushes values every refresh;
/// numbers are only re-formatted when they actually changed, so a steady value costs no string allocation.
/// </summary>
public sealed class InfoItem : ObservableObject
{
    private string _value = DisplayText.Missing;

    // Inputs of the last formatted number (compared by reference for the constant strings).
    private bool _hasNumber;
    private double _lastNumber;
    private string _lastPrefix;
    private string _lastFormat;
    private string _lastSuffix;

    /// <param name="label">Caption, e.g. "Slip source".</param>
    /// <param name="toolTip">Optional explanation shown on hover.</param>
    public InfoItem(string label, string toolTip = null)
    {
        Label = label;
        ToolTip = toolTip;
    }

    public string Label { get; }

    public string ToolTip { get; }

    /// <summary>Displayed value text (never null).</summary>
    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    /// <summary>Shows a ready-made text (null → <see cref="DisplayText.Missing"/>).</summary>
    public void SetText(string text)
    {
        _hasNumber = false;
        Value = string.IsNullOrEmpty(text) ? DisplayText.Missing : text;
    }

    public void SetFlag(bool value) => SetText(DisplayText.YesNo(value));

    /// <summary>Shows a formatted number (NaN → <see cref="DisplayText.Missing"/>).</summary>
    public void SetNumber(double value, string format) => SetFormatted(null, value, format, null);

    /// <summary>
    /// Shows <c>prefix + number + suffix</c>, e.g. <c>SetFormatted("TC (lvl ", 3, "0", ")")</c> → "TC (lvl 3)".
    /// A non-finite number shows as <see cref="DisplayText.Missing"/> between prefix and suffix.
    /// </summary>
    public void SetFormatted(string prefix, double value, string format, string suffix)
    {
        if (_hasNumber && value.Equals(_lastNumber) && ReferenceEquals(prefix, _lastPrefix) &&
            ReferenceEquals(format, _lastFormat) && ReferenceEquals(suffix, _lastSuffix))
        {
            return;
        }

        _hasNumber = true;
        _lastNumber = value;
        _lastPrefix = prefix;
        _lastFormat = format;
        _lastSuffix = suffix;
        Value = string.Concat(prefix, DisplayText.Number(value, format), suffix);
    }
}
