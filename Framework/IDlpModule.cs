using System;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Framework;

/// <summary>
/// One feature area of DLP (e.g. "Haptics", "SpeedDial"). The shell (<c>DLP.cs</c>) owns the SimHub lifecycle and
/// calls every module in a fixed order; a module that throws gets <see cref="OnFault"/> and the other modules keep
/// running.
/// <para>
/// Threading: <see cref="Init"/> and <see cref="End"/> run on SimHub's plugin thread; every other member runs on
/// SimHub's data thread (<c>DataUpdate</c>). UI-facing host members of a module hand data-thread work to
/// <see cref="ModuleContext.Dispatcher"/>.
/// </para>
/// <para>
/// Per-frame order: the module's queued dispatcher actions; then, while the game runs, <see cref="OnGameChanged"/>,
/// <see cref="OnCarChanged"/> and <see cref="OnSessionChanged"/> when they apply, then <see cref="Update"/>; on the
/// first frame without a running game <see cref="OnGameStopped"/> instead; finally <see cref="Tick"/> every frame.
/// </para>
/// </summary>
internal interface IDlpModule
{
    /// <summary>Stable id ("Haptics", "SpeedDial"): data folder <c>PluginsData\DLP\&lt;Id&gt;\</c> and log prefix.</summary>
    string Id { get; }

    /// <summary>Header of the module's tab on the settings page.</summary>
    string DisplayName { get; }

    /// <summary>Loads settings and registers properties and actions (allocations are fine here).</summary>
    void Init(ModuleContext context);

    /// <summary>The game name changed (including the first game after start-up).</summary>
    void OnGameChanged(FrameContext frame);

    /// <summary>
    /// The shared car identity changed. <see cref="CarIdentity.None"/> means no car (also sent on every game change,
    /// right after <see cref="OnGameChanged"/>). While <see cref="ICarIdentityState.IsProvisional"/> is true the key
    /// is a livery-specific placeholder that may still be replaced by the sim's native model name: do not persist
    /// per-car data under it.
    /// </summary>
    void OnCarChanged(CarIdentity car);

    /// <summary>SimHub's <c>SessionId</c> changed (not sent for the first session seen after a game change).</summary>
    void OnSessionChanged(FrameContext frame);

    /// <summary>Every frame while the game runs. Must be allocation-free in steady state.</summary>
    void Update(FrameContext frame);

    /// <summary>First frame without a running game after it ran: zero the outputs.</summary>
    void OnGameStopped();

    /// <summary>Every frame (game running or not): persistence scheduling, UI snapshot refresh. Allocation-free.</summary>
    /// <param name="now">Monotonic wall-clock seconds of this frame.</param>
    /// <param name="gameRunning">True when the game runs this frame (<see cref="Update"/> was due).</param>
    void Tick(double now, bool gameRunning);

    /// <summary>
    /// Called after any member above threw (the shell already reported the exception), or after a shell stage
    /// failed. Zero the outputs and reset filters so a persistent error can never freeze an effect at its last value.
    /// </summary>
    void OnFault(Exception error);

    /// <summary>SimHub shuts down: save everything synchronously and stop background work.</summary>
    void End();
}
