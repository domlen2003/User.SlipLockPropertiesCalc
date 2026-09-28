using System;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.UI.ViewModels;

/// <summary>
/// A global numeric setting edited with a SimHub <c>TitledSlider</c> (debug view). Reads the setting through a
/// delegate and hands edits to a write delegate, which the view model queues to the data thread (the settings object
/// is owned there). The item keeps the value it wrote, so the slider shows the edit immediately although the queued
/// write lands a frame later; <see cref="Refresh"/> re-reads the setting, <see cref="ShowValue"/> shows a value that
/// was written elsewhere (e.g. reset to defaults) before the data thread applied it.
/// </summary>
public sealed class SliderItem : ObservableObject
{
    /// <summary>
    /// Decimals kept when storing a value: slider snapping computes <c>min + n·step</c> in floating point
    /// (e.g. 0.030000000000000002); rounding keeps the settings file clean without affecting any tuning step.
    /// </summary>
    internal const int StoredDecimals = 6;

    private readonly Func<double> _getter;
    private readonly Action<double> _setter;
    private readonly Action _changed;
    private double _value;

    /// <param name="title">Slider caption including the unit, e.g. "Slip attack (ms)".</param>
    /// <param name="minimum">Slider minimum.</param>
    /// <param name="maximum">Slider maximum.</param>
    /// <param name="step">Tick/snap and number-box increment.</param>
    /// <param name="format">.NET number format for the number box, e.g. "0" or "0.00".</param>
    /// <param name="defaultValue">Value restored by the slider's reset button.</param>
    /// <param name="help">Tooltip text (what the setting does).</param>
    /// <param name="getter">Reads the current setting.</param>
    /// <param name="setter">Writes (or queues the write of) the setting.</param>
    /// <param name="changed">Called after every accepted edit (optional).</param>
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
        Action changed)
    {
        Title = title;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Format = format;
        DefaultValue = defaultValue;
        Help = help;
        _getter = getter ?? throw new ArgumentNullException(nameof(getter));
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
        _changed = changed;
        _value = getter();
    }

    public string Title { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    public string Format { get; }

    public double DefaultValue { get; }

    public string Help { get; }

    /// <summary>The live setting. Non-finite input is ignored; values are clamped to the slider range.</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (!MathUtil.IsFinite(value))
            {
                return;
            }

            double clamped = Math.Round(MathUtil.Clamp(value, Minimum, Maximum), StoredDecimals);
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

    /// <summary>Re-reads the setting (it was changed elsewhere and already applied).</summary>
    public void Refresh()
    {
        _value = _getter();
        OnPropertyChanged(nameof(Value));
    }

    /// <summary>Shows <paramref name="value"/> without writing it (the caller queued the matching write itself).</summary>
    public void ShowValue(double value)
    {
        _value = value;
        OnPropertyChanged(nameof(Value));
    }
}
