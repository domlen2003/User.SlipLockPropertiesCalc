using System.Text;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Haptics.Balance.Sources;

/// <summary>
/// One telemetry value of a vehicle-state source, read from the first of several candidate property paths
/// (e.g. ACC <c>SteerAngle</c> vs. AC EVO <c>steerAngle</c>).
/// </summary>
/// <remarks>
/// Candidate paths are full strings built once at construction, so reading never concatenates.
/// While no candidate has been found, candidates are only probed when the owning source allows it
/// (rate-limited), because a lookup of a missing raw-data path is comparatively expensive in SimHub and
/// fields that a sim simply does not have (AC slip angles, rF2 wetness) would otherwise cost lookups every frame.
/// Once a candidate produced a value it stays selected until <see cref="Reset"/> (game change / retest).
/// </remarks>
internal sealed class SourceField
{
    private readonly string[] candidates;

    /// <summary>Creates a field. <paramref name="candidates"/> are full property paths in order of preference.</summary>
    public SourceField(string label, params string[] candidates)
    {
        Label = label;
        this.candidates = candidates;
    }

    /// <summary>Short name used in diagnostics, e.g. "V".</summary>
    public string Label { get; }

    /// <summary>The candidate path that produced a value, or null while unresolved.</summary>
    public string ResolvedPath { get; private set; }

    /// <summary>True when the most recent <see cref="Read"/> returned a value.</summary>
    public bool Found { get; private set; }

    /// <summary>
    /// Reads the value (NaN when missing or not numeric). Unresolved fields only look up their candidates when
    /// <paramref name="probe"/> is true. Allocation-free.
    /// </summary>
    public double Read(ITelemetryReader reader, bool probe)
    {
        double value;
        if (ResolvedPath != null)
        {
            Found = reader.TryGetDouble(ResolvedPath, out value);
            return value;
        }

        if (probe)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                if (reader.TryGetDouble(candidates[i], out value))
                {
                    ResolvedPath = candidates[i];
                    Found = true;
                    return value;
                }
            }
        }

        Found = false;
        return double.NaN;
    }

    /// <summary>Forgets the resolved candidate.</summary>
    public void Reset()
    {
        ResolvedPath = null;
        Found = false;
    }

    /// <summary>Appends one diagnostics line (allocates; UI/diagnostics path only).</summary>
    public void Describe(StringBuilder builder)
    {
        builder.Append("  ").Append(Label).Append(": ");
        if (ResolvedPath == null)
        {
            builder.Append("not found (").Append(string.Join(" | ", candidates)).Append(')');
        }
        else
        {
            builder.Append(ResolvedPath);
            if (!Found)
            {
                builder.Append(" (currently missing)");
            }
        }

        builder.AppendLine();
    }
}
