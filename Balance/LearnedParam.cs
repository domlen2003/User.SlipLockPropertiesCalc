namespace User.SlipLockPropertiesCalc.Balance;

/// <summary>A learned scalar with its confidence (0..1) and sample count. Value is NaN until learned.</summary>
public sealed class LearnedParam
{
    public double Value = double.NaN;
    public double Confidence;
    public long Samples;

    public void Clear()
    {
        Value = double.NaN;
        Confidence = 0;
        Samples = 0;
    }

    public void CopyTo(LearnedParam target)
    {
        target.Value = Value;
        target.Confidence = Confidence;
        target.Samples = Samples;
    }
}
