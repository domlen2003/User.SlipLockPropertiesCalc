using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.SlipLock;
using DivebombLogistics.Haptics.Telemetry;

namespace DivebombLogistics.Haptics.Diagnostics;

/// <summary>
/// Optional 1 Hz diagnostic log (<c>Logs\DLP_debug.log</c>; v1/v2 wrote <c>Logs\SlipLock_debug.log</c>), enabled by
/// <c>HapticsSettings.DebugFileLog</c>.
/// The rate check happens before any string is built, so a disabled or not-yet-due log costs nothing per frame.
/// Lines are queued and written on the thread pool so the data thread never blocks on file IO.
/// The file is truncated with a header on the first write of each SimHub session (like v1 at start-up).
/// </summary>
internal sealed class DebugFileLog
{
    /// <summary>Minimum time between two frame lines (v1: one line per second).</summary>
    public const double IntervalSeconds = 1.0;

    /// <summary>File name inside SimHub's <c>Logs</c> folder (or the temp folder as a fallback).</summary>
    public const string FileName = "DLP_debug.log";

    /// <summary>Prefixes probed for our own property (v1 SELF-PROBE block) to document SimHub's naming.</summary>
    private static readonly string[] SelfProbePrefixes =
    {
        string.Empty,
        DlpNames.PropertyPrefix,
        "DivebombLogistics.DLP.",
        PropertyPaths.GameDataPrefix,
    };

    private const string SelfProbeProperty = "SlipLock.MaxSway";

    private readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();
    private readonly ILog log;
    private string path;
    private int writerScheduled;
    private bool headerWritten;
    private bool fallbackUsed;
    private bool scanPending = true;
    private double nextLineTime = double.NegativeInfinity;
    private long framesSinceLine;

