using System;
using System.Collections.Generic;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Framework;

/// <summary>
/// The SimHub-independent part of the plugin shell: owns the module list and calls the <see cref="IDlpModule"/>
/// members in the documented order, with game, car (<see cref="CarIdentityTracker"/>) and session change detection
/// and per-module fault isolation. <c>DLP.cs</c> only adds what needs SimHub (filling the <see cref="FrameContext"/>,
/// SimHub services, the settings page), so this orchestration is unit-tested with fake modules.
/// <para>
/// Per frame: <see cref="BeginFrame"/> (drain the module dispatchers), fill the frame, <see cref="PrepareFrame"/> (or
/// <see cref="FrameFailed"/> when filling threw), <see cref="EndFrame"/>. Allocation-free in steady state; the
/// change notifications allocate a closure, which only happens on game/car/session changes.
/// </para>
/// <para>
/// Fault isolation: a module that throws has the exception reported to its <see cref="ErrorReporter"/> and gets
/// <see cref="IDlpModule.OnFault"/>; the other modules are unaffected. A failure of the shell stage (filling the frame,
/// identity resolution) is reported to the shell reporter and faults every module, which then skip
/// <see cref="IDlpModule.Update"/> for that frame. Data thread only, except <see cref="Add"/>/<see cref="Initialize"/>
/// (plugin Init) and <see cref="End"/>.
/// </para>
/// </summary>
internal sealed class ModuleHost
{
    private readonly FrameContext frame;
    private readonly ITelemetryReader reader;
    private readonly ILog log;
    private readonly ErrorReporter shellErrors;
    private readonly CarIdentityTracker car = new CarIdentityTracker();
    private readonly ShellDiagnostics diagnostics = new ShellDiagnostics();
    private readonly List<ModuleSlot> slotList = new List<ModuleSlot>();
    private ModuleSlot[] slots = new ModuleSlot[0];

    private bool wasRunning;
    private bool gameKnown;
    private string lastGame = string.Empty;
    private bool sessionKnown;
    private Guid lastSessionId;

    /// <param name="frame">The shell's reused frame context (filled before <see cref="PrepareFrame"/>).</param>
    /// <param name="reader">Telemetry reader (car identity resolution).</param>
    /// <param name="log">Shell logger.</param>
    public ModuleHost(FrameContext frame, ITelemetryReader reader, ILog log)
    {
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.log = log ?? NullLog.Instance;
        shellErrors = new ErrorReporter(this.log);
    }

    /// <summary>The modules in processing order.</summary>
    public IReadOnlyList<ModuleSlot> Slots => slots;

    /// <summary>Shared car identity (pass to every <see cref="ModuleContext.Car"/>).</summary>
    public ICarIdentityState Car => car;

    /// <summary>Frame statistics (pass to every <see cref="ModuleContext.Diagnostics"/>).</summary>
    public ShellDiagnostics Diagnostics => diagnostics;

    /// <summary>Errors of the shell's own stages.</summary>
    public ErrorReporter ShellErrors => shellErrors;

    /// <summary>Appends a module (plugin Init only) and creates its dispatcher.</summary>
    /// <param name="module">The module.</param>
    /// <param name="moduleLog">The module's logger (receives its rate-limited errors).</param>
    public ModuleSlot Add(IDlpModule module, ILog moduleLog)
    {
        var slot = new ModuleSlot(module, new DataThreadDispatcher(), new ErrorReporter(moduleLog ?? log));
        slotList.Add(slot);
        slots = slotList.ToArray();
        return slot;
    }

    /// <summary>
    /// Calls <see cref="IDlpModule.Init"/>. A module whose Init throws stays inactive (never called again) and the
    /// reason is kept in <see cref="ModuleSlot.InitError"/>.
    /// </summary>
    /// <returns>True when the module is active.</returns>
    public bool Initialize(ModuleSlot slot, ModuleContext context)
    {
        try
        {
            slot.Module.Init(context);
            slot.Active = true;
        }
        catch (Exception ex)
        {
            slot.Active = false;
            slot.InitError = ex.GetType().Name + ": " + ex.Message;
            log.Error("Module " + slot.Module.Id + " could not start and is disabled: " + ex);
        }

        return slot.Active;
    }

