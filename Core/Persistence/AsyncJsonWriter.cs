using System;
using System.IO;
using System.Threading;

namespace DivebombLogistics.Core.Persistence;

/// <summary>
/// Writes a JSON settings object off the data thread (no file IO on the hot path). <see cref="SaveAsync"/> takes a
/// deep copy on the calling thread (the data thread owns the live object) and writes the copy on the thread pool.
/// Writes never overlap and a newer copy always wins over an older one still queued, including across the
/// synchronous <see cref="Save"/> used at shutdown.
/// <para>
/// The production constructor writes the copy atomically to a JSON file (<see cref="JsonFile.WriteAllTextAtomic"/>
/// with the plugin's JSON conventions). The test constructor injects the write, so the ordering logic is testable
/// without a disk.
/// </para>
/// </summary>
/// <typeparam name="T">The settings DTO (public fields, JSON round-trippable).</typeparam>
internal sealed class AsyncJsonWriter<T>
    where T : class
{
    private readonly Action<T> _write;
    private readonly Func<T, T> _clone;
    private readonly ILog _log;

    /// <summary>What is written, for log messages (the file name, or "settings" for an injected writer).</summary>
    private readonly string _what;

    /// <summary>Guards the pending fields and <see cref="_activeWriters"/>.</summary>
    private readonly object _stateLock = new object();

    /// <summary>Held while writing; serializes writers.</summary>
    private readonly object _fileLock = new object();

    private T _pending;
    private long _pendingSequence;
    private long _nextSequence;
    private bool _writerQueued;
    private int _activeWriters;

    /// <summary>Sequence of the copy written last (guarded by <see cref="_fileLock"/>).</summary>
    private long _writtenSequence;

    /// <summary>Creates a writer that stores the object as an indented JSON file at <paramref name="path"/>.</summary>
    /// <param name="path">Target file; its directory is created on the first write.</param>
    /// <param name="log">Receives write failures.</param>
    public AsyncJsonWriter(string path, ILog log)
        : this(CreateFileWriter(path), log, null, Path.GetFileName(path))
    {
        FilePath = path;
    }

    /// <summary>Test constructor: <paramref name="write"/> receives each copy (on the thread pool or the caller of <see cref="Save"/>).</summary>
    /// <param name="write">Writes a copy (may throw; failures are logged).</param>
    /// <param name="log">Receives write failures.</param>
    /// <param name="clone">Deep copy of the live object; defaults to a JSON round trip.</param>
    internal AsyncJsonWriter(Action<T> write, ILog log, Func<T, T> clone = null)
        : this(write, log, clone, "settings")
    {
    }

    private AsyncJsonWriter(Action<T> write, ILog log, Func<T, T> clone, string what)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _log = log ?? NullLog.Instance;
        _clone = clone ?? DeepCopy;
        _what = string.IsNullOrEmpty(what) ? "settings" : what;
    }

    /// <summary>Target file of the production constructor; null for an injected writer.</summary>
    public string FilePath { get; }

    /// <summary>Copies <paramref name="live"/> now and writes the copy on the thread pool. Never throws.</summary>
    public void SaveAsync(T live)
    {
        T copy = TryCopy(live);
        if (copy == null)
        {
            return;
        }

        lock (_stateLock)
        {
            _pending = copy;
            _pendingSequence = ++_nextSequence;
            if (_writerQueued)
            {
                return; // the queued writer picks the newer copy up
            }

            _writerQueued = true;
            _activeWriters++;
            try
            {
                ThreadPool.QueueUserWorkItem(Drain);
            }
            catch (Exception ex)
            {
                _writerQueued = false;
                _activeWriters--;
                _log.Error("Could not queue saving " + _what + ": " + ex.Message);
            }
        }
    }

    /// <summary>Writes <paramref name="live"/> synchronously (shutdown), superseding queued copies. Never throws.</summary>
    /// <returns>True when written.</returns>
    public bool Save(T live)
    {
        T copy = TryCopy(live);
        if (copy == null)
        {
            return false;
        }

        long sequence;
        lock (_stateLock)
        {
            sequence = ++_nextSequence;
            _pending = null;
        }

        return WriteIfNewest(copy, sequence);
    }

    /// <summary>Blocks until queued writes finished or the timeout elapsed; true when nothing is pending.</summary>
    public bool WaitForPendingWrites(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        lock (_stateLock)
        {
            while (_activeWriters > 0)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_stateLock, remaining))
                {
                    return _activeWriters == 0;
                }
            }

            return true;
        }
    }

    private static Action<T> CreateFileWriter(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("A file path is required.", nameof(path));
        }

        return copy => JsonFile.WriteAllTextAtomic(path, JsonFile.Serialize(copy));
    }

    private static T DeepCopy(T live) => JsonFile.Deserialize<T>(JsonFile.Serialize(live), out _);

    private T TryCopy(T live)
    {
        if (live == null)
        {
            return null;
        }

        try
        {
            return _clone(live);
        }
        catch (Exception ex)
        {
            _log.Error("Could not copy " + _what + " for saving: " + ex.Message);
            return null;
        }
    }

    private void Drain(object state)
    {
        try
        {
            while (true)
            {
                T copy;
                long sequence;
                lock (_stateLock)
                {
                    copy = _pending;
                    sequence = _pendingSequence;
                    _pending = null;
                    if (copy == null)
                    {
                        _writerQueued = false;
                        return;
                    }
                }

                WriteIfNewest(copy, sequence);
            }
        }
        catch (Exception ex)
        {
            // Last line of defense: an unhandled exception on the thread pool would terminate SimHub.
            lock (_stateLock)
            {
                _writerQueued = false;
            }

            _log.Error("Writer for " + _what + " failed: " + ex.Message);
        }
        finally
        {
            lock (_stateLock)
            {
                _activeWriters--;
                Monitor.PulseAll(_stateLock);
            }
        }
    }

    private bool WriteIfNewest(T copy, long sequence)
    {
        lock (_fileLock)
        {
            if (sequence <= _writtenSequence)
            {
                return true;
            }

            try
            {
                _write(copy);
                _writtenSequence = sequence;
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Saving " + _what + " failed: " + ex.Message);
                return false;
            }
        }
    }
}
