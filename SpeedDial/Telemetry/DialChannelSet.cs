using System;
using System.Text;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>
/// The shared implementation behind every <c>IDialTelemetry</c>: one optional <see cref="DialChannelSource"/> per
/// channel (null = the sim does not expose it) plus the cached diagnostics text. The per-sim classes only decide
/// which paths and conversions to use.
/// </summary>
internal sealed class DialChannelSet
{
    private readonly ITelemetryReader reader;
    private readonly DialChannelSource[] sources = new DialChannelSource[DialChannels.Count];
    private readonly int[] describedSignatures = new int[DialChannels.Count];
    private readonly string header;
    private string description;

    /// <summary>
    /// Creates the set. <paramref name="header"/> is the first diagnostics line (source name and property prefix).
    /// Every entry of <paramref name="channelSources"/> must belong to a different, valid channel.
    /// </summary>
    public DialChannelSet(ITelemetryReader reader, string header, params DialChannelSource[] channelSources)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.header = header ?? string.Empty;
        if (channelSources == null)
        {
            return;
        }

        for (int i = 0; i < channelSources.Length; i++)
        {
            DialChannelSource source = channelSources[i];
            if (source == null || !DialChannels.IsValid(source.Channel) || sources[(int)source.Channel] != null)
            {
                throw new ArgumentException("Each channel needs exactly one valid source.", nameof(channelSources));
            }

            sources[(int)source.Channel] = source;
        }
    }

    /// <summary>True when the sim exposes the channel. Allocation-free.</summary>
    public bool IsSupported(DialChannel channel) => Get(channel) != null;

    /// <summary>See <c>IDialTelemetry.TryRead</c>. Allocation-free, never throws for bad data.</summary>
    public bool TryRead(DialChannel channel, out double value)
    {
        DialChannelSource source = Get(channel);
        if (source == null)
        {
            value = double.NaN;
            return false;
        }

        return source.TryRead(reader, out value);
    }

    /// <summary>See <c>IDialTelemetry.TryGetMax</c>. Allocation-free.</summary>
    public bool TryGetMax(DialChannel channel, out double max)
    {
        DialChannelSource source = Get(channel);
        if (source == null)
        {
            max = double.NaN;
            return false;
        }

        return source.TryGetMax(reader, out max);
    }

    /// <summary>
    /// Diagnostics text: refreshes every supported channel's value and maximum lookups (allocation-free) and rebuilds
    /// the text only when a lookup result changed since the previous call; otherwise returns the same instance.
    /// </summary>
    public string Describe()
    {
        bool changed = description == null;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            DialChannelSource source = sources[i];
            if (source == null)
            {
                continue;
            }

            source.TryRead(reader, out _);
            source.TryGetMax(reader, out _);
            int signature = source.Signature();
            if (signature != describedSignatures[i])
            {
                describedSignatures[i] = signature;
                changed = true;
            }
        }

        if (changed)
        {
            description = Build();
        }

        return description;
    }

    private DialChannelSource Get(DialChannel channel) => DialChannels.IsValid(channel) ? sources[(int)channel] : null;

    private string Build()
    {
        var builder = new StringBuilder();
        builder.AppendLine(header);
        for (int i = 0; i < DialChannels.Count; i++)
        {
            DialChannelSource source = sources[i];
            if (source != null)
            {
                source.Describe(builder);
            }
            else
            {
                builder.Append("  ").Append(DialChannels.DisplayName((DialChannel)i)).AppendLine(": not supported by this sim");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
