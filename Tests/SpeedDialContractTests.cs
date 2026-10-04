using System;
using System.Collections.Generic;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.SpeedDial;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.SpeedDial.UI;

namespace DivebombLogistics.Tests;

/// <summary>
/// SpeedDial shared contracts (channel registry, JSON DTOs, dial request/status, UI snapshot, names). These pin the
/// file format and the names users bind to; implementation tests live in the per-agent test files.
/// </summary>
internal static class SpeedDialContractTests
{
    [Test]
    public static void Channels_IdsAndNamesArePinned()
    {
        Assert.Equal(5, DialChannels.Count);
        Assert.Equal(DialChannels.Count, DialChannels.All.Count);
        string[] ids = { "TC1", "TC2", "TC3", "ABS", "BB" };
        string[] names = { "TC", "TC2 (Cut)", "TC3 (Slip)", "ABS", "Brake bias" };
        for (int i = 0; i < ids.Length; i++)
        {
            var channel = (DialChannel)i;
            Assert.Equal(channel, DialChannels.All[i]);
            Assert.Equal(ids[i], DialChannels.Id(channel));
            Assert.Equal(names[i], DialChannels.DisplayName(channel));
            Assert.True(DialChannels.TryParseId(ids[i].ToLowerInvariant(), out DialChannel parsed), "parse " + ids[i]);
            Assert.Equal(channel, parsed);
        }

        Assert.False(DialChannels.TryParseId("TC4", out _), "unknown id");
        Assert.False(DialChannels.TryParseId(null, out _), "null id");
        Assert.Equal(string.Empty, DialChannels.Id((DialChannel)99), "invalid channel");
        Assert.Equal(DialChannelKind.Continuous, DialChannels.Kind(DialChannel.BrakeBias));
        Assert.Equal(DialChannelKind.Integer, DialChannels.Kind(DialChannel.Abs));
    }

    [Test]
    public static void Channels_MatchesAndSanitize()
    {
        Assert.True(DialChannels.Matches(DialChannel.Tc1, 3.0, 3.0, 0), "equal");
        Assert.True(DialChannels.Matches(DialChannel.Tc1, 3.2, 2.8, 0), "equal after rounding");
        Assert.False(DialChannels.Matches(DialChannel.Tc1, 3.0, 4.0, 0), "different level");
        Assert.True(DialChannels.Matches(DialChannel.BrakeBias, 54.24, 54.2, 0), "within 0.05");
        Assert.False(DialChannels.Matches(DialChannel.BrakeBias, 54.3, 54.2, 0), "outside 0.05");
        Assert.True(DialChannels.Matches(DialChannel.BrakeBias, 54.4, 54.2, 0.5), "within half a learned step");
        Assert.False(DialChannels.Matches(DialChannel.BrakeBias, double.NaN, 54.2, 0), "NaN never matches");

        Assert.Equal(3.0, DialChannels.Sanitize(DialChannel.Tc2, 2.6));
        Assert.Equal(0.0, DialChannels.Sanitize(DialChannel.Abs, -4));
        Assert.Equal(100.0, DialChannels.Sanitize(DialChannel.BrakeBias, 140));
        Assert.Equal(54.2, DialChannels.Sanitize(DialChannel.BrakeBias, (1 - 0.458) * 100));
        Assert.True(double.IsNaN(DialChannels.Sanitize(DialChannel.BrakeBias, double.PositiveInfinity)), "infinity");
        Assert.Equal("54.2 %", DialChannels.FormatValue(DialChannel.BrakeBias, 54.2));
        Assert.Equal("3", DialChannels.FormatValue(DialChannel.Tc1, 3));
        Assert.Equal(DialChannels.MissingValueText, DialChannels.FormatValue(DialChannel.Tc1, double.NaN));
    }

