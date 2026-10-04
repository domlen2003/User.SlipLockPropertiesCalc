using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;
using DivebombLogistics.Tests.Fakes;

namespace DivebombLogistics.Tests;

/// <summary>
/// Tests of the module framework: <see cref="DataThreadDispatcher"/>, <see cref="ErrorReporter"/>,
/// <see cref="ShellDiagnostics"/>, <see cref="CarIdentityTracker"/> and the shell orchestration in
/// <see cref="ModuleHost"/> (call order, game/car/session changes, fault isolation).
/// </summary>
internal sealed class FrameworkTests
{
    private const string LmuModelPath = CarIdentityResolver.LmuVehicleModelPath;

    // ---------------------------------------------------------------- DataThreadDispatcher

    [Test]
    public void Dispatcher_RunsPostedActionsInOrderAndIsolatesFailures()
    {
        var dispatcher = new DataThreadDispatcher();
        var errors = new ErrorReporter(NullLog.Instance);
        var order = new List<int>();
        dispatcher.Post(() => order.Add(1));
        dispatcher.Post(() => throw new InvalidOperationException("boom"));
        dispatcher.Post(() => order.Add(3));
        dispatcher.Post(null);

        Assert.Equal(3, dispatcher.PendingCount, "null is ignored");
        Assert.Equal(3, dispatcher.Drain(errors, 1.0), "all run");
        Assert.Equal("1,3", string.Join(",", order), "order kept, failure isolated");
        Assert.True(errors.LastError.Contains("InvalidOperationException: boom"), errors.LastError);
        Assert.Equal(0, dispatcher.Drain(errors, 2.0), "queue empty");
    }

    [Test]
    public void Dispatcher_RunExecutesDirectlyOnTheDataThread()
    {
        var dispatcher = new DataThreadDispatcher();
        Assert.False(dispatcher.IsDataThread, "unbound");
        dispatcher.BindToCurrentThread();
        Assert.True(dispatcher.IsDataThread, "bound");
        Assert.Equal("direct", dispatcher.Run(() => "direct", 0), "no queueing on the data thread");
        Assert.Equal(0, dispatcher.PendingCount, "nothing queued");
    }

    [Test]
    public void Dispatcher_RunFromAnotherThreadWaitsForTheDataThread()
    {
        var dispatcher = new DataThreadDispatcher();
        var errors = new ErrorReporter(NullLog.Instance);
        dispatcher.BindToCurrentThread();
        string result = null;
        int actionThread = -1;
        var caller = new Thread(() => result = dispatcher.Run(
            () =>
            {
                actionThread = Thread.CurrentThread.ManagedThreadId;
                return "done";
            },
            5000));
        caller.Start();

        // Play the data thread until the request has been served.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (caller.IsAlive && DateTime.UtcNow < deadline)
        {
            dispatcher.Drain(errors, 0);
            Thread.Sleep(1);
        }

        caller.Join();
        Assert.Equal("done", result, "result returned to the caller");
        Assert.Equal(Thread.CurrentThread.ManagedThreadId, actionThread, "executed on the data thread");
    }

    [Test]
    public void Dispatcher_RunTimesOutAndReportsExceptions()
    {
        var dispatcher = new DataThreadDispatcher();
        string timedOut = null;
        var caller = new Thread(() => timedOut = dispatcher.Run(() => "late", 20));
        caller.Start();
        caller.Join();
        Assert.Equal(DataThreadDispatcher.TimeoutMessage, timedOut, "nobody drained");

        // The abandoned request is drained when the data thread comes back, but never runs: the caller was told it
        // failed, so it must not be applied behind their back (for example a profile import).
        bool ranLate = false;
        var abandoned = new Thread(() => dispatcher.Run(() => { ranLate = true; return "late"; }, 20));
        abandoned.Start();
        abandoned.Join();
        Assert.Equal(2, dispatcher.Drain(new ErrorReporter(NullLog.Instance), 0), "late requests drained");
        Assert.False(ranLate, "an abandoned request never runs");

        string failed = null;
        dispatcher.BindToCurrentThread();
        var other = new Thread(() => failed = dispatcher.Run(() => throw new InvalidOperationException("nope"), 5000));
        other.Start();
        while (other.IsAlive)
        {
            dispatcher.Drain(new ErrorReporter(NullLog.Instance), 0);
            Thread.Sleep(1);
        }

        Assert.Equal("nope", failed, "exception message returned");
    }

