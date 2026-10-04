using DivebombLogistics.Core;

namespace DivebombLogistics.Integration;

/// <summary>
/// <see cref="ILog"/> backed by SimHub's log4net logger (<c>SimHub.Logging.Current</c>), so messages from the
/// SimHub-independent code end up in SimHub's regular log files. Every line starts with "DLP" (plus the module id for
/// module loggers) to make the plugin's lines easy to find.
/// </summary>
internal sealed class SimHubLog : ILog
{
    /// <summary>Prefix of every log line of the plugin shell.</summary>
    public const string ShellPrefix = "DLP: ";

    private readonly string prefix;

    /// <summary>Creates the shell logger ("DLP: ...").</summary>
    public SimHubLog()
        : this(ShellPrefix)
    {
    }

    private SimHubLog(string prefix)
    {
        this.prefix = prefix;
    }

    /// <summary>Logger for one module: lines start with "DLP [&lt;id&gt;]: ".</summary>
    public static SimHubLog ForModule(string moduleId) => new SimHubLog("DLP [" + moduleId + "]: ");

    public void Info(string message) => SimHub.Logging.Current.Info(prefix + message);

    public void Warn(string message) => SimHub.Logging.Current.Warn(prefix + message);

    public void Error(string message) => SimHub.Logging.Current.Error(prefix + message);
}
