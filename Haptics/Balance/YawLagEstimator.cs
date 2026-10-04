using System;
using DivebombLogistics.Core;

namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// Learns the steering→yaw lag τ (spec 5.2) from turn-in events. An event starts when the driver turns the wheel
/// quickly (|dθ/dt| &gt; 1 rad/s) at speed while the car is not yet rotating; for the next 0.6 s the requested
/// steady-state yaw rate r_ss and the measured yaw rate r are recorded. Every τ in [0.05, 0.30] s (0.01 s steps) is
/// then scored by simulating the same first-order lag the estimator applies to r_ss, fitting a least-squares scale
/// c ≥ 0 (so a not-yet-learned gain G does not bias τ) and taking the residual sum of squares; the best τ of the
/// event goes into a ring of the last 50 events whose median is the learned value (≥ 10 events).
/// All per-sample work is allocation-free (buffers are preallocated).
/// </summary>
internal sealed class YawLagEstimator
{
    /// <summary>Smallest τ candidate (s); also the lower clamp of the spec.</summary>
    public const double MinTau = 0.05;

    /// <summary>Largest τ candidate (s); also the upper clamp of the spec.</summary>
    public const double MaxTau = 0.30;

    /// <summary>Candidate spacing (s).</summary>
    public const double TauStep = 0.01;

    /// <summary>Events kept for the median.</summary>
    public const int HistoryCapacity = 50;

    /// <summary>Events needed before τ is published.</summary>
    public const int MinEventsForEstimate = 10;

    /// <summary>Events for full confidence.</summary>
    public const double FullConfidenceEvents = 20;

    private const int CandidateCount = 26; // (MaxTau - MinTau) / TauStep + 1

    // Turn-in trigger: quick steering input at speed while the car still drives (nearly) straight.
    private const double TriggerSteeringRate = 1.0;  // rad/s at the steering wheel
    private const double TriggerMinSpeed = 15.0;     // m/s
    private const double TriggerMaxYawRate = 0.05;   // rad/s

    /// <summary>Recording window after the trigger (s): long enough to see the lag of a slow car settle.</summary>
    private const double EventDuration = 0.6;

    /// <summary>The event is abandoned if the car slows below this during the window (m/s).</summary>
    private const double MinSpeedDuringEvent = 10.0;

    /// <summary>A larger gap between samples means ticks were skipped; the lag simulation would be wrong (s).</summary>
    private const double MaxSampleGap = 0.1;

    /// <summary>Buffer capacity: 0.6 s at more than 400 Hz.</summary>
    private const int EventCapacity = 256;

    /// <summary>Fewest samples an event needs to be scored (0.6 s at 20 Hz).</summary>
    private const int MinEventSamples = 12;

    /// <summary>The requested yaw rate must change at least this much (rad/s) for the event to carry information.</summary>
    private const double MinExcitation = 0.05;

    /// <summary>Fraction of the yaw-rate variance the best lag must explain for the event to count.</summary>
    private const double MinExplainedVariance = 0.5;

    private readonly double[] interval = new double[EventCapacity];
    private readonly double[] steadyState = new double[EventCapacity];
    private readonly double[] yawRate = new double[EventCapacity];
    private readonly double[] history = new double[HistoryCapacity];  // ring, oldest at historyStart
    private readonly double[] sortScratch = new double[HistoryCapacity];

    private int eventLength;
    private double eventStartTime;
    private double lastTime;
    private int historyStart;
    private int historyCount;

    public YawLagEstimator()
    {
        Clear();
    }

    /// <summary>True while a turn-in event is being recorded.</summary>
    public bool EventInProgress { get; private set; }

    /// <summary>Accepted events since the last <see cref="Clear"/> (lifetime of the learned baseline).</summary>
    public long EventsTotal { get; private set; }

    /// <summary>Events currently in the median ring (≤ <see cref="HistoryCapacity"/>).</summary>
    public int StoredEvents => historyCount;

    /// <summary>Median of the stored event results (NaN until <see cref="MinEventsForEstimate"/> events).</summary>
    public double Tau { get; private set; }

    /// <summary>min(1, events / 20) once <see cref="Tau"/> is available, else 0.</summary>
    public double Confidence =>
        MathUtil.IsFinite(Tau) ? Math.Min(1.0, EventsTotal / FullConfidenceEvents) : 0.0;

    /// <summary>Forgets all events (including one in progress).</summary>
    public void Clear()
    {
        AbortEvent();
        Array.Clear(history, 0, history.Length);
        historyStart = 0;
        historyCount = 0;
        EventsTotal = 0;
        Tau = double.NaN;
    }

    /// <summary>Drops the event being recorded (a gate closed, so the trace is incomplete).</summary>
    public void AbortEvent()
    {
        EventInProgress = false;
        eventLength = 0;
    }

