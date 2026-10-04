using System;
using DivebombLogistics.Core;

namespace DivebombLogistics.UI.ViewModels;

/// <summary>
/// A global numeric setting edited with a SimHub <c>TitledSlider</c> (shared by the Haptics and Speed Dial tabs,
/// template <c>SliderItemTemplate</c> in <see cref="SharedStyles"/>). Edits go to a write delegate, which the page
/// queues to the data thread (the settings object is owned there). The item keeps the value it wrote, so the slider
/// shows the edit immediately although the queued write lands a frame later; <see cref="Refresh"/> re-reads the
/// setting through the optional getter, <see cref="ShowValue"/> shows a value written elsewhere (stored settings,
/// reset to defaults) without writing it.
/// </summary>
public sealed class SliderItem : ObservableObject
{
    /// <summary>
    /// Default decimals kept when storing a value: slider snapping computes <c>min + n·step</c> in floating point
    /// (e.g. 0.030000000000000002); rounding keeps the settings file clean without affecting any tuning step.
    /// </summary>
    internal const int StoredDecimals = 6;

    private readonly Func<double> _getter;
    private readonly Action<double> _setter;
    private readonly Action _changed;
    private readonly int _decimals;
    private double _value;

    /// <param name="title">Slider caption including the unit, e.g. "Slip attack (ms)".</param>
    /// <param name="minimum">Slider minimum.</param>
    /// <param name="maximum">Slider maximum.</param>
    /// <param name="step">Tick/snap and number-box increment.</param>
    /// <param name="format">.NET number format for the number box, e.g. "0" or "0.00".</param>
    /// <param name="defaultValue">Value restored by the slider's reset button (and the start value without a getter).</param>
    /// <param name="help">Tooltip text (what the setting does).</param>
    /// <param name="getter">Reads the current setting; null = start at <paramref name="defaultValue"/> and rely on <see cref="ShowValue"/>.</param>
    /// <param name="setter">Writes (or queues the write of) the setting.</param>
    /// <param name="changed">Called after every accepted edit (optional).</param>
    /// <param name="decimals">Decimals a stored value is rounded to (0 for whole-number settings).</param>
    public SliderItem(
        string title,
        double minimum,
        double maximum,
        double step,
        string format,
        double defaultValue,
        string help,
        Func<double> getter,
        Action<double> setter,
        Action changed,
        int decimals = StoredDecimals)
    {
        Title = title;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Format = format;
        DefaultValue = defaultValue;
        Help = help;
        _getter = getter;
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
        _changed = changed;
        _decimals = Math.Max(0, Math.Min(15, decimals));
        _value = getter != null ? getter() : defaultValue;
    }

    /// <summary>Caption including the unit.</summary>
    public string Title { get; }

    /// <summary>Slider minimum.</summary>
    public double Minimum { get; }

    /// <summary>Slider maximum.</summary>
    public double Maximum { get; }

    /// <summary>Snap step.</summary>
    public double Step { get; }

    /// <summary>Number format of the slider's number box.</summary>
    public string Format { get; }

    /// <summary>Value of the reset button.</summary>
    public double DefaultValue { get; }

    /// <summary>Tooltip text.</summary>
    public string Help { get; }

    /// <summary>The live setting (two-way). Non-finite input is ignored; values are rounded and clamped to the range.</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (!MathUtil.IsFinite(value))
            {
                return;
            }

            double clamped = Math.Round(MathUtil.Clamp(value, Minimum, Maximum), _decimals);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            _setter(clamped);
            _changed?.Invoke();
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads the setting through the getter (it was changed elsewhere and already applied); no-op without a getter.</summary>
    public void Refresh()
    {
        if (_getter == null)
        {
            return;
        }

        _value = _getter();
        OnPropertyChanged(nameof(Value));
    }

    /// <summary>Shows <paramref name="value"/> without writing it (stored value, or the caller queued the matching write itself).</summary>
    public void ShowValue(double value)
    {
        if (MathUtil.IsFinite(value))
        {
            SetProperty(ref _value, value, nameof(Value));
        }
    }
}
