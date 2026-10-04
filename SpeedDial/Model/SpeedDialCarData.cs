using System;
using System.Collections.Generic;

namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// Everything SpeedDial remembers per car and game, stored as
/// <c>PluginsData\DLP\SpeedDial\Cars\&lt;Sim&gt;\&lt;CarKey&gt;_&lt;fnv1a8&gt;.json</c> (<c>CarFileNaming</c>, same names as
/// Haptics): named presets, slot assignments, the selected preset for cycling, stored set/reset pair values and the
/// learned channel behavior. Also the export/import file format (import takes only <see cref="Presets"/>).
/// <para>
/// Plain JSON DTO (public fields). COMPATIBILITY: field names and channel ids are the file format. Owned by the data
/// thread; the UI sees it only through the snapshot's summaries.
/// </para>
/// </summary>
public sealed class SpeedDialCarData
{
    /// <summary>Current file format version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Most presets per car; <see cref="Normalize"/> drops the rest, the module refuses to create more.</summary>
    public const int MaxPresets = 100;

    /// <summary>File format version (0 when absent).</summary>
    public int SchemaVersion = CurrentSchemaVersion;

    /// <summary>SimHub game name, e.g. "LMU".</summary>
    public string SimKey = string.Empty;

    /// <summary>Stable car key (<c>CarIdentityResolver</c>).</summary>
    public string CarKey = string.Empty;

    /// <summary>Car name for the UI.</summary>
    public string DisplayName = string.Empty;

    /// <summary>Named presets in display/cycle order (unique <see cref="DialPreset.Id"/>s after <see cref="Normalize"/>).</summary>
    public List<DialPreset> Presets = new List<DialPreset>();

    /// <summary>Preset id per Dial slot (index = slot, 0-based; null = empty). Length <see cref="SpeedDialSettings.MaxSlotCount"/> after <see cref="Normalize"/>.</summary>
    public string[] SlotPresetIds = new string[SpeedDialSettings.MaxSlotCount];

    /// <summary>Preset selected for Next/Previous/Apply-selected; null = none.</summary>
    public string SelectedPresetId;

    /// <summary>Stored values per set/reset pair (index = pair). Length <see cref="SpeedDialSettings.MaxPairCount"/>, entries never null after <see cref="Normalize"/>.</summary>
    public List<DialSnapshot> PairSnapshots = CreateEmptyPairSnapshots();

    /// <summary>Learned behavior per channel, keyed by <see cref="DialChannels.Id"/> (case-insensitive; one entry per channel after <see cref="Normalize"/>).</summary>
    public Dictionary<string, ChannelLearning> Learning = CreateDefaultLearning();

    /// <summary>Last save time (UTC), informational.</summary>
    public DateTime LastUpdatedUtc;

    /// <summary>New, normalized data for a car.</summary>
    public static SpeedDialCarData Create(string simKey, string carKey, string displayName)
    {
        var data = new SpeedDialCarData
        {
            SimKey = simKey ?? string.Empty,
            CarKey = carKey ?? string.Empty,
            DisplayName = displayName ?? string.Empty,
        };
        data.Normalize();
        return data;
    }

    /// <summary><see cref="SpeedDialSettings.MaxPairCount"/> empty snapshots.</summary>
    public static List<DialSnapshot> CreateEmptyPairSnapshots()
    {
        var snapshots = new List<DialSnapshot>(SpeedDialSettings.MaxPairCount);
        for (int i = 0; i < SpeedDialSettings.MaxPairCount; i++)
        {
            snapshots.Add(new DialSnapshot());
        }

        return snapshots;
    }

    /// <summary>A default <see cref="ChannelLearning"/> entry per channel.</summary>
    public static Dictionary<string, ChannelLearning> CreateDefaultLearning()
    {
        var learning = new Dictionary<string, ChannelLearning>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < DialChannels.Count; i++)
        {
            learning[DialChannels.Id((DialChannel)i)] = new ChannelLearning();
        }

