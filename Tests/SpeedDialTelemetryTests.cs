using System;
using DivebombLogistics.SpeedDial;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Telemetry;

namespace DivebombLogistics.Tests;

/// <summary>
/// SpeedDial per-sim telemetry readers: factory mapping, paths, unit conversions (brake bias front percent), missing
/// and invalid data, per-car maxima, unsupported channels, AC EVO fallbacks, diagnostics caching and allocation-free
/// reads. Paths are written out literally on purpose, so a typo in <see cref="DialPropertyPaths"/> fails here.
/// </summary>
internal static class SpeedDialTelemetryTests
{
    private const string Lmu = "DataCorePlugin.GameRawData.PlayerNativeTelemetry.";
    private const string IRacing = "DataCorePlugin.GameRawData.Telemetry.";
    private const string Graphics = "DataCorePlugin.GameRawData.Graphics.";
    private const string Physics = "DataCorePlugin.GameRawData.Physics.";
    private const string GameData = "DataCorePlugin.GameData.";
    private const double Tolerance = 1e-6;

    private static readonly DialChannel[] Channels =
    {
        DialChannel.Tc1, DialChannel.Tc2, DialChannel.Tc3, DialChannel.Abs, DialChannel.BrakeBias,
    };

    [Test]
    public static void Factory_MapsGameNamesCaseInsensitively()
    {
        var reader = new FakeTelemetryReader();
        Assert.True(DialTelemetryFactory.Create("LMU", reader) is LmuDialTelemetry, "LMU");
        Assert.True(DialTelemetryFactory.Create("lmu", reader) is LmuDialTelemetry, "lmu");
        Assert.True(DialTelemetryFactory.Create("IRacing", reader) is IRacingDialTelemetry, "IRacing");
        Assert.True(DialTelemetryFactory.Create("iracing", reader) is IRacingDialTelemetry, "iracing");
        Assert.Equal("ACC", DialTelemetryFactory.Create("AssettoCorsaCompetizione", reader).Name);
        Assert.Equal("AC EVO", DialTelemetryFactory.Create("assettocorsaevo", reader).Name);
        Assert.Equal("AC Rally", DialTelemetryFactory.Create("ASSETTOCORSARALLY", reader).Name);
        Assert.True(DialTelemetryFactory.Create("AssettoCorsaEVO", reader) is AccDialTelemetry, "AC EVO type");
        Assert.Equal("LMU native", DialTelemetryFactory.Create("LMU", reader).Name);
        Assert.Equal("iRacing", DialTelemetryFactory.Create("IRacing", reader).Name);

        string[] generic = { "RFactor2", "AssettoCorsa", null, string.Empty, "F12024", "LMU2" };
        foreach (string game in generic)
        {
            IDialTelemetry telemetry = DialTelemetryFactory.Create(game, reader);
            Assert.True(telemetry is GenericDialTelemetry, "generic for '" + (game ?? "null") + "'");
            Assert.Equal("Generic (SimHub)", telemetry.Name);
        }

        Assert.Throws<ArgumentNullException>(() => DialTelemetryFactory.Create("LMU", null));
        Assert.Throws<ArgumentNullException>(() => new LmuDialTelemetry(null));
        Assert.Throws<ArgumentNullException>(() => new AccDialTelemetry(null, "AssettoCorsaCompetizione"));
    }

    [Test]
    public static void Factory_SupportedChannelsPerSim()
    {
        var reader = new FakeTelemetryReader();
        AssertSupported(DialTelemetryFactory.Create("LMU", reader), true, true, true, true, true);
        AssertSupported(DialTelemetryFactory.Create("IRacing", reader), true, true, false, true, true);
        AssertSupported(DialTelemetryFactory.Create("AssettoCorsaCompetizione", reader), true, true, false, true, true);
        AssertSupported(DialTelemetryFactory.Create("AssettoCorsaRally", reader), true, true, false, true, true);
        AssertSupported(DialTelemetryFactory.Create("AssettoCorsaEVO", reader), true, true, false, true, true);
        AssertSupported(DialTelemetryFactory.Create("RFactor2", reader), true, false, false, true, true);
    }

