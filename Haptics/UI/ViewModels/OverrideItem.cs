using System;
using DivebombLogistics.Core;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.UI;
using DivebombLogistics.UI.ViewModels;

namespace DivebombLogistics.Haptics.UI.ViewModels;

/// <summary>
/// A per-car manual override of one vehicle-model parameter (checkbox + value). While the override is off the
/// slider follows the value currently in use (<see cref="Suggest"/>), so ticking the box starts from that value
/// instead of an arbitrary default. User edits go to the change callback (→ host); <see cref="Load"/> does not.
/// </summary>
public sealed class OverrideItem : ObservableObject
{
    private const string InUsePrefix = "In use: ";
    private const string SourceOpen = " (";
    private const string SourceClose = ")";

    private readonly Action<OverrideItem> _changed;
    private bool _isEnabled;
    private double _value;
    private string _inUseText = string.Empty;

    private bool _hasInUse;
    private double _lastInUseValue;
    private string _lastInUseSource;

    /// <param name="kind">Which override this row edits.</param>
    /// <param name="title">Slider caption including the unit.</param>
    /// <param name="minimum">Slider minimum (plausible physical range).</param>
    /// <param name="maximum">Slider maximum.</param>
    /// <param name="step">Tick/snap and number-box increment.</param>
    /// <param name="format">.NET number format.</param>
    /// <param name="initialValue">Value shown before anything is known.</param>
    /// <param name="help">Tooltip text.</param>
    /// <param name="changed">Called after the user toggled the override or changed its value while enabled.</param>
    public OverrideItem(
        BalanceOverrideKind kind,
        string title,
        double minimum,
        double maximum,
        double step,
        string format,
        double initialValue,
        string help,
        Action<OverrideItem> changed)
    {
        Kind = kind;
        Title = title;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Format = format;
        Help = help;
        _value = MathUtil.Clamp(initialValue, minimum, maximum);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    public BalanceOverrideKind Kind { get; }

    public string Title { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    public string Format { get; }

    public string Help { get; }

    /// <summary>Override active (two-way bound to the slider checkbox).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _changed(this);
            }
        }
    }

    /// <summary>Override value (two-way bound to the slider).</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (!MathUtil.IsFinite(value))
            {
                return;
            }

            double stored = Math.Round(MathUtil.Clamp(value, Minimum, Maximum), SliderItem.StoredDecimals);
            if (SetProperty(ref _value, stored) && _isEnabled)
            {
                _changed(this);
            }
        }
    }

    /// <summary>The value to store for this car: null while the override is off.</summary>
    public double? OverrideValue => _isEnabled ? _value : (double?)null;

    /// <summary>Effective value and its source, e.g. "In use: 0.0261 (learned)"; empty when not applicable.</summary>
    public string InUseText
    {
        get => _inUseText;
        private set => SetProperty(ref _inUseText, value);
    }

    /// <summary>Shows the stored override of the current car (no callback).</summary>
    /// <param name="value">Stored override, null = off.</param>
    public void Load(double? value)
    {
        if (_isEnabled != value.HasValue)
        {
            _isEnabled = value.HasValue;
            OnPropertyChanged(nameof(IsEnabled));
        }

        if (value.HasValue)
        {
            SetValueSilently(value.Value);
        }
    }

    /// <summary>While the override is off, moves the slider to <paramref name="value"/> (the value in use).</summary>
    public void Suggest(double value)
    {
        if (!_isEnabled)
        {
            SetValueSilently(value);
        }
    }

    /// <summary>Updates <see cref="InUseText"/>; <paramref name="source"/> must be a constant display string.</summary>
    public void SetInUse(double value, string source)
    {
        if (_hasInUse && value.Equals(_lastInUseValue) && ReferenceEquals(source, _lastInUseSource))
        {
            return;
        }

        _hasInUse = true;
        _lastInUseValue = value;
        _lastInUseSource = source;
        InUseText = string.Concat(InUsePrefix, DisplayText.Number(value, Format), SourceOpen, source, SourceClose);
    }

    /// <summary>Shows a fixed explanation instead of an effective value (e.g. for inputs that only derive G).</summary>
    public void SetInUseNote(string note)
    {
        _hasInUse = false;
        InUseText = note ?? string.Empty;
    }

    private void SetValueSilently(double value)
    {
        if (!MathUtil.IsFinite(value))
        {
            return;
        }

        double clamped = MathUtil.Clamp(value, Minimum, Maximum);
        if (!clamped.Equals(_value))
        {
            _value = clamped;
            OnPropertyChanged(nameof(Value));
        }
    }
}
