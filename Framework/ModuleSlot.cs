using System;

namespace DivebombLogistics.Framework;

/// <summary>A module plus the per-module services the shell keeps for it (dispatcher, error reporter, state).</summary>
internal sealed class ModuleSlot
{
    /// <param name="module">The module.</param>
    /// <param name="dispatcher">The module's queue to the data thread.</param>
    /// <param name="errors">The module's error reporter.</param>
    public ModuleSlot(IDlpModule module, DataThreadDispatcher dispatcher, ErrorReporter errors)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Errors = errors ?? throw new ArgumentNullException(nameof(errors));
    }

    /// <summary>The module.</summary>
    public IDlpModule Module { get; }

    /// <summary>The module's queue to the data thread.</summary>
    public DataThreadDispatcher Dispatcher { get; }

    /// <summary>The module's error reporter (its LastError feeds the module's diagnostics).</summary>
    public ErrorReporter Errors { get; }

    /// <summary>Init succeeded; only active modules are called.</summary>
    public bool Active { get; internal set; }

    /// <summary>Why Init failed (shown on the module's tab); null when it did not.</summary>
    public string InitError { get; internal set; }

    /// <summary>Number of faults (exceptions out of module calls) so far.</summary>
    public int FaultCount { get; internal set; }
}
