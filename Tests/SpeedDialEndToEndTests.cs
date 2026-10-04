using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.SpeedDial;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.SpeedDial.Persistence;
using DivebombLogistics.SpeedDial.UI;
using DivebombLogistics.Tests.Fakes;

namespace DivebombLogistics.Tests;

/// <summary>
/// End to end through the whole stack: the real <see cref="DivebombLogistics.Framework.ModuleHost"/>
/// (<see cref="ModuleTestRig"/>), the real <see cref="SpeedDialModule"/>, the real LMU dial telemetry reading the native
/// player telemetry paths (byte levels, rear brake-bias fraction) and the real <see cref="Dialer"/>, with a small LMU
/// simulator that answers each Control Mapper role press like the game does. Covers a slot dial, a Set/Reset round trip
/// and the LMU placeholder (provisional) car key, which must never be saved.
/// </summary>
internal static class SpeedDialEndToEndTests
{
    private const string Prefix = CarIdentityResolver.LmuNativeTelemetryPrefix;
    private const string Tc = Prefix + "mTC";
    private const string TcMax = Prefix + "mTCMax";
    private const string TcCut = Prefix + "mTCCut";
    private const string TcCutMax = Prefix + "mTCCutMax";
    private const string TcSlip = Prefix + "mTCSlip";
    private const string TcSlipMax = Prefix + "mTCSlipMax";
    private const string Abs = Prefix + "mABS";
    private const string AbsMax = Prefix + "mABSMax";
    private const string RearBias = Prefix + "mRearBrakeBias";

    private const string Lmu = "LMU";
    private const string NativeModel = "Ligier JS P320";
    private const string LiveryCarId = "LMP3_Team Virage 2025_12";
    private const string LiveryCarModel = "Ligier JS P320 #12 Team Virage";
    private const int MaxFrames = 3000;

    [Test]
    public static void Lmu_FinalLiveryKeyIsNotCarriedOverToAnotherCar()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string data = Path.Combine(dir.Path, SpeedDialNames.ModuleId);
        var rig = new ModuleTestRig(dir.Path);
        var module = new SpeedDialModule();
        Assert.True(rig.Add(module).Active, "module initialized");
        ISpeedDialHost host = module;
        var game = new LmuSim(rig);

