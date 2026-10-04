using System;
using System.Collections.Generic;
using DivebombLogistics.Framework;
using SimHub.Plugins;

namespace DivebombLogistics.Integration;

/// <summary>
/// <see cref="IActionRegistry"/> for SimHub: <see cref="PluginManager.AddAction(string, Type, Action{PluginManager, string}, Action{PluginManager, string})"/>
/// with the plugin's runtime type (published as <c>DLP.&lt;name&gt;</c>). SimHub may invoke actions on any thread, so
/// the callbacks only post the module's delegates to its <see cref="IDataThreadDispatcher"/>; the module code runs at
/// the start of the next <c>DataUpdate</c>. One instance per module (it posts to that module's dispatcher); action
/// names are checked for uniqueness across all modules through the shared <c>registeredNames</c> set.
/// </summary>
internal sealed class SimHubActionRegistry : IActionRegistry
{
    private readonly PluginManager pluginManager;
    private readonly Type pluginType;
    private readonly IDataThreadDispatcher dispatcher;
    private readonly HashSet<string> registeredNames;
    private readonly List<string> names = new List<string>();

    /// <param name="pluginManager">SimHub's plugin manager.</param>
    /// <param name="pluginType">The plugin's runtime type (<c>GetType()</c> of <c>DLP</c>).</param>
    /// <param name="dispatcher">The module's dispatcher; receives the callbacks.</param>
    /// <param name="registeredNames">Names registered by any module so far (shared, Init thread only).</param>
    public SimHubActionRegistry(PluginManager pluginManager, Type pluginType, IDataThreadDispatcher dispatcher, HashSet<string> registeredNames)
    {
        this.pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        this.pluginType = pluginType ?? throw new ArgumentNullException(nameof(pluginType));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.registeredNames = registeredNames ?? throw new ArgumentNullException(nameof(registeredNames));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Names => names;

    /// <inheritdoc />
    public void Add(string name, Action pressed, Action released = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("An action name is required.", nameof(name));
        }

        if (pressed == null)
        {
            throw new ArgumentNullException(nameof(pressed));
        }

        if (!registeredNames.Add(name))
        {
            throw new ArgumentException("Action " + DlpNames.FullName(name) + " is already registered.", nameof(name));
        }

        // Closures are created once here; a press only enqueues the cached module delegate.
        IDataThreadDispatcher target = dispatcher;
        Action<PluginManager, string> start = (manager, action) => target.Post(pressed);
        Action<PluginManager, string> end = released == null
            ? (manager, action) => { }
            : (manager, action) => target.Post(released);
        pluginManager.AddAction(name, pluginType, start, end);
        names.Add(name);
    }
}