        return learning;
    }

    /// <summary>Index of the preset with <paramref name="presetId"/> (ordinal, case-insensitive), or -1. Allocation-free.</summary>
    public int IndexOfPreset(string presetId)
    {
        if (string.IsNullOrEmpty(presetId) || Presets == null)
        {
            return -1;
        }

        for (int i = 0; i < Presets.Count; i++)
        {
            DialPreset preset = Presets[i];
            if (preset != null && string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The preset with <paramref name="presetId"/>, or null. Allocation-free.</summary>
    public DialPreset FindPreset(string presetId)
    {
        int index = IndexOfPreset(presetId);
        return index < 0 ? null : Presets[index];
    }

    /// <summary>The preset assigned to slot <paramref name="slotIndex"/> (0-based), or null (empty slot, unknown id, out of range).</summary>
    public DialPreset GetSlotPreset(int slotIndex) =>
        SlotPresetIds != null && slotIndex >= 0 && slotIndex < SlotPresetIds.Length ? FindPreset(SlotPresetIds[slotIndex]) : null;

    /// <summary>The stored values of pair <paramref name="pairIndex"/> (0-based), or null when out of range.</summary>
    public DialSnapshot GetPairSnapshot(int pairIndex) =>
        PairSnapshots != null && pairIndex >= 0 && pairIndex < PairSnapshots.Count ? PairSnapshots[pairIndex] : null;

    /// <summary>The learning entry of <paramref name="channel"/> (never null after <see cref="Normalize"/>; null before when missing). Allocation-free.</summary>
    public ChannelLearning GetLearning(DialChannel channel)
    {
        if (Learning != null && Learning.TryGetValue(DialChannels.Id(channel), out ChannelLearning learning))
        {
            return learning;
        }

        return null;
    }

    /// <summary>
    /// Removes the preset and every reference to it (slots, selection). Returns false when it does not exist.
    /// </summary>
    public bool RemovePreset(string presetId)
    {
        int index = IndexOfPreset(presetId);
        if (index < 0)
        {
            return false;
        }

        string id = Presets[index].Id;
        Presets.RemoveAt(index);
        if (SlotPresetIds != null)
        {
            for (int i = 0; i < SlotPresetIds.Length; i++)
            {
                if (string.Equals(SlotPresetIds[i], id, StringComparison.OrdinalIgnoreCase))
                {
                    SlotPresetIds[i] = null;
                }
            }
        }

        if (string.Equals(SelectedPresetId, id, StringComparison.OrdinalIgnoreCase))
        {
            SelectedPresetId = null;
        }

        return true;
    }

    /// <summary>
    /// Repairs a deserialized or hand-edited instance: null strings and collections; presets (null entries dropped,
    /// at most <see cref="MaxPresets"/>, each normalized, duplicate ids replaced by fresh ones); slot array resized to
    /// <see cref="SpeedDialSettings.MaxSlotCount"/> with references to unknown presets cleared; unknown selection
    /// cleared; pair snapshots resized to <see cref="SpeedDialSettings.MaxPairCount"/> without null entries; learning
    /// keyed by canonical ids (unknown ids dropped, missing channels added, entries normalized).
    /// </summary>
    public void Normalize()
    {
        SimKey ??= string.Empty;
        CarKey ??= string.Empty;
        DisplayName ??= string.Empty;

        var presets = new List<DialPreset>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Presets != null)
        {
            foreach (DialPreset preset in Presets)
            {
                if (preset == null || presets.Count >= MaxPresets)
                {
                    continue;
                }

                preset.Normalize(presets.Count);
                while (!seenIds.Add(preset.Id))
                {
                    preset.Id = DialPreset.NewId();
                }

                presets.Add(preset);
            }
        }

        Presets = presets;

        var slots = new string[SpeedDialSettings.MaxSlotCount];
        if (SlotPresetIds != null)
        {
            for (int i = 0; i < slots.Length && i < SlotPresetIds.Length; i++)
            {
                DialPreset preset = FindPreset(SlotPresetIds[i]?.Trim());
                slots[i] = preset?.Id;
            }
        }

        SlotPresetIds = slots;
        SelectedPresetId = FindPreset(SelectedPresetId?.Trim())?.Id;

        var snapshots = new List<DialSnapshot>(SpeedDialSettings.MaxPairCount);
        for (int i = 0; i < SpeedDialSettings.MaxPairCount; i++)
        {
            DialSnapshot snapshot = PairSnapshots != null && i < PairSnapshots.Count ? PairSnapshots[i] : null;
            snapshot ??= new DialSnapshot();
            snapshot.Normalize();
            snapshots.Add(snapshot);
        }

        PairSnapshots = snapshots;

        var learning = new Dictionary<string, ChannelLearning>(StringComparer.OrdinalIgnoreCase);
        if (Learning != null)
        {
            foreach (KeyValuePair<string, ChannelLearning> entry in Learning)
            {
                if (DialChannels.TryParseId(entry.Key?.Trim(), out DialChannel channel))
                {
                    ChannelLearning value = entry.Value ?? new ChannelLearning();
                    value.Normalize();
                    learning[DialChannels.Id(channel)] = value;
                }
            }
        }

        for (int i = 0; i < DialChannels.Count; i++)
        {
            string id = DialChannels.Id((DialChannel)i);
            if (!learning.ContainsKey(id))
            {
                learning[id] = new ChannelLearning();
            }
        }

        Learning = learning;
        SchemaVersion = CurrentSchemaVersion;
    }

    /// <summary>Independent copy (asynchronous writes, export).</summary>
    public SpeedDialCarData DeepCopy()
    {
        var copy = new SpeedDialCarData
        {
            SchemaVersion = SchemaVersion,
            SimKey = SimKey,
            CarKey = CarKey,
            DisplayName = DisplayName,
            Presets = new List<DialPreset>(),
            SlotPresetIds = SlotPresetIds == null ? new string[SpeedDialSettings.MaxSlotCount] : (string[])SlotPresetIds.Clone(),
            SelectedPresetId = SelectedPresetId,
            PairSnapshots = new List<DialSnapshot>(),
            Learning = new Dictionary<string, ChannelLearning>(StringComparer.OrdinalIgnoreCase),
            LastUpdatedUtc = LastUpdatedUtc,
        };

        if (Presets != null)
        {
            foreach (DialPreset preset in Presets)
            {
                copy.Presets.Add(preset?.DeepCopy());
            }
        }

        if (PairSnapshots != null)
        {
            foreach (DialSnapshot snapshot in PairSnapshots)
            {
                copy.PairSnapshots.Add(snapshot?.DeepCopy());
            }
        }

        if (Learning != null)
        {
            foreach (KeyValuePair<string, ChannelLearning> entry in Learning)
            {
                if (entry.Key != null)
                {
                    copy.Learning[entry.Key] = entry.Value?.DeepCopy();
                }
            }
        }

        return copy;
    }
}