    // ---------------------------------------------------------------- ErrorReporter / ShellDiagnostics

    [Test]
    public void ErrorReporter_LogsFirstErrorThenRateLimitsWithSuppressedCount()
    {
        var log = new ListLog();
        var reporter = new ErrorReporter(log, "DataUpdate error", () => new DateTime(2026, 10, 4, 13, 14, 15));
        Assert.Equal(string.Empty, reporter.LastError, "no error yet");

        reporter.Report(new InvalidOperationException("first"), 0.0);
        reporter.Report(new ArgumentException("second"), 1.0);
        reporter.Report(new ArgumentException("third"), 9.9);
        Assert.Equal(1, log.Errors.Count, "rate limited");
        Assert.Equal("13:14:15 ArgumentException: third", reporter.LastError, "newest error shown");

        reporter.Report(new TimeoutException("fourth"), 10.0);
        Assert.Equal(2, log.Errors.Count, "logged again after 10 s");
        Assert.True(log.Errors[1].StartsWith("DataUpdate error (2 further errors suppressed): System.TimeoutException: fourth", StringComparison.Ordinal), log.Errors[1]);

        reporter.Record(new FormatException("recorded"));
        Assert.Equal(2, log.Errors.Count, "Record does not log");
        Assert.Equal("13:14:15 FormatException: recorded", reporter.LastError);
        Assert.Equal(5, reporter.Count, "all counted");
    }

    [Test]
    public void ShellDiagnostics_CountsFramesAndSmoothsTheDuration()
    {
        var diagnostics = new ShellDiagnostics();
        diagnostics.RecordFrame(1.0);
        Assert.Equal(1L, diagnostics.FrameCount);
        Assert.Near(ShellDiagnostics.TimingSmoothing, diagnostics.DataUpdateMs, 1e-12, "first step of the EMA");
        for (int i = 0; i < 1000; i++)
        {
            diagnostics.RecordFrame(1.0);
        }

        Assert.Near(1.0, diagnostics.DataUpdateMs, 1e-9, "converges");
        Assert.Equal(1001L, diagnostics.FrameCount);
    }

    // ---------------------------------------------------------------- CarIdentityTracker

    [Test]
    public void CarTracker_ResolvesOnlyWhenTheCarChanges()
    {
        var tracker = new CarIdentityTracker();
        var frame = Frame("IRacing", "id-1", "Mazda MX-5 Cup");
        var reader = new FakeTelemetryReader();

        Assert.True(tracker.Update(frame, reader, 1.0), "first car");
        Assert.Equal("Mazda MX-5 Cup", tracker.Current.CarKey);
        Assert.False(tracker.Update(frame, reader, 2.0), "unchanged");
        Assert.False(tracker.IsProvisional(2.0), "no retry outside rFactor sims");

        frame.CarId = "id-2"; // another livery of the same model: resolved again, same profile
        Assert.False(tracker.Update(frame, reader, 3.0), "same car key");

        frame.CarModel = "Dallara IR18";
        Assert.True(tracker.Update(frame, reader, 4.0), "car changed");
        Assert.Equal("Dallara IR18", tracker.Current.CarKey);

        tracker.Reset();
        Assert.False(tracker.Current.HasCar, "reset forgets the car");
        Assert.True(tracker.Update(frame, reader, 5.0), "resolved again after reset");
    }

