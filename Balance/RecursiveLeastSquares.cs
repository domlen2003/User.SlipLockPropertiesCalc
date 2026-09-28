using System;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Balance;

/// <summary>
/// Exponentially weighted recursive least squares for the straight line <c>y = a + b·x</c>, kept in information
/// (normal-equation) form: every sample updates the forgetting-weighted sums Σw, Σwx, Σwx², Σwy, Σwxy and Σwy², and
/// the 2×2 system is solved in closed form.
/// </summary>
/// <remarks>
/// <para>
/// Why the information form instead of the textbook covariance update: the parameter covariance
/// <c>P = (R + R0)⁻¹</c> is symmetric positive definite and bounded by the prior covariance <c>R0⁻¹</c> by
/// construction, so it cannot wind up while x hardly varies (a car driven at one speed band) and needs no
/// re-symmetrization or re-conditioning. The state is six plain sums that persist as JSON, and constrained fits
/// (b fixed as a multiple of a) and residual sums of squares are evaluated exactly from the same sums.
/// </para>
/// <para>
/// A weak, constant Gaussian prior (mean a0/b0, standard deviations σa/σb) regularizes directions the data does not
/// excite. Unlike the P0 of the covariance form it does not decay away, which is what keeps P bounded.
/// Callers scale x so that a and b have similar magnitudes; the prior standard deviations are in those units.
/// All members are allocation-free.
/// </para>
/// </remarks>
internal sealed class RecursiveLeastSquares
{
    /// <summary>Tolerance for the Cauchy–Schwarz check (Σw·Σwx² ≥ (Σwx)²) on loaded sums.</summary>
    private const double LoadConsistencyTolerance = 1e-9;

    private readonly double priorInformationA;
    private readonly double priorInformationB;
    private double priorA;
    private double priorB;

    private double sumW;
    private double sumWX;
    private double sumWXX;
    private double sumWY;
    private double sumWXY;
    private double sumWYY;

    // Regularized solution and determinant of (R + R0), refreshed after every change.
    private double a;
    private double b;
    private double determinant;

    /// <param name="priorA">Prior mean of the intercept.</param>
    /// <param name="priorB">Prior mean of the slope.</param>
    /// <param name="priorStdA">Prior standard deviation of the intercept (&gt; 0; large = weak prior).</param>
    /// <param name="priorStdB">Prior standard deviation of the slope (&gt; 0; large = weak prior).</param>
    public RecursiveLeastSquares(double priorA, double priorB, double priorStdA, double priorStdB)
    {
        if (!(priorStdA > 0) || !(priorStdB > 0) || !MathUtil.IsFinite(priorStdA) || !MathUtil.IsFinite(priorStdB))
        {
            throw new ArgumentOutOfRangeException(nameof(priorStdA), "prior standard deviations must be finite and positive");
        }

        priorInformationA = 1.0 / (priorStdA * priorStdA);
        priorInformationB = 1.0 / (priorStdB * priorStdB);
        this.priorA = MathUtil.FiniteOr(priorA, 0.0);
        this.priorB = MathUtil.FiniteOr(priorB, 0.0);
        Solve();
    }

    /// <summary>Intercept of the current (prior-regularized) solution.</summary>
    public double A => a;

    /// <summary>Slope of the current (prior-regularized) solution.</summary>
    public double B => b;

    /// <summary>Forgetting-weighted sum of sample weights (0 when no data).</summary>
    public double SumW => sumW;

    public double SumWX => sumWX;

    public double SumWXX => sumWXX;

    public double SumWY => sumWY;

    public double SumWXY => sumWXY;

    public double SumWYY => sumWYY;

    /// <summary>True when no sample has been accumulated since the last <see cref="Clear"/>.</summary>
    public bool IsEmpty => !(sumW > 0);

    /// <summary>Forgets all data; the solution returns to the prior mean.</summary>
    public void Clear()
    {
        sumW = 0;
        sumWX = 0;
        sumWXX = 0;
        sumWY = 0;
        sumWXY = 0;
        sumWYY = 0;
        Solve();
    }

    /// <summary>Moves the prior mean (non-finite values are ignored). The prior strength is fixed.</summary>
    public void SetPriorMean(double meanA, double meanB)
    {
        if (!MathUtil.IsFinite(meanA) || !MathUtil.IsFinite(meanB))
        {
            return;
        }

        priorA = meanA;
        priorB = meanB;
        Solve();
    }

    /// <summary>
    /// Adds one weighted sample after decaying the existing sums by <paramref name="lambda"/>.
    /// Returns false (and leaves the state untouched) for non-finite input, weight &lt;= 0 or lambda outside (0, 1].
    /// </summary>
    public bool Update(double x, double y, double weight, double lambda)
    {
        if (!MathUtil.IsFinite(x) || !MathUtil.IsFinite(y) || !MathUtil.IsFinite(weight) || !(weight > 0)
            || !(lambda > 0) || lambda > 1.0)
        {
            return false;
        }

        double wx = weight * x;
        double wy = weight * y;
        sumW = (lambda * sumW) + weight;
        sumWX = (lambda * sumWX) + wx;
        sumWXX = (lambda * sumWXX) + (wx * x);
        sumWY = (lambda * sumWY) + wy;
        sumWXY = (lambda * sumWXY) + (wx * y);
        sumWYY = (lambda * sumWYY) + (wy * y);

        // Unreachable with finite, validated input; a reset beats propagating NaN into every later estimate.
        if (!SumsAreFinite())
        {
            Clear();
            return false;
        }

        Solve();
        return true;
    }

