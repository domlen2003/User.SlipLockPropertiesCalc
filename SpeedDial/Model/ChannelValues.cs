using System;
using System.Collections.Generic;
using DivebombLogistics.Core;

namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// Helpers for the per-channel value maps of the JSON DTOs (<see cref="DialPreset.Values"/>,
/// <see cref="DialSnapshot.Values"/>): keys are <see cref="DialChannels.Id"/> strings (case-insensitive), values are
/// sanitized with <see cref="DialChannels.Sanitize"/>. Lookups are allocation-free.
/// </summary>
internal static class ChannelValues
{
    /// <summary>A new empty, case-insensitive map.</summary>
    public static Dictionary<string, double> Create() => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads <paramref name="channel"/>; false (and NaN) when the map is null, lacks the key or holds a non-finite value.</summary>
    public static bool TryGet(Dictionary<string, double> values, DialChannel channel, out double value)
    {
        if (values != null && values.TryGetValue(DialChannels.Id(channel), out value) && MathUtil.IsFinite(value))
        {
            return true;
        }

        value = double.NaN;
        return false;
    }

    /// <summary>Stores the sanitized value, or removes the key when <paramref name="value"/> is null or not finite.</summary>
    public static void Set(Dictionary<string, double> values, DialChannel channel, double? value)
    {
        if (values == null || !DialChannels.IsValid(channel))
        {
            return;
        }

        string id = DialChannels.Id(channel);
        double sanitized = value.HasValue ? DialChannels.Sanitize(channel, value.Value) : double.NaN;
        if (MathUtil.IsFinite(sanitized))
        {
            values[id] = sanitized;
        }
        else
        {
            values.Remove(id);
        }
    }

    /// <summary>Number of channels with a finite value.</summary>
    public static int CountFinite(Dictionary<string, double> values)
    {
        int count = 0;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            if (TryGet(values, (DialChannel)i, out _))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Returns a repaired copy: case-insensitive, canonical ids, unknown ids and non-finite values dropped, values
    /// sanitized. Null input gives an empty map.
    /// </summary>
    public static Dictionary<string, double> Normalize(Dictionary<string, double> source)
    {
        Dictionary<string, double> result = Create();
        if (source == null)
        {
            return result;
        }

        foreach (KeyValuePair<string, double> entry in source)
        {
            if (DialChannels.TryParseId(entry.Key?.Trim(), out DialChannel channel))
            {
                Set(result, channel, entry.Value);
            }
        }

        return result;
    }

    /// <summary>Independent case-insensitive copy (null gives an empty map).</summary>
    public static Dictionary<string, double> Copy(Dictionary<string, double> source)
    {
        Dictionary<string, double> result = Create();
        if (source != null)
        {
            foreach (KeyValuePair<string, double> entry in source)
            {
                if (entry.Key != null)
                {
                    result[entry.Key] = entry.Value;
                }
            }
        }

        return result;
    }
}
