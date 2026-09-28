using System;
using SimHub.Plugins;
using User.SlipLockPropertiesCalc.Telemetry;

namespace User.SlipLockPropertiesCalc.Integration;

/// <summary>
/// Production <see cref="ITelemetryReader"/>: reads SimHub properties by full path through
/// <see cref="PluginManager.GetPropertyValue(string)"/>, which returns null for unknown paths.
/// </summary>
internal sealed class PluginManagerTelemetryReader : ITelemetryReader
{
    private readonly PluginManager pluginManager;

    public PluginManagerTelemetryReader(PluginManager pluginManager)
    {
        this.pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
    }

    public object GetValue(string propertyPath) => pluginManager.GetPropertyValue(propertyPath);
}
