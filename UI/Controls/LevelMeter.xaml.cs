using System.Windows;
using System.Windows.Controls;

namespace DivebombLogistics.UI.Controls;

/// <summary>Compact horizontal level bar (0..<see cref="Maximum"/>) with the value printed next to it.</summary>
public partial class LevelMeter : UserControl
{
    public const double DefaultMaximum = 100.0;
    public const string DefaultValueFormat = "0";
    public const double DefaultBarHeight = 10.0;
    public const double DefaultValueWidth = 40.0;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelMeter), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LevelMeter), new PropertyMetadata(DefaultMaximum));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(LevelMeter), new PropertyMetadata(DefaultValueFormat));

    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(
        nameof(BarHeight), typeof(double), typeof(LevelMeter), new PropertyMetadata(DefaultBarHeight));

    public static readonly DependencyProperty ValueWidthProperty = DependencyProperty.Register(
        nameof(ValueWidth), typeof(double), typeof(LevelMeter), new PropertyMetadata(DefaultValueWidth));

    public LevelMeter()
    {
        InitializeComponent();
    }

    /// <summary>Displayed level (the bar starts at 0).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Level at which the bar is full (default 100).</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>.NET number format of the value text (default "0"); NaN shows "-".</summary>
    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    /// <summary>Bar thickness in pixels (default 10).</summary>
    public double BarHeight
    {
        get => (double)GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    /// <summary>Minimum width of the value text so bars in a list line up (default 40).</summary>
    public double ValueWidth
    {
        get => (double)GetValue(ValueWidthProperty);
        set => SetValue(ValueWidthProperty, value);
    }
}
