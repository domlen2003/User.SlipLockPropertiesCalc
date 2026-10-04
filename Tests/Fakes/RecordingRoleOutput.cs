using System.Collections.Generic;
using DivebombLogistics.Framework;

namespace DivebombLogistics.Tests.Fakes;

/// <summary><see cref="IRoleOutput"/> that records presses instead of sending them.</summary>
internal sealed class RecordingRoleOutput : IRoleOutput
{
    public bool IsAvailable { get; set; } = true;

    public List<string> Roles { get; } = new List<string>();

    /// <summary>Pressed roles in call order.</summary>
    public List<string> Presses { get; } = new List<string>();

    public bool Press(string role, int durationMs)
    {
        if (!IsAvailable || string.IsNullOrEmpty(role))
        {
            return false;
        }

        Presses.Add(role);
        return true;
    }

    public IReadOnlyList<string> GetButtonRoles() => Roles.ToArray();
}
