using System;
using System.Collections.Generic;
using System.IO;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;

namespace DivebombLogistics.Tests.Fakes;

/// <summary>
/// Runs modules through the real <see cref="ModuleHost"/> with fake SimHub services, frame by frame, the way
/// <c>DLP.DataUpdate</c> does (minus <c>FrameContextBuilder</c>: tests set <see cref="Frame"/> directly).
/// </summary>
internal sealed class ModuleTestRig
{
    /// <summary>Frame period used by <see cref="RunFrames"/> (60 Hz).</summary>
    public const double FrameSeconds = 1.0 / 60.0;

    public ModuleTestRig(string dataRoot, ILog log = null)
    {
        DataRoot = dataRoot;
        Log = log ?? NullLog.Instance;
        Host = new ModuleHost(Frame, Reader, Log);
    }

    public FrameContext Frame { get; } = new FrameContext();

    public FakeTelemetryReader Reader { get; } = new FakeTelemetryReader();

    public RecordingPropertyRegistry Properties { get; } = new RecordingPropertyRegistry();

    public RecordingRoleOutput Roles { get; } = new RecordingRoleOutput();

    public ModuleHost Host { get; }

    public ILog Log { get; }

    /// <summary>Root standing in for <c>PluginsData\DLP</c>; each module gets <c>&lt;root&gt;\&lt;Id&gt;</c>.</summary>
    public string DataRoot { get; }

    /// <summary>Monotonic clock of the rig (seconds).</summary>
    public double Now { get; set; } = 100.0;

    /// <summary>Action registries per module id.</summary>
    public Dictionary<string, RecordingActionRegistry> Actions { get; } = new Dictionary<string, RecordingActionRegistry>();

    /// <summary>Adds and initializes a module like <c>DLP.Init</c>.</summary>
    public ModuleSlot Add(IDlpModule module)
    {
        ModuleSlot slot = Host.Add(module, Log);
        var actions = new RecordingActionRegistry(slot.Dispatcher);
        Actions[module.Id] = actions;
        Host.Initialize(slot, new ModuleContext
        {
            Id = module.Id,
            Log = Log,
            Reader = Reader,
            Frame = Frame,
            Car = Host.Car,
            Properties = Properties,
            Actions = actions,
            Roles = Roles,
            Dispatcher = slot.Dispatcher,
            Errors = slot.Errors,
            Diagnostics = Host.Diagnostics,
            DataDirectory = Path.Combine(DataRoot, module.Id),
            LogDirectory = Path.Combine(DataRoot, "Logs"),
            Clock = () => Now,
            ListPropertyNames = () => new List<string>(Properties.Names),
        });
        return slot;
    }

    /// <summary>Sets a running game with the given car (SimHub-normalized fields).</summary>
    public void SetGame(string game, string carId, string carModel, string carClass = "")
    {
        Frame.GameRunning = true;
        Frame.GameName = game;
        Frame.CarId = carId ?? string.Empty;
        Frame.CarModel = carModel ?? string.Empty;
        Frame.CarClass = carClass ?? string.Empty;
    }

    /// <summary>One <c>DataUpdate</c>: advance the clock, drain, prepare, update and tick.</summary>
    public void RunFrame(double seconds = FrameSeconds)
    {
        Now += seconds;
        Frame.WallTime = Now;
        Host.BeginFrame(Now);
        bool running = Host.PrepareFrame(Now);
        Host.EndFrame(Now, running);
        Host.Diagnostics.RecordFrame(0.1);
    }

    /// <summary>Several frames.</summary>
    public void RunFrames(int count, double seconds = FrameSeconds)
    {
        for (int i = 0; i < count; i++)
        {
            RunFrame(seconds);
        }
    }

    /// <summary>A frame whose context could not be filled (the shell stage threw).</summary>
    public void RunFailedFrame(Exception error)
    {
        Now += FrameSeconds;
        Host.BeginFrame(Now);
        Host.FrameFailed(error, Now);
        Host.EndFrame(Now, false);
    }
}