    [Test]
    public static void Lmu_ReadsByteLevelsAndMaxima()
    {
        var reader = new FakeTelemetryReader()
            .Set(Lmu + "mTC", (byte)3).Set(Lmu + "mTCMax", (byte)11)
            .Set(Lmu + "mTCCut", (byte)2).Set(Lmu + "mTCCutMax", (byte)8)
            .Set(Lmu + "mTCSlip", (byte)4).Set(Lmu + "mTCSlipMax", (byte)6)
            .Set(Lmu + "mABS", (byte)5).Set(Lmu + "mABSMax", (byte)0);
        var telemetry = new LmuDialTelemetry(reader);

        AssertRead(telemetry, DialChannel.Tc1, 3);
        AssertRead(telemetry, DialChannel.Tc2, 2);
        AssertRead(telemetry, DialChannel.Tc3, 4);
        AssertRead(telemetry, DialChannel.Abs, 5);
        AssertMax(telemetry, DialChannel.Tc1, 11);
        AssertMax(telemetry, DialChannel.Tc2, 8);
        AssertMax(telemetry, DialChannel.Tc3, 6);

        // mABSMax 0 = not adjustable: no real limit to report.
        AssertNoMax(telemetry, DialChannel.Abs);

        // LMU reports no brake bias maximum.
        reader.Set(Lmu + "mRearBrakeBias", 0.45);
        AssertNoMax(telemetry, DialChannel.BrakeBias);

        reader.Remove(Lmu + "mTCMax");
        AssertNoMax(telemetry, DialChannel.Tc1);
        reader.Set(Lmu + "mTCMax", double.NaN);
        AssertNoMax(telemetry, DialChannel.Tc1);
    }

    [Test]
    public static void Lmu_BrakeBiasIsFrontPercentOfRearFraction()
    {
        var reader = new FakeTelemetryReader().Set(Lmu + "mRearBrakeBias", 0.458);
        var telemetry = new LmuDialTelemetry(reader);
        AssertRead(telemetry, DialChannel.BrakeBias, 54.2);

        reader.Set(Lmu + "mRearBrakeBias", 0.5f);
        AssertRead(telemetry, DialChannel.BrakeBias, 50.0);

        // Zeroed shared memory (no car) would read as 100 % front: treated as unknown, like 0 % and NaN.
        reader.Set(Lmu + "mRearBrakeBias", 0.0);
        AssertNoRead(telemetry, DialChannel.BrakeBias);
        reader.Set(Lmu + "mRearBrakeBias", 1.0);
        AssertNoRead(telemetry, DialChannel.BrakeBias);
        reader.Set(Lmu + "mRearBrakeBias", double.NaN);
        AssertNoRead(telemetry, DialChannel.BrakeBias);
        reader.Set(Lmu + "mRearBrakeBias", double.PositiveInfinity);
        AssertNoRead(telemetry, DialChannel.BrakeBias);
        reader.Remove(Lmu + "mRearBrakeBias");
        AssertNoRead(telemetry, DialChannel.BrakeBias);
    }

    [Test]
    public static void IRacing_ReadsDcVariables()
    {
        var reader = new FakeTelemetryReader()
            .Set(IRacing + "dcTractionControl", 4.0f)
            .Set(IRacing + "dcTractionControl2", 1.0f)
            .Set(IRacing + "dcABS", 3)
            .Set(IRacing + "dcBrakeBias", 58.25f);
        var telemetry = new IRacingDialTelemetry(reader);

        AssertRead(telemetry, DialChannel.Tc1, 4);
        AssertRead(telemetry, DialChannel.Tc2, 1);
        AssertRead(telemetry, DialChannel.Abs, 3);
        AssertRead(telemetry, DialChannel.BrakeBias, 58.25);
        AssertNoRead(telemetry, DialChannel.Tc3);
        for (int i = 0; i < Channels.Length; i++)
        {
            AssertNoMax(telemetry, Channels[i]);
        }

        // A car without the second TC map has no dcTractionControl2 variable.
        reader.Remove(IRacing + "dcTractionControl2");
        AssertNoRead(telemetry, DialChannel.Tc2);
        AssertRead(telemetry, DialChannel.Tc1, 4);
    }

