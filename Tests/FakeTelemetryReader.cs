using System.Collections.Generic;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Tests;

/// <summary>Dictionary-backed <see cref="ITelemetryReader"/> for tests. Missing paths return null.</summary>
internal sealed class FakeTelemetryReader : ITelemetryReader
{
    private readonly Dictionary<string, object> values = new Dictionary<string, object>();

    /// <summary>Number of GetValue calls (for hot-path lookup-count assertions).</summary>
    public int ReadCount { get; private set; }

    public object GetValue(string propertyPath)
    {
        ReadCount++;
        return propertyPath != null && values.TryGetValue(propertyPath, out object value) ? value : null;
    }

    public FakeTelemetryReader Set(string path, object value)
    {
        values[path] = value;
        return this;
    }

    public FakeTelemetryReader Remove(string path)
    {
        values.Remove(path);
        return this;
    }

    public void Clear() => values.Clear();
}
