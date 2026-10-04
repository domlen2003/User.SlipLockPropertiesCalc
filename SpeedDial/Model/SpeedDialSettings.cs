using System;
using System.Collections.Generic;

namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// Global SpeedDial settings, stored as <c>PluginsData\DLP\SpeedDial\Settings.json</c>: Control Mapper role bindings
/// per channel, slot count, set/reset pair definitions, dial timing and channel order. Per-car data (presets, slot
/// assignments, stored pair values, learning) lives in <see cref="SpeedDialCarData"/>.
/// <para>
/// Plain JSON DTO (public fields). COMPATIBILITY: field names and the channel ids used as keys are the file format.
/// The data thread owns the live object; the UI edits it only through <c>ISpeedDialHost.EditSettings</c> and reads
/// <see cref="DeepCopy"/> copies from the snapshot.
/// </para>
/// </summary>
public sealed class SpeedDialSettings
{
    /// <summary>Current file format version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Lowest <see cref="SlotCount"/>.</summary>
    public const int MinSlotCount = 1;

    /// <summary>Highest <see cref="SlotCount"/>; also the number of registered <c>SpeedDial.DialN</c> actions and the length of <see cref="SpeedDialCarData.SlotPresetIds"/>.</summary>
    public const int MaxSlotCount = 8;

    /// <summary>Default <see cref="SlotCount"/>.</summary>
    public const int DefaultSlotCount = 4;

    /// <summary>Lowest number of <see cref="Pairs"/>.</summary>
    public const int MinPairCount = 1;

    /// <summary>Highest number of <see cref="Pairs"/>; also the number of registered Set/Reset actions and the length of <see cref="SpeedDialCarData.PairSnapshots"/>.</summary>
    public const int MaxPairCount = 4;

    /// <summary>File format version (0 when absent).</summary>
    public int SchemaVersion = CurrentSchemaVersion;

    /// <summary>Binding per channel, keyed by <see cref="DialChannels.Id"/> (case-insensitive; one entry per channel after <see cref="Normalize"/>).</summary>
    public Dictionary<string, ChannelBinding> Channels = CreateDefaultBindings();

    /// <summary>Number of Dial buttons in use (1..8); slots beyond it are ignored, their assignments kept.</summary>
    public int SlotCount = DefaultSlotCount;

    /// <summary>Set/reset pair definitions (1..4; index = pair).</summary>
    public List<SetResetPairDefinition> Pairs = CreateDefaultPairs();

    /// <summary>Dial timing and safety limits.</summary>
    public DialTiming Timing = new DialTiming();

    /// <summary>Order in which a job dials its channels (ids; every channel exactly once after <see cref="Normalize"/>).</summary>
    public string[] ChannelOrder = CreateDefaultOrder();

    /// <summary>Default bindings of every channel (the user's Control Mapper roles).</summary>
    public static Dictionary<string, ChannelBinding> CreateDefaultBindings()
    {
        var bindings = new Dictionary<string, ChannelBinding>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < DialChannels.Count; i++)
        {
            var channel = (DialChannel)i;
            bindings[DialChannels.Id(channel)] = ChannelBinding.CreateDefault(channel);
        }