    /// <param name="path">Log file path (normally <c>&lt;SimHub&gt;\Logs\DLP_debug.log</c>).</param>
    /// <param name="log">Receives a single warning if the file cannot be written.</param>
    public DebugFileLog(string path, ILog log)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
        this.log = log ?? NullLog.Instance;
    }

    /// <summary>Current log file path (falls back to the temp folder if the SimHub folder is not writable).</summary>
    public string FilePath => Volatile.Read(ref path);

    /// <summary>Requests the SCAN/SELF-PROBE block for the next logged frame (called on game change).</summary>
    public void RequestScan() => scanPending = true;

    /// <summary>
    /// Per-frame entry point. Returns true when a frame line is due; the caller then calls <see cref="WriteFrame"/>.
    /// Allocation-free.
    /// </summary>
    public bool IsDue(bool enabled, double now)
    {
        framesSinceLine++;
        if (!enabled || now < nextLineTime)
        {
            return false;
        }

        nextLineTime = now + IntervalSeconds;
        return true;
    }

    /// <summary>Writes the v1 SCAN and SELF-PROBE blocks if a game change requested them.</summary>
    public void WriteScanIfPending(ITelemetryReader reader, string gameName, string presetName, string balanceSource)
    {
        if (!scanPending)
        {
            return;
        }

        scanPending = false;
        var sb = new StringBuilder();
        sb.Append(Stamp()).Append("=== SCAN === game=").Append(gameName)
            .Append(" preset=").Append(presetName)
            .Append(" balanceSource=").Append(balanceSource);
        foreach (string property in PropertyPaths.DiagnosticScanPaths)
        {
            sb.Append("\r\n  ").Append(property).Append('=').Append(reader.GetText(property) ?? "NULL");
        }

        sb.Append("\r\n=== SELF-PROBE ===");
        string resolved = null;
        foreach (string prefix in SelfProbePrefixes)
        {
            string value = reader.GetText(prefix + SelfProbeProperty);
            sb.Append("\r\n  [").Append(prefix).Append(SelfProbeProperty).Append("] = ").Append(value ?? "NULL");
            if (value != null && resolved == null)
            {
                resolved = prefix;
            }
        }

        sb.Append("\r\n  RESOLVED PREFIX: '").Append(resolved ?? string.Empty).Append('\'');
        Enqueue(sb.ToString());
    }

    /// <summary>Formats and queues one frame line (only call when <see cref="IsDue"/> returned true).</summary>
    public void WriteFrame(
        FrameContext ctx,
        SlipLockInputs inputs,
        SlipLockOutputs outputs,
        SlipSourceKind source,
        BalanceOutputs balance)
    {
        double[] raw = inputs.BaseSlip;
        double[] loads = outputs.Loads;
        string line = Stamp()
            + FormattableString.Invariant(
                $"RAW[{source}] [{raw[0]:F4},{raw[1]:F4},{raw[2]:F4},{raw[3]:F4}] signed={inputs.BaseSlipIsSigned} speed={ctx.SpeedKmh / 3.6:F1} thr={ctx.Throttle:F0} brake={ctx.Brake:F0} sway={ctx.Sway:F2} surge={ctx.Surge:F2}")
            + FormattableString.Invariant(
                $" | PREPROC slip=[{outputs.Slip[0]:F1},{outputs.Slip[1]:F1},{outputs.Slip[2]:F1},{outputs.Slip[3]:F1}] lock=[{outputs.Lock[0]:F1},{outputs.Lock[1]:F1},{outputs.Lock[2]:F1},{outputs.Lock[3]:F1}] loads=[{loads[0]:F1},{loads[1]:F1},{loads[2]:F1},{loads[3]:F1}] shakeItLock={inputs.HasShakeItLock}")
            + FormattableString.Invariant($" | OUT slipTC={outputs.SlipTcMono:F1} lockABS={outputs.LockAbsMono:F1}")
            + FormattableString.Invariant(
                $" | BAL US={balance.Understeer:F2} OS={balance.Oversteer:F2} gate={balance.Gate} path={balance.Path} conf={balance.Confidence:F2}");
        Enqueue(line);
    }

    private string Stamp()
    {
        string stamp = "[" + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] (f#"
            + framesSinceLine.ToString(CultureInfo.InvariantCulture) + ") ";
        framesSinceLine = 0;
        return stamp;
    }

    private void Enqueue(string text)
    {
        pending.Enqueue(text);
        ScheduleWriter();
    }

    private void ScheduleWriter()
    {
        if (Interlocked.CompareExchange(ref writerScheduled, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => Drain());
        }
    }

    /// <summary>Thread-pool body. Never throws (an unhandled exception would terminate SimHub).</summary>
    private void Drain()
    {
        try
        {
            var sb = new StringBuilder();
            while (pending.TryDequeue(out string text))
            {
                sb.Append(text).Append("\r\n");
            }

            if (sb.Length > 0)
            {
                WriteToFile(sb.ToString());
            }
        }
        catch (Exception ex)
        {
            log.Warn("Debug file log failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref writerScheduled, 0);
            if (!pending.IsEmpty)
            {
                ScheduleWriter();
            }
        }
    }

    private void WriteToFile(string text)
    {
        try
        {
            AppendOrCreate(text);
        }
        catch (Exception ex) when (IsIoFailure(ex) && !fallbackUsed)
        {
            // v1 behaviour: fall back to the temp folder when the SimHub folder is not writable.
            fallbackUsed = true;
            headerWritten = false;
            Volatile.Write(ref path, Path.Combine(Path.GetTempPath(), FileName));
            log.Warn("Debug file log: " + ex.Message + " Using " + FilePath);
            AppendOrCreate(text);
        }
    }

    private void AppendOrCreate(string text)
    {
        string target = FilePath;
        if (!headerWritten)
        {
            string directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string header = "=== DLP " +DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===\r\n";
            File.WriteAllText(target, header + text);
            headerWritten = true;
            return;
        }

        File.AppendAllText(target, text);
    }

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException;
}
