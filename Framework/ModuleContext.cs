using System;
using System.Collections.Generic;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Framework;

/// <summary>
/// Everything a module gets from the shell, built once per module before <see cref="IDlpModule.Init"/>. Modules
/// never see SimHub types: these services are implemented in <c>Integration/</c> for SimHub and by fakes in tests.
/// </summary>
internal sealed class ModuleContext
{
    /// <summary>The module's <see cref="IDlpModule.Id"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Logger whose lines carry the module's prefix.</summary>
    public ILog Log { get; set; } = NullLog.Instance;

    /// <summary>Reads SimHub properties by full path (data thread; also used on the UI thread for diagnostics).</summary>
    public ITelemetryReader Reader { get; set; }

    /// <summary>The shell's per-frame context (one reused instance, filled at the start of every frame; data thread only).</summary>
    public FrameContext Frame { get; set; }

    /// <summary>Shared car identity and its retry window.</summary>
    public ICarIdentityState Car { get; set; }

    /// <summary>Registers <c>DLP.*</c> properties (Init only).</summary>
    public IPropertyRegistry Properties { get; set; }

    /// <summary>Registers <c>DLP.*</c> actions (Init only); callbacks run on the data thread.</summary>
    public IActionRegistry Actions { get; set; }

    /// <summary>Control Mapper role presses (non-blocking).</summary>
    public IRoleOutput Roles { get; set; }

    /// <summary>The module's own queue to the data thread (UI edits, export/import, action callbacks).</summary>
    public IDataThreadDispatcher Dispatcher { get; set; }

    /// <summary>The module's error reporter: stage errors inside the module go here; its LastError feeds diagnostics.</summary>
    public ErrorReporter Errors { get; set; }

    /// <summary>Shell frame statistics (data thread only).</summary>
    public ShellDiagnostics Diagnostics { get; set; }

    /// <summary>The module's data folder, <c>&lt;SimHub&gt;\PluginsData\DLP\&lt;Id&gt;</c> (created on the first write).</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>SimHub's log folder (<c>&lt;SimHub&gt;\Logs</c>), for optional diagnostic files.</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>Monotonic wall-clock seconds (the same clock as <see cref="FrameContext.WallTime"/>). Any thread.</summary>
    public Func<double> Clock { get; set; } = () => 0.0;

    /// <summary>Names of every SimHub property (raw game data included), for diagnostic dumps. Allocates; not per frame.</summary>
    public Func<IList<string>> ListPropertyNames { get; set; } = () => new List<string>();
}
