using System;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Balance;

/// <summary>
/// Fixed-bin histogram with integer counts over a linear or logarithmic value range. Percentiles interpolate inside
/// the bin (linearly, or geometrically for log bins). Forgetting is slow and cheap: once the total exceeds
/// <see cref="ForgetThreshold"/> every bin is halved, so old driving keeps shaping the result but newer driving
/// (new tyres, setup, weather) gradually takes over. Values outside the range count in the first/last bin.
/// Allocation-free except for the persistence helper <see cref="ToTrimmedArray"/>.
/// </summary>
internal sealed class Histogram
{
    private readonly int[] counts;
    private readonly bool logarithmic;

    // Range and bin width in value units (linear) or log10 units (logarithmic).
    private readonly double rangeMin;
    private readonly double binWidth;

    private long total;

    private Histogram(int binCount, double rangeMin, double rangeMax, bool logarithmic, long forgetThreshold)
    {
        if (binCount < 1 || !(rangeMax > rangeMin) || forgetThreshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(binCount), "invalid histogram layout");
        }

        counts = new int[binCount];
        this.logarithmic = logarithmic;
        this.rangeMin = rangeMin;
        binWidth = (rangeMax - rangeMin) / binCount;
        ForgetThreshold = forgetThreshold;
    }

    /// <summary>Total count above which every bin is halved.</summary>
    public long ForgetThreshold { get; }

    public int BinCount => counts.Length;

    /// <summary>Current (possibly halved) number of samples in the histogram.</summary>
    public long Total => total;

    /// <summary>Linear bins of equal width covering [min, max].</summary>
    public static Histogram Linear(double min, double max, int binCount, long forgetThreshold) =>
        new Histogram(binCount, min, max, logarithmic: false, forgetThreshold);

    /// <summary>Logarithmically spaced bins covering [min, max] (both &gt; 0), for unit-agnostic magnitudes.</summary>
    public static Histogram Logarithmic(double min, double max, int binCount, long forgetThreshold)
    {
        if (!(min > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(min), "logarithmic histograms need a positive range");
        }

        return new Histogram(binCount, Math.Log10(min), Math.Log10(max), logarithmic: true, forgetThreshold);
    }

    /// <summary>Counts <paramref name="value"/>; non-finite values (and non-positive ones on log bins) are ignored.</summary>
    public bool Add(double value)
    {
        if (!MathUtil.IsFinite(value) || (logarithmic && !(value > 0)))
        {
            return false;
        }

        counts[BinOf(value)]++;
        total++;
        if (total > ForgetThreshold)
        {
            HalveAllBins();
        }

        return true;
    }

    /// <summary>
    /// Value below which <paramref name="fraction"/> (0..1) of the samples lie, interpolated inside the bin that
    /// contains the target rank. NaN when empty.
    /// </summary>
    public double Percentile(double fraction)
    {
        if (total <= 0 || !MathUtil.IsFinite(fraction))
        {
            return double.NaN;
        }

        double target = MathUtil.Clamp01(fraction) * total;
        long below = 0;
        int lastNonEmpty = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            int count = counts[i];
            if (count == 0)
            {
                continue;
            }

            if (below + count >= target)
            {
                return Position(i + ((target - below) / count));
            }

            below += count;
            lastNonEmpty = i;
        }

        // Only reachable through rounding of target at fraction 1.
        return Position(lastNonEmpty + 1);
    }

    public void Clear()
    {
        Array.Clear(counts, 0, counts.Length);
        total = 0;
    }

    /// <summary>Copy of the counts without trailing empty bins (persistence; allocates).</summary>
    public int[] ToTrimmedArray()
    {
        int length = counts.Length;
        while (length > 0 && counts[length - 1] == 0)
        {
            length--;
        }

        var copy = new int[length];
        Array.Copy(counts, copy, length);
        return copy;
    }

    /// <summary>
    /// Restores counts saved by <see cref="ToTrimmedArray"/>. Null or short arrays are tolerated (missing bins are
    /// empty); extra bins are ignored. Negative counts make the data invalid: the histogram is cleared and false
    /// returned.
    /// </summary>
    public bool Load(int[] source)
    {
        Clear();
        if (source == null)
        {
            return true;
        }

        int length = Math.Min(source.Length, counts.Length);
        long sum = 0;
        for (int i = 0; i < length; i++)
        {
            if (source[i] < 0)
            {
                Clear();
                return false;
            }

            counts[i] = source[i];
            sum += source[i];
        }

        total = sum;
        if (total > ForgetThreshold)
        {
            HalveAllBins();
        }

        return true;
    }

    private int BinOf(double value)
    {
        double position = logarithmic ? Math.Log10(value) : value;
        double bin = Math.Floor((position - rangeMin) / binWidth);
        if (bin < 0)
        {
            return 0;
        }

        return bin >= counts.Length ? counts.Length - 1 : (int)bin;
    }

    /// <summary>Value at a fractional bin position (0 = lower edge of the first bin).</summary>
    private double Position(double binPosition)
    {
        double scaled = rangeMin + (binPosition * binWidth);
        return logarithmic ? Math.Pow(10.0, scaled) : scaled;
    }

    private void HalveAllBins()
    {
        long sum = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] >>= 1;
            sum += counts[i];
        }

        total = sum;
    }
}

