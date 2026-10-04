using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;

namespace DivebombLogistics.SpeedDial.Persistence;

/// <summary>
/// Writes text files off the data thread with strict per-file ordering (the write machinery of the haptics car profile
/// store, independent of the content type). <see cref="WriteAsync"/> queues the newest content of a file and a
/// thread-pool worker writes it atomically (<see cref="JsonFile.WriteAllTextAtomic"/>): contents queued for one file
/// before the write happens coalesce into one write of the newest, writes of one file never overlap, and a newer content
/// always wins over an older one still queued, including across the synchronous <see cref="Write"/> used at shutdown.
/// Queued content stays visible through <see cref="GetPending"/> until it is on disk (also while it is written and
/// while a failed write waits for its retry), so a reader never falls back to an older file in between. A failed
/// asynchronous write is retried after a back-off; after the last retry its content is dropped (the next save of the
/// file writes it again).
/// <para>Thread-safe. Never throws: failures are logged.</para>
/// </summary>
internal sealed class OrderedFileWriter
{
    /// <summary>Delays (ms) between the attempts of a failed asynchronous write, as for the haptics car profiles.</summary>
    private static readonly int[] DefaultRetryDelaysMs = { 250, 1000, 4000, 15000 };

    private readonly string what;
    private readonly ILog log;
    private readonly int[] retryDelaysMs;

    /// <summary>Guards <see cref="slots"/>, every slot's pending fields and <see cref="activeWriters"/>.</summary>
    private readonly object stateLock = new object();

    private readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
    private long nextSequence;
    private int activeWriters;

    /// <param name="what">What the files contain, for log messages (e.g. "Speed Dial car data").</param>
    /// <param name="log">Receives write failures.</param>
    /// <param name="retryDelaysMs">Back-off of failed asynchronous writes; null = 250 ms, 1 s, 4 s, 15 s.</param>
    public OrderedFileWriter(string what, ILog log, int[] retryDelaysMs = null)
    {
        this.what = string.IsNullOrEmpty(what) ? "file" : what;
        this.log = log ?? NullLog.Instance;
        this.retryDelaysMs = retryDelaysMs ?? DefaultRetryDelaysMs;
    }

