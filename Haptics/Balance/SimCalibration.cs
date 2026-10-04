namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// Per-sim sign/unit calibration discovered at runtime by the estimator (spec: "auto-verify sign conventions").
/// Persisted in <c>HapticsSettings.BalanceCalibration[simKey]</c> so it is not re-learned every session.
/// </summary>
public sealed class SimCalibration
{
    /// <summary>+1 or -1: multiplies the adapter's steering angle so that sign(theta) matches sign(yaw rate).</summary>
    public int SteeringSign = 1;

    /// <summary>True once the steering sign was confirmed or corrected from ≥ 200 samples.</summary>
    public bool SteeringSignVerified;

    /// <summary>+1 or -1: multiplies the adapter's longitudinal velocity so forward driving is positive.</summary>
    public int ForwardSign = 1;

    public bool ForwardSignVerified;

    /// <summary>Whether the sim's vertical acceleration includes gravity (null = not yet detected).</summary>
    public bool? GravityIncluded;

    public void CopyTo(SimCalibration target)
    {
        target.SteeringSign = SteeringSign;
        target.SteeringSignVerified = SteeringSignVerified;
        target.ForwardSign = ForwardSign;
        target.ForwardSignVerified = ForwardSignVerified;
        target.GravityIncluded = GravityIncluded;
    }

    /// <summary>Repairs invalid values after deserialization.</summary>
    public void Normalize()
    {
        if (SteeringSign != -1)
        {
            SteeringSign = 1;
        }

        if (ForwardSign != -1)
        {
            ForwardSign = 1;
        }
    }
}