    /// <summary>
    /// Feeds one gated tick. <paramref name="t"/> is the sample time (s); the interval used for the lag simulation is
    /// the difference to the previous traced sample, so skipped ticks are detected. Returns true when this tick
    /// completed an event that was accepted (the learned τ may have changed).
    /// </summary>
    public bool AddTrace(double t, double dt, double rSs, double r, double dThetaDt, double v)
    {
        if (!MathUtil.IsFinite(t) || !MathUtil.IsFinite(dt) || !(dt > 0) || !MathUtil.IsFinite(rSs)
            || !MathUtil.IsFinite(r) || !MathUtil.IsFinite(dThetaDt) || !MathUtil.IsFinite(v))
        {
            AbortEvent();
            return false;
        }

        if (!EventInProgress)
        {
            if (Math.Abs(dThetaDt) > TriggerSteeringRate && v > TriggerMinSpeed && Math.Abs(r) < TriggerMaxYawRate)
            {
                EventInProgress = true;
                eventStartTime = t;
                lastTime = t;
                eventLength = 0;
                Append(0.0, rSs, r);
            }

            return false;
        }

        double gap = t - lastTime;
        if (!(gap > 0) || gap > MaxSampleGap || v < MinSpeedDuringEvent)
        {
            AbortEvent();
            return false;
        }

        lastTime = t;
        Append(gap, rSs, r);
        if (t - eventStartTime < EventDuration && eventLength < EventCapacity)
        {
            return false;
        }

        EventInProgress = false;
        bool accepted = TryEvaluate(out double bestTau);
        eventLength = 0;
        if (accepted)
        {
            AddToHistory(bestTau);
            EventsTotal++;
            RefreshMedian();
        }

        return accepted;
    }

    /// <summary>Stored event results, oldest first (persistence; allocates).</summary>
    public double[] ToArray()
    {
        var result = new double[historyCount];
        for (int i = 0; i < historyCount; i++)
        {
            result[i] = history[(historyStart + i) % HistoryCapacity];
        }

        return result;
    }

    /// <summary>
    /// Restores stored event results (oldest first) and the lifetime event count. Values outside the τ range are
    /// dropped (returns false); only the newest <see cref="HistoryCapacity"/> are kept.
    /// </summary>
    public bool Load(double[] events, long eventsTotal)
    {
        Clear();
        bool valid = true;
        if (events != null)
        {
            int first = Math.Max(0, events.Length - HistoryCapacity);
            for (int i = first; i < events.Length; i++)
            {
                double tau = events[i];
                if (MathUtil.IsFinite(tau) && tau >= MinTau && tau <= MaxTau)
                {
                    AddToHistory(tau);
                }
                else
                {
                    valid = false;
                }
            }
        }

        EventsTotal = Math.Max(historyCount, eventsTotal);
        RefreshMedian();
        return valid;
    }

    private void Append(double gap, double rSs, double r)
    {
        interval[eventLength] = gap;
        steadyState[eventLength] = rSs;
        yawRate[eventLength] = r;
        eventLength++;
    }

    /// <summary>Scores every τ candidate on the recorded event; false when the event carries too little information.</summary>
    private bool TryEvaluate(out double bestTau)
    {
        bestTau = double.NaN;
        int n = eventLength;
        if (n < MinEventSamples)
        {
            return false;
        }

        double initialSteadyState = steadyState[0];
        double excitation = 0.0;
        double meanYaw = 0.0;
        for (int i = 1; i < n; i++)
        {
            excitation = Math.Max(excitation, Math.Abs(steadyState[i] - initialSteadyState));
            meanYaw += yawRate[i];
        }

        meanYaw /= n - 1;
        double yawVariance = 0.0;
        double yawSquares = 0.0;
        for (int i = 1; i < n; i++)
        {
            double deviation = yawRate[i] - meanYaw;
            yawVariance += deviation * deviation;
            yawSquares += yawRate[i] * yawRate[i];
        }

        if (excitation < MinExcitation || !(yawVariance > 0))
        {
            return false;
        }

        double bestResidual = double.PositiveInfinity;
        double bestScale = 0.0;
        for (int k = 0; k < CandidateCount; k++)
        {
            double tau = MinTau + (k * TauStep);

            // Same discretization as the estimator's r_ref filter, started from the measured yaw rate.
            double lagged = yawRate[0];
            double crossSum = 0.0;
            double laggedSquares = 0.0;
            for (int i = 1; i < n; i++)
            {
                lagged += (steadyState[i] - lagged) * MathUtil.LagAlpha(interval[i], tau);
                crossSum += yawRate[i] * lagged;
                laggedSquares += lagged * lagged;
            }

            double scale = laggedSquares > 0 ? crossSum / laggedSquares : 0.0;
            double residual = scale > 0 ? yawSquares - (scale * crossSum) : yawSquares;
            if (residual < bestResidual)
            {
                bestResidual = residual;
                bestScale = scale;
                bestTau = tau;
            }
        }

        // Positive scale: the car turned the way it was steered. Explained variance: the lag model fits the trace.
        return bestScale > 0 && (1.0 - (bestResidual / yawVariance)) >= MinExplainedVariance;
    }

    private void AddToHistory(double tau)
    {
        if (historyCount < HistoryCapacity)
        {
            history[(historyStart + historyCount) % HistoryCapacity] = tau;
            historyCount++;
        }
        else
        {
            history[historyStart] = tau;
            historyStart = (historyStart + 1) % HistoryCapacity;
        }
    }

    private void RefreshMedian()
    {
        if (historyCount < MinEventsForEstimate)
        {
            Tau = double.NaN;
            return;
        }

        // Insertion sort of at most 50 values into a preallocated scratch buffer.
        for (int i = 0; i < historyCount; i++)
        {
            double value = history[(historyStart + i) % HistoryCapacity];
            int j = i - 1;
            while (j >= 0 && sortScratch[j] > value)
            {
                sortScratch[j + 1] = sortScratch[j];
                j--;
            }

            sortScratch[j + 1] = value;
        }

        int middle = historyCount / 2;
        Tau = (historyCount % 2) == 1
            ? sortScratch[middle]
            : 0.5 * (sortScratch[middle - 1] + sortScratch[middle]);
    }
}