    [Test]
    public void CarTracker_LmuRetriesTheNativeModelInsideTheWindowOnly()
    {
        var tracker = new CarIdentityTracker();
        var frame = Frame("LMU", "GT3_Iron Lynx 2026_61", "296GT3 Custom Team 2025");
        var reader = new FakeTelemetryReader().Set(LmuModelPath, new byte[30]);

        Assert.True(tracker.Update(frame, reader, 10.0), "placeholder car");
        Assert.Equal(CarKeySource.CarModel, tracker.Current.KeySource);
        Assert.True(tracker.IsProvisional(10.0), "placeholder inside the window");
        Assert.Near(10.0 + CarIdentityTracker.RetryWindowSeconds, tracker.ProvisionalUntil, 1e-12, "window end");

        reader.Set(LmuModelPath, Encoding.ASCII.GetBytes("Ferrari 296 GT3\0"));
        Assert.False(tracker.Update(frame, reader, 10.5), "no retry before the interval");
        Assert.True(tracker.Update(frame, reader, 11.0), "retry after one second finds the native model");
        Assert.Equal("Ferrari 296 GT3", tracker.Current.CarKey);
        Assert.Equal(CarKeySource.NativeModel, tracker.Current.KeySource);
        Assert.False(tracker.IsProvisional(11.0), "final key");

        // A placeholder that never resolves: retries stop after the window, the key becomes final.
        var late = new CarIdentityTracker();
        reader.Set(LmuModelPath, new byte[30]);
        Assert.True(late.Update(frame, reader, 0.0), "placeholder");
        for (double t = 1.0; t <= CarIdentityTracker.RetryWindowSeconds; t += 1.0)
        {
            Assert.False(late.Update(frame, reader, t), "retry without result at " + t);
        }

        Assert.False(late.IsProvisional(CarIdentityTracker.RetryWindowSeconds + 0.01), "window over");
        reader.Set(LmuModelPath, Encoding.ASCII.GetBytes("Ferrari 296 GT3\0"));
        Assert.False(late.Update(frame, reader, CarIdentityTracker.RetryWindowSeconds + 1.0), "no retry after the window");
        Assert.Equal("296GT3 Custom Team 2025", late.Current.CarKey, "placeholder kept");
    }

    // ---------------------------------------------------------------- ModuleHost

    [Test]
    public void Host_CallsModulesInTheDocumentedOrder()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new FakeModule();
        rig.Add(module);
        Assert.Equal("Init", string.Join(",", module.Calls), "init");
        module.Calls.Clear();

        rig.RunFrame(); // no game yet
        Assert.Equal("Tick", string.Join(",", module.Calls), "only Tick without a game");
        module.Calls.Clear();

        rig.SetGame("IRacing", "id-1", "Mazda MX-5 Cup");
        rig.RunFrame();
        Assert.Equal("Game:IRacing,Car:none,Car:IRacing/Mazda MX-5 Cup,Update,Tick", string.Join(",", module.Calls), "first game frame");
        module.Calls.Clear();

        rig.RunFrame();
        Assert.Equal("Update,Tick", string.Join(",", module.Calls), "steady state");
        module.Calls.Clear();

        rig.Frame.SessionId = Guid.NewGuid();
        rig.RunFrame();
        Assert.Equal("Session,Update,Tick", string.Join(",", module.Calls), "new session");
        module.Calls.Clear();

        rig.SetGame("LMU", "x", "Oreca 07 #38");
        rig.RunFrame();
        Assert.Equal("Game:LMU,Car:none,Car:LMU/Oreca 07 #38,Update,Tick", string.Join(",", module.Calls), "game change resets the car");
        module.Calls.Clear();

        rig.Frame.GameRunning = false;
        rig.RunFrames(3);
        Assert.Equal("Stopped,Tick,Tick,Tick", string.Join(",", module.Calls), "stopped once");
        Assert.Equal(8L, rig.Host.Diagnostics.FrameCount, "frames counted");