    [Test]
    public static void Acc_ReadsGraphicsLevelsAndRawBrakeBias()
    {
        var reader = new FakeTelemetryReader()
            .Set(Graphics + "TC", 3).Set(Graphics + "TCCut", 2).Set(Graphics + "ABS", 4)
            .Set(Physics + "BrakeBias", 0.625f);
        foreach (string game in new[] { "AssettoCorsaCompetizione", "AssettoCorsaRally" })
        {
            IDialTelemetry telemetry = DialTelemetryFactory.Create(game, reader);
            AssertRead(telemetry, DialChannel.Tc1, 3);
            AssertRead(telemetry, DialChannel.Tc2, 2);
            AssertRead(telemetry, DialChannel.Abs, 4);
            AssertRead(telemetry, DialChannel.BrakeBias, 62.5);
            AssertNoRead(telemetry, DialChannel.Tc3);
            AssertNoMax(telemetry, DialChannel.Tc1);
            AssertNoMax(telemetry, DialChannel.BrakeBias);
        }

        // Before a car is loaded the physics page is zeroed.
        reader.Set(Physics + "BrakeBias", 0.0f);
        AssertNoRead(DialTelemetryFactory.Create("AssettoCorsaCompetizione", reader), DialChannel.BrakeBias);
    }

    [Test]
    public static void AcEvo_PrefersElectronicsLevelsWithLimits()
    {
        var reader = new FakeTelemetryReader()
            .Set(Graphics + "electronics.tc_level", (sbyte)5)
            .Set(Graphics + "electronics.tc_cut_level", (sbyte)2)
            .Set(Graphics + "electronics.abs_level", (sbyte)3)
            .Set(Graphics + "electronics_max_limit.tc_level", (sbyte)10)
            .Set(Graphics + "electronics_max_limit.tc_cut_level", (sbyte)0)
            .Set(Graphics + "electronics_max_limit.abs_level", (sbyte)12)
            .Set(Physics + "tc", 0.2f)
            .Set(Physics + "abs", 0.4f)
            .Set(Physics + "brakeBias", 0.5625f);
        var telemetry = new AccDialTelemetry(reader, "AssettoCorsaEVO");

        AssertRead(telemetry, DialChannel.Tc1, 5);
        AssertRead(telemetry, DialChannel.Tc2, 2);
        AssertRead(telemetry, DialChannel.Abs, 3);
        AssertRead(telemetry, DialChannel.BrakeBias, 56.25);
        AssertMax(telemetry, DialChannel.Tc1, 10);
        AssertNoMax(telemetry, DialChannel.Tc2);
        AssertMax(telemetry, DialChannel.Abs, 12);
        AssertNoMax(telemetry, DialChannel.BrakeBias);

        // A control the car lacks reads -1: unknown, not a level.
        reader.Set(Graphics + "electronics.abs_level", (sbyte)-1);
        AssertNoRead(telemetry, DialChannel.Abs);
    }

    [Test]
    public static void AcEvo_FallsBackToCamelCasePhysics()
    {
        var reader = new FakeTelemetryReader()
            .Set(Physics + "tc", 2.0f)
            .Set(Physics + "abs", 1.0f)
            .Set(Graphics + "electronics.brake_bias", 0.5625f);
        var telemetry = new AccDialTelemetry(reader, "assettocorsaevo");

        AssertRead(telemetry, DialChannel.Tc1, 2);
        AssertRead(telemetry, DialChannel.Abs, 1);
        AssertRead(telemetry, DialChannel.BrakeBias, 56.25);
        AssertNoRead(telemetry, DialChannel.Tc2);

        // Once a fallback was resolved it stays selected for this game session.
        reader.Set(Graphics + "electronics.tc_level", (sbyte)7);
        AssertRead(telemetry, DialChannel.Tc1, 2);
        Assert.True(telemetry.Describe().IndexOf(Physics + "tc (present)", StringComparison.Ordinal) >= 0, telemetry.Describe());

        // The electronics brake bias may be percent as well.
        var percentReader = new FakeTelemetryReader().Set(Graphics + "electronics.brake_bias", 55.5f);
        AssertRead(new AccDialTelemetry(percentReader, "AssettoCorsaEVO"), DialChannel.BrakeBias, 55.5);
    }