    [Test]
    public static void Settings_DefaultsMatchTheUsersRoles()
    {
        var settings = new SpeedDialSettings();
        settings.Normalize();
        Assert.Equal(SpeedDialSettings.DefaultSlotCount, settings.SlotCount);
        Assert.Equal("TractionControl+", settings.GetBinding(DialChannel.Tc1).IncreaseRole);
        Assert.Equal("TractionControl-", settings.GetBinding(DialChannel.Tc1).DecreaseRole);
        Assert.Equal("TC_PowerCut+", settings.GetBinding(DialChannel.Tc2).IncreaseRole);
        Assert.Equal("TC_SlipAngle-", settings.GetBinding(DialChannel.Tc3).DecreaseRole);
        Assert.Equal("ABS+", settings.GetBinding(DialChannel.Abs).IncreaseRole);
        Assert.Equal("BrakeBalanceFront", settings.GetBinding(DialChannel.BrakeBias).IncreaseRole);
        Assert.Equal("BrakeBalanceRear", settings.GetBinding(DialChannel.BrakeBias).DecreaseRole);
        Assert.Equal(2, settings.Pairs.Count);
        Assert.Equal(5, settings.Pairs[0].Channels.Count);
        Assert.True(settings.Pairs[1].Includes(DialChannel.BrakeBias) && settings.Pairs[1].Channels.Count == 1, "pair 2 = BB");
        Assert.Equal(70, settings.Timing.PressMs);
        Assert.Equal(90, settings.Timing.GapMs);
        Assert.Equal(800, settings.Timing.ConfirmTimeoutMs);
        Assert.Equal(3, settings.Timing.MaxStallPresses);
        Assert.Equal(150, settings.Timing.MaxPressesPerChannel);
        Assert.Equal(10000, settings.Timing.GatePauseTimeoutMs);
        Assert.Equal("TC1,TC2,TC3,ABS,BB", string.Join(",", settings.ChannelOrder));
    }

    [Test]
    public static void Settings_NormalizeRepairsHandEditedFile()
    {
        const string json = "{\"Channels\":{\"tc1\":{\"Enabled\":false,\"IncreaseRole\":\" X+ \",\"DecreaseRole\":null},"
            + "\"TC9\":{\"IncreaseRole\":\"nope\"},\"BB\":null},\"SlotCount\":42,\"Pairs\":[null,{\"Name\":\"\",\"Channels\":[\"bb\",\"BB\",\"zz\",\"TC1\"]}],"
            + "\"Timing\":{\"PressMs\":1,\"GapMs\":99999},\"ChannelOrder\":[\"BB\",\"bb\",\"x\"]}";
        SpeedDialSettings settings = JsonFile.Deserialize<SpeedDialSettings>(json, out _);
        settings.Normalize();

        Assert.Equal(DialChannels.Count, settings.Channels.Count, "one binding per channel");
        Assert.False(settings.Channels.ContainsKey("TC9"), "unknown id dropped");
        ChannelBinding tc1 = settings.GetBinding(DialChannel.Tc1);
        Assert.False(tc1.Enabled, "tc1 disabled kept");
        Assert.Equal("X+", tc1.IncreaseRole);
        Assert.Equal(string.Empty, tc1.DecreaseRole);
        Assert.Equal("BrakeBalanceFront", settings.GetBinding(DialChannel.BrakeBias).IncreaseRole, "null binding repaired");
        Assert.Equal(SpeedDialSettings.MaxSlotCount, settings.SlotCount);
        Assert.Equal(1, settings.Pairs.Count, "null pair dropped");
        Assert.Equal("Pair 1", settings.Pairs[0].Name);
        Assert.Equal("TC1,BB", string.Join(",", settings.Pairs[0].Channels));
        Assert.Equal(DialTiming.MinPressMs, settings.Timing.PressMs);
        Assert.Equal(DialTiming.MaxGapMs, settings.Timing.GapMs);
        Assert.Equal("BB,TC1,TC2,TC3,ABS", string.Join(",", settings.ChannelOrder));

        var order = new DialChannel[DialChannels.Count];
        Assert.Equal(DialChannels.Count, settings.FillChannelOrder(order));
        Assert.Equal(DialChannel.BrakeBias, order[0]);
        Assert.Equal(DialChannel.Abs, order[4]);
    }

