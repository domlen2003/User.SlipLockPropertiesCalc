using DivebombLogistics.SpeedDial.Model;

namespace DivebombLogistics.SpeedDial.Engine;

/// <summary>
/// Every text the <see cref="Dialer"/> puts into <see cref="DialStatus.Message"/> or its log, built once (type
/// initialization) so the per-frame code only assigns references. Per-channel texts use
/// <see cref="DialChannels.DisplayName"/>; lookups with an invalid channel or result return a generic text instead of
/// throwing. The module and the UI may reuse <see cref="ResultText"/> and the constants.
/// </summary>
internal static class DialerMessages
{
    /// <summary>A job was started and has not reached its first channel yet.</summary>
    public const string Starting = "Starting";

    /// <summary>The request had no target at all (finished at once as Completed).</summary>
    public const string NothingToDial = "Nothing to dial";

    /// <summary>The gate is closed (game paused, menu, replay); the job waits.</summary>
    public const string Paused = "Paused: waiting for the game";

    /// <summary>Completed: every targeted channel reached (or came as close as possible to) its target.</summary>
    public const string Done = "Done";

    /// <summary>Default reason of <see cref="Dialer.Cancel"/>.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>The gate stayed closed longer than <c>GatePauseTimeoutMs</c>.</summary>
    public const string GateCancelled = "Cancelled: game paused too long";

    private const string DialingPrefix = "Dialing ";
    private const string PartialPrefix = "Partly done: ";
    private const string FailedPrefix = "Failed: ";
    private const string Separator = " ";
    private const string UnknownText = "unknown";

    /// <summary>Number of <see cref="ChannelResult"/> values (index range of the per-result tables).</summary>
    private const int ResultCount = (int)ChannelResult.Skipped + 1;

    private static readonly string[] DialingTexts = BuildPerChannel(DialingPrefix, string.Empty);
    private static readonly string[] PartialTexts = BuildPerResult(PartialPrefix);
    private static readonly string[] FailedTexts = BuildPerResult(FailedPrefix);
    private static readonly string[] RaisesLogs = BuildPerChannel(string.Empty, ": the Increase role raises the value (direction learned).");
    private static readonly string[] LowersLogs = BuildPerChannel(string.Empty, ": the Increase role lowers the value (reversed direction learned).");
    private static readonly string[] DirectionErrorLogs = BuildPerChannel(
        string.Empty,
        ": the value moved the wrong way again after the direction was flipped; check the Increase/Decrease roles.");

    private static readonly string[] WrapLogs = BuildPerChannel(
        string.Empty,
        ": the value wraps around at its limit; dialing back to the last value before the wrap.");

    /// <summary>Lower-case outcome text of a channel ("reached", "no response", ...), for status texts and the UI.</summary>
    public static string ResultText(ChannelResult result) => result switch
    {
        ChannelResult.Pending => "pending",
        ChannelResult.Reached => "reached",
        ChannelResult.ClosestPossible => "closest possible",
        ChannelResult.NoTelemetry => "no telemetry",
        ChannelResult.NoBinding => "no binding",
        ChannelResult.NoResponse => "no response",
        ChannelResult.LimitReached => "at its limit",
        ChannelResult.DirectionError => "moved the wrong way",
        ChannelResult.MaxPresses => "too many presses",
        ChannelResult.Cancelled => "cancelled",
        ChannelResult.Skipped => "skipped",
        _ => UnknownText,
    };

    /// <summary>"Dialing TC2 (Cut)" while <paramref name="channel"/> is being dialed.</summary>
    public static string Dialing(DialChannel channel) => PerChannel(DialingTexts, channel, Starting);

    /// <summary>"Partly done: ABS no response": the job ended Partial, <paramref name="channel"/> is its first failure.</summary>
    public static string Partial(DialChannel channel, ChannelResult result) => PerResult(PartialTexts, channel, result);

    /// <summary>"Failed: TC no binding": the job ended Failed, <paramref name="channel"/> is its first failure.</summary>
    public static string Failed(DialChannel channel, ChannelResult result) => PerResult(FailedTexts, channel, result);

    /// <summary>Log line after the direction of <paramref name="channel"/> was learned or flipped to <paramref name="direction"/>.</summary>
    public static string DirectionLog(DialChannel channel, int direction) =>
        PerChannel(direction == ChannelLearning.IncreaseLowers ? LowersLogs : RaisesLogs, channel, UnknownText);

    /// <summary>Log line of a <see cref="ChannelResult.DirectionError"/>.</summary>
    public static string DirectionErrorLog(DialChannel channel) => PerChannel(DirectionErrorLogs, channel, UnknownText);

    /// <summary>Log line when <paramref name="channel"/> was found to wrap around at a limit.</summary>
    public static string WrapLog(DialChannel channel) => PerChannel(WrapLogs, channel, UnknownText);

    private static string PerChannel(string[] texts, DialChannel channel, string fallback) =>
        DialChannels.IsValid(channel) ? texts[(int)channel] : fallback;

    private static string PerResult(string[] texts, DialChannel channel, ChannelResult result)
    {
        int resultIndex = (int)result;
        if (!DialChannels.IsValid(channel) || resultIndex < 0 || resultIndex >= ResultCount)
        {
            return UnknownText;
        }

        return texts[((int)channel * ResultCount) + resultIndex];
    }

    private static string[] BuildPerChannel(string prefix, string suffix)
    {
        var texts = new string[DialChannels.Count];
        for (int i = 0; i < texts.Length; i++)
        {
            texts[i] = prefix + DialChannels.DisplayName((DialChannel)i) + suffix;
        }

        return texts;
    }

    private static string[] BuildPerResult(string prefix)
    {
        var texts = new string[DialChannels.Count * ResultCount];
        for (int channel = 0; channel < DialChannels.Count; channel++)
        {
            for (int result = 0; result < ResultCount; result++)
            {
                texts[(channel * ResultCount) + result] =
                    prefix + DialChannels.DisplayName((DialChannel)channel) + Separator + ResultText((ChannelResult)result);
            }
        }

        return texts;
    }
}
