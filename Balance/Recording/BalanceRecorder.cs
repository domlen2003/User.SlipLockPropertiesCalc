using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Balance.Recording;

/// <summary>
/// Debug recorder: captures every balance tick into a CSV file (<see cref="BalanceCsv"/>) for offline replay.
/// </summary>
/// <remarks>
/// <see cref="Record"/> runs on the data thread and only copies a struct into a preallocated ring.
/// All file IO (directory creation, opening, writing, flushing, closing) happens on a dedicated background
/// thread that drains the ring every flush interval, so a slow disk can never stall DataUpdate; if the writer
/// falls behind, the oldest samples are dropped and counted in <see cref="DroppedCount"/>.
/// Start/Stop may be called from any thread.
/// </remarks>
internal sealed class BalanceRecorder : IDisposable
{
    /// <summary>≈ 68 s at 120 Hz: plenty of headroom for a writer that flushes every second.</summary>
    public const int DefaultCapacity = 8192;

    public const int DefaultFlushIntervalMs = 1000;

    public const int DefaultStopTimeoutMs = 2000;

    /// <summary>Upper bound for each sanitized name part so paths stay well below MAX_PATH.</summary>
    private const int MaxNamePartLength = 60;

    private const string FileExtension = ".csv";
    private const string TimestampFormat = "yyyyMMdd_HHmmss";
    private const string UnknownNamePart = "unknown";

    /// <summary>Attempts to find a free file name when several recordings start within the same second.</summary>
    private const int MaxUniqueNameAttempts = 100;

    private readonly ILog log;
    private readonly int capacity;
    private readonly int flushIntervalMs;
    private readonly object control = new object();

    /// <summary>Current (or last) recording; read lock-free by <see cref="Record"/>.</summary>
    private volatile Session session;

    /// <summary>Buffers of a finished session, reused by the next one to avoid reallocating the ring.</summary>
    private Session reusable;