    /// <summary>Start of a frame: binds every dispatcher to the data thread and runs the queued actions.</summary>
    public void BeginFrame(double now)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ModuleSlot slot = slots[i];
            slot.Dispatcher.BindToCurrentThread();
            slot.Dispatcher.Drain(slot.Errors, now);
        }
    }

    /// <summary>
    /// After the frame context was filled: game, car and session changes while the game runs; the game-stopped
    /// notification on the first frame without it. Never throws.
    /// </summary>
    /// <returns>True when the game runs (modules get <see cref="IDlpModule.Update"/> in <see cref="EndFrame"/>).</returns>
    public bool PrepareFrame(double now)
    {
        try
        {
            if (!frame.GameRunning)
            {
                if (wasRunning)
                {
                    wasRunning = false;
                    NotifyGameStopped(now);
                }

                return false;
            }

            wasRunning = true;
            if (!gameKnown || !string.Equals(frame.GameName, lastGame, StringComparison.Ordinal))
            {
                gameKnown = true;
                lastGame = frame.GameName;
                Notify(m => m.OnGameChanged(frame), now);

                // No car until the new sim's car resolves (below, same frame).
                car.Reset();
                Notify(m => m.OnCarChanged(CarIdentity.None), now);
                sessionKnown = false;
            }

            if (car.Update(frame, reader, now))
            {
                CarIdentity current = car.Current;
                Notify(m => m.OnCarChanged(current), now);
            }

            Guid sessionId = frame.SessionId;
            if (!sessionKnown)
            {
                sessionKnown = true;
                lastSessionId = sessionId;
            }
            else if (sessionId != lastSessionId)
            {
                lastSessionId = sessionId;
                Notify(m => m.OnSessionChanged(frame), now);
            }

            return true;
        }
        catch (Exception ex)
        {
            FrameFailed(ex, now);
            return false;
        }
    }

    /// <summary>The shell stage failed (e.g. filling the frame threw): report it and fault every module. Never throws.</summary>
    public void FrameFailed(Exception error, double now)
    {
        shellErrors.Report(error, now);
        for (int i = 0; i < slots.Length; i++)
        {
            ModuleSlot slot = slots[i];
            if (!slot.Active)
            {
                continue;
            }

            slot.Errors.Record(error);
            slot.FaultCount++;
            try
            {
                slot.Module.OnFault(error);
            }
            catch (Exception ex)
            {
                slot.Errors.Report(ex, now);
            }
        }
    }

    /// <summary>End of a frame: <see cref="IDlpModule.Update"/> (when <paramref name="running"/>), then <see cref="IDlpModule.Tick"/>.</summary>
    public void EndFrame(double now, bool running)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ModuleSlot slot = slots[i];
            if (!slot.Active || !running)
            {
                continue;
            }

            try
            {
                slot.Module.Update(frame);
            }
            catch (Exception ex)
            {
                Fault(slot, ex, now);
            }
        }

        for (int i = 0; i < slots.Length; i++)
        {
            ModuleSlot slot = slots[i];
            if (!slot.Active)
            {
                continue;
            }

            try
            {
                slot.Module.Tick(now, running);
            }
            catch (Exception ex)
            {
                Fault(slot, ex, now);
            }
        }
    }

    /// <summary>SimHub shuts down: applies each module's queued actions, then calls <see cref="IDlpModule.End"/>. Never throws.</summary>
    public void End(double now)
    {
        foreach (ModuleSlot slot in slots)
        {
            try
            {
                // SimHub no longer calls DataUpdate: apply what the UI queued last (an import, a slider edit).
                slot.Dispatcher.Drain(slot.Errors, now);
                if (slot.Active)
                {
                    slot.Module.End();
                }
            }
            catch (Exception ex)
            {
                log.Error("End of module " + slot.Module.Id + " failed: " + ex);
            }
        }
    }

    private void NotifyGameStopped(double now)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ModuleSlot slot = slots[i];
            if (!slot.Active)
            {
                continue;
            }

            try
            {
                slot.Module.OnGameStopped();
            }
            catch (Exception ex)
            {
                Fault(slot, ex, now);
            }
        }
    }

    /// <summary>Calls <paramref name="call"/> on every active module, guarded per module (rare events only: the caller allocates a closure).</summary>
    private void Notify(Action<IDlpModule> call, double now)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ModuleSlot slot = slots[i];
            if (!slot.Active)
            {
                continue;
            }

            try
            {
                call(slot.Module);
            }
            catch (Exception ex)
            {
                Fault(slot, ex, now);
            }
        }
    }

    /// <summary>A module threw: report it (rate-limited) and let the module zero its outputs.</summary>
    private static void Fault(ModuleSlot slot, Exception error, double now)
    {
        slot.FaultCount++;
        slot.Errors.Report(error, now);
        try
        {
            slot.Module.OnFault(error);
        }
        catch (Exception ex)
        {
            slot.Errors.Report(ex, now);
        }
    }
}
