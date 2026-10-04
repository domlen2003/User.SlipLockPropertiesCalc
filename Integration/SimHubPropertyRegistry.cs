using System;
using System.Collections.Generic;
using DivebombLogistics.Framework;
using SimHub.Plugins;

namespace DivebombLogistics.Integration;

/// <summary>
/// <see cref="IPropertyRegistry"/> for SimHub: <see cref="PluginManager.AttachDelegate{T}(string, Type, Func{T}, string, bool, SupportStatus)"/>
/// with the plugin's runtime type, so SimHub publishes the properties as <c>DLP.&lt;name&gt;</c> (the prefix is the
/// runtime class name). Never use the <c>IPluginExtensions.AttachDelegate</c> extension on an interface-typed
/// receiver: it takes the prefix from the compile-time type ("IPlugin.*"). One instance is shared by all modules.
/// </summary>
internal sealed class SimHubPropertyRegistry : IPropertyRegistry
{
    private readonly PluginManager pluginManager;
    private readonly Type pluginType;
    private readonly List<string> names = new List<string>();
    private readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <param name="pluginManager">SimHub's plugin manager (from <c>Init</c>).</param>
    /// <param name="pluginType">The plugin's runtime type (<c>GetType()</c> of <c>DLP</c>); its name is the prefix.</param>
    public SimHubPropertyRegistry(PluginManager pluginManager, Type pluginType)
    {
        this.pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        this.pluginType = pluginType ?? throw new ArgumentNullException(nameof(pluginType));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Names => names;

    /// <summary>True when SimHub publishes the properties under <see cref="DlpNames.PropertyPrefix"/> (plugin class not renamed).</summary>
    public bool PrefixMatchesPluginType => string.Equals(pluginType.Name + ".", DlpNames.PropertyPrefix, StringComparison.Ordinal);

    /// <inheritdoc />
    public void Attach<T>(string name, Func<T> provider)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A property name is required.", nameof(name));
        }

        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (!known.Add(name))
        {
            throw new ArgumentException("Property " + DlpNames.FullName(name) + " is already registered.", nameof(name));
        }

        pluginManager.AttachDelegate(name, pluginType, provider);
        names.Add(name);
    }

    /// <summary>True when SimHub returns a value for <c>DLP.&lt;name&gt;</c> (start-up self-check).</summary>
    public bool IsReadable(string name) => pluginManager.GetPropertyValue(DlpNames.FullName(name)) != null;
}
