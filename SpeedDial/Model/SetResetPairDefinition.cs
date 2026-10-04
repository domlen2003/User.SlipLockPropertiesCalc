using System;
using System.Collections.Generic;
using System.Globalization;

namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// Global definition of one set/reset pair (<see cref="SpeedDialSettings.Pairs"/>): "Set" stores the current values
/// of <see cref="Channels"/> per car (<see cref="SpeedDialCarData.PairSnapshots"/>), "Reset" dials back to them.
/// Plain JSON DTO (public fields).
/// </summary>
public sealed class SetResetPairDefinition
{
    /// <summary>User-visible name, e.g. "Pair 1".</summary>
    public string Name = string.Empty;

    /// <summary>Channel ids (<see cref="DialChannels.Id"/>) the pair captures; after <see cref="Normalize"/> canonical, unique, in enum order. May be empty.</summary>
    public List<string> Channels = new List<string>();

    /// <summary>The default name of the pair at <paramref name="index"/> ("Pair 1", ...).</summary>
    public static string DefaultName(int index) => "Pair " + (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>A pair with the given name and channels.</summary>
    public static SetResetPairDefinition Create(string name, params DialChannel[] channels)
    {
        var pair = new SetResetPairDefinition { Name = name ?? string.Empty };
        if (channels != null)
        {
            foreach (DialChannel channel in channels)
            {
                pair.Channels.Add(DialChannels.Id(channel));
            }
        }

        return pair;
    }

    /// <summary>True when the pair captures <paramref name="channel"/>. Allocation-free.</summary>
    public bool Includes(DialChannel channel)
    {
        if (Channels == null || !DialChannels.IsValid(channel))
        {
            return false;
        }

        string id = DialChannels.Id(channel);
        for (int i = 0; i < Channels.Count; i++)
        {
            if (string.Equals(Channels[i], id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Includes or excludes <paramref name="channel"/> (keeps enum order).</summary>
    public void SetIncluded(DialChannel channel, bool included)
    {
        if (!DialChannels.IsValid(channel))
        {
            return;
        }

        var next = new List<string>(DialChannels.Count);
        for (int i = 0; i < DialChannels.Count; i++)
        {
            var candidate = (DialChannel)i;
            bool keep = candidate == channel ? included : Includes(candidate);
            if (keep)
            {
                next.Add(DialChannels.Id(candidate));
            }
        }

        Channels = next;
    }

    /// <summary>
    /// Repairs a deserialized instance: name cleaned (<see cref="DefaultName"/> when empty, cut to
    /// <see cref="DialPreset.MaxNameLength"/>); channels canonical, unknown ids and duplicates dropped, enum order.
    /// </summary>
    /// <param name="index">Position in <see cref="SpeedDialSettings.Pairs"/> (for the fallback name).</param>
    public void Normalize(int index)
    {
        Name = DialPreset.CleanName(Name, DefaultName(index));
        var present = new bool[DialChannels.Count];
        if (Channels != null)
        {
            foreach (string id in Channels)
            {
                if (DialChannels.TryParseId(id?.Trim(), out DialChannel channel))
                {
                    present[(int)channel] = true;
                }
            }
        }

        var next = new List<string>(DialChannels.Count);
        for (int i = 0; i < DialChannels.Count; i++)
        {
            if (present[i])
            {
                next.Add(DialChannels.Id((DialChannel)i));
            }
        }

        Channels = next;
    }

    /// <summary>Independent copy.</summary>
    public SetResetPairDefinition DeepCopy() => new SetResetPairDefinition
    {
        Name = Name,
        Channels = Channels == null ? new List<string>() : new List<string>(Channels),
    };
}
