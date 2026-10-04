using DivebombLogistics.UI;

namespace DivebombLogistics.Haptics.UI.ViewModels;

/// <summary>One corner of a <see cref="WheelValues"/> set (label + live value).</summary>
public sealed class WheelValue : ObservableObject
{
    private double _value;

    public WheelValue(string label)
    {
        Label = label;
    }

    /// <summary>Short corner label, e.g. "FL".</summary>
    public string Label { get; }

    public double Value
    {
        get => _value;
        internal set => SetProperty(ref _value, value);
    }
}