    [Test]
    public static void Settings_RoundTripAndDeepCopyAreIndependent()
    {
        var settings = new SpeedDialSettings();
        settings.SlotCount = 6;
        settings.GetBinding(DialChannel.Abs).IncreaseRole = "MyAbsUp";
        settings.Pairs.Add(SetResetPairDefinition.Create("Wet", DialChannel.Tc1, DialChannel.Abs));
        settings.Normalize();

        SpeedDialSettings loaded = JsonFile.Deserialize<SpeedDialSettings>(JsonFile.Serialize(settings), out int skipped);
        loaded.Normalize();
        Assert.Equal(0, skipped);
        Assert.Equal(6, loaded.SlotCount);
        Assert.Equal("MyAbsUp", loaded.GetBinding(DialChannel.Abs).IncreaseRole);
        Assert.Equal("Wet", loaded.Pairs[2].Name);

        SpeedDialSettings copy = settings.DeepCopy();
        copy.GetBinding(DialChannel.Abs).IncreaseRole = "changed";
        copy.Pairs[0].Channels.Clear();
        copy.Timing.PressMs = 500;
        Assert.Equal("MyAbsUp", settings.GetBinding(DialChannel.Abs).IncreaseRole);
        Assert.Equal(5, settings.Pairs[0].Channels.Count);
        Assert.Equal(DialTiming.DefaultPressMs, settings.Timing.PressMs);
    }

    [Test]
    public static void CarData_NormalizeRepairsAndRoundTrips()
    {
        const string json = "{\"Presets\":[{\"Id\":\"a\",\"Name\":\"  Quali  \",\"Values\":{\"tc1\":2.6,\"BB\":\"NaN\",\"XX\":3}},"
            + "null,{\"Id\":\"A\",\"Name\":\"\",\"Values\":null}],\"SlotPresetIds\":[\"a\",\"missing\"],\"SelectedPresetId\":\"zzz\","
            + "\"PairSnapshots\":[null],\"Learning\":{\"abs\":{\"Direction\":7,\"Step\":-1},\"QQ\":{}}}";
        SpeedDialCarData data = JsonFile.Deserialize<SpeedDialCarData>(json, out _);
        data.Normalize();

        Assert.Equal(2, data.Presets.Count, "null preset dropped");
        DialPreset quali = data.Presets[0];
        Assert.Equal("Quali", quali.Name);
        Assert.True(quali.TryGetValue(DialChannel.Tc1, out double tc1) && tc1 == 3.0, "tc1 sanitized");
        Assert.False(quali.Includes(DialChannel.BrakeBias), "NaN dropped");
        Assert.Equal(1, quali.IncludedCount());
        Assert.False(string.Equals(data.Presets[0].Id, data.Presets[1].Id, StringComparison.OrdinalIgnoreCase), "duplicate id replaced");
        Assert.Equal("Preset 2", data.Presets[1].Name);
        Assert.Equal(SpeedDialSettings.MaxSlotCount, data.SlotPresetIds.Length);
        Assert.Equal("a", data.SlotPresetIds[0]);
        Assert.True(data.SlotPresetIds[1] == null, "unknown slot reference cleared");
        Assert.True(data.SelectedPresetId == null, "unknown selection cleared");
        Assert.Equal(SpeedDialSettings.MaxPairCount, data.PairSnapshots.Count);
        Assert.False(data.GetPairSnapshot(0).HasValues(), "empty snapshot");
        Assert.Equal(DialChannels.Count, data.Learning.Count);
        Assert.Equal(ChannelLearning.IncreaseRaises, data.GetLearning(DialChannel.Abs).Direction);
        Assert.Equal(0.0, data.GetLearning(DialChannel.Abs).Step);

        data.GetPairSnapshot(1).SetValue(DialChannel.BrakeBias, 55.5);
        data.GetPairSnapshot(1).CapturedUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        data.GetLearning(DialChannel.Tc1).Observe(4);
        data.GetLearning(DialChannel.Tc1).RecordStep(-1);
        SpeedDialCarData loaded = JsonFile.Deserialize<SpeedDialCarData>(JsonFile.Serialize(data), out int skipped);
        loaded.Normalize();
        Assert.Equal(0, skipped);
        Assert.True(loaded.GetPairSnapshot(1).TryGetValue(DialChannel.BrakeBias, out double bb) && bb == 55.5, "stored BB");
        Assert.Equal(data.GetPairSnapshot(1).CapturedUtc, loaded.GetPairSnapshot(1).CapturedUtc);
        Assert.Equal(1.0, loaded.GetLearning(DialChannel.Tc1).Step);
        Assert.Equal(4.0, loaded.GetLearning(DialChannel.Tc1).ObservedMax);
        Assert.True(double.IsNaN(loaded.GetLearning(DialChannel.Tc2).ObservedMin), "NaN survives the round trip");

        SpeedDialCarData copy = data.DeepCopy();
        copy.Presets[0].SetValue(DialChannel.Abs, 5);
        copy.SlotPresetIds[0] = null;
        copy.GetLearning(DialChannel.Tc1).Reset();
        Assert.False(data.Presets[0].Includes(DialChannel.Abs), "preset copy independent");
        Assert.Equal("a", data.SlotPresetIds[0]);
        Assert.Equal(1.0, data.GetLearning(DialChannel.Tc1).Step);
    }