    [Test]
    public static void IntegerChannels_RejectFractionalReadings()
    {
        // AC EVO Physics.tc / Physics.abs are floats (not the setting level) when the electronics levels are not
        // exposed. A fractional reading must never be dialed: it rounds to a wrong level and would press towards the
        // limit. Whole-number floats (2.0f above) stay valid.
        var reader = new FakeTelemetryReader()
            .Set(Physics + "tc", 0.12f)
            .Set(Physics + "abs", 2.5f);
        var telemetry = new AccDialTelemetry(reader, "AssettoCorsaEVO");

        AssertNoRead(telemetry, DialChannel.Tc1);
        AssertNoRead(telemetry, DialChannel.Abs);

        reader.Set(Physics + "tc", 3.0f);
        AssertRead(telemetry, DialChannel.Tc1, 3);
    }

    [Test]
    public static void AcEvo_FallbackProbingIsRateLimited()
    {
        var reader = new FakeTelemetryReader();
        var telemetry = new AccDialTelemetry(reader, "AssettoCorsaEVO");

        // First call probes both candidates; the fallback then appears.
        AssertNoRead(telemetry, DialChannel.Tc1);
        reader.Set(Physics + "tc", 3.0f);
        int reads = reader.ReadCount;
        int calls = 0;
        bool resolved = false;
        while (calls < DialChannelSource.FallbackProbeInterval + 1 && !resolved)
        {
            calls++;
            resolved = telemetry.TryRead(DialChannel.Tc1, out _);
        }

        Assert.True(resolved, "fallback resolved within the probe interval");
        Assert.Equal(DialChannelSource.FallbackProbeInterval, calls, "calls until the fallback is probed again");

        // Primary read every call, fallback only on the probing call.
        Assert.Equal(calls + 1, reader.ReadCount - reads, "lookups while unresolved");

        // The primary candidate is still read on every call while unresolved.
        var primaryReader = new FakeTelemetryReader();
        var primary = new AccDialTelemetry(primaryReader, "AssettoCorsaEVO");
        AssertNoRead(primary, DialChannel.Tc1);
        primaryReader.Set(Graphics + "electronics.tc_level", (sbyte)4);
        AssertRead(primary, DialChannel.Tc1, 4);
    }

    [Test]
    public static void Generic_ReadsNormalizedGameData()
    {
        var reader = new FakeTelemetryReader()
            .Set(GameData + "TCLevel", 2)
            .Set(GameData + "ABSLevel", 6)
            .Set(GameData + "BrakeBias", 55.5);
        IDialTelemetry telemetry = DialTelemetryFactory.Create("RFactor2", reader);

        AssertRead(telemetry, DialChannel.Tc1, 2);
        AssertRead(telemetry, DialChannel.Abs, 6);
        AssertRead(telemetry, DialChannel.BrakeBias, 55.5);
        AssertNoRead(telemetry, DialChannel.Tc2);
        AssertNoRead(telemetry, DialChannel.Tc3);
        AssertNoMax(telemetry, DialChannel.Tc1);

        // SimHub reports 0 when the game has no brake bias.
        reader.Set(GameData + "BrakeBias", 0.0);
        AssertNoRead(telemetry, DialChannel.BrakeBias);

        // TC 0 is a real level (off).
        reader.Set(GameData + "TCLevel", 0);
        AssertRead(telemetry, DialChannel.Tc1, 0);
    }

    [Test]
    public static void AllSims_MissingOrBadDataReadsFalseWithoutThrowing()
    {
        string[] games = { "LMU", "IRacing", "AssettoCorsaCompetizione", "AssettoCorsaEVO", "AssettoCorsaRally", "RFactor2" };
        var empty = new FakeTelemetryReader();
        var garbage = new FakeTelemetryReader();
        string[] allPaths = AllPaths();
        foreach (string path in allPaths)
        {
            garbage.Set(path, "not a number");
        }

        foreach (string game in games)
        {
            foreach (FakeTelemetryReader reader in new[] { empty, garbage })
            {
                IDialTelemetry telemetry = DialTelemetryFactory.Create(game, reader);
                for (int i = 0; i < Channels.Length; i++)
                {
                    AssertNoRead(telemetry, Channels[i]);
                    AssertNoMax(telemetry, Channels[i]);
                }

                Assert.False(telemetry.IsSupported((DialChannel)99), game + " invalid channel supported");
                AssertNoRead(telemetry, (DialChannel)99);
                AssertNoMax(telemetry, (DialChannel)(-1));
                Assert.True(telemetry.Describe().Length > 0, game + " describe");
            }
        }

        // Non-finite numbers count as unknown for levels too.
        var nan = new FakeTelemetryReader().Set(IRacing + "dcABS", double.NaN).Set(IRacing + "dcTractionControl", float.NegativeInfinity);
        var iracing = new IRacingDialTelemetry(nan);
        AssertNoRead(iracing, DialChannel.Abs);
        AssertNoRead(iracing, DialChannel.Tc1);
    }

