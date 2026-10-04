using System;
using System.Globalization;
using DivebombLogistics.Core;

namespace DivebombLogistics.Framework;

/// <summary>
/// Rate-limited error reporting for the data thread: the first error is logged immediately, further ones at most
/// every <see cref="LogIntervalSeconds"/> with the number of suppressed errors. <see cref="LastError"/> always holds
/// the newest error for the UI's diagnostics. The shell keeps one per module plus one for its own stages.
/// <para>
/// Threading: <see cref="Report"/> and <see cref="Record"/> are called on the data thread; <see cref="LastError"/>
/// may be read from any thread (reference reads are atomic). Never throws.
/// </para>
/// </summary>
internal sealed class ErrorReporter
{
    /// <summary>After the first error, further errors are logged at most this often.</summary>
    public const double LogIntervalSeconds = 10.0;

    private const string TimeFormat = "HH:mm:ss";

    private readonly ILog log;
    private readonly string context;
    private readonly Func<DateTime> localNow;
    private volatile string lastError = string.Empty;
    private bool logged;
    private double lastLogTime = double.NegativeInfinity;
    private int suppressed;

    /// <param name="log">Receives the rate-limited error lines.</param>
    /// <param name="context">Start of every logged line, e.g. "DataUpdate error".</param>
    /// <param name="localNow">Clock for the time stamp in <see cref="LastError"/>; defaults to <see cref="DateTime.Now"/>.</param>
    public ErrorReporter(ILog log, string context = "DataUpdate error", Func<DateTime> localNow = null)
    {
        this.log = log ?? NullLog.Instance;
        this.context = string.IsNullOrEmpty(context) ? "Error" : context;
        this.localNow = localNow ?? (() => DateTime.Now);
    }

    /// <summary>"HH:mm:ss Type: message" of the newest error; empty when none happened.</summary>
    public string LastError => lastError;

    /// <summary>Number of errors reported so far (reported and recorded).</summary>
    public int Count { get; private set; }

    /// <summary>Records <paramref name="error"/> as <see cref="LastError"/> and logs it (rate-limited).</summary>
    /// <param name="error">The exception.</param>
    /// <param name="now">Monotonic seconds, for the rate limit.</param>
    public void Report(Exception error, double now)
    {
        try
        {
            Record(error);
            if (logged && now - lastLogTime < LogIntervalSeconds)
            {
                suppressed++;
                return;
            }

            string suppressedText = suppressed > 0
                ? " (" + suppressed.ToString(CultureInfo.InvariantCulture) + " further errors suppressed)"
                : string.Empty;
            logged = true;
            lastLogTime = now;
            suppressed = 0;
            log.Error(context + suppressedText + ": " + error);
        }
        catch
        {
            // Error path: must never throw out of DataUpdate.
        }
    }

    /// <summary>Records <paramref name="error"/> as <see cref="LastError"/> without logging (already logged elsewhere).</summary>
    public void Record(Exception error)
    {
        try
        {
            Count++;
            lastError = localNow().ToString(TimeFormat, CultureInfo.InvariantCulture) + " "
                + (error == null ? "Unknown error" : error.GetType().Name + ": " + error.Message);
        }
        catch
        {
            // Error path: must never throw out of DataUpdate.
        }
    }
}
