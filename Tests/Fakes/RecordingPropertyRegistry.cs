using System;
using System.Collections.Generic;
using DivebombLogistics.Framework;

namespace DivebombLogistics.Tests.Fakes;

/// <summary><see cref="IPropertyRegistry"/> that keeps the providers so tests can read the exported values.</summary>
internal sealed class RecordingPropertyRegistry : IPropertyRegistry
{
    private readonly List<string> names = new List<string>();
    private readonly Dictionary<string, Func<object>> providers = new Dictionary<string, Func<object>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Names => names;

    public void Attach<T>(string name, Func<T> provider)
    {
        if (string.IsNullOrEmpty(name) || providers.ContainsKey(name))
        {
            throw new ArgumentException("invalid or duplicate property " + name, nameof(name));
        }

        providers[name] = () => provider();
        names.Add(name);
    }

    /// <summary>Current value of a registered property (relative name).</summary>
    public object Read(string name) => providers[name]();
}
