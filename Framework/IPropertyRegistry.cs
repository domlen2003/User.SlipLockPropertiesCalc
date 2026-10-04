using System;
using System.Collections.Generic;

namespace DivebombLogistics.Framework;

/// <summary>
/// Registers SimHub properties as delegates, published as <c>DLP.&lt;name&gt;</c>. Call only from
/// <see cref="IDlpModule.Init"/>: the delegates are created once and read preallocated fields, so nothing is
/// allocated or pushed per frame (SimHub evaluates them on demand).
/// </summary>
internal interface IPropertyRegistry
{
    /// <summary>Every registered name (without prefix), in registration order.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>Registers <paramref name="provider"/> as property <c>DLP.&lt;name&gt;</c>.</summary>
    /// <param name="name">Name without <see cref="DlpNames.PropertyPrefix"/>, e.g. <c>SlipLock.MaxSway</c>.</param>
    /// <param name="provider">Returns the current value; must be cheap, allocation-free and never throw.</param>
    /// <exception cref="ArgumentException">Empty name, or the name is already registered.</exception>
    void Attach<T>(string name, Func<T> provider);
}