    [Test]
    public static void Describe_IsCachedUntilPresenceChanges()
    {
        var reader = new FakeTelemetryReader().Set(Lmu + "mTC", (byte)3);
        var telemetry = new LmuDialTelemetry(reader);

        string first = telemetry.Describe();
        Assert.True(first.StartsWith("LMU native", StringComparison.Ordinal), first);
        Assert.True(first.IndexOf(Lmu + "mTC (present)", StringComparison.Ordinal) >= 0, first);
        Assert.True(first.IndexOf(Lmu + "mABS (missing)", StringComparison.Ordinal) >= 0, first);
        Assert.True(first.IndexOf("max " + Lmu + "mTCMax (missing)", StringComparison.Ordinal) >= 0, first);
        Assert.True(ReferenceEquals(first, telemetry.Describe()), "same instance without changes");

        // A value change alone does not rebuild the text.
        reader.Set(Lmu + "mTC", (byte)4);
        Assert.True(ReferenceEquals(first, telemetry.Describe()), "same instance after a value change");

        reader.Set(Lmu + "mABS", (byte)2);
        string second = telemetry.Describe();
        Assert.False(ReferenceEquals(first, second), "rebuilt after a property appeared");
        Assert.True(second.IndexOf(Lmu + "mABS (present)", StringComparison.Ordinal) >= 0, second);

        reader.Set(Lmu + "mABS", double.NaN);
        string third = telemetry.Describe();
        Assert.True(third.IndexOf(Lmu + "mABS (no valid value)", StringComparison.Ordinal) >= 0, third);

        string iracing = new IRacingDialTelemetry(reader).Describe();
        Assert.True(iracing.IndexOf("TC3 (Slip): not supported", StringComparison.Ordinal) >= 0, iracing);

        string evo = new AccDialTelemetry(new FakeTelemetryReader(), "AssettoCorsaEVO").Describe();
        Assert.True(
            evo.IndexOf(Graphics + "electronics.tc_level | " + Physics + "tc (missing)", StringComparison.Ordinal) >= 0,
            evo);
    }

    [Test]
    public static void Reads_DoNotAllocate()
    {
        var lmuReader = new FakeTelemetryReader()
            .Set(Lmu + "mTC", (byte)3).Set(Lmu + "mTCMax", (byte)11)
            .Set(Lmu + "mTCCut", (byte)2).Set(Lmu + "mTCCutMax", (byte)8)
            .Set(Lmu + "mTCSlip", (byte)4).Set(Lmu + "mTCSlipMax", (byte)6)
            .Set(Lmu + "mABS", (byte)5).Set(Lmu + "mABSMax", (byte)9)
            .Set(Lmu + "mRearBrakeBias", 0.46);
        var evoReader = new FakeTelemetryReader()
            .Set(Graphics + "electronics.abs_level", (sbyte)3)
            .Set(Physics + "brakeBias", 0.5625f);
        var genericReader = new FakeTelemetryReader().Set(GameData + "TCLevel", 1);
        IDialTelemetry[] all =
        {
            new LmuDialTelemetry(lmuReader),
            new IRacingDialTelemetry(new FakeTelemetryReader().Set(IRacing + "dcBrakeBias", 57.0f)),
            new AccDialTelemetry(new FakeTelemetryReader().Set(Graphics + "TC", 2), "AssettoCorsaCompetizione"),
            new AccDialTelemetry(evoReader, "AssettoCorsaEVO"),
            new GenericDialTelemetry(genericReader),
        };

        // Pre-boxed values so swapping them inside the loop allocates nothing.
        object tcA = (byte)3;
        object tcB = (byte)4;

        RunReads(all, lmuReader, tcA, tcB, 10);

        const int Iterations = 10000;
        const long AllowanceBytes = 8 * 1024;
        AppDomain.MonitoringIsEnabled = true;
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        double sum = RunReads(all, lmuReader, tcA, tcB, Iterations);
        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        Assert.True(allocated <= AllowanceBytes, "allocated " + allocated + " bytes in " + Iterations + " iterations");
        Assert.True(sum > 0.0, "values were read");
    }