    /// <summary>Prediction of the regularized solution at <paramref name="x"/>.</summary>
    public double Predict(double x) => a + (b * x);

    /// <summary>
    /// <c>φᵀ·P·φ</c> for <c>φ = [1, x]</c>: the parameter uncertainty at <paramref name="x"/> relative to one unit of
    /// sample weight. Multiplied by a sample's weight it tells how much the fit itself (rather than the sample) may be
    /// off there: small where the data is dense, large when extrapolating along a direction only the prior covers.
    /// </summary>
    public double PredictionVariance(double x)
    {
        double r11 = sumW + priorInformationA;
        double r12 = sumWX;
        double r22 = sumWXX + priorInformationB;
        return (r22 - (2.0 * x * r12) + (x * x * r11)) / determinant;
    }

    /// <summary>
    /// Least-squares intercept of the constrained line <c>y = a·(1 + ratio·x)</c> (slope fixed at ratio·a), from the
    /// data alone (no prior). False when there is no data or the fit is degenerate.
    /// </summary>
    public bool TrySolveWithFixedRatio(double ratio, out double intercept)
    {
        double denominator = sumW + (2.0 * ratio * sumWX) + (ratio * ratio * sumWXX);
        if (!(denominator > 0) || !MathUtil.IsFinite(ratio))
        {
            intercept = double.NaN;
            return false;
        }

        intercept = (sumWY + (ratio * sumWXY)) / denominator;
        return MathUtil.IsFinite(intercept);
    }

    /// <summary>
    /// Forgetting-weighted residual sum of squares <c>Σw·(y − a − b·x)²</c> of the accumulated data for the given
    /// line (0 without data; clamped at 0 against rounding).
    /// </summary>
    public double ResidualSumOfSquares(double intercept, double slope)
    {
        double squares = sumWYY
            - (2.0 * ((intercept * sumWY) + (slope * sumWXY)))
            + (intercept * intercept * sumW)
            + (2.0 * intercept * slope * sumWX)
            + (slope * slope * sumWXX);
        return Math.Max(0.0, squares);
    }

    /// <summary>Replaces the data with <paramref name="scale"/> × the data of <paramref name="source"/> (prior unchanged).</summary>
    public void CopyScaledFrom(RecursiveLeastSquares source, double scale)
    {
        if (source == null || !(scale >= 0) || !MathUtil.IsFinite(scale))
        {
            Clear();
            return;
        }

        sumW = source.sumW * scale;
        sumWX = source.sumWX * scale;
        sumWXX = source.sumWXX * scale;
        sumWY = source.sumWY * scale;
        sumWXY = source.sumWXY * scale;
        sumWYY = source.sumWYY * scale;
        Solve();
    }

    /// <summary>
    /// Restores persisted sums. Invalid or inconsistent values (non-finite, negative weights, Σw·Σwx² &lt; (Σwx)²)
    /// clear the state and return false.
    /// </summary>
    public bool TryLoad(double w, double wx, double wxx, double wy, double wxy, double wyy)
    {
        bool finite = MathUtil.IsFinite(w) && MathUtil.IsFinite(wx) && MathUtil.IsFinite(wxx)
            && MathUtil.IsFinite(wy) && MathUtil.IsFinite(wxy) && MathUtil.IsFinite(wyy);
        bool consistent = finite && w >= 0 && wxx >= 0 && wyy >= 0
            && (w * wxx) >= ((wx * wx) * (1.0 - LoadConsistencyTolerance)) - LoadConsistencyTolerance;
        if (!consistent)
        {
            Clear();
            return false;
        }

        sumW = w;
        sumWX = wx;
        sumWXX = wxx;
        sumWY = wy;
        sumWXY = wxy;
        sumWYY = wyy;
        Solve();
        return true;
    }

    private bool SumsAreFinite() =>
        MathUtil.IsFinite(sumW) && MathUtil.IsFinite(sumWX) && MathUtil.IsFinite(sumWXX)
        && MathUtil.IsFinite(sumWY) && MathUtil.IsFinite(sumWXY) && MathUtil.IsFinite(sumWYY);

    private void Solve()
    {
        // det(R + R0) written as (data spread determinant) + (prior terms): the data part is >= 0 in exact arithmetic
        // and only rounding can push it below, while the prior terms keep the total strictly positive.
        double dataDeterminant = Math.Max(0.0, (sumW * sumWXX) - (sumWX * sumWX));
        determinant = dataDeterminant
            + (priorInformationA * (sumWXX + priorInformationB))
            + (priorInformationB * sumW);

        double r11 = sumW + priorInformationA;
        double r12 = sumWX;
        double r22 = sumWXX + priorInformationB;
        double z1 = sumWY + (priorInformationA * priorA);
        double z2 = sumWXY + (priorInformationB * priorB);
        a = ((r22 * z1) - (r12 * z2)) / determinant;
        b = ((r11 * z2) - (r12 * z1)) / determinant;
    }
}
