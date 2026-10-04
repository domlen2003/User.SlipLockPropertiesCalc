using System;
using System.Collections.Generic;

namespace DivebombLogistics.Framework;

/// <summary>
/// Registers SimHub actions, published as <c>DLP.&lt;name&gt;</c>. Users bind buttons to them in SimHub's
/// "Controls and events" page or in a <c>SimHub.Plugins.UI.ControlsEditor</c> on the module's tab. Call only from
/// <see cref="IDlpModule.Init"/>.
/// </summary>
internal interface IActionRegistry
{
    /// <summary>Every registered name (without prefix), in registration order.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Registers action <c>DLP.&lt;name&gt;</c>. SimHub may invoke actions on any thread; the callbacks are posted to
    /// the module's <see cref="IDataThreadDispatcher"/> and run at the start of the next <c>DataUpdate</c>, so they may
    /// touch data-thread state directly. Pass cached delegates: nothing is allocated per press then.
    /// </summary>
    /// <param name="name">Name without the prefix, e.g. <c>SpeedDial.Dial1</c>.</param>
    /// <param name="pressed">Runs when the bound button is pressed.</param>
    /// <param name="released">Runs when it is released (optional).</param>
    /// <exception cref="ArgumentException">Empty name, or the name is already registered.</exception>
    void Add(string name, Action pressed, Action released = null);
}
