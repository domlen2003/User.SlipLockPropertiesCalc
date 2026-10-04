using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;
using DivebombLogistics.SpeedDial;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.SpeedDial.Persistence;
using DivebombLogistics.SpeedDial.UI;
using DivebombLogistics.Tests.Fakes;

namespace DivebombLogistics.Tests;

/// <summary>
/// <see cref="SpeedDialModule"/> inside the real <see cref="ModuleHost"/> (<see cref="ModuleTestRig"/>) with the real
/// dialer and telemetry: property/action contract, button actions turned into closed-loop role presses (a small game
/// simulator moves the telemetry values per press), the press gate, cancellation, persistence, the LMU placeholder key,
/// export/import, host edits through the dispatcher, snapshot versions and the allocation-free steady state.
/// </summary>
internal static class SpeedDialModuleTests
{
    // Literal paths (CONTRACTS-SPEEDDIAL section 4: no compile dependency on the telemetry constants).
    private const string IRacingTc = "DataCorePlugin.GameRawData.Telemetry.dcTractionControl";
    private const string IRacingTc2 = "DataCorePlugin.GameRawData.Telemetry.dcTractionControl2";
    private const string IRacingAbs = "DataCorePlugin.GameRawData.Telemetry.dcABS";
    private const string IRacingBb = "DataCorePlugin.GameRawData.Telemetry.dcBrakeBias";
    private const string LmuTc = "DataCorePlugin.GameRawData.PlayerNativeTelemetry.mTC";
    private const string LmuTcMax = "DataCorePlugin.GameRawData.PlayerNativeTelemetry.mTCMax";

    private const string IRacing = "IRacing";
    private const string DefaultCar = "Mazda MX-5 Cup";
    private const string ModuleId = SpeedDialNames.ModuleId;
    private const int MaxDialFrames = 3000;

    [Test]
    public static void Init_RegistersPropertiesAndActionsInContractOrder()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new SpeedDialModule();
        ModuleSlot slot = rig.Add(module);

        Assert.True(slot.Active, "initialized: " + slot.InitError);
        Assert.Equal("SpeedDial", module.Id);
        Assert.Equal("Speed Dial", module.DisplayName);
        Assert.Equal(string.Join(",", SpeedDialNames.AllProperties), string.Join(",", rig.Properties.Names), "property names and order");
        Assert.Equal(string.Join(",", SpeedDialNames.AllActions), string.Join(",", rig.Actions[ModuleId].Names), "action names and order");
        Assert.Equal(12, module.PropertyCount, "properties");
        Assert.Equal(20, module.ActionCount, "actions (always 8 slots and 4 pairs)");

        Assert.Equal(false, (bool)rig.Properties.Read(SpeedDialNames.BusyProperty));
        Assert.Equal(string.Empty, (string)rig.Properties.Read(SpeedDialNames.StatusProperty));
        Assert.Equal(string.Empty, (string)rig.Properties.Read(SpeedDialNames.ActivePresetProperty));
        Assert.Equal(string.Empty, (string)rig.Properties.Read(SpeedDialNames.SelectedPresetProperty));
        Assert.Equal(0, (int)rig.Properties.Read(SpeedDialNames.SelectedIndexProperty));
        Assert.Equal(0, (int)rig.Properties.Read(SpeedDialNames.PresetCountProperty));
        Assert.Equal(string.Empty, (string)rig.Properties.Read(SpeedDialNames.LastResultProperty));
        foreach (DialChannel channel in DialChannels.All)
        {
            Assert.Equal(0.0, (double)rig.Properties.Read(SpeedDialNames.ValueProperty(channel)), "value " + DialChannels.Id(channel));
        }

