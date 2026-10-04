using System;
using System.Collections.Generic;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Model;

namespace DivebombLogistics.SpeedDial.UI;

// Contract file (SpeedDial Stage B): the UI snapshot and the immutable list items it carries. Pure (no WPF), linked
// into the tests.

/// <summary>
/// Plain copy of everything the Speed Dial tab displays. The module fills one instance on the data thread (under its
/// snapshot lock, at most 20 Hz); the view model copies it via <see cref="ISpeedDialHost.CopySnapshot"/> into its own
/// instance every 100 ms while the tab is visible.
/// <para>
/// Copy rules (allocation-free <see cref="CopyTo"/>): value fields and fixed-length arrays are copied element-wise;
/// strings are assigned by reference (the module assigns them only when they change); <see cref="Presets"/>,
/// <see cref="Pairs"/> and <see cref="Settings"/> are immutable objects that the module replaces (never mutates) when
/// their content changes, bumping the matching version, so <see cref="CopyTo"/> copies the references and the view
/// model rebuilds its rows only when a version changed. The UI must never mutate them.
/// </para>
/// <para>Per-channel arrays are indexed by <c>(int)DialChannel</c> and have <c>DialChannels.Count</c> entries.</para>
/// </summary>
public sealed class SpeedDialSnapshot
{
    private const int ChannelCount = DialChannels.Count;

    private static readonly PresetSummary[] NoPresets = new PresetSummary[0];
    private static readonly PairSummary[] NoPairs = new PairSummary[0];

    // ---- General ----

    /// <summary>A game is running.</summary>
    public bool GameRunning;

    /// <summary>SimHub game name ("LMU", ...); empty when none.</summary>
    public string GameName = string.Empty;

    /// <summary>A car is loaded (per-car operations possible).</summary>
    public bool HasCar;

    /// <summary>Stable car key (<c>CarIdentity.CarKey</c>).</summary>
    public string CarKey = string.Empty;

    /// <summary>Car name for the UI.</summary>
    public string CarDisplayName = string.Empty;

    /// <summary>The car key is a livery-specific placeholder (LMU's first seconds): edits are kept in memory and saved once the native key is known.</summary>
    public bool CarKeyProvisional;

    /// <summary>Path of the car's SpeedDial file (diagnostics); empty without a car.</summary>
    public string CarFilePath = string.Empty;

    /// <summary>Incremented whenever the car data instance changes (car change, import).</summary>
    public int CarDataVersion;

    // ---- Telemetry and Control Mapper ----

    /// <summary><c>IDialTelemetry.Name</c> of the current game's source; empty without a game.</summary>
    public string TelemetryName = string.Empty;

    /// <summary><c>IDialTelemetry.Describe()</c> (cached by the source).</summary>
    public string TelemetryDescription = string.Empty;

    /// <summary>Per channel: the sim exposes it (<c>IDialTelemetry.IsSupported</c>).</summary>
    public readonly bool[] ChannelSupported = new bool[ChannelCount];

    /// <summary>Per channel: current value (NaN = unknown).</summary>
    public readonly double[] CurrentValues = CreateNaNArray();

    /// <summary>Per channel: the car's maximum when the sim reports one (NaN = unknown).</summary>
    public readonly double[] MaxValues = CreateNaNArray();

    /// <summary>SimHub's Control Mapper is available.</summary>
    public bool ControlMapperAvailable;

    // ---- Dial job ----

    /// <summary>Copy of the dialer's status (state, label, per-channel progress, message, version).</summary>
    public readonly DialStatus Dial = new DialStatus();

    /// <summary>Same text as the <c>DLP.SpeedDial.Status</c> property (latest action/dial status).</summary>
    public string Status = string.Empty;

    /// <summary>Same text as <c>DLP.SpeedDial.LastResult</c>.</summary>
    public string LastResult = string.Empty;

    // ---- Presets of the current car ----

    /// <summary>The car's presets in list order (immutable; replaced on change).</summary>
    public IReadOnlyList<PresetSummary> Presets = NoPresets;