        // Car A never reports its model name: after the retry window its livery key is final.
        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, new byte[32]);
        game.Set(tc: 3, tcCut: 2, tcSlip: 4, abs: 4, frontBias: 54.0);
        rig.SetGame(Lmu, LiveryCarId, LiveryCarModel, "LMP3");
        rig.RunFrame();
        rig.RunFrames((int)DivebombLogistics.Framework.CarIdentityTracker.RetryWindowSeconds + 2, 1.0);
        host.CreatePreset("A quali", false);
        rig.RunFrame();
        Assert.Equal(1, module.CarData.Presets.Count, "preset of car A");

        // Another car (different CarId) that reports its native model name.
        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, Encoding.ASCII.GetBytes("Porsche 963\0"));
        rig.SetGame(Lmu, "HY_Penske 2026_5", "963 Penske 2026", "Hypercar");
        rig.RunFrame();
        SpeedDialSnapshot snapshot = Snapshot(rig, host);
        Assert.Equal("Porsche 963", snapshot.CarKey, "native key of car B");
        Assert.Equal(0, snapshot.Presets.Count, "car B does not inherit car A's presets");
        Assert.True(WaitForFile(CarFileNaming.GetCarFilePath(data, Lmu, LiveryCarModel)), "car A's edit saved under its final key");
        rig.Host.End(rig.Now);
    }

    [Test]
    public static void Lmu_DialSlotSetResetAndProvisionalCarKey()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string data = Path.Combine(dir.Path, SpeedDialNames.ModuleId);
        string carsRoot = Path.Combine(data, CarFileNaming.CarsFolderName);
        var rig = new ModuleTestRig(dir.Path);
        var module = new SpeedDialModule();
        Assert.True(rig.Add(module).Active, "module initialized");
        ISpeedDialHost host = module;
        var game = new LmuSim(rig);

        // LMU's first frames: the native model name is not there yet, so the car key is the livery placeholder.
        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, new byte[32]);
        game.Set(tc: 3, tcCut: 2, tcSlip: 4, abs: 4, frontBias: 54.0);
        rig.SetGame(Lmu, LiveryCarId, LiveryCarModel, "LMP3");
        rig.RunFrame();
        SpeedDialSnapshot snapshot = Snapshot(rig, host);
        Assert.Equal(LiveryCarModel, snapshot.CarKey, "placeholder key");
        Assert.True(snapshot.CarKeyProvisional, "provisional");
        Assert.Equal("LMU native", snapshot.TelemetryName);
        Assert.Near(54.0, snapshot.CurrentValues[(int)DialChannel.BrakeBias], 1e-9, "front % from the rear fraction");
        Assert.Equal(10.0, snapshot.MaxValues[(int)DialChannel.Tc1], "per-car maximum");

        // Edits under the placeholder key stay in memory: nothing is written, not even after the save debounce.
        host.CreatePreset("Race", false);
        rig.RunFrame();
        string presetId = module.CarData.Presets[0].Id;
        host.SetPresetValue(presetId, DialChannel.Tc1, 6);
        host.SetPresetValue(presetId, DialChannel.Abs, 2);
        host.SetPresetValue(presetId, DialChannel.BrakeBias, 55.5);
        host.AssignSlot(0, presetId);
        rig.RunFrame();
        rig.RunFrames(4, SaveScheduler.SettingsDebounceSeconds);
        Assert.False(Directory.Exists(carsRoot), "no car file under the provisional key");

        // The native model name appears: same car, final key, data carried over and saved under that key only.
        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, Encoding.ASCII.GetBytes(NativeModel + "\0"));
        rig.RunFrames(90);
        snapshot = Snapshot(rig, host);
        Assert.Equal(NativeModel, snapshot.CarKey, "native key");
        Assert.False(snapshot.CarKeyProvisional, "final key");
        Assert.Equal(1, snapshot.Presets.Count, "preset carried over");
        Assert.Equal(presetId, snapshot.SlotPresetIds[0], "slot carried over");
        rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        string nativePath = CarFileNaming.GetCarFilePath(data, Lmu, NativeModel);
        Assert.True(WaitForFile(nativePath), "saved under the native key");
        Assert.Equal(1, Directory.GetFiles(carsRoot, "*.json", SearchOption.AllDirectories).Length, "no placeholder file");

        // Dial 1: TC 3 -> 6, ABS 4 -> 2, BB 54.0 -> 55.5, one press per step, channels in the default order.
        rig.Actions[SpeedDialNames.ModuleId].Press(SpeedDialNames.DialAction(0));
        RunUntilIdle(rig, module, game);
        Assert.Equal(
            "TractionControl+,TractionControl+,TractionControl+,ABS-,ABS-,BrakeBalanceFront,BrakeBalanceFront,BrakeBalanceFront",
            string.Join(",", rig.Roles.Presses),
            "role presses");
        DialStatus dial = module.DialerStatus;
        Assert.Equal(DialState.Completed, dial.State, dial.Message);
        Assert.Equal(ChannelResult.Reached, dial.Get(DialChannel.Tc1).Result);
        Assert.Equal(ChannelResult.Reached, dial.Get(DialChannel.Abs).Result);
        Assert.Equal(ChannelResult.Reached, dial.Get(DialChannel.BrakeBias).Result);
        Assert.Equal(ChannelResult.Skipped, dial.Get(DialChannel.Tc2).Result, "not in the preset");
        Assert.Equal(ChannelResult.Skipped, dial.Get(DialChannel.Tc3).Result, "not in the preset");
        Assert.Equal(3, dial.Get(DialChannel.Tc1).Presses);
        Assert.Equal(3.0, dial.Get(DialChannel.Tc1).Start);
        Assert.Equal(6.0, dial.Get(DialChannel.Tc1).Current);
        Assert.Equal(6.0, game.Level(Tc), "game TC");
        Assert.Equal(2.0, game.Level(Abs), "game ABS");
        Assert.Near(55.5, game.FrontBias, 1e-9, "game brake bias");
        Assert.Equal("Completed", Text(rig, SpeedDialNames.LastResultProperty));
        Assert.Equal("Race", Text(rig, SpeedDialNames.ActivePresetProperty));
        Assert.Equal(false, (bool)rig.Properties.Read(SpeedDialNames.BusyProperty));
        Assert.Equal(6.0, Value(rig, DialChannel.Tc1), "exported TC");
        Assert.Near(55.5, Value(rig, DialChannel.BrakeBias), 1e-9, "exported BB");
        ChannelLearning tcLearning = module.CarData.GetLearning(DialChannel.Tc1);
        Assert.True(tcLearning.DirectionConfirmed && tcLearning.Direction == ChannelLearning.IncreaseRaises, "TC direction learned");
        Assert.Near(0.5, module.CarData.GetLearning(DialChannel.BrakeBias).Step, 1e-9, "BB step learned");

        // Set 1 stores every channel of pair 1 (default: all five).
        rig.Actions[SpeedDialNames.ModuleId].Press(SpeedDialNames.SetAction(0));
        rig.RunFrame();
        Assert.Equal("Pair 1 stored", Text(rig, SpeedDialNames.StatusProperty));
        snapshot = Snapshot(rig, host);
        PairSummary pair = snapshot.Pairs[0];
        Assert.True(pair.HasStoredValues, "stored");
        Assert.Equal(6.0, pair.GetStoredValue(DialChannel.Tc1));
        Assert.Equal(2.0, pair.GetStoredValue(DialChannel.Tc2));
        Assert.Equal(4.0, pair.GetStoredValue(DialChannel.Tc3));
        Assert.Equal(2.0, pair.GetStoredValue(DialChannel.Abs));
        Assert.Near(55.5, pair.GetStoredValue(DialChannel.BrakeBias), 1e-9);

        // The driver changes TC, TC3 and BB by hand; Reset 1 dials back only what differs.
        game.Set(tc: 8, tcCut: 2, tcSlip: 5, abs: 2, frontBias: 53.0);
        rig.RunFrame();
        int pressesBefore = rig.Roles.Presses.Count;
        rig.Actions[SpeedDialNames.ModuleId].Press(SpeedDialNames.ResetAction(0));
        RunUntilIdle(rig, module, game);
        Assert.Equal(
            "TractionControl-,TractionControl-,TC_SlipAngle-,BrakeBalanceFront,BrakeBalanceFront,BrakeBalanceFront,BrakeBalanceFront,BrakeBalanceFront",
            string.Join(",", rig.Roles.Presses.GetRange(pressesBefore, rig.Roles.Presses.Count - pressesBefore)),
            "reset presses");
        dial = module.DialerStatus;
        Assert.Equal(DialState.Completed, dial.State, dial.Message);
        Assert.Equal(ChannelResult.Skipped, dial.Get(DialChannel.Tc2).Result, "already at the stored value");
        Assert.Equal(ChannelResult.Skipped, dial.Get(DialChannel.Abs).Result, "already at the stored value");
        Assert.Equal("Reset Pair 1", Text(rig, SpeedDialNames.ActivePresetProperty));
        Assert.Equal(6.0, game.Level(Tc));
        Assert.Equal(4.0, game.Level(TcSlip));
        Assert.Near(55.5, game.FrontBias, 1e-9);

        // A second Reset finds everything in place and presses nothing.
        pressesBefore = rig.Roles.Presses.Count;
        rig.Actions[SpeedDialNames.ModuleId].Press(SpeedDialNames.ResetAction(0));
        rig.RunFrames(30);
        Assert.Equal(pressesBefore, rig.Roles.Presses.Count, "no presses");
        Assert.Equal("Pair 1 already set", Text(rig, SpeedDialNames.StatusProperty));

        // Shutdown: everything is in the native file, nothing anywhere else.
        rig.Host.End(rig.Now);
        Assert.Equal(1, Directory.GetFiles(carsRoot, "*.json", SearchOption.AllDirectories).Length, "one car file");
        SpeedDialCarData saved = new SpeedDialStore(data, NullLog.Instance).LoadCarData(Lmu, NativeModel, null);
        Assert.Equal("Race", saved.Presets[0].Name);
        Assert.Equal(presetId, saved.SlotPresetIds[0]);
        Assert.True(saved.GetPairSnapshot(0).TryGetValue(DialChannel.BrakeBias, out double storedBias) && Math.Abs(storedBias - 55.5) < 1e-9, "pair values persisted");
        Assert.Equal(1.0, saved.GetLearning(DialChannel.Tc1).Step, "learning persisted");
    }

    private static SpeedDialSnapshot Snapshot(ModuleTestRig rig, ISpeedDialHost host)
    {
        rig.RunFrame(0.06); // longer than the 20 Hz snapshot interval
        var snapshot = new SpeedDialSnapshot();
        host.CopySnapshot(snapshot);
        return snapshot;
    }

    private static void RunUntilIdle(ModuleTestRig rig, SpeedDialModule module, LmuSim game)
    {
        rig.RunFrame(); // runs the posted action: the job starts
        game.Apply();
        for (int i = 0; i < MaxFrames && module.DialerStatus.IsBusy; i++)
        {
            rig.RunFrame();
            game.Apply();
        }

        Assert.False(module.DialerStatus.IsBusy, "the job finished: " + module.DialerStatus.Message);
    }

    private static string Text(ModuleTestRig rig, string property) => (string)rig.Properties.Read(property);

    private static double Value(ModuleTestRig rig, DialChannel channel) => (double)rig.Properties.Read(SpeedDialNames.ValueProperty(channel));

    private static bool WaitForFile(string path)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        return File.Exists(path);
    }

    /// <summary>
    /// LMU's side of the loop: native telemetry as the game publishes it (byte levels with per-car maxima of 10, the
    /// rear brake-bias fraction), moved by the user's Control Mapper roles; brake bias moves 0.5 % front per press.
    /// </summary>
    private sealed class LmuSim
    {
        private const byte MaxLevel = 10;
        private const double BiasStep = 0.5;

        private readonly ModuleTestRig rig;
        private readonly Dictionary<string, KeyValuePair<string, int>> levelRoles = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.Ordinal)
        {
            { "TractionControl+", new KeyValuePair<string, int>(Tc, 1) },
            { "TractionControl-", new KeyValuePair<string, int>(Tc, -1) },
            { "TC_PowerCut+", new KeyValuePair<string, int>(TcCut, 1) },
            { "TC_PowerCut-", new KeyValuePair<string, int>(TcCut, -1) },
            { "TC_SlipAngle+", new KeyValuePair<string, int>(TcSlip, 1) },
            { "TC_SlipAngle-", new KeyValuePair<string, int>(TcSlip, -1) },
            { "ABS+", new KeyValuePair<string, int>(Abs, 1) },
            { "ABS-", new KeyValuePair<string, int>(Abs, -1) },
        };

        private int handled;

        public LmuSim(ModuleTestRig rig)
        {
            this.rig = rig;
            rig.Reader.Set(TcMax, MaxLevel).Set(TcCutMax, MaxLevel).Set(TcSlipMax, MaxLevel).Set(AbsMax, MaxLevel);
        }

        public double FrontBias { get; private set; }

        public void Set(byte tc, byte tcCut, byte tcSlip, byte abs, double frontBias)
        {
            rig.Reader.Set(Tc, tc).Set(TcCut, tcCut).Set(TcSlip, tcSlip).Set(Abs, abs);
            SetFrontBias(frontBias);
            handled = rig.Roles.Presses.Count;
        }

        public double Level(string path) => Convert.ToDouble(rig.Reader.GetValue(path), CultureInfo.InvariantCulture);

        /// <summary>Applies the presses recorded since the last call (the game reacts before the next frame).</summary>
        public void Apply()
        {
            while (handled < rig.Roles.Presses.Count)
            {
                string role = rig.Roles.Presses[handled++];
                if (levelRoles.TryGetValue(role, out KeyValuePair<string, int> effect))
                {
                    int level = (byte)rig.Reader.GetValue(effect.Key) + effect.Value;
                    rig.Reader.Set(effect.Key, (byte)Math.Max(0, Math.Min(MaxLevel, level)));
                }
                else if (role == "BrakeBalanceFront")
                {
                    SetFrontBias(FrontBias + BiasStep);
                }
                else if (role == "BrakeBalanceRear")
                {
                    SetFrontBias(FrontBias - BiasStep);
                }
            }
        }

        private void SetFrontBias(double front)
        {
            FrontBias = front;
            rig.Reader.Set(RearBias, 1.0 - front / 100.0);
        }
    }
}