        rig.Host.End(rig.Now);
        Assert.Equal("End", module.Calls[module.Calls.Count - 1], "end");
    }

    [Test]
    public void Host_IsolatesAFailingModule()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var failing = new FakeModule("Failing") { ThrowOn = "Update" };
        var healthy = new FakeModule("Healthy");
        ModuleSlot failingSlot = rig.Add(failing);
        ModuleSlot healthySlot = rig.Add(healthy);

        rig.SetGame("IRacing", "id", "Mazda MX-5 Cup");
        rig.RunFrames(2);

        Assert.Equal(2, failing.Count("Fault"), "fault after each failing update");
        Assert.Equal(2, failing.Count("Tick"), "the failing module still ticks");
        Assert.Equal(2, failingSlot.FaultCount);
        Assert.True(failingSlot.Errors.LastError.Contains("Failing fails in Update"), failingSlot.Errors.LastError);
        Assert.Equal(0, healthy.Count("Fault"), "other module unaffected");
        Assert.Equal(2, healthy.Count("Update"), "other module updated");
        Assert.Equal(string.Empty, healthySlot.Errors.LastError);
    }

    [Test]
    public void Host_DisablesAModuleWhoseInitFails()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var broken = new FakeModule("Broken") { ThrowOn = "Init" };
        ModuleSlot slot = rig.Add(broken);

        Assert.False(slot.Active, "inactive");
        Assert.True(slot.InitError.Contains("Broken fails in Init"), slot.InitError);
        rig.SetGame("IRacing", "id", "car");
        rig.RunFrames(3);
        rig.Host.End(rig.Now);
        Assert.Equal("Init", string.Join(",", broken.Calls), "never called again");
    }

    [Test]
    public void Host_ShellFailureFaultsEveryModuleAndSkipsUpdate()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var a = new FakeModule("A");
        var b = new FakeModule("B");
        ModuleSlot slotA = rig.Add(a);
        rig.Add(b);
        rig.SetGame("IRacing", "id", "car");
        rig.RunFrame();
        a.Calls.Clear();
        b.Calls.Clear();

        rig.RunFailedFrame(new InvalidOperationException("frame broken"));
        Assert.Equal("Fault,Tick", string.Join(",", a.Calls), "faulted, no update");
        Assert.Equal("Fault,Tick", string.Join(",", b.Calls), "faulted, no update");
        Assert.True(slotA.Errors.LastError.Contains("frame broken"), "module diagnostics show the shell error");
        Assert.True(rig.Host.ShellErrors.LastError.Contains("frame broken"), "shell reporter");
    }

    [Test]
    public void Host_PostedActionsRunBeforeTheFrameOnTheDataThread()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        var module = new FakeModule();
        ModuleSlot slot = rig.Add(module);
        int pressed = 0;
        rig.Actions[module.Id].Add("Fake.Press", () =>
        {
            Assert.True(slot.Dispatcher.IsDataThread, "action runs on the data thread");
            module.Calls.Add("Action");
            pressed++;
        });

        rig.Actions[module.Id].Press("Fake.Press");
        Assert.Equal(0, pressed, "posted, not run");
        module.Calls.Clear();
        rig.RunFrame();
        Assert.Equal(1, pressed, "run in the next frame");
        Assert.Equal("Action,Tick", string.Join(",", module.Calls), "before the module calls");
        Assert.Equal("Fake.Press", rig.Actions[module.Id].Names[0]);
    }

    [Test]
    public void Host_SteadyStateFrameDoesNotAllocate()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var rig = new ModuleTestRig(dir.Path);
        rig.Add(new NoOpModule());
        rig.SetGame("IRacing", "id", "car");
        rig.RunFrames(10);

        // The monitoring counter has allocation-context granularity: one byte per frame would exceed the allowance.
        const int Frames = 10000;
        const long AllowanceBytes = 8 * 1024;
        AppDomain.MonitoringIsEnabled = true;
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        for (int i = 0; i < Frames; i++)
        {
            rig.Now += ModuleTestRig.FrameSeconds;
            rig.Host.BeginFrame(rig.Now);
            bool running = rig.Host.PrepareFrame(rig.Now);
            rig.Host.EndFrame(rig.Now, running);
            rig.Host.Diagnostics.RecordFrame(0.1);
        }

        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        Assert.True(allocated <= AllowanceBytes, "allocated " + allocated + " bytes in " + Frames + " frames");
    }

    private static FrameContext Frame(string game, string carId, string carModel) =>
        new FrameContext { GameRunning = true, GameName = game, CarId = carId, CarModel = carModel };

    /// <summary>A module that does nothing (allocation test).</summary>
    private sealed class NoOpModule : IDlpModule
    {
        public string Id => "NoOp";

        public string DisplayName => "NoOp";

        public void Init(ModuleContext context)
        {
        }

        public void OnGameChanged(FrameContext frame)
        {
        }

        public void OnCarChanged(CarIdentity car)
        {
        }

        public void OnSessionChanged(FrameContext frame)
        {
        }

        public void Update(FrameContext frame)
        {
        }

        public void OnGameStopped()
        {
        }

        public void Tick(double now, bool gameRunning)
        {
        }

        public void OnFault(Exception error)
        {
        }

        public void End()
        {
        }
    }

    /// <summary>ILog that keeps the error lines.</summary>
    private sealed class ListLog : ILog
    {
        public List<string> Errors { get; } = new List<string>();

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message) => Errors.Add(message);
    }
}
