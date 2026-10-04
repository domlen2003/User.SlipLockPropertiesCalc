namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// Per-car manual overrides (spec 5.5). Null = not overridden. Persisted in the car profile.
/// Mutated only on the data thread (UI edits are marshalled through the host command queue).
/// </summary>
public sealed class BalanceOverrides
{
    public double? SteeringRatio;
    public double? WheelbaseM;
    public double? G;
    public double? K;
    public double? Theta0Deg;
    public double? TauYawS;

    /// <summary>Manually selected class preset; null = auto-detect from the car class.</summary>
    public BalanceClassPreset? ClassPreset;

    public double? Get(BalanceOverrideKind kind)
    {
        switch (kind)
        {
            case BalanceOverrideKind.SteeringRatio:
                return SteeringRatio;
            case BalanceOverrideKind.WheelbaseM:
                return WheelbaseM;
            case BalanceOverrideKind.G:
                return G;
            case BalanceOverrideKind.K:
                return K;
            case BalanceOverrideKind.Theta0Deg:
                return Theta0Deg;
            case BalanceOverrideKind.TauYawS:
                return TauYawS;
            default:
                return null;
        }
    }

    public void Set(BalanceOverrideKind kind, double? value)
    {
        switch (kind)
        {
            case BalanceOverrideKind.SteeringRatio:
                SteeringRatio = value;
                break;
            case BalanceOverrideKind.WheelbaseM:
                WheelbaseM = value;
                break;
            case BalanceOverrideKind.G:
                G = value;
                break;
            case BalanceOverrideKind.K:
                K = value;
                break;
            case BalanceOverrideKind.Theta0Deg:
                Theta0Deg = value;
                break;
            case BalanceOverrideKind.TauYawS:
                TauYawS = value;
                break;
        }
    }

    public void CopyTo(BalanceOverrides target)
    {
        target.SteeringRatio = SteeringRatio;
        target.WheelbaseM = WheelbaseM;
        target.G = G;
        target.K = K;
        target.Theta0Deg = Theta0Deg;
        target.TauYawS = TauYawS;
        target.ClassPreset = ClassPreset;
    }
}
