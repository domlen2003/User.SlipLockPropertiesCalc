
namespace User.SlipLockPropertiesCalc.UI.ViewModels;

/// <summary>
/// One row of the balance vehicle-model table: effective value, where it came from, confidence and sample count.
/// Texts are re-formatted only when the underlying numbers change.
/// </summary>
public sealed class ParameterItem : ObservableObject
{
    private readonly string _format;

    private string _value = DisplayText.Missing;
    private string _source = DisplayText.Missing;
    private string _confidence = DisplayText.Missing;
    private string _samples = DisplayText.Missing;

    private bool _hasData;
    private double _lastValue;
    private double _lastConfidence;
    private long _lastSamples;

    /// <param name="name">Parameter name including its unit, e.g. "G (1/m)".</param>
    /// <param name="format">.NET number format of the value.</param>
    /// <param name="toolTip">What the parameter means.</param>
    public ParameterItem(string name, string format, string toolTip)
    {
        Name = name;
        _format = format;
        ToolTip = toolTip;
    }

    public string Name { get; }

    public string ToolTip { get; }

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public string Confidence
    {
        get => _confidence;
        private set => SetProperty(ref _confidence, value);
    }

    public string Samples
    {
        get => _samples;
        private set => SetProperty(ref _samples, value);
    }

    /// <summary>Updates the row. Pass NaN for an unknown value or confidence.</summary>
    /// <param name="value">Effective value.</param>
    /// <param name="source">Constant source text (see <see cref="DisplayText.Source"/>).</param>
    /// <param name="confidence">0..1, or NaN when the learner does not report one.</param>
    /// <param name="samples">Learning samples collected.</param>
    public void Update(double value, string source, double confidence, long samples)
    {
        Source = source;
        if (_hasData && value.Equals(_lastValue) && confidence.Equals(_lastConfidence) && samples == _lastSamples)
        {
            return;
        }

        _hasData = true;
        _lastValue = value;
        _lastConfidence = confidence;
        _lastSamples = samples;
        Value = DisplayText.Number(value, _format);
        Confidence = DisplayText.Percent(confidence);
        Samples = samples.ToString(DisplayText.Culture);
    }
}
