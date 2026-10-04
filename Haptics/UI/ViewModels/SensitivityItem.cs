using System;
using DivebombLogistics.Core;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.Settings;
using DivebombLogistics.UI;

namespace DivebombLogistics.Haptics.UI.ViewModels;

/// <summary>
/// One per-car sensitivity slider of the simple view (percent, 100 = v1 behavior) with its live signal level.
/// User edits go to the change callback (→ host); <see cref="Load"/> shows the stored value of a newly loaded car
/// without echoing it back to the host.
/// </summary>
public sealed class SensitivityItem : ObservableObject
{
    /// <summary>Slider snap step in percent.</summary>
    public const double Step = 5.0;

    private readonly Action<SensitivityKind, double> _changed;
    private double _value = CarProfile.DefaultSensitivity;
    private double _level;
    private bool _isAvailable = true;
    private string _note = string.Empty;

    /// <param name="kind">Which sensitivity this row edits.</param>
    /// <param name="title">Slider caption.</param>
    /// <param name="help">One-line help shown under the slider and as tooltip.</param>
    /// <param name="levelToolTip">Explains which signal the live meter shows.</param>
    /// <param name="changed">Called with the new percent after a user edit.</param>
    public SensitivityItem(SensitivityKind kind, string title, string help, string levelToolTip, Action<SensitivityKind, double> changed)
    {
        Kind = kind;
        Title = title;
        Help = help;
        LevelToolTip = levelToolTip;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    public SensitivityKind Kind { get; }

    public string Title { get; }

    public string Help { get; }

    public string LevelToolTip { get; }

    public double Minimum => CarProfile.MinSensitivity;

    public double Maximum => CarProfile.MaxSensitivity;

    public double DefaultValue => CarProfile.DefaultSensitivity;

    public double SnapStep => Step;

    /// <summary>Sensitivity in percent (two-way bound to the slider).</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (!MathUtil.IsFinite(value))
            {
                return;
            }

            if (SetProperty(ref _value, CarProfile.ClampSensitivity(value)))
            {
                _changed(Kind, _value);
            }
        }
    }

    /// <summary>Live output level 0..100 of the channel this sensitivity scales.</summary>
    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    /// <summary>False when the slider has no effect in the current sim (e.g. no understeer/oversteer source).</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    /// <summary>Situational explanation shown instead of <see cref="Help"/> (empty when none applies).</summary>
    public string Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HintText));
            }
        }
    }

    /// <summary>Line under the slider: the situational <see cref="Note"/> if any, else <see cref="Help"/>.</summary>
    public string HintText => _note.Length > 0 ? _note : Help;

    /// <summary>Updates availability and the situational note (pass constant strings: called on every refresh).</summary>
    public void SetState(bool available, string note)
    {
        IsAvailable = available;
        Note = note;
    }

    /// <summary>Shows the stored sensitivity of the current car (no callback).</summary>
    public void Load(double percent)
    {
        double value = CarProfile.ClampSensitivity(percent);
        if (!value.Equals(_value))
        {
            _value = value;
            OnPropertyChanged(nameof(Value));
        }
    }

    /// <summary>Updates the live meter (non-finite values show as 0).</summary>
    public void SetLevel(double level) => Level = MathUtil.FiniteOr(level, 0.0);
}