    [Test]
    public static void CarData_RemovePresetClearsReferences()
    {
        SpeedDialCarData data = SpeedDialCarData.Create("LMU", "Ligier JS P325", "Ligier JS P325");
        DialPreset preset = DialPreset.Create("Race");
        data.Presets.Add(preset);
        data.SlotPresetIds[2] = preset.Id;
        data.SelectedPresetId = preset.Id;
        Assert.True(data.GetSlotPreset(2) == preset, "slot resolves");
        Assert.True(data.RemovePreset(preset.Id.ToUpperInvariant()), "removed (case-insensitive id)");
        Assert.True(data.SlotPresetIds[2] == null && data.SelectedPresetId == null, "references cleared");
        Assert.False(data.RemovePreset(preset.Id), "second remove");
    }

    [Test]
    public static void Request_LoadsPresetsAndPairs()
    {
        var preset = DialPreset.Create("Quali");
        preset.SetValue(DialChannel.Tc1, 4);
        preset.SetValue(DialChannel.BrakeBias, 56.04);
        var request = new DialRequest();
        request.LoadPreset(preset);
        Assert.Equal("Quali", request.Label);
        Assert.Equal(preset.Id, request.PresetId);
        Assert.Equal(2, request.TargetCount);
        Assert.Equal(56.04, request.GetTarget(DialChannel.BrakeBias));
        Assert.False(request.HasTarget(DialChannel.Abs), "abs skipped");

        var stored = new DialSnapshot();
        stored.SetValue(DialChannel.Tc1, 2);
        stored.SetValue(DialChannel.BrakeBias, 54);
        request.LoadPairSnapshot("Reset Pair 2", SetResetPairDefinition.Create("Pair 2", DialChannel.BrakeBias), stored);
        Assert.Equal("Reset Pair 2", request.Label);
        Assert.True(request.PresetId == null, "no preset");
        Assert.Equal(1, request.TargetCount);
        Assert.Equal(54.0, request.GetTarget(DialChannel.BrakeBias));

        var copy = new DialRequest();
        copy.CopyFrom(request);
        request.Clear("x");
        Assert.Equal(54.0, copy.GetTarget(DialChannel.BrakeBias));
    }

