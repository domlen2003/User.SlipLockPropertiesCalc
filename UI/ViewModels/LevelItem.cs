namespace User.SlipLockPropertiesCalc.UI.ViewModels;

/// <summary>A labelled live level shown with a <see cref="LevelMeter"/> (e.g. a raw detector intensity).</summary>
public sealed class LevelItem : ObservableObject
{
    private double _value;

    /// <param name="label">Caption left of the meter.</param>
    /// <param name="maximum">Value at which the bar is full (the minimum is 0).</param>
    /// <param name="format">.NET number format of the value text.</param>
    /// <param name="toolTip">Optional explanation shown on hover.</param>
    public LevelItem(string label, double maximum, string format, string toolTip = null)
    {
        Label = label;
        Maximum = maximum;
        Format = format;
        ToolTip = toolTip;
    }

    public string Label { get; }

    public double Maximum { get; }

    public string Format { get; }

    public string ToolTip { get; }

    public double Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    /// <summary>Updates the displayed level.</summary>
    public void Set(double value) => Value = value;
}
