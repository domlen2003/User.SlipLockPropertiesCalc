using System;
using System.Collections.Generic;
using DivebombLogistics.Framework;

namespace DivebombLogistics.Tests.Fakes;

/// <summary><see cref="IActionRegistry"/> that posts presses to a dispatcher like the SimHub implementation.</summary>
internal sealed class RecordingActionRegistry : IActionRegistry
{
    private readonly IDataThreadDispatcher dispatcher;
    private readonly List<string> names = new List<string>();
    private readonly Dictionary<string, Action> pressed = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action> released = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);

    public RecordingActionRegistry(IDataThreadDispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
    }

    public IReadOnlyList<string> Names => names;

    public void Add(string name, Action onPressed, Action onReleased = null)
    {
        if (string.IsNullOrEmpty(name) || onPressed == null || pressed.ContainsKey(name))
        {
            throw new ArgumentException("invalid or duplicate action " + name, nameof(name));
        }

        pressed[name] = onPressed;
        if (onReleased != null)
        {
            released[name] = onReleased;
        }

        names.Add(name);
    }

    /// <summary>Simulates SimHub invoking the action from its own thread: the callback is posted, not run.</summary>
    public void Press(string name) => dispatcher.Post(pressed[name]);

    /// <summary>Simulates the button release.</summary>
    public void Release(string name)
    {
        if (released.TryGetValue(name, out Action action))
        {
            dispatcher.Post(action);
        }
    }
}
