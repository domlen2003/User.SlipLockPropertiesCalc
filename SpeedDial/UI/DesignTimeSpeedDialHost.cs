using System;
using System.Collections.Generic;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Model;

namespace DivebombLogistics.SpeedDial.UI;

/// <summary>
/// Sample data for the XAML designer (<see cref="SpeedDialViewModel()"/>): an LMU car with three presets, two pairs
/// and a running dial job. Never used at runtime; every mutation is ignored.
/// </summary>
internal sealed class DesignTimeSpeedDialHost : ISpeedDialHost
{
    private static readonly string[] Roles =
    {
        "TractionControl+", "TractionControl-", "TC_PowerCut+", "TC_PowerCut-", "TC_SlipAngle+", "TC_SlipAngle-",
        "ABS+", "ABS-", "BrakeBalanceFront", "BrakeBalanceRear", "TC OFF", "ABS OFF",
    };

    private readonly SpeedDialSnapshot _sample = CreateSample();

    /// <inheritdoc />
    public bool ControlMapperAvailable => true;

    /// <inheritdoc />
    public void CopySnapshot(SpeedDialSnapshot target) => _sample.CopyTo(target);

    /// <inheritdoc />
    public void EditSettings(Action<SpeedDialSettings> edit)
    {
    }

    /// <inheritdoc />
    public void CreatePreset(string name, bool fromCurrentValues)
    {
    }

    /// <inheritdoc />
    public void RenamePreset(string presetId, string name)
    {
    }

    /// <inheritdoc />
    public void DeletePreset(string presetId)
    {
    }

    /// <inheritdoc />
    public void SetPresetValue(string presetId, DialChannel channel, double? value)
    {
    }

    /// <inheritdoc />
    public void CapturePresetFromCurrent(string presetId)
    {
    }

    /// <inheritdoc />
    public void AssignSlot(int slotIndex, string presetId)
    {
    }

    /// <inheritdoc />
    public void SelectPreset(string presetId)
    {
    }

    /// <inheritdoc />
    public void ApplyPreset(string presetId)
    {
    }

    /// <inheritdoc />
    public void SetPair(int pairIndex)
    {
    }

    /// <inheritdoc />
    public void ResetPair(int pairIndex)
    {
    }

    /// <inheritdoc />
    public void CancelDial()
    {
    }

    /// <inheritdoc />
    public void TestRole(DialChannel channel, bool increase)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetButtonRoles() => Roles;

    /// <inheritdoc />
    public void ResetLearning(DialChannel? channel)
    {
    }

    /// <inheritdoc />
    public string ExportPresets(string filePath) => string.Empty;

    /// <inheritdoc />
    public string ImportPresets(string filePath) => string.Empty;

    private static SpeedDialSnapshot CreateSample()
    {
        var car = SpeedDialCarData.Create("LMU", "Porsche 963", "Porsche 963");
        DialPreset race = DialPreset.Create("Race");
        race.SetValue(DialChannel.Tc1, 4);
        race.SetValue(DialChannel.Tc2, 3);
        race.SetValue(DialChannel.Abs, 5);
        race.SetValue(DialChannel.BrakeBias, 54.2);
        DialPreset wet = DialPreset.Create("Wet");
        wet.SetValue(DialChannel.Tc1, 7);
        wet.SetValue(DialChannel.Tc2, 6);
        wet.SetValue(DialChannel.Tc3, 5);
        wet.SetValue(DialChannel.Abs, 8);
        wet.SetValue(DialChannel.BrakeBias, 52.5);
        DialPreset quali = DialPreset.Create("Quali");
        quali.SetValue(DialChannel.Tc1, 2);
        car.Presets.Add(race);
        car.Presets.Add(wet);
        car.Presets.Add(quali);
        car.SlotPresetIds[0] = race.Id;
        car.SlotPresetIds[1] = wet.Id;
        car.SelectedPresetId = race.Id;
        car.PairSnapshots[1].SetValue(DialChannel.BrakeBias, 55.0);
        car.PairSnapshots[1].CapturedUtc = DateTime.UtcNow;

        var s = new SpeedDialSnapshot
        {
            GameRunning = true,
            GameName = "LMU",
            HasCar = true,
            CarKey = car.CarKey,
            CarDisplayName = car.DisplayName,
            CarDataVersion = 1,
            TelemetryName = "LMU native",
            TelemetryDescription = "TC1 mTC, TC2 mTCCut, TC3 mTCSlip, ABS mABS, BB mRearBrakeBias",
            ControlMapperAvailable = true,
            Status = "Dialing Race",
            Presets = PresetSummary.BuildList(car),
            PresetsVersion = 1,
            SelectedPresetId = car.SelectedPresetId,
            Pairs = PairSummary.BuildList(new SpeedDialSettings(), car),
            PairsVersion = 1,
            SettingsVersion = 1,
        };

        double[] current = { 3, 3, 4, 5, 53.8 };
        double[] max = { 10, 8, 8, 12, double.NaN };
        for (int i = 0; i < DialChannels.Count; i++)
        {
            s.ChannelSupported[i] = true;
            s.CurrentValues[i] = current[i];
            s.MaxValues[i] = max[i];
        }

        for (int i = 0; i < SpeedDialSettings.MaxSlotCount; i++)
        {
            s.SlotPresetIds[i] = car.SlotPresetIds[i];
        }

        s.Dial.State = DialState.Running;
        s.Dial.Label = race.Name;
        s.Dial.PresetId = race.Id;
        s.Dial.CurrentChannel = DialChannel.Tc1;
        s.Dial.Version = 1;
        SetProgress(s.Dial.Get(DialChannel.Tc1), 3, 4, 3, 1, ChannelResult.Pending);
        SetProgress(s.Dial.Get(DialChannel.Tc2), 3, 3, 3, 0, ChannelResult.Pending);
        SetProgress(s.Dial.Get(DialChannel.Tc3), double.NaN, double.NaN, 4, 0, ChannelResult.Skipped);
        SetProgress(s.Dial.Get(DialChannel.Abs), 5, 5, 5, 0, ChannelResult.Pending);
        SetProgress(s.Dial.Get(DialChannel.BrakeBias), 53.8, 54.2, 53.8, 0, ChannelResult.Pending);
        s.SetLearning(car);
        return s;
    }

    private static void SetProgress(ChannelProgress progress, double start, double target, double current, int presses, ChannelResult result)
    {
        progress.Start = start;
        progress.Target = target;
        progress.Current = current;
        progress.Presses = presses;
        progress.Result = result;
    }
}