    /// <summary>Incremented whenever <see cref="Presets"/> is replaced.</summary>
    public int PresetsVersion;

    /// <summary>Number of Dial slots in use (<see cref="SpeedDialSettings.SlotCount"/>).</summary>
    public int SlotCount = SpeedDialSettings.DefaultSlotCount;

    /// <summary>Preset id per slot (length <see cref="SpeedDialSettings.MaxSlotCount"/>; null = empty).</summary>
    public readonly string[] SlotPresetIds = new string[SpeedDialSettings.MaxSlotCount];

    /// <summary>Preset selected for cycling; null = none.</summary>
    public string SelectedPresetId;

    // ---- Set/reset pairs (definitions + this car's stored values) ----

    /// <summary>One entry per defined pair (immutable; replaced on change).</summary>
    public IReadOnlyList<PairSummary> Pairs = NoPairs;

    /// <summary>Incremented whenever <see cref="Pairs"/> is replaced.</summary>
    public int PairsVersion;

    // ---- Global settings ----

    /// <summary>Copy of the global settings (<see cref="SpeedDialSettings.DeepCopy"/>; immutable by convention, replaced on change). Edit through <see cref="ISpeedDialHost.EditSettings"/>.</summary>
    public SpeedDialSettings Settings = new SpeedDialSettings();

    /// <summary>Incremented whenever <see cref="Settings"/> is replaced.</summary>
    public int SettingsVersion;

    // ---- Learning of the current car (per channel) ----

    /// <summary><see cref="ChannelLearning.Direction"/> (+1/-1).</summary>
    public readonly int[] LearnedDirection = CreateDirectionArray();

    /// <summary><see cref="ChannelLearning.DirectionConfirmed"/>.</summary>
    public readonly bool[] DirectionConfirmed = new bool[ChannelCount];

    /// <summary><see cref="ChannelLearning.Step"/> (0 = unknown).</summary>
    public readonly double[] LearnedStep = new double[ChannelCount];

    /// <summary><see cref="ChannelLearning.ObservedMin"/> (NaN = unknown).</summary>
    public readonly double[] ObservedMin = CreateNaNArray();

    /// <summary><see cref="ChannelLearning.ObservedMax"/> (NaN = unknown).</summary>
    public readonly double[] ObservedMax = CreateNaNArray();

    // ---- Diagnostics ----

    /// <summary>Newest error of the module ("HH:mm:ss Type: message"); empty when none.</summary>
    public string LastError = string.Empty;

    /// <summary>Frames processed by the shell (from <c>ShellDiagnostics</c>).</summary>
    public long FrameCount;

    /// <summary>Copies every field into <paramref name="t"/> (allocation-free; see the class remarks).</summary>
    public void CopyTo(SpeedDialSnapshot t)
    {
        t.GameRunning = GameRunning;
        t.GameName = GameName;
        t.HasCar = HasCar;
        t.CarKey = CarKey;
        t.CarDisplayName = CarDisplayName;
        t.CarKeyProvisional = CarKeyProvisional;
        t.CarFilePath = CarFilePath;
        t.CarDataVersion = CarDataVersion;
        t.TelemetryName = TelemetryName;
        t.TelemetryDescription = TelemetryDescription;
        t.ControlMapperAvailable = ControlMapperAvailable;
        for (int i = 0; i < ChannelCount; i++)
        {
            t.ChannelSupported[i] = ChannelSupported[i];
            t.CurrentValues[i] = CurrentValues[i];
            t.MaxValues[i] = MaxValues[i];
            t.LearnedDirection[i] = LearnedDirection[i];
            t.DirectionConfirmed[i] = DirectionConfirmed[i];
            t.LearnedStep[i] = LearnedStep[i];
            t.ObservedMin[i] = ObservedMin[i];
            t.ObservedMax[i] = ObservedMax[i];
        }

        Dial.CopyTo(t.Dial);
        t.Status = Status;
        t.LastResult = LastResult;
        t.Presets = Presets;
        t.PresetsVersion = PresetsVersion;
        t.SlotCount = SlotCount;
        for (int i = 0; i < SlotPresetIds.Length; i++)
        {
            t.SlotPresetIds[i] = SlotPresetIds[i];
        }

        t.SelectedPresetId = SelectedPresetId;
        t.Pairs = Pairs;
        t.PairsVersion = PairsVersion;
        t.Settings = Settings;
        t.SettingsVersion = SettingsVersion;
        t.LastError = LastError;
        t.FrameCount = FrameCount;
    }