/// <summary>
/// The car's normal body-slip envelope (spec 5.2): the 95th percentile of |β| in 10 bins of normalized lateral
/// acceleration |ay|/ay_max, learned from driving without oversteer. <see cref="Evaluate"/> interpolates linearly
/// between the centres of learned bins; an unlearned bin borrows the nearest learned bin below it (the envelope
/// grows with load, so a lower bin is the conservative choice), or 0 when there is none.
/// Allocation-free except for the persistence helpers.
/// </summary>
internal sealed class BetaEnvelope
{
    /// <summary>Number of |ay|/ay_max bins.</summary>
    public const int LoadBinCount = 10;

    /// <summary>|β| histogram range per load bin (degrees).</summary>
    public const double MaxBetaDeg = 20.0;

    /// <summary>0.1° resolution over <see cref="MaxBetaDeg"/>.</summary>
    public const int BetaBinCount = 200;

    /// <summary>Percentile that defines the "normal" slip of the car.</summary>
    public const double EnvelopePercentile = 0.95;

    /// <summary>A load bin is trusted once it holds this many samples.</summary>
    public const int MinSamplesPerBin = 200;

    private readonly Histogram[] histograms = new Histogram[LoadBinCount];
    private readonly double[] learned = new double[LoadBinCount];     // NaN = bin not learned yet
    private readonly double[] effective = new double[LoadBinCount];   // per-bin value used for interpolation

    public BetaEnvelope(long forgetThreshold)
    {
        for (int i = 0; i < LoadBinCount; i++)
        {
            histograms[i] = Histogram.Linear(0.0, MaxBetaDeg, BetaBinCount, forgetThreshold);
        }

        Clear();
    }

    /// <summary>Number of load bins that have enough samples.</summary>
    public int LearnedBinCount { get; private set; }

    /// <summary>Learned 95th percentile of a load bin in degrees (NaN while unlearned).</summary>
    public double BinPercentile(int loadBin) => learned[loadBin];

    /// <summary>Adds one sample; returns false for non-finite input.</summary>
    public bool Add(double ayNorm, double absBetaDeg)
    {
        if (!MathUtil.IsFinite(ayNorm) || !MathUtil.IsFinite(absBetaDeg))
        {
            return false;
        }

        int bin = LoadBinOf(ayNorm);
        Histogram histogram = histograms[bin];
        histogram.Add(Math.Abs(absBetaDeg));
        if (histogram.Total >= MinSamplesPerBin)
        {
            learned[bin] = histogram.Percentile(EnvelopePercentile);
            RefreshEffective();
        }

        return true;
    }

    /// <summary>Envelope |β| in degrees at a normalized lateral acceleration (0 until anything is learned).</summary>
    public double Evaluate(double ayNorm)
    {
        if (LearnedBinCount == 0 || !MathUtil.IsFinite(ayNorm))
        {
            return 0.0;
        }

        // Bin centres sit at (i + 0.5) / LoadBinCount; express ayNorm in "centre index" units.
        double position = (MathUtil.Clamp01(ayNorm) * LoadBinCount) - 0.5;
        if (position <= 0)
        {
            return effective[0];
        }

        if (position >= LoadBinCount - 1)
        {
            return effective[LoadBinCount - 1];
        }

        int lower = (int)position;
        double fraction = position - lower;
        return effective[lower] + ((effective[lower + 1] - effective[lower]) * fraction);
    }

    public void Clear()
    {
        for (int i = 0; i < LoadBinCount; i++)
        {
            histograms[i].Clear();
            learned[i] = double.NaN;
            effective[i] = 0.0;
        }

        LearnedBinCount = 0;
    }

    /// <summary>Per load bin, the trimmed |β| counts (persistence; allocates).</summary>
    public int[][] ToTrimmedArrays()
    {
        var result = new int[LoadBinCount][];
        for (int i = 0; i < LoadBinCount; i++)
        {
            result[i] = histograms[i].ToTrimmedArray();
        }

        return result;
    }

    /// <summary>Restores counts saved by <see cref="ToTrimmedArrays"/>; null or short arrays are tolerated.</summary>
    public bool Load(int[][] source)
    {
        Clear();
        bool valid = true;
        if (source != null)
        {
            int length = Math.Min(source.Length, LoadBinCount);
            for (int i = 0; i < length; i++)
            {
                valid &= histograms[i].Load(source[i]);
            }
        }

        for (int i = 0; i < LoadBinCount; i++)
        {
            if (histograms[i].Total >= MinSamplesPerBin)
            {
                learned[i] = histograms[i].Percentile(EnvelopePercentile);
            }
        }

        RefreshEffective();
        return valid;
    }

    private static int LoadBinOf(double ayNorm)
    {
        int bin = (int)Math.Floor(MathUtil.Clamp01(ayNorm) * LoadBinCount);
        return bin >= LoadBinCount ? LoadBinCount - 1 : bin;
    }

    private void RefreshEffective()
    {
        int count = 0;
        double carried = 0.0;
        for (int i = 0; i < LoadBinCount; i++)
        {
            if (MathUtil.IsFinite(learned[i]))
            {
                carried = learned[i];
                count++;
            }

            effective[i] = carried;
        }

        LearnedBinCount = count;
    }
}
