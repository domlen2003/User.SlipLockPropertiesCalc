using System;
using DivebombLogistics.Core;
using DivebombLogistics.Haptics.Balance;

namespace DivebombLogistics.Haptics.Settings;

/// <summary>
/// Everything remembered per car (spec 5.4): the simple-view sensitivities, balance overrides and learned model.
/// Stored as JSON at <c>PluginsData\DLP\Haptics\Cars\&lt;SimKey&gt;\&lt;CarKey&gt;_&lt;fnv1a8&gt;.json</c> (<c>CarFileNaming</c>).
/// Sensitivities are percent (100 = v1 behavior). Owned by the data thread: UI edits arrive through the command queue.
/// </summary>
public sealed class CarProfile
{
    public const int CurrentSchemaVersion = 1;
    public const double MinSensitivity = 10.0;
    public const double MaxSensitivity = 500.0;
    public const double DefaultSensitivity = 100.0;

    public int SchemaVersion = CurrentSchemaVersion;

    /// <summary>SimHub game name, e.g. "LMU".</summary>
    public string SimKey = string.Empty;

    /// <summary>Stable car-model key (see CarIdentityResolver), e.g. "Ligier JS P320".</summary>
    public string CarKey = string.Empty;

    /// <summary>Human-readable car name for the UI.</summary>
    public string DisplayName = string.Empty;

    /// <summary>Sim-reported car class, e.g. "LMP3".</summary>
    public string CarClass = string.Empty;

    public double SlipSensitivity = DefaultSensitivity;
    public double LockSensitivity = DefaultSensitivity;
    public double UndersteerSensitivity = DefaultSensitivity;
    public double OversteerSensitivity = DefaultSensitivity;

    public BalanceOverrides Overrides = new BalanceOverrides();

    /// <summary>Freeze the persisted baseline (session adaptation still runs).</summary>
    public bool LearningLocked;

    public BalanceLearnedState Learned = new BalanceLearnedState();

    public DateTime LastUpdatedUtc;

    /// <summary>Gets a sensitivity in percent.</summary>
    public double GetSensitivity(SensitivityKind kind)
    {
        switch (kind)
        {
            case SensitivityKind.Slip:
                return SlipSensitivity;
            case SensitivityKind.Lock:
                return LockSensitivity;
            case SensitivityKind.Understeer:
                return UndersteerSensitivity;
            case SensitivityKind.Oversteer:
                return OversteerSensitivity;
            default:
                return DefaultSensitivity;
        }
    }

    /// <summary>Sets a sensitivity in percent (clamped to [<see cref="MinSensitivity"/>, <see cref="MaxSensitivity"/>]).</summary>
    public void SetSensitivity(SensitivityKind kind, double percent)
    {
        double value = ClampSensitivity(percent);
        switch (kind)
        {
            case SensitivityKind.Slip:
                SlipSensitivity = value;
                break;
            case SensitivityKind.Lock:
                LockSensitivity = value;
                break;
            case SensitivityKind.Understeer:
                UndersteerSensitivity = value;
                break;
            case SensitivityKind.Oversteer:
                OversteerSensitivity = value;
                break;
        }
    }

    /// <summary>Repairs a freshly deserialized instance.</summary>
    public void Normalize()
    {
        SimKey ??= string.Empty;
        CarKey ??= string.Empty;
        DisplayName ??= string.Empty;
        CarClass ??= string.Empty;
        SlipSensitivity = ClampSensitivity(SlipSensitivity);
        LockSensitivity = ClampSensitivity(LockSensitivity);
        UndersteerSensitivity = ClampSensitivity(UndersteerSensitivity);
        OversteerSensitivity = ClampSensitivity(OversteerSensitivity);
        Overrides ??= new BalanceOverrides();
        Learned ??= new BalanceLearnedState();
        SchemaVersion = CurrentSchemaVersion;
    }

    public static double ClampSensitivity(double percent) =>
        MathUtil.IsFinite(percent) ? MathUtil.Clamp(percent, MinSensitivity, MaxSensitivity) : DefaultSensitivity;
}