    /// <summary>Fills the learning arrays from the car's learning (null = defaults). Allocation-free; data thread.</summary>
    public void SetLearning(SpeedDialCarData car)
    {
        for (int i = 0; i < ChannelCount; i++)
        {
            ChannelLearning learning = car?.GetLearning((DialChannel)i);
            LearnedDirection[i] = learning?.Direction ?? ChannelLearning.IncreaseRaises;
            DirectionConfirmed[i] = learning?.DirectionConfirmed ?? false;
            LearnedStep[i] = learning?.Step ?? 0.0;
            ObservedMin[i] = learning?.ObservedMin ?? double.NaN;
            ObservedMax[i] = learning?.ObservedMax ?? double.NaN;
        }
    }

    private static double[] CreateNaNArray()
    {
        var values = new double[ChannelCount];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = double.NaN;
        }

        return values;
    }

    private static int[] CreateDirectionArray()
    {
        var values = new int[ChannelCount];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = ChannelLearning.IncreaseRaises;
        }

        return values;
    }
}

/// <summary>
/// Immutable UI view of one preset (<see cref="SpeedDialSnapshot.Presets"/>). Built by the module on the data thread
/// whenever the car's presets change (allocates; never per frame).
/// </summary>
public sealed class PresetSummary
{
    private readonly double[] values = new double[DialChannels.Count];

    /// <summary>Copies <paramref name="preset"/>.</summary>
    /// <param name="preset">Source preset (normalized).</param>
    /// <param name="index">Position in the car's preset list (0-based).</param>
    public PresetSummary(DialPreset preset, int index)
    {
        if (preset == null)
        {
            throw new ArgumentNullException(nameof(preset));
        }

        Id = preset.Id ?? string.Empty;
        Name = preset.Name ?? string.Empty;
        Index = index;
        int included = 0;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = preset.TryGetValue((DialChannel)i, out double value) ? value : double.NaN;
            if (!double.IsNaN(values[i]))
            {
                included++;
            }
        }

        IncludedCount = included;
    }

    /// <summary><see cref="DialPreset.Id"/>.</summary>
    public string Id { get; }

    /// <summary><see cref="DialPreset.Name"/>.</summary>
    public string Name { get; }

    /// <summary>Position in the car's preset list (0-based; <c>DLP.SpeedDial.SelectedIndex</c> is this + 1).</summary>
    public int Index { get; }

    /// <summary>Number of included channels.</summary>
    public int IncludedCount { get; }

    /// <summary>Builds the summaries of every preset of <paramref name="car"/> (empty list without a car).</summary>
    public static IReadOnlyList<PresetSummary> BuildList(SpeedDialCarData car)
    {
        var list = new List<PresetSummary>();
        if (car?.Presets != null)
        {
            for (int i = 0; i < car.Presets.Count; i++)
            {
                if (car.Presets[i] != null)
                {
                    list.Add(new PresetSummary(car.Presets[i], list.Count));
                }
            }
        }

        return list.ToArray();
    }

    /// <summary>The target of <paramref name="channel"/>; NaN when not included.</summary>
    public double GetValue(DialChannel channel) => DialChannels.IsValid(channel) ? values[(int)channel] : double.NaN;

    /// <summary>True when the preset includes <paramref name="channel"/>.</summary>
    public bool Includes(DialChannel channel) => !double.IsNaN(GetValue(channel));
}

