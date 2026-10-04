using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DivebombLogistics.Core;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.Balance.Recording;
using DivebombLogistics.Haptics.Settings;

namespace DivebombLogistics.Tests;

/// <summary>
/// Offline replay of a balance recording (CSV written by <see cref="BalanceRecorder"/>) through a fresh estimator
/// with default tuning and an empty car profile, printing a summary: sample counts, active time, understeer/oversteer
/// statistics, gate distribution, deviation from the recorded outputs and the learned vehicle model.
/// Used via <c>DivebombLogistics.Tests.exe --replay recording.csv</c> to tune detectors on real laps.
/// </summary>
internal static class ReplayTool
{
    public const int ExitOk = 0;
    public const int ExitNoSamples = 1;
    public const int ExitFileError = 2;

    /// <summary>Gaps between samples above this (s) are pauses/discontinuities and do not count as active time.</summary>
    private const double MaxCountedGapSeconds = 0.5;

    public static int Run(string csvPath, TextWriter output)
    {
        if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
        {
            output.WriteLine("Recording not found: " + csvPath);
            return ExitFileError;
        }

        try
        {
            output.WriteLine("Replaying " + Path.GetFullPath(csvPath));
            return Replay(BalanceCsv.Read(csvPath), output);
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
        {
            output.WriteLine("Replay failed: " + ex.Message);
            return ExitFileError;
        }
    }

    /// <summary>Replays <paramref name="records"/> and prints the summary. Returns an exit code.</summary>
    internal static int Replay(IEnumerable<BalanceRecord> records, TextWriter output)
    {
        var estimator = new BalanceEstimator(new BalanceTuning(), new TextWriterLog(output));
        estimator.LoadCar(new CarProfile(), new SimCalibration(), string.Empty);

        var state = new VehicleState();
        var stats = new ReplayStats();
        foreach (BalanceRecord record in records)
        {
            record.ToVehicleState(state);
            estimator.Update(state);
            stats.Add(record, estimator.Outputs);
        }

        if (stats.Samples == 0)
        {
            output.WriteLine("No samples in recording.");
            return ExitNoSamples;
        }

        stats.Print(output, estimator.Outputs);
        return ExitOk;
    }

    private static string F(double value, string format = "0.000") =>
        MathUtil.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "n/a";

    /// <summary>Accumulates replay statistics.</summary>
    private sealed class ReplayStats
    {
        private readonly int[] gateCounts = new int[Enum.GetValues(typeof(BalanceGate)).Length];
        private readonly int[] pathCounts = new int[Enum.GetValues(typeof(BalancePath)).Length];
        private double firstTime = double.NaN;
        private double lastTime = double.NaN;

        private int validSamples;
        private int activeSamples;
        private double activeSeconds;
        private double understeerMax;
        private double oversteerMax;
        private double understeerSum;
        private double oversteerSum;
        private double maxUndersteerDeviation;
        private double maxOversteerDeviation;

        public int Samples { get; private set; }

        public void Add(BalanceRecord record, BalanceOutputs outputs)
        {
            Samples++;
            if (record.Valid)
            {
                validSamples++;
            }

            double time = MathUtil.IsFinite(record.SimTime) ? record.SimTime : record.WallTime;
            double dt = time - lastTime;
            if (MathUtil.IsFinite(time))
            {
                if (!MathUtil.IsFinite(firstTime))
                {
                    firstTime = time;
                }

                lastTime = time;
            }

            Count(gateCounts, (int)outputs.Gate);
            Count(pathCounts, (int)outputs.Path);

            if (outputs.Active)
            {
                activeSamples++;
                if (dt > 0.0 && dt <= MaxCountedGapSeconds)
                {
                    activeSeconds += dt;
                }

                understeerSum += outputs.Understeer;
                oversteerSum += outputs.Oversteer;
            }

            understeerMax = Math.Max(understeerMax, outputs.Understeer);
            oversteerMax = Math.Max(oversteerMax, outputs.Oversteer);
            maxUndersteerDeviation = Math.Max(maxUndersteerDeviation, Math.Abs(outputs.Understeer - record.Understeer));
            maxOversteerDeviation = Math.Max(maxOversteerDeviation, Math.Abs(outputs.Oversteer - record.Oversteer));
        }

        public void Print(TextWriter o, BalanceOutputs last)
        {
            o.WriteLine("Samples:          {0} ({1} valid, {2} active)", Samples, validSamples, activeSamples);
            o.WriteLine("Duration:         {0} s, active {1} s", F(lastTime - firstTime, "0.0"), F(activeSeconds, "0.0"));
            o.WriteLine("Understeer:       max {0}, mean(active) {1}", F(understeerMax), F(Mean(understeerSum)));
            o.WriteLine("Oversteer:        max {0}, mean(active) {1}", F(oversteerMax), F(Mean(oversteerSum)));
            o.WriteLine("Vs. recorded:     max |dUS| {0}, max |dOS| {1}", F(maxUndersteerDeviation), F(maxOversteerDeviation));
            o.WriteLine("Gates:            " + Distribution(gateCounts, typeof(BalanceGate)));
            o.WriteLine("Paths:            " + Distribution(pathCounts, typeof(BalancePath)));
            o.WriteLine("G:                {0} 1/m ({1}, confidence {2}, {3} samples)", F(last.G, "0.00000"), last.GSource, F(last.ConfG, "0.00"), last.SamplesG);
            o.WriteLine("K:                {0} s2/m2 ({1}, confidence {2}, {3} samples)", F(last.K, "0.000000"), last.KSource, F(last.ConfK, "0.00"), last.SamplesK);
            o.WriteLine("Theta0:           {0} deg ({1}, {2} samples)", F(last.Theta0Deg, "0.00"), last.Theta0Source, last.SamplesTheta0);
            o.WriteLine("TauYaw:           {0} s ({1}, {2} events)", F(last.TauYaw), last.TauSource, last.SamplesTau);
            o.WriteLine("AyMax:            {0} m/s2 ({1} samples)", F(last.AyMax, "0.00"), last.SamplesAy);
            o.WriteLine("AlphaPeak:        {0} ({1} samples)", F(last.AlphaPeak, "0.0000"), last.SamplesAlpha);
            o.WriteLine(
                "Signs:            steering {0}, forward {1}, gravity in Az {2}",
                last.SteeringSign,
                last.ForwardSign,
                last.GravityIncluded.HasValue ? (last.GravityIncluded.Value ? "yes" : "no") : "unknown");
        }

        private static void Count(int[] counts, int index)
        {
            if (index >= 0 && index < counts.Length)
            {
                counts[index]++;
            }
        }

        private double Mean(double sum) => activeSamples > 0 ? sum / activeSamples : double.NaN;

        private string Distribution(int[] counts, Type enumType)
        {
            var parts = new List<string>();
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > 0)
                {
                    parts.Add(Enum.GetName(enumType, i) + " " + F(100.0 * counts[i] / Samples, "0.0") + "%");
                }
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>Forwards estimator log messages (sign flips, calibration) into the replay output.</summary>
    private sealed class TextWriterLog : ILog
    {
        private readonly TextWriter output;

        public TextWriterLog(TextWriter output)
        {
            this.output = output;
        }

        public void Info(string message) => output.WriteLine("  [info] " + message);

        public void Warn(string message) => output.WriteLine("  [warn] " + message);

        public void Error(string message) => output.WriteLine("  [error] " + message);
    }
}
