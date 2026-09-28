using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Integration;

/// <summary>
/// <see cref="ILog"/> backed by SimHub's log4net logger (<c>SimHub.Logging.Current</c>), so messages from the
/// SimHub-independent modules end up in SimHub's regular log files. Every message gets a common prefix to make
/// the plugin's lines easy to find.
/// </summary>
internal sealed class SimHubLog : ILog
{
    /// <summary>Prefix of every log line written by this plugin.</summary>
    public const string Prefix = "SlipLock: ";

    public void Info(string message) => SimHub.Logging.Current.Info(Prefix + message);

    public void Warn(string message) => SimHub.Logging.Current.Warn(Prefix + message);

    public void Error(string message) => SimHub.Logging.Current.Error(Prefix + message);
}