/// <summary>
/// Immutable UI view of one set/reset pair: its global definition plus the current car's stored values
/// (<see cref="SpeedDialSnapshot.Pairs"/>). Built by the module whenever the pair definitions or the stored values
/// change (allocates; never per frame).
/// </summary>
public sealed class PairSummary
{
    private readonly bool[] included = new bool[DialChannels.Count];
    private readonly double[] stored = new double[DialChannels.Count];

    /// <param name="index">Pair index (0-based; actions <c>SpeedDial.Set{index+1}</c>/<c>Reset{index+1}</c>).</param>
    /// <param name="definition">The pair definition (normalized).</param>
    /// <param name="snapshot">The car's stored values for the pair; null = nothing stored (no car).</param>
    public PairSummary(int index, SetResetPairDefinition definition, DialSnapshot snapshot)
    {
        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        Index = index;
        Name = definition.Name ?? string.Empty;
        var channels = new List<DialChannel>(DialChannels.Count);
        int storedCount = 0;
        for (int i = 0; i < included.Length; i++)
        {
            var channel = (DialChannel)i;
            included[i] = definition.Includes(channel);
            if (included[i])
            {
                channels.Add(channel);
            }

            stored[i] = included[i] && snapshot != null && snapshot.TryGetValue(channel, out double value) ? value : double.NaN;
            if (!double.IsNaN(stored[i]))
            {
                storedCount++;
            }
        }

        Channels = channels.ToArray();
        HasStoredValues = storedCount > 0;
        CapturedUtc = HasStoredValues ? snapshot.CapturedUtc : null;
    }

    /// <summary>Pair index (0-based).</summary>
    public int Index { get; }

    /// <summary><see cref="SetResetPairDefinition.Name"/>.</summary>
    public string Name { get; }

    /// <summary>The pair's channels in enum order.</summary>
    public IReadOnlyList<DialChannel> Channels { get; }

    /// <summary>True when the car has a stored value for at least one of the pair's channels ("nothing stored" otherwise).</summary>
    public bool HasStoredValues { get; }

    /// <summary>When the stored values were captured (UTC); null when nothing is stored.</summary>
    public DateTime? CapturedUtc { get; }

    /// <summary>Builds one summary per pair of <paramref name="settings"/> with the stored values of <paramref name="car"/> (null = no car).</summary>
    public static IReadOnlyList<PairSummary> BuildList(SpeedDialSettings settings, SpeedDialCarData car)
    {
        var list = new List<PairSummary>();
        if (settings?.Pairs != null)
        {
            for (int i = 0; i < settings.Pairs.Count; i++)
            {
                if (settings.Pairs[i] != null)
                {
                    list.Add(new PairSummary(i, settings.Pairs[i], car?.GetPairSnapshot(i)));
                }
            }
        }

        return list.ToArray();
    }

    /// <summary>True when both lists describe the same pairs (names, channels, stored values and capture times).</summary>
    public static bool ListsEqual(IReadOnlyList<PairSummary> a, IReadOnlyList<PairSummary> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a == null || b == null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].SameAs(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="other"/> shows exactly the same pair (NaN equals NaN).</summary>
    public bool SameAs(PairSummary other)
    {
        if (other == null || other.Index != Index || !string.Equals(other.Name, Name, StringComparison.Ordinal)
            || other.CapturedUtc != CapturedUtc)
        {
            return false;
        }

        for (int i = 0; i < included.Length; i++)
        {
            if (other.included[i] != included[i] || !other.stored[i].Equals(stored[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the pair includes <paramref name="channel"/>.</summary>
    public bool Includes(DialChannel channel) => DialChannels.IsValid(channel) && included[(int)channel];

    /// <summary>The stored value of <paramref name="channel"/>; NaN when none (or not part of the pair).</summary>
    public double GetStoredValue(DialChannel channel) => DialChannels.IsValid(channel) ? stored[(int)channel] : double.NaN;
}
