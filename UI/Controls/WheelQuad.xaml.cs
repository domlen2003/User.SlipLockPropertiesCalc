using System.Windows;
using System.Windows.Controls;
using DivebombLogistics.Haptics.UI.ViewModels;

namespace DivebombLogistics.UI.Controls;

/// <summary>
/// Per-wheel display: a title and four vertical bars (FL FR / RL RR) with their values.
/// Replaces the v1 copy-pasted 2×2 ProgressBar grids.
/// </summary>
public partial class WheelQuad : UserControl
{
    public const double DefaultMaximum = 100.0;
    public const string DefaultValueFormat = "0";
    public const double DefaultBarWidth = 26.0;
    public const double DefaultBarHeight = 40.0;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(WheelQuad), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(WheelValues), typeof(WheelQuad), new PropertyMetadata(null));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(WheelQuad), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(WheelQuad), new PropertyMetadata(DefaultMaximum));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(WheelQuad), new PropertyMetadata(DefaultValueFormat));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(double), typeof(WheelQuad), new PropertyMetadata(DefaultBarWidth));

    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(
        nameof(BarHeight), typeof(double), typeof(WheelQuad), new PropertyMetadata(DefaultBarHeight));

    public WheelQuad()
    {
        InitializeComponent();
    }

    /// <summary>Caption above the bars (hidden when empty).</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The four values to show.</summary>
    public WheelValues Values
    {
        get => (WheelValues)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Bar minimum (default 0; −100 for signed raw slip).</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Bar maximum (default 100).</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>.NET number format of the value texts (default "0").</summary>
    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    /// <summary>Bar width in pixels (default 26).</summary>
    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    /// <summary>Bar height in pixels (default 40).</summary>
    public double BarHeight
    {
        get => (double)GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }
}
