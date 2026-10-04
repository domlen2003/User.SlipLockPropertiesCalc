namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// Global Control Mapper binding of one dial channel (part of <see cref="SpeedDialSettings.Channels"/>, keyed by
/// <see cref="DialChannels.Id"/>). The roles are Control Mapper output role names, e.g. <c>TractionControl+</c>.
/// Which role really raises the value is learned per car (<see cref="ChannelLearning.Direction"/>).
/// Plain JSON DTO (public fields).
/// </summary>
public sealed class ChannelBinding
{
    /// <summary>False: the dialer skips this channel (result <c>Skipped</c>) even when a preset contains it.</summary>
    public bool Enabled = true;

    /// <summary>Role pressed to increase the value (empty = not bound).</summary>
    public string IncreaseRole = string.Empty;

    /// <summary>Role pressed to decrease the value (empty = not bound).</summary>
    public string DecreaseRole = string.Empty;

    /// <summary>The default binding of <paramref name="channel"/> (enabled, the user's default roles).</summary>
    public static ChannelBinding CreateDefault(DialChannel channel) => new ChannelBinding
    {
        Enabled = true,
        IncreaseRole = DialChannels.DefaultIncreaseRole(channel),
        DecreaseRole = DialChannels.DefaultDecreaseRole(channel),
    };

    /// <summary>The role for one direction (never null after <see cref="Normalize"/>). Allocation-free.</summary>
    public string GetRole(bool increase) => (increase ? IncreaseRole : DecreaseRole) ?? string.Empty;

    /// <summary>True when the role for <paramref name="increase"/> is set.</summary>
    public bool HasRole(bool increase) => !string.IsNullOrEmpty(GetRole(increase));

    /// <summary>Repairs a deserialized or hand-edited instance: null roles become empty, roles are trimmed. An empty role stays empty (the user cleared it).</summary>
    public void Normalize()
    {
        IncreaseRole = (IncreaseRole ?? string.Empty).Trim();
        DecreaseRole = (DecreaseRole ?? string.Empty).Trim();
    }

    /// <summary>Independent copy.</summary>
    public ChannelBinding DeepCopy() => new ChannelBinding
    {
        Enabled = Enabled,
        IncreaseRole = IncreaseRole,
        DecreaseRole = DecreaseRole,
    };
}