        return bindings;
    }

    /// <summary>Default pairs: "Pair 1" with every channel, "Pair 2" with brake bias only.</summary>
    public static List<SetResetPairDefinition> CreateDefaultPairs() => new List<SetResetPairDefinition>
    {
        SetResetPairDefinition.Create(SetResetPairDefinition.DefaultName(0), DialChannel.Tc1, DialChannel.Tc2, DialChannel.Tc3, DialChannel.Abs, DialChannel.BrakeBias),
        SetResetPairDefinition.Create(SetResetPairDefinition.DefaultName(1), DialChannel.BrakeBias),
    };

    /// <summary>Default channel order: TC1, TC2, TC3, ABS, BB.</summary>
    public static string[] CreateDefaultOrder()
    {
        var order = new string[DialChannels.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = DialChannels.Id((DialChannel)i);
        }

        return order;
    }

    /// <summary>The binding of <paramref name="channel"/> (never null after <see cref="Normalize"/>; null before when missing). Allocation-free.</summary>
    public ChannelBinding GetBinding(DialChannel channel)
    {
        if (Channels != null && Channels.TryGetValue(DialChannels.Id(channel), out ChannelBinding binding))
        {
            return binding;
        }

        return null;
    }

    /// <summary>The pair at <paramref name="pairIndex"/> (0-based), or null when out of range.</summary>
    public SetResetPairDefinition GetPair(int pairIndex) =>
        Pairs != null && pairIndex >= 0 && pairIndex < Pairs.Count ? Pairs[pairIndex] : null;

    /// <summary>
    /// Writes the dial order into <paramref name="target"/> (length ≥ <see cref="DialChannels.Count"/>) and returns
    /// the number written (= <see cref="DialChannels.Count"/> after <see cref="Normalize"/>). Unknown or duplicate ids
    /// are skipped; channels missing from <see cref="ChannelOrder"/> are appended in enum order. Allocation-free.
    /// </summary>
    public int FillChannelOrder(DialChannel[] target)
    {
        if (target == null)
        {
            return 0;
        }

        int count = 0;
        if (ChannelOrder != null)
        {
            for (int i = 0; i < ChannelOrder.Length && count < target.Length; i++)
            {
                if (DialChannels.TryParseId(ChannelOrder[i], out DialChannel channel) && !Contains(target, count, channel))
                {
                    target[count++] = channel;
                }
            }
        }

        for (int i = 0; i < DialChannels.Count && count < target.Length; i++)
        {
            var channel = (DialChannel)i;
            if (!Contains(target, count, channel))
            {
                target[count++] = channel;
            }
        }

        return count;
    }

    /// <summary>
    /// Repairs a deserialized or hand-edited instance: one binding per channel (unknown ids dropped, missing channels
    /// get defaults, null bindings repaired), <see cref="SlotCount"/> clamped to 1..8, pairs repaired (none → the
    /// defaults, more than 4 → cut), timing clamped, <see cref="ChannelOrder"/> complete and canonical.
    /// </summary>
    public void Normalize()
    {
        var bindings = new Dictionary<string, ChannelBinding>(StringComparer.OrdinalIgnoreCase);
        if (Channels != null)
        {
            foreach (KeyValuePair<string, ChannelBinding> entry in Channels)
            {
                if (DialChannels.TryParseId(entry.Key?.Trim(), out DialChannel channel))
                {
                    ChannelBinding binding = entry.Value ?? ChannelBinding.CreateDefault(channel);
                    binding.Normalize();
                    bindings[DialChannels.Id(channel)] = binding;
                }
            }
        }

        for (int i = 0; i < DialChannels.Count; i++)
        {
            var channel = (DialChannel)i;
            string id = DialChannels.Id(channel);
            if (!bindings.ContainsKey(id))
            {
                bindings[id] = ChannelBinding.CreateDefault(channel);
            }
        }

        Channels = bindings;

        if (SlotCount < MinSlotCount)
        {
            SlotCount = MinSlotCount;
        }
        else if (SlotCount > MaxSlotCount)
        {
            SlotCount = MaxSlotCount;
        }

        var pairs = new List<SetResetPairDefinition>(MaxPairCount);
        if (Pairs != null)
        {
            foreach (SetResetPairDefinition pair in Pairs)
            {
                if (pair != null && pairs.Count < MaxPairCount)
                {
                    pairs.Add(pair);
                }
            }
        }

        if (pairs.Count < MinPairCount)
        {
            pairs = CreateDefaultPairs();
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            pairs[i].Normalize(i);
        }

        Pairs = pairs;

        Timing ??= new DialTiming();
        Timing.Normalize();

        var order = new DialChannel[DialChannels.Count];
        int count = FillChannelOrder(order);
        ChannelOrder = new string[count];
        for (int i = 0; i < count; i++)
        {
            ChannelOrder[i] = DialChannels.Id(order[i]);
        }

        SchemaVersion = CurrentSchemaVersion;
    }

    /// <summary>Independent copy (for asynchronous writes and the UI snapshot).</summary>
    public SpeedDialSettings DeepCopy()
    {
        var copy = new SpeedDialSettings
        {
            SchemaVersion = SchemaVersion,
            Channels = new Dictionary<string, ChannelBinding>(StringComparer.OrdinalIgnoreCase),
            SlotCount = SlotCount,
            Pairs = new List<SetResetPairDefinition>(),
            Timing = Timing?.DeepCopy() ?? new DialTiming(),
            ChannelOrder = ChannelOrder == null ? CreateDefaultOrder() : (string[])ChannelOrder.Clone(),
        };

        if (Channels != null)
        {
            foreach (KeyValuePair<string, ChannelBinding> entry in Channels)
            {
                if (entry.Key != null)
                {
                    copy.Channels[entry.Key] = entry.Value?.DeepCopy();
                }
            }
        }

        if (Pairs != null)
        {
            foreach (SetResetPairDefinition pair in Pairs)
            {
                copy.Pairs.Add(pair?.DeepCopy());
            }
        }

        return copy;
    }

    private static bool Contains(DialChannel[] channels, int count, DialChannel channel)
    {
        for (int i = 0; i < count; i++)
        {
            if (channels[i] == channel)
            {
                return true;
            }
        }

        return false;
    }
}
