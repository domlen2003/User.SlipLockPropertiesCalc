using System;
using System.Collections.Generic;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Framework;

namespace DivebombLogistics.Tests.Fakes;

/// <summary>Records every <see cref="IDlpModule"/> call; can be told to throw from any member.</summary>
internal sealed class FakeModule : IDlpModule
{
    public FakeModule(string id = "Fake")
    {
        Id = id;
    }

    public string Id { get; }

    public string DisplayName => Id;

    /// <summary>Calls in order, e.g. "Init", "Game:LMU", "Car:LMU/x", "Car:none", "Session", "Update", "Stopped", "Tick", "Fault", "End".</summary>
    public List<string> Calls { get; } = new List<string>();

    /// <summary>Name of the call that throws (same names as <see cref="Calls"/> without the argument), or null.</summary>
    public string ThrowOn { get; set; }

    public ModuleContext Context { get; private set; }

    public CarIdentity LastCar { get; private set; } = CarIdentity.None;

    public void Init(ModuleContext context)
    {
        Context = context;
        Record("Init", null);
    }

    public void OnGameChanged(FrameContext frame) => Record("Game", frame.GameName);

    public void OnCarChanged(CarIdentity car)
    {
        LastCar = car;
        Record("Car", car.HasCar ? car.SimKey + "/" + car.CarKey : "none");
    }

    public void OnSessionChanged(FrameContext frame) => Record("Session", null);

    public void Update(FrameContext frame) => Record("Update", null);

    public void OnGameStopped() => Record("Stopped", null);

    public void Tick(double now, bool gameRunning) => Record("Tick", null);

    public void OnFault(Exception error) => Calls.Add("Fault");

    public void End() => Record("End", null);

    /// <summary>Calls of one kind (e.g. "Update").</summary>
    public int Count(string call)
    {
        int count = 0;
        foreach (string entry in Calls)
        {
            if (entry == call || entry.StartsWith(call + ":", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private void Record(string call, string argument)
    {
        Calls.Add(argument == null ? call : call + ":" + argument);
        if (ThrowOn == call)
        {
            throw new InvalidOperationException(Id + " fails in " + call);
        }
    }
}
