using System;
using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>One channel checkbox of a pair definition (Setup expander).</summary>
public sealed class PairChannelOption : ObservableObject
{
    private readonly Action<DialChannel, bool> _changed;
    private bool _isIncluded;

    /// <param name="channel">The channel.</param>
    /// <param name="changed">Stores the user's choice.</param>
    internal PairChannelOption(DialChannel channel, Action<DialChannel, bool> changed)
    {
        Channel = channel;
        Label = DialChannels.ShortName(channel);
        ToolTip = DialChannels.DisplayName(channel);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    /// <summary>The channel.</summary>
    public DialChannel Channel { get; }

    /// <summary>Short channel name.</summary>
    public string Label { get; }

    /// <summary>Full channel name.</summary>
    public string ToolTip { get; }

    /// <summary>The pair captures this channel (two-way).</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (SetProperty(ref _isIncluded, value))
            {
                _changed(Channel, value);
            }
        }
    }

    /// <summary>Shows the stored state (no callback).</summary>
    internal void Sync(bool included) => SetProperty(ref _isIncluded, included, nameof(IsIncluded));
}