    /// <summary>Queues <paramref name="contents"/> for <paramref name="path"/> and writes it on the thread pool. Never throws.</summary>
    public void WriteAsync(string path, string contents)
    {
        if (string.IsNullOrEmpty(path) || contents == null)
        {
            return;
        }

        lock (stateLock)
        {
            Slot slot = GetSlot(path);
            slot.PendingContents = contents;
            slot.PendingSequence = ++nextSequence;
            if (slot.WriterQueued)
            {
                // The running/queued writer (or the pending retry) of this file picks the new content up.
                return;
            }

            slot.WriterQueued = true;
            activeWriters++;
            try
            {
                ThreadPool.QueueUserWorkItem(Drain, slot);
            }
            catch (Exception ex)
            {
                slot.WriterQueued = false;
                activeWriters--;
                log.Error("Could not queue saving " + what + " " + path + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Writes synchronously (SimHub shutdown), superseding any older content still queued for the file. Never throws.
    /// </summary>
    /// <returns>True when written (or a newer write already superseded this one).</returns>
    public bool Write(string path, string contents)
    {
        if (string.IsNullOrEmpty(path) || contents == null)
        {
            return false;
        }

        Slot slot;
        long sequence;
        lock (stateLock)
        {
            slot = GetSlot(path);
            sequence = ++nextSequence;

            // Anything queued so far is older than this content: drop it rather than let it overwrite us later.
            slot.PendingContents = null;
        }

        return WriteIfNewest(slot, sequence, contents);
    }

    /// <summary>The newest content queued for <paramref name="path"/> that is not written yet, or null.</summary>
    public string GetPending(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        lock (stateLock)
        {
            return slots.TryGetValue(path, out Slot slot) ? slot.PendingContents : null;
        }
    }

    /// <summary>
    /// The lock held while <paramref name="path"/> is written. Readers hold it too, so a read never meets a file that is
    /// being replaced (a rare path: car loads).
    /// </summary>
    public object GetFileLock(string path)
    {
        lock (stateLock)
        {
            return GetSlot(path ?? string.Empty).FileLock;
        }
    }

    /// <summary>Blocks until all queued writes have finished or the timeout elapsed.</summary>
    /// <returns>True if nothing is pending anymore.</returns>
    public bool WaitForPendingWrites(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        lock (stateLock)
        {
            while (activeWriters > 0)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(stateLock, remaining))
                {
                    return activeWriters == 0;
                }
            }

            return true;
        }
    }

    private Slot GetSlot(string path)
    {
        if (!slots.TryGetValue(path, out Slot slot))
        {
            slot = new Slot(path);
            slots[path] = slot;
        }

        return slot;
    }

    /// <summary>
    /// Thread-pool (or retry timer) worker: writes the newest pending content of one file until nothing is pending.
    /// The content stays pending until it is on disk, so <see cref="GetPending"/> never misses it while a write is in
    /// progress or waits for a retry. A failed write schedules a retry (see <see cref="ScheduleRetry"/>).
    /// </summary>
    private void Drain(object state)
    {
        var slot = (Slot)state;
        try
        {
            while (true)
            {
                string contents;
                long sequence;
                lock (stateLock)
                {
                    contents = slot.PendingContents;
                    sequence = slot.PendingSequence;
                    if (contents == null)
                    {
                        slot.WriterQueued = false;
                        slot.RetryCount = 0;
                        return;
                    }
                }

                if (WriteIfNewest(slot, sequence, contents))
                {
                    lock (stateLock)
                    {
                        // On disk now: no longer pending, unless newer content arrived meanwhile (written next).
                        if (slot.PendingSequence == sequence)
                        {
                            slot.PendingContents = null;
                        }

                        slot.RetryCount = 0;
                    }
                }
                else if (ScheduleRetry(slot, sequence))
                {
                    return; // the retry timer continues; WriterQueued stays set so new content coalesces
                }
            }
        }
        catch (Exception ex)
        {
            // WriteIfNewest already swallows IO errors; this is the last line of defense for the thread pool, where an
            // unhandled exception would terminate SimHub.
            lock (stateLock)
            {
                // Pending content always has a writer (or a retry) behind it; without one it would never be written.
                slot.WriterQueued = false;
                slot.PendingContents = null;
            }

            log.Error("Writer for " + what + " " + slot.Path + " failed: " + ex.Message);
        }
        finally
        {
            lock (stateLock)
            {
                activeWriters--;
                Monitor.PulseAll(stateLock);
            }
        }
    }

    /// <summary>
    /// After a failed write of <paramref name="sequence"/> (still pending): re-runs the writer after the next back-off
    /// delay. Returns false when the caller should keep draining instead: newer content was queued meanwhile (written
    /// right away), a synchronous <see cref="Write"/> superseded it, or the retries are used up (the content is
    /// dropped; the next save writes it again).
    /// </summary>
    private bool ScheduleRetry(Slot slot, long sequence)
    {
        lock (stateLock)
        {
            if (slot.PendingContents == null || slot.PendingSequence != sequence)
            {
                slot.RetryCount = 0;
                return false;
            }

            if (slot.RetryCount >= retryDelaysMs.Length)
            {
                slot.RetryCount = 0;
                slot.PendingContents = null;
                log.Error("Giving up saving " + what + " " + slot.Path + " after "
                    + (retryDelaysMs.Length + 1).ToString(CultureInfo.InvariantCulture) + " attempts; the next save writes it again.");
                return false;
            }

            int delay = retryDelaysMs[slot.RetryCount++];
            try
            {
                slot.RetryTimer?.Dispose();
                activeWriters++; // the timer's Drain run
                slot.RetryTimer = new Timer(Drain, slot, delay, Timeout.Infinite);
                return true;
            }
            catch (Exception ex)
            {
                activeWriters--;
                slot.PendingContents = null;
                slot.RetryCount = 0;
                log.Error("Could not schedule a retry for " + what + " " + slot.Path + ": " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Writes <paramref name="contents"/> unless a newer content was written already. Never throws.</summary>
    private bool WriteIfNewest(Slot slot, long sequence, string contents)
    {
        lock (slot.FileLock)
        {
            if (sequence <= slot.WrittenSequence)
            {
                return true;
            }

            try
            {
                JsonFile.WriteAllTextAtomic(slot.Path, contents);
                slot.WrittenSequence = sequence;
                return true;
            }
            catch (Exception ex)
            {
                log.Error("Could not save " + what + " " + slot.Path + ": " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Write bookkeeping of one file.</summary>
    private sealed class Slot
    {
        public Slot(string path)
        {
            Path = path;
        }

        public string Path { get; }

        /// <summary>Held while the file is written (and by readers); serializes writers of this path.</summary>
        public object FileLock { get; } = new object();

        /// <summary>
        /// Newest content not on disk yet: queued, being written or waiting for a retry (guarded by the writer's state
        /// lock). Cleared only after it was written, superseded or given up.
        /// </summary>
        public string PendingContents;

        public long PendingSequence;

        /// <summary>A thread-pool writer is queued or running for this path, or a retry is scheduled (state lock).</summary>
        public bool WriterQueued;

        /// <summary>Sequence of the content currently on disk (guarded by <see cref="FileLock"/>).</summary>
        public long WrittenSequence;

        /// <summary>Failed attempts of the pending content so far (state lock).</summary>
        public int RetryCount;

        /// <summary>Back-off timer of the next attempt; kept referenced so it is not collected (state lock).</summary>
        public Timer RetryTimer;
    }
}
