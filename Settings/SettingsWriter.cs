using System;
using System.Threading;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Settings;

/// <summary>
/// Writes the global <see cref="PluginSettings"/> off the data thread (DESIGN 2: no file IO on the hot path).
/// <see cref="SaveAsync"/> takes a deep copy on the calling thread (the data thread owns the live object) and hands
/// it to the injected write callback on the thread pool. Writes never overlap and a newer copy always wins over an
/// older one still queued, including across the synchronous <see cref="Save"/> used at shutdown.
/// Pure: the actual file write (SimHub's <c>SaveCommonSettings</c>) is injected, so this class is testable.
/// </summary>
internal sealed class SettingsWriter
{
    private readonly Action<PluginSettings> _write;
    private readonly Func<PluginSettings, PluginSettings> _clone;
    private readonly ILog _log;

    /// <summary>Guards the pending fields and <see cref="_activeWriters"/>.</summary>
    private readonly object _stateLock = new object();

    /// <summary>Held while writing; serializes writers.</summary>
    private readonly object _fileLock = new object();

    private PluginSettings _pending;
    private long _pendingSequence;
    private long _nextSequence;
    private bool _writerQueued;
    private int _activeWriters;

    /// <summary>Sequence of the copy written last (guarded by <see cref="_fileLock"/>).</summary>
    private long _writtenSequence;

    /// <param name="write">Writes a settings copy to disk (may throw; failures are logged).</param>
    /// <param name="log">Receives write failures.</param>
    /// <param name="clone">Deep copy of the live settings; defaults to a JSON round trip.</param>
    public SettingsWriter(Action<PluginSettings> write, ILog log, Func<PluginSettings, PluginSettings> clone = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _log = log ?? NullLog.Instance;
        _clone = clone ?? DeepCopy;
    }

    /// <summary>Copies <paramref name="live"/> now and writes the copy on the thread pool. Never throws.</summary>
    public void SaveAsync(PluginSettings live)
    {
        PluginSettings copy = TryCopy(live);
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
                _log.Error("SlipLock: could not queue the settings save: " + ex.Message);
            }
        }
    }

    /// <summary>Writes <paramref name="live"/> synchronously (shutdown), superseding queued copies. Never throws.</summary>
    /// <returns>True when written.</returns>
    public bool Save(PluginSettings live)
    {
        PluginSettings copy = TryCopy(live);
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

    private static PluginSettings DeepCopy(PluginSettings live) =>
        JsonFile.Deserialize<PluginSettings>(JsonFile.Serialize(live), out _);

    private PluginSettings TryCopy(PluginSettings live)
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
            _log.Error("SlipLock: could not copy the settings for saving: " + ex.Message);
            return null;
        }
    }

    private void Drain(object state)
    {
        try
        {
            while (true)
            {
                PluginSettings copy;
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

            _log.Error("SlipLock: settings writer failed: " + ex.Message);
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

    private bool WriteIfNewest(PluginSettings copy, long sequence)
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
                _log.Error("SlipLock: saving settings failed: " + ex.Message);
                return false;
            }
        }
    }
}
