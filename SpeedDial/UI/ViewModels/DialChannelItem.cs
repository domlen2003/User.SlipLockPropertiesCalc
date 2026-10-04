using DivebombLogistics.Core;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.UI;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// One channel row of the dial progress panel: start → target, current value, presses and result. Updated only when
/// the dial status version changed.
/// </summary>
public sealed class DialChannelItem : ObservableObject
{
    private const string Arrow = " → ";
    private const string DialingText = "dialing...";

    private bool _isTargeted;
    private bool _isCurrent;
    private string _targetText = string.Empty;
    private string _currentText = SpeedDialText.Missing;
    private string _pressesText = string.Empty;
    private string _resultText = string.Empty;

    /// <param name="channel">The channel shown.</param>
    public DialChannelItem(DialChannel channel)
    {
        Channel = channel;
        Name = DialChannels.DisplayName(channel);
    }

    /// <summary>The channel shown.</summary>
    public DialChannel Channel { get; }

    /// <summary>Channel display name.</summary>
    public string Name { get; }

    /// <summary>The job dials this channel (rows of untargeted channels are hidden).</summary>
    public bool IsTargeted
    {
        get => _isTargeted;
        private set => SetProperty(ref _isTargeted, value);
    }

    /// <summary>The dialer is working on this channel right now.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        private set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>"3 → 5" (start → target).</summary>
    public string TargetText
    {
        get => _targetText;
        private set => SetProperty(ref _targetText, value);
    }

    /// <summary>Last value the dialer read.</summary>
    public string CurrentText
    {
        get => _currentText;
        private set => SetProperty(ref _currentText, value);
    }

    /// <summary>Number of presses sent for this channel.</summary>
    public string PressesText
    {
        get => _pressesText;
        private set => SetProperty(ref _pressesText, value);
    }

    /// <summary>Result text ("reached", "no response", ...).</summary>
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    /// <summary>
    /// True when <paramref name="progress"/> belongs to a channel the job dials: it has a target, or a result other than
    /// "skipped" (a disabled binding is skipped later but still has its target).
    /// </summary>
    public static bool IsTargetedProgress(ChannelProgress progress) =>
        progress != null && (MathUtil.IsFinite(progress.Target) || progress.Result != ChannelResult.Skipped);

    /// <summary>Shows <paramref name="progress"/>; <paramref name="current"/> marks the channel being dialed.</summary>
    public void Update(ChannelProgress progress, bool current)
    {
        if (progress == null)
        {
            IsTargeted = false;
            return;
        }

        IsTargeted = IsTargetedProgress(progress);
        IsCurrent = current;
        TargetText = DialChannels.FormatValue(Channel, progress.Start) + Arrow + DialChannels.FormatValue(Channel, progress.Target);
        CurrentText = DialChannels.FormatValue(Channel, progress.Current);
        PressesText = progress.Presses.ToString(SpeedDialText.Culture);
        ResultText = current && progress.Result == ChannelResult.Pending ? DialingText : SpeedDialText.Result(progress.Result);
    }
}
