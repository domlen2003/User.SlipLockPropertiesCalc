using System.Collections.Generic;

namespace DivebombLogistics.Framework;

/// <summary>
/// Simulated button presses through SimHub's Control Mapper output roles (e.g. <c>TractionControl+</c>), which the
/// Control Mapper forwards to a virtual joystick the game has bound. Thread-safe.
/// </summary>
internal interface IRoleOutput
{
    /// <summary>True when the Control Mapper plugin is active and defines button roles (as of its last check).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Presses <paramref name="role"/> for <paramref name="durationMs"/>. Non-blocking: the press is queued to a
    /// worker thread and presses run one after the other in call order, so it is safe on the data thread.
    /// </summary>
    /// <returns>False when the Control Mapper is unavailable, <paramref name="role"/> is empty or not a role the Control Mapper defines (nothing queued).</returns>
    bool Press(string role, int durationMs);

    /// <summary>Button roles defined in the Control Mapper, for UI pickers (may allocate; any thread; empty when unavailable).</summary>
    IReadOnlyList<string> GetButtonRoles();
}