    private static double RunReads(IDialTelemetry[] all, FakeTelemetryReader lmuReader, object tcA, object tcB, int iterations)
    {
        double sum = 0.0;
        for (int n = 0; n < iterations; n++)
        {
            lmuReader.Set(Lmu + "mTC", (n & 1) == 0 ? tcA : tcB);
            for (int t = 0; t < all.Length; t++)
            {
                IDialTelemetry telemetry = all[t];
                for (int c = 0; c < Channels.Length; c++)
                {
                    if (telemetry.IsSupported(Channels[c]) && telemetry.TryRead(Channels[c], out double value))
                    {
                        sum += value;
                    }

                    if (telemetry.TryGetMax(Channels[c], out double max))
                    {
                        sum += max;
                    }
                }

                sum += telemetry.Describe().Length;
            }
        }

        return sum;
    }

    private static string[] AllPaths() => new[]
    {
        DialPropertyPaths.LmuTc, DialPropertyPaths.LmuTcMax, DialPropertyPaths.LmuTcCut, DialPropertyPaths.LmuTcCutMax,
        DialPropertyPaths.LmuTcSlip, DialPropertyPaths.LmuTcSlipMax, DialPropertyPaths.LmuAbs, DialPropertyPaths.LmuAbsMax,
        DialPropertyPaths.LmuRearBrakeBias, DialPropertyPaths.IRacingTc, DialPropertyPaths.IRacingTc2,
        DialPropertyPaths.IRacingAbs, DialPropertyPaths.IRacingBrakeBias, DialPropertyPaths.AccTc, DialPropertyPaths.AccTcCut,
        DialPropertyPaths.AccAbs, DialPropertyPaths.AccBrakeBias, DialPropertyPaths.EvoTcLevel,
        DialPropertyPaths.EvoTcCutLevel, DialPropertyPaths.EvoAbsLevel, DialPropertyPaths.EvoElectronicsBrakeBias,
        DialPropertyPaths.EvoTcMax, DialPropertyPaths.EvoTcCutMax, DialPropertyPaths.EvoAbsMax, DialPropertyPaths.EvoPhysicsTc,
        DialPropertyPaths.EvoPhysicsAbs, DialPropertyPaths.EvoPhysicsBrakeBias, DialPropertyPaths.GenericTcLevel,
        DialPropertyPaths.GenericAbsLevel, DialPropertyPaths.GenericBrakeBias,
    };

    private static void AssertSupported(IDialTelemetry telemetry, bool tc1, bool tc2, bool tc3, bool abs, bool bb)
    {
        bool[] expected = { tc1, tc2, tc3, abs, bb };
        for (int i = 0; i < Channels.Length; i++)
        {
            Assert.Equal(expected[i], telemetry.IsSupported(Channels[i]), telemetry.Name + " supports " + DialChannels.Id(Channels[i]));
        }
    }

    private static void AssertRead(IDialTelemetry telemetry, DialChannel channel, double expected)
    {
        Assert.True(telemetry.TryRead(channel, out double value), telemetry.Name + " reads " + DialChannels.Id(channel));
        Assert.Near(expected, value, Tolerance, telemetry.Name + " " + DialChannels.Id(channel));
    }

    private static void AssertNoRead(IDialTelemetry telemetry, DialChannel channel)
    {
        Assert.False(telemetry.TryRead(channel, out double value), telemetry.Name + " must not read " + (int)channel);
        Assert.True(double.IsNaN(value), telemetry.Name + " NaN for " + (int)channel);
    }

    private static void AssertMax(IDialTelemetry telemetry, DialChannel channel, double expected)
    {
        Assert.True(telemetry.TryGetMax(channel, out double max), telemetry.Name + " max " + DialChannels.Id(channel));
        Assert.Near(expected, max, Tolerance, telemetry.Name + " max " + DialChannels.Id(channel));
    }

    private static void AssertNoMax(IDialTelemetry telemetry, DialChannel channel)
    {
        Assert.False(telemetry.TryGetMax(channel, out double max), telemetry.Name + " must not report a max for " + (int)channel);
        Assert.True(double.IsNaN(max), telemetry.Name + " NaN max for " + (int)channel);
    }
}
