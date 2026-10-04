namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// Persisted learner state for one car (spec 5.4): the complete baseline of <see cref="BalanceLearner"/>, enough to
/// resume learning where it stopped. Plain JSON DTO (public fields and arrays only, no logic); written by
/// <see cref="BalanceLearner.SaveTo"/>, read by <see cref="BalanceLearner.Load"/>.
/// </summary>
/// <remarks>
/// <para>
/// The summary block is for people reading or sharing the file; <see cref="BalanceLearner.Load"/> ignores it and
/// recomputes every result from the accumulators. Null arrays load as empty data and short arrays partially (missing
/// bins are empty), as in older or hand-edited files; invalid parts (negative counts, non-finite sums) are reset;
/// a file with a newer <see cref="SchemaVersion"/> is ignored.
/// </para>
/// <para>
/// Json.NET formats doubles with "R", which on .NET Framework x64 can be off by one ulp for a few values in a
/// million. A reloaded learner therefore matches the saved one to ~1e-15 relative, not always bit for bit.
/// </para>
/// </remarks>
public sealed class BalanceLearnedState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion = CurrentSchemaVersion;

    // ---- Summary (informational, ignored on load; null = not learned) ----

    /// <summary>Steering gain G = 1 / (steering ratio · wheelbase) in 1/m.</summary>
    public double? G;

    public double GConfidence;

    /// <summary>Understeer factor K in s²/m².</summary>
    public double? K;

    public double KConfidence;

    /// <summary>Steering offset in steering-wheel degrees.</summary>
    public double? Theta0Deg;

    public double Theta0Confidence;

    /// <summary>Steering→yaw lag in seconds.</summary>
    public double? TauYawS;

    public double TauYawConfidence;

    /// <summary>98th percentile of |ay| in m/s².</summary>
    public double? AyMax;

    public double AyMaxConfidence;

    /// <summary>Near-peak tyre slip angle in the sim's native unit.</summary>
    public double? AlphaPeak;

    public double AlphaPeakConfidence;

    // ---- Steering-gain fit: y = v·θeff/r = a + b·x with x = v²/1000 (G = 1/a, K = b/(1000·a)) ----

    /// <summary>Accepted model samples (lifetime).</summary>
    public long FitSamples;

    /// <summary>Samples rejected as outliers (lifetime, diagnostic).</summary>
    public long FitRejected;

    /// <summary>Forgetting-weighted least-squares sums (weights ≈ 1/ŷ², so residuals are relative).</summary>
    public double FitSumW;

    public double FitSumWX;
    public double FitSumWXX;
    public double FitSumWY;
    public double FitSumWXY;
    public double FitSumWYY;

    /// <summary>Robust scale of the standardized relative residual (outlier gate).</summary>
    public double FitResidualScale;

    /// <summary>Residuals folded into <see cref="FitResidualScale"/> (the outlier gate opens after 50).</summary>
    public long FitResidualSamples;

    /// <summary>Forgetting-weighted count, Σv and Σv² of accepted samples (speed spread for trusting K).</summary>
    public double FitSpeedWeight;

    public double FitSpeedSum;
    public double FitSpeedSquareSum;

    // ---- Steering offset θ0 (radians, EMA of straight-line steering) ----
    public double Theta0Ema;
    public long Theta0Samples;

    // ---- Histograms (integer counts, trailing empty bins trimmed) ----

    /// <summary>|ay| counts: 0.25 m/s² bins from 0 to 50 m/s².</summary>
    public int[] AyHistogram;

    /// <summary>Lateral samples offered (lifetime; the histogram itself is halved for slow forgetting).</summary>
    public long AySamples;

    /// <summary>Per |ay|/ay_max bin (10), |β| counts: 0.1° bins from 0 to 20°.</summary>
    public int[][] BetaHistograms;

    public long BetaSamples;

    /// <summary>max(α_front, α_rear) counts: 120 log-spaced bins from 1e-4 to 100 (native unit).</summary>
    public int[] AlphaHistogram;

    public long AlphaSamples;

    // ---- Steering→yaw lag ----

    /// <summary>Best τ (s) of the most recent turn-in events, oldest first (at most 50).</summary>
    public double[] TauEvents;

    /// <summary>Accepted turn-in events (lifetime).</summary>
    public long TauEventsTotal;
}