        // The snapshot is usable before the first frame.
        var snapshot = new SpeedDialSnapshot();
        module.CopySnapshot(snapshot);
        module.CopySnapshot(null);
        Assert.False(snapshot.HasCar, "no car");
        Assert.True(snapshot.Settings != null && snapshot.Settings.SlotCount == SpeedDialSettings.DefaultSlotCount, "settings copy");
        Assert.Equal(2, snapshot.Pairs.Count, "pair summaries");
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ModuleId)), "nothing written at start");
    }

    [Test]
    public static void Values_AreExportedPerChannelWithUnknownAsZero()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);

        Assert.Equal(2.0, h.Value(DialChannel.Tc1));
        Assert.Equal(1.0, h.Value(DialChannel.Tc2));
        Assert.Equal(0.0, h.Value(DialChannel.Tc3), "iRacing has no TC3");
        Assert.Equal(3.0, h.Value(DialChannel.Abs));
        Assert.Equal(54.0, h.Value(DialChannel.BrakeBias));

        h.Rig.Reader.Remove(IRacingAbs);
        h.Rig.RunFrame(0.06);
        Assert.Equal(0.0, h.Value(DialChannel.Abs), "missing value exported as 0");
        SpeedDialSnapshot snapshot = h.Snapshot();
        Assert.True(double.IsNaN(snapshot.CurrentValues[(int)DialChannel.Abs]), "unknown in the snapshot");
        Assert.Equal(54.0, snapshot.CurrentValues[(int)DialChannel.BrakeBias]);
        Assert.False(snapshot.ChannelSupported[(int)DialChannel.Tc3], "TC3 unsupported");
        Assert.True(snapshot.ChannelSupported[(int)DialChannel.Tc1], "TC1 supported");
        Assert.Equal("iRacing", snapshot.TelemetryName);
        Assert.True(snapshot.TelemetryDescription.Length > 0, "telemetry diagnostics");
        Assert.True(snapshot.GameRunning && snapshot.HasCar, "game and car");
        Assert.Equal(DefaultCar, snapshot.CarKey);
        Assert.False(snapshot.CarKeyProvisional, "final key");
    }

    [Test]
    public static void DialSlot_DialsTheAssignedPresetClosedLoop()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Quali", (DialChannel.Tc1, 5), (DialChannel.BrakeBias, 55.0));
        h.Host.AssignSlot(0, id);
        h.Rig.RunFrame();

        h.Press(SpeedDialNames.DialAction(0));
        h.RunUntilIdle();

        Assert.Equal(5.0, h.Raw(IRacingTc), "TC dialed");
        Assert.Equal(55.0, h.Raw(IRacingBb), "brake bias dialed");
        Assert.Equal(1.0, h.Raw(IRacingTc2), "not part of the preset");
        Assert.Equal(
            "TractionControl+,TractionControl+,TractionControl+,BrakeBalanceFront,BrakeBalanceFront",
            string.Join(",", h.Rig.Roles.Presses),
            "one press per step, channels in the default order");
        Assert.Equal("Completed", h.Text(SpeedDialNames.LastResultProperty));
        Assert.Equal("Quali", h.Text(SpeedDialNames.ActivePresetProperty));
        Assert.Equal(false, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty));
        Assert.Equal(DialState.Completed, h.Module.DialerStatus.State);
        Assert.Equal(ChannelResult.Reached, h.Module.DialerStatus.Get(DialChannel.Tc1).Result);
        Assert.Equal(ChannelResult.Skipped, h.Module.DialerStatus.Get(DialChannel.Abs).Result);

        SpeedDialSnapshot snapshot = h.Snapshot();
        Assert.Equal(DialState.Completed, snapshot.Dial.State, "dial status copied");
        Assert.Equal(5, snapshot.Dial.TotalPresses());
        Assert.Equal(5.0, snapshot.CurrentValues[(int)DialChannel.Tc1]);
    }

    [Test]
    public static void AssignSlot_MovesThePresetSoItNeverSitsOnTwoSlots()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string race = h.CreatePreset("Race", (DialChannel.Tc1, 4));
        string quali = h.CreatePreset("Quali", (DialChannel.Tc1, 5));
        string wet = h.CreatePreset("Wet", (DialChannel.Tc1, 6));
        h.Host.AssignSlot(0, race);
        h.Host.AssignSlot(1, quali);
        h.Host.AssignSlot(2, wet);
        h.Rig.RunFrame();

        // Two quick moves before the UI saw the first one (Down, Down on the slot box).
        h.Host.AssignSlot(1, race);
        h.Host.AssignSlot(2, race);
        h.Rig.RunFrame();

        string[] slots = h.Module.CarData.SlotPresetIds;
        int holding = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (string.Equals(slots[i], race, StringComparison.OrdinalIgnoreCase))
            {
                holding++;
            }
        }

        Assert.Equal(1, holding, "Race on exactly one slot");
        Assert.Equal(race, slots[2], "on the last chosen slot");
        Assert.True(slots[0] == null, "old slot freed");
        Assert.True(slots[1] == null, "intermediate slot freed");
    }

    [Test]
    public static void DialSlot_LearnsTheChannelsAndPersistsTheLearning()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Race", (DialChannel.Tc1, 4), (DialChannel.BrakeBias, 53.0));
        h.Host.AssignSlot(1, id);
        h.Rig.RunFrame();

        h.Press(SpeedDialNames.DialAction(1));
        h.RunUntilIdle();

        ChannelLearning tc = h.Module.CarData.GetLearning(DialChannel.Tc1);
        ChannelLearning bb = h.Module.CarData.GetLearning(DialChannel.BrakeBias);
        Assert.Equal(1.0, tc.Step, "TC step");
        Assert.True(tc.DirectionConfirmed, "TC direction confirmed");
        Assert.Equal(0.5, bb.Step, "BB step");
        Assert.Equal(ChannelLearning.IncreaseRaises, bb.Direction);
        SpeedDialSnapshot snapshot = h.Snapshot();
        Assert.Equal(0.5, snapshot.LearnedStep[(int)DialChannel.BrakeBias], "learning in the snapshot");

        h.Rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        Assert.True(WaitForFile(h.CarFilePath()), "car data saved after the debounce");
        Assert.True(h.Module.Store.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "write finished");
        SpeedDialCarData saved = new SpeedDialStore(h.DataDirectory, NullLog.Instance).LoadCarData(IRacing, DefaultCar, null);
        Assert.Equal(1.0, saved.GetLearning(DialChannel.Tc1).Step, "learned step persisted");
        Assert.Equal(0.5, saved.GetLearning(DialChannel.BrakeBias).Step);
    }

    [Test]
    public static void LastResult_ReportsPartialAndFailedJobs()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Mixed", (DialChannel.Tc1, 4), (DialChannel.Abs, 5));
        h.Host.EditSettings(s => s.GetBinding(DialChannel.Abs).IncreaseRole = string.Empty);
        h.Rig.RunFrame();

        h.Host.ApplyPreset(id);
        h.RunUntilIdle();
        Assert.Equal("Partial", h.Text(SpeedDialNames.LastResultProperty), "TC reached, ABS without a role");
        Assert.Equal(ChannelResult.NoBinding, h.Module.DialerStatus.Get(DialChannel.Abs).Result);
        Assert.Equal(4.0, h.Raw(IRacingTc));
        Assert.Equal(h.Module.DialerStatus.Message, h.Text(SpeedDialNames.StatusProperty), "status shows the dialer's message");

        h.Rig.Roles.IsAvailable = false;
        string far = h.CreatePreset("Far", (DialChannel.Tc1, 8));
        h.Host.ApplyPreset(far);
        h.RunUntilIdle();
        Assert.Equal("Failed", h.Text(SpeedDialNames.LastResultProperty), "Control Mapper unavailable");
        Assert.Equal(ChannelResult.NoBinding, h.Module.DialerStatus.Get(DialChannel.Tc1).Result);
        Assert.Equal(4.0, h.Raw(IRacingTc), "nothing pressed");
        Assert.Equal("Far", h.Text(SpeedDialNames.ActivePresetProperty), "label of the last job");
    }

    [Test]
    public static void DialSlot_UnusedOrEmptySlotsAreIgnoredWithAStatus()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);

        h.Press(SpeedDialNames.DialAction(4));
        h.Rig.RunFrame();
        Assert.Equal("Slot 5 is not in use", h.Text(SpeedDialNames.StatusProperty), "beyond the slot count (4)");

        h.Press(SpeedDialNames.DialAction(1));
        h.Rig.RunFrame();
        Assert.Equal("Slot 2 is empty", h.Text(SpeedDialNames.StatusProperty));

        h.Host.EditSettings(s => s.SlotCount = 6);
        h.Rig.RunFrame();
        h.Press(SpeedDialNames.DialAction(4));
        h.Rig.RunFrame();
        Assert.Equal("Slot 5 is empty", h.Text(SpeedDialNames.StatusProperty), "in use after raising the slot count");

        h.RunFrames(20);
        Assert.Equal(0, h.Rig.Roles.Presses.Count, "nothing pressed");
        Assert.Equal(false, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty));
        Assert.Equal(string.Empty, h.Text(SpeedDialNames.LastResultProperty), "no job ran");
    }

    [Test]
    public static void Actions_WithoutCarOrRunningGame_OnlyReportAStatus()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new SpeedDialModule();
        rig.Add(module);
        rig.RunFrame();
        RecordingActionRegistry actions = rig.Actions[ModuleId];

        foreach (string action in new[]
        {
            SpeedDialNames.DialAction(0), SpeedDialNames.NextPresetAction, SpeedDialNames.PreviousPresetAction,
            SpeedDialNames.ApplySelectedPresetAction, SpeedDialNames.SetAction(0), SpeedDialNames.ResetAction(0),
        })
        {
            actions.Press(action);
            rig.RunFrame();
            Assert.Equal("No car loaded", (string)rig.Properties.Read(SpeedDialNames.StatusProperty), action);
        }

        actions.Press(SpeedDialNames.CancelAction);
        rig.RunFrame(); // nothing to cancel: no error
        ISpeedDialHost noCar = module;
        Assert.Equal("Error: no car loaded.", noCar.ExportPresets(Path.Combine(dir.Path, "x.json")), "export without a car");
        Assert.Equal("Error: no car loaded.", noCar.ImportPresets(Path.Combine(dir.Path, "x.json")), "import without a car");
        Assert.False(File.Exists(Path.Combine(dir.Path, "x.json")), "nothing exported");

        // A car stays loaded after the game stops, but dialing needs the running game.
        var h = new Harness(Path.Combine(dir.Path, "second"));
        string id = h.CreatePreset("Quali", (DialChannel.Tc1, 5));
        h.Host.AssignSlot(0, id);
        h.Rig.RunFrame();
        h.Rig.Frame.GameRunning = false;
        h.Rig.RunFrame();
        h.Press(SpeedDialNames.DialAction(0));
        h.Rig.RunFrame();
        Assert.Equal("No game running", h.Text(SpeedDialNames.StatusProperty));
        Assert.Equal(0, h.Rig.Roles.Presses.Count, "nothing pressed");
        Assert.Equal(0, rig.Roles.Presses.Count, "nothing pressed without a car");
    }

    [Test]
    public static void NextPrevious_WrapAroundAndApplySelectedDialsTheSelection()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        h.CreatePreset("A", (DialChannel.Tc1, 4));
        h.CreatePreset("B", (DialChannel.Tc1, 3));
        h.CreatePreset("C", (DialChannel.Abs, 5));

        Assert.Equal("A", h.Text(SpeedDialNames.SelectedPresetProperty), "the first preset became selected");
        Assert.Equal(1, h.Int(SpeedDialNames.SelectedIndexProperty));
        Assert.Equal(3, h.Int(SpeedDialNames.PresetCountProperty));

        h.Host.SelectPreset(null);
        h.Rig.RunFrame();
        Assert.Equal(0, h.Int(SpeedDialNames.SelectedIndexProperty), "cleared");
        Assert.Equal(string.Empty, h.Text(SpeedDialNames.SelectedPresetProperty));

        string[] expected = { "C", "A", "B", "A", "C" };
        string[] actions =
        {
            SpeedDialNames.PreviousPresetAction, // none selected: Previous picks the last
            SpeedDialNames.NextPresetAction, // wraps from the last to the first
            SpeedDialNames.NextPresetAction,
            SpeedDialNames.PreviousPresetAction,
            SpeedDialNames.PreviousPresetAction, // wraps from the first to the last
        };
        int[] indexes = { 3, 1, 2, 1, 3 };
        for (int i = 0; i < actions.Length; i++)
        {
            h.Press(actions[i]);
            h.Rig.RunFrame();
            Assert.Equal(expected[i], h.Text(SpeedDialNames.SelectedPresetProperty), "step " + i);
            Assert.Equal(expected[i], h.Text(SpeedDialNames.StatusProperty), "status shows the name, step " + i);
            Assert.Equal(indexes[i], h.Int(SpeedDialNames.SelectedIndexProperty), "index, step " + i);
        }

        Assert.Equal(0, h.Rig.Roles.Presses.Count, "selecting presses nothing");
        h.Press(SpeedDialNames.ApplySelectedPresetAction);
        h.RunUntilIdle();
        Assert.Equal(5.0, h.Raw(IRacingAbs), "C dialed");
        Assert.Equal(2.0, h.Raw(IRacingTc), "TC untouched");
        Assert.Equal("C", h.Text(SpeedDialNames.ActivePresetProperty));
        Assert.Equal("Completed", h.Text(SpeedDialNames.LastResultProperty));
        Assert.Equal(h.Module.CarData.Presets[2].Id, h.Snapshot().SelectedPresetId, "selection in the snapshot");

        h.Host.SelectPreset(null);
        h.Rig.RunFrame();
        h.Press(SpeedDialNames.ApplySelectedPresetAction);
        h.Rig.RunFrame();
        Assert.Equal("No preset selected", h.Text(SpeedDialNames.StatusProperty));
    }

    [Test]
    public static void SetReset_StoresValidChannelsAndResetDialsOnlyWhatDiffers()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);

        h.Press(SpeedDialNames.SetAction(0));
        h.Rig.RunFrame();
        Assert.Equal("Pair 1 stored", h.Text(SpeedDialNames.StatusProperty));
        DialSnapshot stored = h.Module.CarData.GetPairSnapshot(0);
        Assert.Equal(4, ChannelValues.CountFinite(stored.Values), "TC3 has no telemetry on iRacing: not stored");
        Assert.False(stored.TryGetValue(DialChannel.Tc3, out _), "no TC3");
        Assert.True(stored.TryGetValue(DialChannel.BrakeBias, out double bb) && bb == 54.0, "BB stored");
        Assert.True(stored.CapturedUtc.HasValue, "capture time");
        Assert.True(h.Snapshot().Pairs[0].HasStoredValues, "pair summary rebuilt");

        // The driver changes TC and brake bias by hand, then presses Reset.
        h.SetValues(tc: 4, tc2: 1, abs: 3, bb: 55.5);
        h.Rig.RunFrame();
        h.Press(SpeedDialNames.ResetAction(0));
        h.RunUntilIdle();

        Assert.Equal(2.0, h.Raw(IRacingTc), "TC back");
        Assert.Equal(54.0, h.Raw(IRacingBb), "BB back");
        Assert.Equal(
            "TractionControl-,TractionControl-,BrakeBalanceRear,BrakeBalanceRear,BrakeBalanceRear",
            string.Join(",", h.Rig.Roles.Presses),
            "only the differing channels");
        Assert.Equal(ChannelResult.Skipped, h.Module.DialerStatus.Get(DialChannel.Abs).Result, "ABS not part of the job");
        Assert.Equal(ChannelResult.Skipped, h.Module.DialerStatus.Get(DialChannel.Tc2).Result, "TC2 not part of the job");
        Assert.Equal("Reset Pair 1", h.Text(SpeedDialNames.ActivePresetProperty));
        Assert.Equal("Completed", h.Text(SpeedDialNames.LastResultProperty));

        int presses = h.Rig.Roles.Presses.Count;
        h.Press(SpeedDialNames.ResetAction(0));
        h.Rig.RunFrame();
        Assert.Equal("Pair 1 already set", h.Text(SpeedDialNames.StatusProperty));
        h.RunFrames(10);
        Assert.Equal(presses, h.Rig.Roles.Presses.Count, "no new job");
        Assert.Equal(false, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty));
    }

    [Test]
    public static void SetReset_EdgeCasesReportAStatus()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);

        h.Press(SpeedDialNames.ResetAction(1));
        h.Rig.RunFrame();
        Assert.Equal("Pair 2: nothing stored", h.Text(SpeedDialNames.StatusProperty));

        h.Press(SpeedDialNames.SetAction(1));
        h.Rig.RunFrame();
        Assert.Equal("Pair 2 stored", h.Text(SpeedDialNames.StatusProperty), "default pair 2 = brake bias");

        h.Rig.Reader.Remove(IRacingBb);
        h.Rig.RunFrame();
        h.Press(SpeedDialNames.SetAction(1));
        h.Rig.RunFrame();
        Assert.Equal("Pair 2: no values to store", h.Text(SpeedDialNames.StatusProperty));
        Assert.True(h.Module.CarData.GetPairSnapshot(1).TryGetValue(DialChannel.BrakeBias, out double kept) && kept == 54.0, "old value kept");

        h.Press(SpeedDialNames.SetAction(2));
        h.Rig.RunFrame();
        Assert.Equal("Pair 3 is not defined", h.Text(SpeedDialNames.StatusProperty));
        h.Press(SpeedDialNames.ResetAction(3));
        h.Rig.RunFrame();
        Assert.Equal("Pair 4 is not defined", h.Text(SpeedDialNames.StatusProperty));

        // Renamed pairs use their name in the texts and the job label.
        h.Host.EditSettings(s => s.Pairs[1].Name = "Wet");
        h.Rig.Reader.Set(IRacingBb, 56.0);
        h.Rig.RunFrame();
        h.Press(SpeedDialNames.ResetAction(1));
        h.RunUntilIdle();
        Assert.Equal("Reset Wet", h.Text(SpeedDialNames.ActivePresetProperty));
        Assert.Equal(54.0, h.Raw(IRacingBb), "dialed back");
        Assert.Equal(0, h.Rig.Roles.Presses.FindAll(r => r.StartsWith("Traction", StringComparison.Ordinal)).Count, "BB only");
    }

    [Test]
    public static void Gate_NoPressesWhilePausedInMenuReplayOrSpectating()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string[] gates = { "paused", "menu", "replay", "spectating" };
        foreach (string gate in gates)
        {
            var h = new Harness(Path.Combine(dir.Path, gate));
            string id = h.CreatePreset("Quali", (DialChannel.Tc1, 5));
            h.Host.AssignSlot(0, id);
            h.Rig.RunFrame();

            SetGate(h.Rig.Frame, gate, true);
            h.Press(SpeedDialNames.DialAction(0));
            h.RunFrames(60);
            Assert.Equal(0, h.Rig.Roles.Presses.Count, gate + ": no presses while the gate is closed");
            Assert.Equal(DialState.Paused, h.Module.DialerStatus.State, gate + ": paused");
            Assert.Equal(true, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty), gate + ": still busy");

            SetGate(h.Rig.Frame, gate, false);
            h.RunUntilIdle();
            Assert.Equal(5.0, h.Raw(IRacingTc), gate + ": resumed and completed");
            Assert.Equal("Completed", h.Text(SpeedDialNames.LastResultProperty), gate);
        }
    }

    [Test]
    public static void Gate_PausedTooLongCancelsTheJob()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Quali", (DialChannel.Tc1, 5));
        h.Host.AssignSlot(0, id);
        h.Rig.RunFrame();

        h.Rig.Frame.GamePaused = true;
        h.Press(SpeedDialNames.DialAction(0));
        h.Rig.RunFrame();
        double timeoutSeconds = DialTiming.DefaultGatePauseTimeoutMs / 1000.0;
        for (double waited = 0; waited < timeoutSeconds + 1.0; waited += 0.5)
        {
            h.Rig.RunFrame(0.5);
        }

        Assert.Equal(false, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty), "cancelled");
        Assert.Equal(DialState.Cancelled, h.Module.DialerStatus.State);
        Assert.Equal("Cancelled", h.Text(SpeedDialNames.LastResultProperty));
        h.Rig.Frame.GamePaused = false;
        h.RunFrames(30);
        Assert.Equal(0, h.Rig.Roles.Presses.Count, "never pressed");
    }

    [Test]
    public static void GameStop_CancelsTheJobAndZeroesTheValues()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Far", (DialChannel.Tc1, 9));
        h.Host.AssignSlot(0, id);
        h.Rig.RunFrame();
        h.Press(SpeedDialNames.DialAction(0));
        h.RunFrames(3);
        Assert.Equal(true, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty), "running");

        h.Rig.Frame.GameRunning = false;
        h.Rig.RunFrame();

        Assert.Equal(false, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty));
        Assert.Equal("Cancelled", h.Text(SpeedDialNames.LastResultProperty));
        Assert.Equal("Cancelled: game stopped", h.Text(SpeedDialNames.StatusProperty));
        Assert.Equal(0.0, h.Value(DialChannel.Tc1), "values zeroed");
        Assert.Equal(0.0, h.Value(DialChannel.BrakeBias));
        int presses = h.Rig.Roles.Presses.Count;
        h.RunFrames(30);
        Assert.Equal(presses, h.Rig.Roles.Presses.Count, "no presses after the stop");
    }

    [Test]
    public static void Cancel_StopsTheJob()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Far", (DialChannel.Tc1, 9));
        h.Host.ApplyPreset(id);
        h.RunFrames(3);
        Assert.Equal(true, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty), "running");

        h.Press(SpeedDialNames.CancelAction);
        h.RunFrames(1);
        int presses = h.Rig.Roles.Presses.Count;
        h.RunFrames(30);

        Assert.Equal(false, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty));
        Assert.Equal("Cancelled", h.Text(SpeedDialNames.LastResultProperty));
        Assert.Equal("Cancelled", h.Text(SpeedDialNames.StatusProperty));
        Assert.Equal(presses, h.Rig.Roles.Presses.Count, "no presses after the cancel");
        Assert.True(h.Raw(IRacingTc) < 9.0, "stopped before the target");

        h.Host.ApplyPreset(id);
        h.RunFrames(3);
        h.Host.CancelDial();
        h.RunFrames(1);
        Assert.Equal(DialState.Cancelled, h.Module.DialerStatus.State, "host cancel");
    }

    [Test]
    public static void CarChange_CancelsSavesTheOldCarAndLoadsTheNewOne()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path, "Car A");
        string id = h.CreatePreset("A1", (DialChannel.Tc1, 9));
        h.Host.AssignSlot(0, id);
        h.Rig.RunFrame();
        h.Press(SpeedDialNames.DialAction(0));
        h.RunFrames(3);
        Assert.Equal(true, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty), "running");
        SpeedDialSnapshot before = h.Snapshot();

        h.Rig.SetGame(IRacing, "id-b", "Car B");
        h.Rig.RunFrame();

        Assert.Equal(false, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty), "cancelled by the car change");
        Assert.Equal("Cancelled", h.Text(SpeedDialNames.LastResultProperty));
        Assert.Equal(0, h.Int(SpeedDialNames.PresetCountProperty), "car B has no presets");
        Assert.Equal(string.Empty, h.Text(SpeedDialNames.SelectedPresetProperty));
        string pathA = CarFileNaming.GetCarFilePath(h.DataDirectory, IRacing, "Car A");
        string pathB = CarFileNaming.GetCarFilePath(h.DataDirectory, IRacing, "Car B");
        Assert.True(WaitForFile(pathA), "car A flushed on the car change");
        Assert.False(File.Exists(pathB), "untouched car B is not written");
        SpeedDialSnapshot after = h.Snapshot();
        Assert.Equal("Car B", after.CarKey);
        Assert.True(after.CarDataVersion > before.CarDataVersion, "car data version");
        Assert.True(after.PresetsVersion > before.PresetsVersion, "presets version");
        Assert.Equal(pathB, after.CarFilePath);

        h.Rig.SetGame(IRacing, "id-a", "Car A");
        h.Rig.RunFrame();
        Assert.Equal(1, h.Int(SpeedDialNames.PresetCountProperty), "car A's presets again");
        Assert.Equal("A1", h.Module.CarData.Presets[0].Name);
        Assert.Equal(h.Module.CarData.Presets[0].Id, h.Module.CarData.SlotPresetIds[0], "slot kept");

        h.Rig.Host.End(h.Rig.Now);
        Assert.False(File.Exists(pathB), "End saves only edited data");
    }

    [Test]
    public static void Fault_CancelsTheJobAndZeroesTheOutputs()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Far", (DialChannel.Tc1, 9));
        h.Host.ApplyPreset(id);
        h.RunFrames(3);
        Assert.Equal(true, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty), "running");

        h.Rig.RunFailedFrame(new InvalidOperationException("frame broken"));

        Assert.Equal(false, (bool)h.Rig.Properties.Read(SpeedDialNames.BusyProperty));
        Assert.Equal("Cancelled", h.Text(SpeedDialNames.LastResultProperty));
        Assert.Equal(0.0, h.Value(DialChannel.Tc1), "values zeroed");
        var snapshot = new SpeedDialSnapshot();
        h.Host.CopySnapshot(snapshot);
        Assert.True(snapshot.LastError.Contains("frame broken"), "shell error in the diagnostics: " + snapshot.LastError);
    }

    [Test]
    public static void RoleOutputException_IsContainedByTheModule()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var frame = new FrameContext();
        var reader = new FakeTelemetryReader();
        var host = new ModuleHost(frame, reader, NullLog.Instance);
        var module = new SpeedDialModule();
        ModuleSlot slot = host.Add(module, NullLog.Instance);
        var actions = new RecordingActionRegistry(slot.Dispatcher);
        var properties = new RecordingPropertyRegistry();
        double now = 100.0;
        Assert.True(host.Initialize(slot, new ModuleContext
        {
            Id = module.Id,
            Reader = reader,
            Frame = frame,
            Car = host.Car,
            Properties = properties,
            Actions = actions,
            Roles = new ThrowingRoleOutput(),
            Dispatcher = slot.Dispatcher,
            Errors = slot.Errors,
            Diagnostics = host.Diagnostics,
            DataDirectory = Path.Combine(dir.Path, module.Id),
            Clock = () => now,
        }), "initialized");
        reader.Set(IRacingTc, 2.0);
        frame.GameRunning = true;
        frame.GameName = IRacing;
        frame.CarModel = DefaultCar;
        void RunFrame()
        {
            now += ModuleTestRig.FrameSeconds;
            frame.WallTime = now;
            host.BeginFrame(now);
            host.EndFrame(now, host.PrepareFrame(now));
        }

        RunFrame();
        module.CreatePreset("Quali", false);
        RunFrame();
        module.SetPresetValue(module.CarData.Presets[0].Id, DialChannel.Tc1, 5);
        module.ApplyPreset(module.CarData.Presets[0].Id);
        for (int i = 0; i < 10; i++)
        {
            RunFrame();
        }

        Assert.Equal(false, (bool)properties.Read(SpeedDialNames.BusyProperty), "job cancelled after the error");
        Assert.Equal("Cancelled", (string)properties.Read(SpeedDialNames.LastResultProperty));
        Assert.True(slot.Errors.LastError.Contains(ThrowingRoleOutput.Message), "reported: " + slot.Errors.LastError);
        Assert.Equal(0, slot.FaultCount, "contained inside the module");
        Assert.Equal(2.0, (double)properties.Read(SpeedDialNames.ValueProperty(DialChannel.Tc1)), "values still read");
    }

    [Test]
    public static void Settings_AreEditedOnTheDataThreadAndSavedDebouncedAndOnEnd()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string settingsPath = Path.Combine(h.DataDirectory, SpeedDialStore.SettingsFileName);
        int version = h.Snapshot().SettingsVersion;

        h.Host.EditSettings(s => s.SlotCount = 6);
        Assert.Equal(SpeedDialSettings.DefaultSlotCount, h.Module.Settings.SlotCount, "queued, not applied on the UI thread");
        h.Rig.RunFrame(0.06);
        Assert.Equal(6, h.Module.Settings.SlotCount, "applied on the data thread");
        SpeedDialSnapshot snapshot = h.Snapshot();
        Assert.Equal(version + 1, snapshot.SettingsVersion, "settings version");
        Assert.Equal(6, snapshot.Settings.SlotCount, "copy for the UI");
        Assert.Equal(6, snapshot.SlotCount);
        Assert.False(ReferenceEquals(snapshot.Settings, h.Module.Settings), "never the live object");
        Assert.False(File.Exists(settingsPath), "debounced");

        h.Rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        Assert.True(WaitForFile(settingsPath), "saved after the debounce");

        // A failing edit is still repaired and saved.
        h.Host.EditSettings(s =>
        {
            s.SlotCount = 99;
            throw new InvalidOperationException("edit failed");
        });
        h.Rig.RunFrame(0.06);
        Assert.Equal(SpeedDialSettings.MaxSlotCount, h.Module.Settings.SlotCount, "normalized after the failing edit");
        Assert.True(h.Snapshot().LastError.Contains("edit failed"), "edit error reported");

        h.Host.EditSettings(s => s.Timing.GapMs = 150);
        h.Rig.RunFrame();
        h.Rig.Host.End(h.Rig.Now);
        SpeedDialSettings saved = new SpeedDialStore(h.DataDirectory, NullLog.Instance).LoadSettings(out _);
        Assert.Equal(SpeedDialSettings.MaxSlotCount, saved.SlotCount, "saved");
        Assert.Equal(150, saved.Timing.GapMs, "synchronous save in End");

        var restarted = new Harness(dir.Path);
        Assert.Equal(150, restarted.Module.Settings.Timing.GapMs, "loaded at Init");
    }

    [Test]
    public static void CarData_IsSavedDebouncedAndOnEndAndLoadedAtTheNextStart()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string path = h.CarFilePath();

        h.Host.CreatePreset("Race", true);
        h.Rig.RunFrame();
        Assert.False(File.Exists(path), "debounced");
        h.Rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        Assert.True(WaitForFile(path), "saved after the debounce");
        Assert.True(h.Module.Store.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "write finished");
        SpeedDialCarData saved = new SpeedDialStore(h.DataDirectory, NullLog.Instance).LoadCarData(IRacing, DefaultCar, null);
        Assert.Equal("Race", saved.Presets[0].Name);
        Assert.True(saved.Presets[0].TryGetValue(DialChannel.BrakeBias, out double bb) && bb == 54.0, "from the current values");
        Assert.Equal(4, saved.Presets[0].IncludedCount(), "every channel with telemetry");

        h.Host.RenamePreset(saved.Presets[0].Id, "Race 2");
        h.Rig.RunFrame();
        h.Rig.Host.End(h.Rig.Now);
        Assert.Equal("Race 2", new SpeedDialStore(h.DataDirectory, NullLog.Instance).LoadCarData(IRacing, DefaultCar, null).Presets[0].Name, "End saves");

        var restarted = new Harness(dir.Path);
        Assert.Equal(1, restarted.Int(SpeedDialNames.PresetCountProperty), "loaded for the car");
        Assert.Equal("Race 2", restarted.Text(SpeedDialNames.SelectedPresetProperty), "selection persisted");
    }

    [Test]
    public static void CarData_CorruptFileIsQuarantinedAndTheCarStartsEmpty()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string data = Path.Combine(dir.Path, ModuleId);
        string path = CarFileNaming.GetCarFilePath(data, IRacing, DefaultCar);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"Presets\":[{");

        var h = new Harness(dir.Path);

        Assert.Equal(0, h.Int(SpeedDialNames.PresetCountProperty), "new data");
        Assert.Equal(1, Directory.GetFiles(Path.GetDirectoryName(path), "*" + JsonFile.QuarantineInfix + "*").Length, "quarantined");
        h.Host.CreatePreset("Fresh", false);
        h.Rig.RunFrame();
        h.Rig.Host.End(h.Rig.Now);
        Assert.Equal("Fresh", new SpeedDialStore(data, NullLog.Instance).LoadCarData(IRacing, DefaultCar, null).Presets[0].Name, "saved normally");
    }

    [Test]
    public static void LmuPlaceholderKey_IsNeverSavedAndCarriesOverToTheNativeKey()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new SpeedDialModule();
        rig.Add(module);
        ISpeedDialHost host = module;
        string carsRoot = Path.Combine(dir.Path, ModuleId, CarFileNaming.CarsFolderName);
        var game = new GameSim(rig).Map("TractionControl+", LmuTc, 1).Map("TractionControl-", LmuTc, -1);

        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, new byte[30]); // LMU's first frames: no model name yet
        rig.Reader.Set(LmuTc, (byte)2);
        rig.Reader.Set(LmuTcMax, (byte)10);
        rig.SetGame("LMU", "GT3_Iron Lynx 2026_61", "296GT3 Custom Team 2025", "GT3");
        rig.RunFrame();
        var snapshot = new SpeedDialSnapshot();
        rig.RunFrame(0.1);
        host.CopySnapshot(snapshot);
        Assert.Equal("296GT3 Custom Team 2025", snapshot.CarKey, "placeholder key");
        Assert.True(snapshot.CarKeyProvisional, "shown as provisional");
        Assert.Equal(10.0, snapshot.MaxValues[(int)DialChannel.Tc1], "LMU maximum");

        host.CreatePreset("Quali", false);
        rig.RunFrame();
        host.SetPresetValue(module.CarData.Presets[0].Id, DialChannel.Tc1, 6);
        host.AssignSlot(0, module.CarData.Presets[0].Id);
        rig.RunFrame();
        rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        Assert.False(Directory.Exists(carsRoot), "no file for the placeholder key");

        // A dial started under the placeholder key survives the switch to the native key of the same car.
        rig.Actions[ModuleId].Press(SpeedDialNames.DialAction(0));
        rig.RunFrame();
        game.Apply();
        Assert.True(module.DialerStatus.IsBusy, "dialing");
        rig.Reader.Set(CarIdentityResolver.LmuVehicleModelPath, Encoding.ASCII.GetBytes("Ferrari 296 GT3\0"));
        for (int i = 0; i < MaxDialFrames && (module.DialerStatus.IsBusy || i < 70); i++)
        {
            rig.RunFrame();
            game.Apply();
        }

        rig.RunFrame(0.1);
        host.CopySnapshot(snapshot);
        Assert.Equal("Ferrari 296 GT3", snapshot.CarKey, "native key");
        Assert.False(snapshot.CarKeyProvisional, "final");
        Assert.Equal(1, snapshot.Presets.Count, "preset carried over");
        Assert.Equal("Quali", snapshot.Presets[0].Name);
        Assert.Equal(snapshot.Presets[0].Id, snapshot.SlotPresetIds[0], "slot carried over");
        Assert.Equal(DialState.Completed, snapshot.Dial.State, "the job was not cancelled by the key resolution");
        Assert.Equal(6.0, Convert.ToDouble(rig.Reader.GetValue(LmuTc), CultureInfo.InvariantCulture), "dialed");

        rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        string nativePath = CarFileNaming.GetCarFilePath(Path.Combine(dir.Path, ModuleId), "LMU", "Ferrari 296 GT3");
        Assert.True(WaitForFile(nativePath), "native data saved");
        rig.Host.End(rig.Now);
        Assert.Equal(1, Directory.GetFiles(carsRoot, "*.json", SearchOption.AllDirectories).Length, "only the native file exists");
        Assert.Equal("Quali", new SpeedDialStore(Path.Combine(dir.Path, ModuleId), NullLog.Instance).LoadCarData("LMU", "Ferrari 296 GT3", null).Presets[0].Name);
    }

    [Test]
    public static void ExportImport_AppendsPresetsToTheCurrentCar()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path, "Car A");
        string p1 = h.CreatePreset("P1", (DialChannel.Tc1, 3));
        h.CreatePreset("P2", (DialChannel.Abs, 5));
        h.Host.AssignSlot(0, p1);
        h.Rig.RunFrame();
        string file = Path.Combine(dir.Path, "exports", "car-a.json");

        string exported = h.Host.ExportPresets(file);
        Assert.True(exported.StartsWith("Exported 2 preset(s) of Car A", StringComparison.Ordinal), exported);
        Assert.True(File.Exists(file), "file written");

        h.Rig.SetGame(IRacing, "id-b", "Car B");
        h.Rig.RunFrame();
        int carVersion = h.Snapshot().CarDataVersion;
        string imported = h.Host.ImportPresets(file);
        Assert.True(imported.StartsWith("Imported 2 preset(s) into Car B", StringComparison.Ordinal), imported);
        Assert.Equal(2, h.Int(SpeedDialNames.PresetCountProperty), "appended");
        Assert.True(h.Module.CarData.SlotPresetIds[0] == null, "slots of the file ignored");
        Assert.Equal(p1, h.Module.CarData.Presets[0].Id, "free ids kept");
        h.Rig.RunFrame(0.06);
        Assert.True(h.Snapshot().CarDataVersion > carVersion, "car data version");

        imported = h.Host.ImportPresets(file);
        Assert.True(imported.StartsWith("Imported 2 preset(s)", StringComparison.Ordinal), imported);
        Assert.Equal(4, h.Module.CarData.Presets.Count, "appended again");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DialPreset preset in h.Module.CarData.Presets)
        {
            Assert.True(ids.Add(preset.Id), "colliding ids renewed");
        }

        string foreign = Path.Combine(dir.Path, "foreign.json");
        File.WriteAllText(foreign, "{\"SchemaVersion\":1,\"CarKey\":\"x\"}");
        Assert.Equal("Error: no file selected.", h.Host.ImportPresets(string.Empty));
        Assert.True(h.Host.ImportPresets(Path.Combine(dir.Path, "missing.json")).StartsWith("Error: ", StringComparison.Ordinal), "missing file");
        Assert.True(h.Host.ImportPresets(foreign).StartsWith("Error: not a DLP Speed Dial file", StringComparison.Ordinal), "foreign file");
        Assert.Equal("Error: no file selected.", h.Host.ExportPresets(null));

        h.Rig.Host.End(h.Rig.Now);
        Assert.Equal(4, new SpeedDialStore(h.DataDirectory, NullLog.Instance).LoadCarData(IRacing, "Car B", null).Presets.Count, "import saved");
    }

    [Test]
    public static void HostEdits_AreAppliedOnTheDataThreadAndBumpOnlyTheirVersions()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        SpeedDialSnapshot s0 = h.Snapshot();

        h.Host.CreatePreset("   ", false);
        Assert.Equal(0, h.Module.CarData.Presets.Count, "queued");
        SpeedDialSnapshot s1 = h.Snapshot();
        Assert.Equal(1, s1.Presets.Count, "applied by the next frame");
        Assert.Equal("Preset 1", s1.Presets[0].Name, "default name");
        string id = s1.Presets[0].Id;
        Assert.Equal(id, s1.SelectedPresetId, "selected because none was");
        Assert.Equal(s0.PresetsVersion + 1, s1.PresetsVersion, "presets version");
        Assert.Equal(s0.PairsVersion, s1.PairsVersion, "pairs untouched");
        Assert.Equal(s0.CarDataVersion, s1.CarDataVersion, "same car data instance");
        Assert.Equal(s0.SettingsVersion, s1.SettingsVersion, "settings untouched");

        h.Host.SetPresetValue(id, DialChannel.Tc1, null); // not included anyway
        h.Host.RenamePreset(id, " Preset 1 ");
        Assert.Equal(s1.PresetsVersion, h.Snapshot().PresetsVersion, "no-op edits keep the version");

        h.Host.SetPresetValue(id, DialChannel.Tc1, 4.4);
        SpeedDialSnapshot s2 = h.Snapshot();
        Assert.Equal(s1.PresetsVersion + 1, s2.PresetsVersion, "value edit");
        Assert.Equal(4.0, s2.Presets[0].GetValue(DialChannel.Tc1), "sanitized");

        h.Host.CreatePreset(null, false);
        h.Host.CreatePreset(string.Empty, false);
        SpeedDialSnapshot s3 = h.Snapshot();
        Assert.Equal("Preset 2,Preset 3", s3.Presets[1].Name + "," + s3.Presets[2].Name, "next free default names");
        Assert.Equal(id, s3.SelectedPresetId, "selection kept");

        h.Host.AssignSlot(2, id);
        h.Host.AssignSlot(7, s3.Presets[2].Id);
        h.Host.AssignSlot(8, id); // out of range: ignored
        SpeedDialSnapshot s4 = h.Snapshot();
        Assert.Equal(id, s4.SlotPresetIds[2]);
        Assert.Equal(s3.Presets[2].Id, s4.SlotPresetIds[7]);

        h.Press(SpeedDialNames.SetAction(1));
        SpeedDialSnapshot s5 = h.Snapshot();
        Assert.Equal(s4.PairsVersion + 1, s5.PairsVersion, "pair store");
        Assert.True(s5.Pairs[1].HasStoredValues, "stored");
        Assert.Equal(54.0, s5.Pairs[1].GetStoredValue(DialChannel.BrakeBias));

        h.Host.DeletePreset(id);
        SpeedDialSnapshot s6 = h.Snapshot();
        Assert.Equal(2, s6.Presets.Count, "deleted");
        Assert.True(s6.SlotPresetIds[2] == null, "slot cleared");
        Assert.True(s6.SelectedPresetId == null, "selection cleared");
        Assert.Equal(0, h.Int(SpeedDialNames.SelectedIndexProperty));
        Assert.Equal(2, h.Int(SpeedDialNames.PresetCountProperty));
        Assert.Equal("Deleted Preset 1", h.Text(SpeedDialNames.StatusProperty));

        h.Host.CreatePreset("New", false);
        Assert.Equal("New", h.Snapshot().Presets[2].Name);
        h.Host.RenamePreset("unknown", "x");
        h.Rig.RunFrame();
        Assert.Equal("Preset not found", h.Text(SpeedDialNames.StatusProperty));
    }

    [Test]
    public static void SettingsEdits_BumpThePairsVersionOnlyWhenPairsChange()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        SpeedDialSnapshot s0 = h.Snapshot();

        h.Host.EditSettings(s => s.Timing.GapMs += 10);
        h.Host.EditSettings(s => s.GetBinding(DialChannel.Tc1).IncreaseRole = "TractionControl+");
        SpeedDialSnapshot s1 = h.Snapshot();
        Assert.Equal(s0.SettingsVersion + 2, s1.SettingsVersion, "settings applied");
        Assert.Equal(s0.PairsVersion, s1.PairsVersion, "timing and role edits leave the pairs alone");

        h.Host.EditSettings(s => s.GetPair(0).Name = "Tyres");
        SpeedDialSnapshot s2 = h.Snapshot();
        Assert.Equal(s1.PairsVersion + 1, s2.PairsVersion, "a pair rename is a pair change");
        Assert.Equal("Tyres", s2.Pairs[0].Name);
    }

    [Test]
    public static void CapturePresetFromCurrent_UpdatesIncludedChannelsOrEveryValidOne()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string empty = h.CreatePreset("Empty");
        string partial = h.CreatePreset("Partial", (DialChannel.Tc1, 9));

        h.Host.CapturePresetFromCurrent(empty);
        h.Host.CapturePresetFromCurrent(partial);
        h.Rig.RunFrame();

        DialPreset all = h.Module.CarData.FindPreset(empty);
        Assert.Equal(4, all.IncludedCount(), "every channel with telemetry (no TC3 on iRacing)");
        Assert.True(all.TryGetValue(DialChannel.BrakeBias, out double bb) && bb == 54.0);
        DialPreset onlyTc = h.Module.CarData.FindPreset(partial);
        Assert.Equal(1, onlyTc.IncludedCount(), "only its own channel");
        Assert.True(onlyTc.TryGetValue(DialChannel.Tc1, out double tc) && tc == 2.0, "current TC");
        Assert.Equal("Captured current values into Partial", h.Text(SpeedDialNames.StatusProperty));

        h.Rig.Reader.Remove(IRacingTc);
        h.Rig.RunFrame();
        h.Host.CapturePresetFromCurrent(partial);
        h.Rig.RunFrame();
        Assert.Equal("No valid telemetry values", h.Text(SpeedDialNames.StatusProperty));
        Assert.True(onlyTc.TryGetValue(DialChannel.Tc1, out tc) && tc == 2.0, "kept");
    }

    [Test]
    public static void TestRole_PressesOnceAndIsIgnoredWhileBusy()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        h.Rig.Roles.Roles.Add("ABS+");
        Assert.True(h.Host.ControlMapperAvailable, "available");
        Assert.Equal("ABS+", string.Join(",", h.Host.GetButtonRoles()), "roles for the pickers");

        h.Host.TestRole(DialChannel.Abs, true);
        h.Rig.RunFrame();
        Assert.Equal("ABS+", string.Join(",", h.Rig.Roles.Presses), "one press");
        Assert.Equal("Pressed ABS+ (ABS +)", h.Text(SpeedDialNames.StatusProperty));

        h.Host.EditSettings(s => s.GetBinding(DialChannel.Abs).DecreaseRole = string.Empty);
        h.Host.TestRole(DialChannel.Abs, false);
        h.Rig.RunFrame();
        Assert.True(h.Text(SpeedDialNames.StatusProperty).StartsWith("Error: no role bound", StringComparison.Ordinal), h.Text(SpeedDialNames.StatusProperty));

        string id = h.CreatePreset("Far", (DialChannel.Tc1, 9));
        h.Host.ApplyPreset(id);
        h.RunFrames(2);
        int presses = h.Rig.Roles.Presses.Count;
        h.Host.TestRole(DialChannel.Abs, true);
        h.Rig.RunFrame();
        Assert.Equal("Busy dialing", h.Text(SpeedDialNames.StatusProperty));
        Assert.False(h.Rig.Roles.Presses.GetRange(presses, h.Rig.Roles.Presses.Count - presses).Contains("ABS+"), "no test press while busy");
        h.Host.CancelDial();
        h.Rig.RunFrame();

        h.Rig.Roles.IsAvailable = false;
        Assert.False(h.Host.ControlMapperAvailable, "unavailable");
        h.Host.TestRole(DialChannel.Abs, true);
        h.Rig.RunFrame();
        Assert.Equal("Error: could not press ABS+ (Control Mapper not available)", h.Text(SpeedDialNames.StatusProperty));
        Assert.False(h.Snapshot().ControlMapperAvailable, "in the snapshot");
    }

    [Test]
    public static void ResetLearning_ForgetsOneOrEveryChannel()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Race", (DialChannel.Tc1, 3), (DialChannel.BrakeBias, 54.5));
        h.Host.ApplyPreset(id);
        h.RunUntilIdle();
        Assert.Equal(1.0, h.Module.CarData.GetLearning(DialChannel.Tc1).Step, "learned");

        h.Host.ResetLearning(DialChannel.Tc1);
        h.Rig.RunFrame();
        Assert.Equal(0.0, h.Module.CarData.GetLearning(DialChannel.Tc1).Step, "TC forgotten");
        Assert.Equal(0.5, h.Module.CarData.GetLearning(DialChannel.BrakeBias).Step, "BB kept");

        h.Host.ResetLearning(null);
        SpeedDialSnapshot snapshot = h.Snapshot();
        Assert.Equal(0.0, h.Module.CarData.GetLearning(DialChannel.BrakeBias).Step, "everything forgotten");
        Assert.Equal(0.0, snapshot.LearnedStep[(int)DialChannel.BrakeBias], "snapshot");
        Assert.True(double.IsNaN(snapshot.ObservedMax[(int)DialChannel.Tc1]), "range forgotten");
    }

    [Test]
    public static void SteadyState_UpdateAndTickDoNotAllocate()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var h = new Harness(dir.Path);
        string id = h.CreatePreset("Quali", (DialChannel.Tc1, 3));
        h.Host.AssignSlot(0, id);
        h.Press(SpeedDialNames.SetAction(0));
        h.Rig.RunFrame();

        AssertNoAllocation(h, "idle");

        // After a finished job the values are stable again: still nothing allocated per frame.
        h.Press(SpeedDialNames.DialAction(0));
        h.RunUntilIdle();
        Assert.Equal(3.0, h.Raw(IRacingTc), "dialed");
        AssertNoAllocation(h, "after a job");

        // The game paused with a finished job (gate closed): same.
        h.Rig.Frame.GamePaused = true;
        AssertNoAllocation(h, "paused");
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    private static void AssertNoAllocation(Harness h, string what)
    {
        // Debounced saves serialize and write (save events may allocate): let them happen before measuring.
        h.Rig.RunFrame(SaveScheduler.SettingsDebounceSeconds + 0.1);
        Assert.True(h.Module.Store.WaitForPendingWrites(TimeSpan.FromSeconds(5)), what + ": writes finished");
        h.RunFrames(100);

        // The monitoring counter has allocation-context granularity: a few bytes per frame would exceed the allowance.
        const int Frames = 10000;
        const long AllowanceBytes = 8 * 1024;
        AppDomain.MonitoringIsEnabled = true;
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        for (int i = 0; i < Frames; i++)
        {
            h.Rig.RunFrame();
        }

        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        Assert.True(allocated <= AllowanceBytes, what + ": allocated " + allocated + " bytes in " + Frames + " frames");
    }

    private static void SetGate(FrameContext frame, string gate, bool closed)
    {
        switch (gate)
        {
            case "paused":
                frame.GamePaused = closed;
                break;
            case "menu":
                frame.GameInMenu = closed;
                break;
            case "replay":
                frame.IsReplay = closed;
                break;
            default:
                frame.Spectating = closed;
                break;
        }
    }

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
    /// The module on an iRacing car (TC 2, TC2 1, ABS 3, BB 54 %) with a game simulator mapped to the default roles:
    /// TC/TC2/ABS move by 1 per press, brake bias by 0.5 %.
    /// </summary>
    private sealed class Harness
    {
        public Harness(string dataRoot, string car = DefaultCar)
        {
            Rig = new ModuleTestRig(dataRoot);
            Module = new SpeedDialModule();
            ModuleSlot slot = Rig.Add(Module);
            Assert.True(slot.Active, "initialized: " + slot.InitError);
            DataDirectory = Path.Combine(dataRoot, ModuleId);
            Game = new GameSim(Rig)
                .Map("TractionControl+", IRacingTc, 1).Map("TractionControl-", IRacingTc, -1)
                .Map("TC_PowerCut+", IRacingTc2, 1).Map("TC_PowerCut-", IRacingTc2, -1)
                .Map("ABS+", IRacingAbs, 1).Map("ABS-", IRacingAbs, -1)
                .Map("BrakeBalanceFront", IRacingBb, 0.5).Map("BrakeBalanceRear", IRacingBb, -0.5);
            SetValues(tc: 2, tc2: 1, abs: 3, bb: 54.0);
            Rig.SetGame(IRacing, "car-id", car);
            Rig.RunFrame();
            CarKey = car;
        }

        public ModuleTestRig Rig { get; }

        public SpeedDialModule Module { get; }

        public ISpeedDialHost Host => Module;

        public GameSim Game { get; }

        public string DataDirectory { get; }

        public string CarKey { get; }

        public void SetValues(double tc, double tc2, double abs, double bb)
        {
            Rig.Reader.Set(IRacingTc, tc);
            Rig.Reader.Set(IRacingTc2, tc2);
            Rig.Reader.Set(IRacingAbs, abs);
            Rig.Reader.Set(IRacingBb, bb);
        }

        /// <summary>Creates a preset with the given targets through the host and returns its id.</summary>
        public string CreatePreset(string name, params (DialChannel Channel, double Value)[] targets)
        {
            Host.CreatePreset(name, false);
            Rig.RunFrame();
            string id = Module.CarData.Presets[Module.CarData.Presets.Count - 1].Id;
            foreach ((DialChannel Channel, double Value) target in targets)
            {
                Host.SetPresetValue(id, target.Channel, target.Value);
            }

            Rig.RunFrame();
            return id;
        }

        public void Press(string action) => Rig.Actions[ModuleId].Press(action);

        /// <summary>Frames with the game reacting to the presses in between.</summary>
        public void RunFrames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Rig.RunFrame();
                Game.Apply();
            }
        }

        /// <summary>Runs frames (the game reacting in between) until the dial job is no longer busy.</summary>
        public void RunUntilIdle()
        {
            for (int i = 0; i < MaxDialFrames; i++)
            {
                Rig.RunFrame();
                Game.Apply();
                if (!Module.DialerStatus.IsBusy)
                {
                    return;
                }
            }

            Assert.Fail("the dial job did not finish: " + Module.DialerStatus.State + " " + Module.DialerStatus.Message);
        }

        /// <summary>A frame long enough for the 20 Hz snapshot refresh, then a copy of the snapshot.</summary>
        public SpeedDialSnapshot Snapshot()
        {
            Rig.RunFrame(0.06);
            var snapshot = new SpeedDialSnapshot();
            Host.CopySnapshot(snapshot);
            return snapshot;
        }

        public double Raw(string path) => Convert.ToDouble(Rig.Reader.GetValue(path), CultureInfo.InvariantCulture);

        public double Value(DialChannel channel) => (double)Rig.Properties.Read(SpeedDialNames.ValueProperty(channel));

        public string Text(string property) => (string)Rig.Properties.Read(property);

        public int Int(string property) => (int)Rig.Properties.Read(property);

        public string CarFilePath() => CarFileNaming.GetCarFilePath(DataDirectory, IRacing, CarKey);
    }

    /// <summary>The game side of the closed loop: each recorded role press moves one telemetry property by a step.</summary>
    private sealed class GameSim
    {
        private readonly ModuleTestRig rig;
        private readonly Dictionary<string, KeyValuePair<string, double>> effects = new Dictionary<string, KeyValuePair<string, double>>(StringComparer.Ordinal);
        private int handled;

        public GameSim(ModuleTestRig rig)
        {
            this.rig = rig;
        }

        public GameSim Map(string role, string path, double delta)
        {
            effects[role] = new KeyValuePair<string, double>(path, delta);
            return this;
        }

        /// <summary>Applies the presses recorded since the last call (the game reacts before the next frame).</summary>
        public void Apply()
        {
            while (handled < rig.Roles.Presses.Count)
            {
                string role = rig.Roles.Presses[handled++];
                if (effects.TryGetValue(role, out KeyValuePair<string, double> effect))
                {
                    object raw = rig.Reader.GetValue(effect.Key);
                    double value = raw == null ? 0.0 : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    rig.Reader.Set(effect.Key, value + effect.Value);
                }
            }
        }
    }

    /// <summary>A Control Mapper whose presses throw (the module must contain it).</summary>
    private sealed class ThrowingRoleOutput : IRoleOutput
    {
        public const string Message = "control mapper exploded";

        public bool IsAvailable => true;

        public bool Press(string role, int durationMs) => throw new InvalidOperationException(Message);

        public IReadOnlyList<string> GetButtonRoles() => new string[0];
    }
}
