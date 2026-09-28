namespace User.SlipLockPropertiesCalc.Core;

/// <summary>
/// Minimal logging abstraction so SimHub-independent modules (and tests) can report noteworthy events.
/// The plugin shell provides an implementation backed by <c>SimHub.Logging.Current</c>.
/// Never call from the per-frame hot path except for rare, state-changing events.
/// </summary>
internal interface ILog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);
}

/// <summary>No-op logger (default for tests and optional dependencies).</summary>
internal sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new NullLog();

    private NullLog()
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }
}