    [Test]
    public static void Snapshot_CopyToCopiesEverythingWithoutAllocating()
    {
        var car = SpeedDialCarData.Create("LMU", "car", "car");
        DialPreset preset = DialPreset.Create("Race");
        preset.SetValue(DialChannel.Abs, 6);
        car.Presets.Add(preset);
        car.SlotPresetIds[0] = preset.Id;
        car.GetLearning(DialChannel.Abs).Direction = ChannelLearning.IncreaseLowers;
        var settings = new SpeedDialSettings();
        settings.Normalize();

        var source = new SpeedDialSnapshot
        {
            GameRunning = true,
            GameName = "LMU",
            HasCar = true,
            CarKey = "car",
            Presets = PresetSummary.BuildList(car),
            PresetsVersion = 3,
            Pairs = PairSummary.BuildList(settings, car),
            Settings = settings.DeepCopy(),
            Status = "ok",
        };
        source.CurrentValues[(int)DialChannel.BrakeBias] = 54.2;
        source.SlotPresetIds[0] = preset.Id;
        source.SetLearning(car);
        source.Dial.State = DialState.Running;
        source.Dial.Channels[(int)DialChannel.Abs].Presses = 2;

        var target = new SpeedDialSnapshot();
        source.CopyTo(target);
        Assert.Equal(54.2, target.CurrentValues[(int)DialChannel.BrakeBias]);
        Assert.Equal(preset.Id, target.SlotPresetIds[0]);
        Assert.Equal(-1, target.LearnedDirection[(int)DialChannel.Abs]);
        Assert.Equal(DialState.Running, target.Dial.State);
        Assert.Equal(2, target.Dial.TotalPresses());
        Assert.True(target.Dial.IsBusy, "busy");
        Assert.Equal(6.0, target.Presets[0].GetValue(DialChannel.Abs));
        Assert.Equal(2, target.Pairs.Count);
        Assert.False(target.Pairs[0].HasStoredValues, "nothing stored");

        AppDomain.MonitoringIsEnabled = true;
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        for (int i = 0; i < 10000; i++)
        {
            source.CopyTo(target);
        }

        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        Assert.True(allocated <= 8 * 1024, "allocated " + allocated + " bytes");
    }

    [Test]
    public static void Names_ArePinned()
    {
        var expectedActions = new List<string>();
        for (int i = 1; i <= 8; i++)
        {
            expectedActions.Add("SpeedDial.Dial" + i);
        }

        expectedActions.Add("SpeedDial.NextPreset");
        expectedActions.Add("SpeedDial.PreviousPreset");
        expectedActions.Add("SpeedDial.ApplySelectedPreset");
        for (int i = 1; i <= 4; i++)
        {
            expectedActions.Add("SpeedDial.Set" + i);
        }

        for (int i = 1; i <= 4; i++)
        {
            expectedActions.Add("SpeedDial.Reset" + i);
        }

        expectedActions.Add("SpeedDial.Cancel");
        Assert.Equal(string.Join(",", expectedActions), string.Join(",", SpeedDialNames.AllActions));
        Assert.Equal(
            "SpeedDial.Busy,SpeedDial.Status,SpeedDial.ActivePreset,SpeedDial.SelectedPreset,SpeedDial.SelectedIndex,"
            + "SpeedDial.PresetCount,SpeedDial.Value.TC1,SpeedDial.Value.TC2,SpeedDial.Value.TC3,SpeedDial.Value.ABS,"
            + "SpeedDial.Value.BB,SpeedDial.LastResult",
            string.Join(",", SpeedDialNames.AllProperties));
        Assert.Equal("SpeedDial.Dial3", SpeedDialNames.DialAction(2));
        Assert.True(SpeedDialNames.DialAction(8) == null, "out of range");
        Assert.Equal("SpeedDial.Reset4", SpeedDialNames.ResetAction(3));
        Assert.Equal("SpeedDial", SpeedDialNames.ModuleId);
    }
}
