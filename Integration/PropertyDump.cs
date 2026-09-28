using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SimHub.Plugins;
using User.SlipLockPropertiesCalc.Settings;
using User.SlipLockPropertiesCalc.Telemetry;

namespace User.SlipLockPropertiesCalc.Integration;

/// <summary>
/// Diagnostics "Dump property names": writes every SimHub property name plus the current values of all raw paths
/// the plugin probes to <c>PluginsData\SlipLockPropertiesCalc\property-dump-&lt;timestamp&gt;.txt</c>.
/// User-triggered and rare, so allocations and file IO are fine here (never called from DataUpdate).
/// </summary>
internal static class PropertyDump
{
    private const string FilePrefix = "property-dump-";
    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    /// <summary>Writes the dump and returns its path.</summary>
    /// <param name="pluginManager">SimHub plugin manager.</param>
    /// <param name="reader">Reader used to fetch the candidate values.</param>
    /// <param name="directory">Output directory (created if missing).</param>
    /// <param name="header">Free text describing the current state (game, car, sources).</param>
    public static string Write(PluginManager pluginManager, ITelemetryReader reader, string directory, string header)
    {
        var sb = new StringBuilder();
        sb.Append("SlipLock property dump ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
        if (!string.IsNullOrEmpty(header))
        {
            sb.Append(header).Append("\r\n");
        }

        sb.Append("\r\n=== Probed paths (current values) ===\r\n");
        foreach (string path in CandidatePaths())
        {
            sb.Append(path).Append(" = ").Append(reader.GetText(path) ?? "NULL").Append("\r\n");
        }

        List<string> names = pluginManager.GetAllPropertiesNames() ?? new List<string>();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        sb.Append("\r\n=== All property names (").Append(names.Count.ToString(CultureInfo.InvariantCulture)).Append(") ===\r\n");
        foreach (string name in names)
        {
            sb.Append(name).Append("\r\n");
        }

        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, FilePrefix + DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".txt");
        JsonFile.WriteAllTextAtomic(file, sb.ToString());
        return file;
    }

    /// <summary>Every raw path the telemetry resolution probes, without duplicates, in a stable order.</summary>
    private static IEnumerable<string> CandidatePaths()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in PropertyPaths.DiagnosticScanPaths)
        {
            if (seen.Add(path))
            {
                yield return path;
            }
        }

        foreach (string[] group in new[]
        {
            PropertyPaths.ShakeItWheelSlip,
            PropertyPaths.ShakeItProxySlip,
            PropertyPaths.ShakeItWheelLock,
            PropertyPaths.AccWheelSlip,
            PropertyPaths.RFactorWheelRotation,
            PropertyPaths.RFactorWheelRadius,
            PropertyPaths.IRacingWheelSpeed,
            PropertyPaths.BareWheelSpeed,
        })
        {
            foreach (string path in group)
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }
}
