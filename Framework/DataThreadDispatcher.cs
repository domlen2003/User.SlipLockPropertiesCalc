using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DivebombLogistics.Framework;

/// <summary>
/// <see cref="IDataThreadDispatcher"/> backed by a <see cref="ConcurrentQueue{T}"/>. The shell creates one per module,
/// calls <see cref="BindToCurrentThread"/> at the start of every <c>DataUpdate</c> and then <see cref="Drain"/> (also
/// once in <c>End</c>, so the last UI edits are applied before the final save).
/// </summary>
internal sealed class DataThreadDispatcher : IDataThreadDispatcher
{
    /// <summary>Result of <see cref="Run"/> when the data thread does not respond in time (SimHub idle or paused).</summary>
    public const string TimeoutMessage = "SimHub is not processing data right now, try again.";

    /// <summary>Result of <see cref="Run"/> when the call started on the data thread but did not finish in time (rare).</summary>
    public const string StillRunningMessage = "SimHub is still processing the request; check the result in a moment.";

    private const int NoThread = -1;

    private readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
    private int dataThreadId = NoThread;

    /// <inheritdoc />
    public bool IsDataThread => Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref dataThreadId);

    /// <summary>Number of queued actions.</summary>
    public int PendingCount => queue.Count;

    /// <summary>Marks the calling thread as the data thread (start of every <c>DataUpdate</c>; allocation-free).</summary>
    public void BindToCurrentThread() => Volatile.Write(ref dataThreadId, Thread.CurrentThread.ManagedThreadId);

    /// <inheritdoc />
    public void Post(Action action)
    {
        if (action != null)
        {
            queue.Enqueue(action);
        }
    }

    /// <summary>
    /// Runs every queued action on the calling thread, each guarded: a throwing action is reported to
    /// <paramref name="errors"/> and the others still run. Allocation-free when nothing is queued.
    /// </summary>
    /// <returns>The number of actions run.</returns>
    public int Drain(ErrorReporter errors, double now)
    {
        int count = 0;
        while (queue.TryDequeue(out Action action))
        {
            count++;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors?.Report(ex, now);
            }
        }

        return count;
    }

    /// <inheritdoc />
    public string Run(Func<string> action, int timeoutMs)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (IsDataThread)
        {
            return action();
        }

        var call = new PendingCall(action);
        queue.Enqueue(call.Execute);
        if (!call.Done.Wait(timeoutMs))
        {
            if (call.TryAbandon())
            {
                // Never started: it is skipped when the data thread drains it, so nothing happens behind the back of
                // a caller who was told to try again. Not disposed: the drain may still touch the event.
                return TimeoutMessage;
            }

            // Already running on the data thread: it completes there; wait once more for its real result.
            if (!call.Done.Wait(timeoutMs))
            {
                return StillRunningMessage;
            }
        }

        call.Done.Dispose();
        return call.Result;
    }

    /// <summary>A <see cref="Run"/> request: executes on the data thread and signals the waiting caller.</summary>
    private sealed class PendingCall
    {
        private const int Queued = 0;
        private const int Started = 1;
        private const int Abandoned = 2;

        private readonly Func<string> action;
        private volatile string result;
        private int state = Queued;

        public PendingCall(Func<string> action)
        {
            this.action = action;
        }

        public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(false);

        public string Result => result;

        /// <summary>The caller gave up: true when the call had not started (it will be skipped), false when it already runs.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref state, Abandoned, Queued) == Queued;

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref state, Started, Queued) != Queued)
            {
                return; // abandoned by a caller that timed out
            }

            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                result = ex.Message;
            }
            finally
            {
                Done.Set();
            }
        }
    }
}
