using System;

namespace DivebombLogistics.Framework;

/// <summary>
/// Hands work from other threads (UI, SimHub action callbacks) to SimHub's data thread, which owns every processing
/// object. SimHub runs as a 32-bit process, where an 8-byte double written on one thread can be read torn on
/// another, so the UI never writes data-thread state directly.
/// </summary>
internal interface IDataThreadDispatcher
{
    /// <summary>True on the data thread (the thread of the latest <c>DataUpdate</c>).</summary>
    bool IsDataThread { get; }

    /// <summary>
    /// Queues <paramref name="action"/>; it runs at the start of the next <c>DataUpdate</c>, guarded: an exception is
    /// reported as an error of the module. Any thread. Posting a cached delegate does not allocate.
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// Runs <paramref name="action"/> on the data thread and waits for it (runs it directly when already there).
    /// For UI requests that need data-thread state (export, import). A call the data thread has not started when
    /// <paramref name="timeoutMs"/> passes is abandoned and never runs, so a request reported as timed out is never
    /// applied later; a call that already started is waited for once more.
    /// </summary>
    /// <returns>The action's result; the exception message if it threw; <see cref="DataThreadDispatcher.TimeoutMessage"/>
    /// when the data thread did not start it within <paramref name="timeoutMs"/> (it will not run);
    /// <see cref="DataThreadDispatcher.StillRunningMessage"/> when it started but did not finish within another
    /// <paramref name="timeoutMs"/>.</returns>
    string Run(Func<string> action, int timeoutMs);
}