    /// <param name="log">Receives start/stop/error notices. Optional.</param>
    /// <param name="capacity">Ring size in ticks.</param>
    /// <param name="flushIntervalMs">How often the writer thread drains the ring and flushes the file.</param>
    public BalanceRecorder(ILog log = null, int capacity = DefaultCapacity, int flushIntervalMs = DefaultFlushIntervalMs)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        }

        this.log = log ?? NullLog.Instance;
        this.capacity = capacity;
        this.flushIntervalMs = Math.Max(1, flushIntervalMs);
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/> (false again if the file could not be written).</summary>
    public bool IsRecording => session?.Recording == true;

    /// <summary>Path of the current (or last) recording file; empty before the first start.</summary>
    public string FilePath => session?.FilePath ?? string.Empty;

    /// <summary>Rows written to the current (or last) file.</summary>
    public long RecordedCount
    {
        get
        {
            Session current = session;
            return current == null ? 0 : Interlocked.Read(ref current.RecordedCount);
        }
    }

    /// <summary>Ticks dropped because the writer fell behind (current or last recording).</summary>
    public long DroppedCount => session?.Ring.Dropped ?? 0;

    /// <summary>IO error of the current (or last) recording, or null.</summary>
    public string LastError => session?.LastError;

    /// <summary>
    /// Builds the file name <c>&lt;sim&gt;_&lt;car&gt;_&lt;yyyyMMdd_HHmmss&gt;.csv</c>: invalid file-name characters and
    /// whitespace become '_', each part is trimmed and length-limited, empty parts become "unknown".
    /// </summary>
    public static string BuildFileName(string simKey, string carKey, DateTime localTime) =>
        SanitizeNamePart(simKey) + "_" + SanitizeNamePart(carKey) + "_"
        + localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>
    /// Starts a new recording (stopping a running one first). Returns the planned file path; the file itself is
    /// created by the writer thread (a numeric suffix is appended if the name is taken, see <see cref="FilePath"/>).
    /// </summary>
    public string Start(string directory, string simKey, string carKey)
    {
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("A recording directory is required.", nameof(directory));
        }

        lock (control)
        {
            StopCore(DefaultStopTimeoutMs);

            string path = Path.Combine(directory, BuildFileName(simKey, carKey, DateTime.Now));
            Session next = reusable ?? new Session(capacity);
            reusable = null;
            next.Begin(path);

            Thread thread = new Thread(() => WriterLoop(next, directory))
            {
                IsBackground = true,
                Name = "SlipLock balance recorder",
                Priority = ThreadPriority.BelowNormal,
            };
            next.Writer = thread;
            session = next;
            thread.Start();
            log.Info("Balance recording started: " + path);
            return path;
        }
    }

    /// <summary>Queues one tick for writing. Allocation-free; a no-op while not recording.</summary>
    public void Record(VehicleState state, BalanceOutputs outputs)
    {
        Session current = session;
        if (current != null && current.Recording)
        {
            current.Ring.Add(state, outputs);
        }
    }

    /// <summary>
    /// Stops recording, writes everything still queued and closes the file. Waits at most
    /// <paramref name="timeoutMs"/>; returns false if the writer did not finish in time (it then completes in the background).
    /// </summary>
    public bool Stop(int timeoutMs = DefaultStopTimeoutMs)
    {
        lock (control)
        {
            return StopCore(timeoutMs);
        }
    }

    /// <summary>Stops a running recording and releases the wait handle of the finished session.</summary>
    public void Dispose()
    {
        lock (control)
        {
            StopCore(DefaultStopTimeoutMs);
            reusable?.StopSignal.Dispose();
            reusable = null;
        }
    }

    private static string SanitizeNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UnknownNamePart;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
        {
            builder.Append(char.IsWhiteSpace(c) || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        // Leading/trailing dots or underscores make awkward (or on Windows, invalid) names.
        string result = builder.ToString().Trim('_', '.');
        if (result.Length > MaxNamePartLength)
        {
            result = result.Substring(0, MaxNamePartLength);
        }

        return result.Length == 0 ? UnknownNamePart : result;
    }

    private bool StopCore(int timeoutMs)
    {
        Session current = session;
        if (current?.Writer == null)
        {
            return true;
        }

        current.Recording = false;
        current.StopSignal.Set();
        bool finished = current.Writer.Join(Math.Max(0, timeoutMs));
        current.Writer = null;
        if (finished)
        {
            log.Info("Balance recording stopped: " + current.FilePath + " ("
                + Interlocked.Read(ref current.RecordedCount).ToString(CultureInfo.InvariantCulture) + " rows, "
                + current.Ring.Dropped.ToString(CultureInfo.InvariantCulture) + " dropped)");
            reusable = current;
        }
        else
        {
            // The abandoned writer keeps its own session; the next recording gets fresh buffers.
            log.Warn("Balance recording: writer did not finish within the stop timeout; it completes in the background.");
        }

        return finished;
    }

    /// <summary>Writer thread body: open the file, then drain and flush until stopped. Never throws.</summary>
    private void WriterLoop(Session target, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using (StreamWriter writer = OpenUnique(target))
            {
                BalanceCsv.WriteHeader(writer);
                bool stopping;
                do
                {
                    stopping = target.StopSignal.WaitOne(flushIntervalMs);
                    WritePending(target, writer);
                    writer.Flush();
                }
                while (!stopping);
            }
        }
        catch (Exception ex)
        {
            // Catch-all on purpose: an unhandled exception on a background thread would terminate SimHub.
            target.Recording = false;
            target.LastError = ex.Message;
            log.Error("Balance recording failed (" + target.FilePath + "): " + ex.Message);
        }
    }

    private static void WritePending(Session target, StreamWriter writer)
    {
        int n;
        while ((n = target.Ring.Drain(target.Batch)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                BalanceCsv.WriteRow(writer, ref target.Batch[i]);
            }

            Interlocked.Add(ref target.RecordedCount, n);
        }
    }

    /// <summary>Creates the file, appending _2, _3, ... when the name already exists.</summary>
    private static StreamWriter OpenUnique(Session target)
    {
        string plannedPath = target.FilePath;
        string directory = Path.GetDirectoryName(plannedPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(plannedPath);
        string path = plannedPath;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                target.FilePath = path;
                return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (IOException) when (File.Exists(path) && attempt < MaxUniqueNameAttempts)
            {
                path = Path.Combine(directory, stem + "_" + (attempt + 1).ToString(CultureInfo.InvariantCulture) + FileExtension);
            }
        }
    }

    /// <summary>One recording: its buffers, writer thread and results. Shared between the data and writer threads.</summary>
    private sealed class Session
    {
        public readonly BalanceRecordRing Ring;
        public readonly BalanceRecord[] Batch;
        public readonly ManualResetEvent StopSignal = new ManualResetEvent(false);
        public volatile bool Recording;
        public volatile string FilePath;
        public volatile string LastError;
        public long RecordedCount;
        public Thread Writer;

        public Session(int capacity)
        {
            Ring = new BalanceRecordRing(capacity);
            Batch = new BalanceRecord[capacity];
        }

        /// <summary>Prepares the session for a new file (called before the writer thread starts).</summary>
        public void Begin(string path)
        {
            Ring.Clear();
            StopSignal.Reset();
            Interlocked.Exchange(ref RecordedCount, 0);
            LastError = null;
            FilePath = path;
            Recording = true;
        }
    }
}
