using System;
using System.Collections.Generic;

namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// Values stored by a "Set" of a set/reset pair (<see cref="SpeedDialCarData.PairSnapshots"/>, index = pair): the
/// current values of the pair's channels at capture time; "Reset" dials back to them. Plain JSON DTO.
/// </summary>
public sealed class DialSnapshot
{
    /// <summary>Captured values keyed by <see cref="DialChannels.Id"/> (only channels that had valid telemetry).</summary>
    public Dictionary<string, double> Values = ChannelValues.Create();

    /// <summary>When the values were captured (UTC); null when nothing is stored.</summary>
    public DateTime? CapturedUtc;

    /// <summary>True when at least one value is stored.</summary>
    public bool HasValues() => ChannelValues.CountFinite(Values) > 0;

    /// <summary>The stored value of <paramref name="channel"/>; false (NaN) when none. Allocation-free.</summary>
    public bool TryGetValue(DialChannel channel, out double value) => ChannelValues.TryGet(Values, channel, out value);

    /// <summary>Stores (sanitized) or removes (null / non-finite) one value.</summary>
    public void SetValue(DialChannel channel, double? value)
    {
        Values ??= ChannelValues.Create();
        ChannelValues.Set(Values, channel, value);
    }

    /// <summary>Forgets everything ("nothing stored").</summary>
    public void Clear()
    {
        Values ??= ChannelValues.Create();
        Values.Clear();
        CapturedUtc = null;
    }

    /// <summary>Repairs a deserialized instance (values normalized; an empty snapshot has no capture time).</summary>
    public void Normalize()
    {
        Values = ChannelValues.Normalize(Values);
        if (Values.Count == 0)
        {
            CapturedUtc = null;
        }
    }

    /// <summary>Independent copy.</summary>
    public DialSnapshot DeepCopy() => new DialSnapshot
    {
        Values = ChannelValues.Copy(Values),
        CapturedUtc = CapturedUtc,
    };
}
