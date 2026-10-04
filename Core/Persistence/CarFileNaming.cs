using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DivebombLogistics.Core.Persistence;

/// <summary>
/// File naming shared by every per-car store (<c>&lt;module data&gt;\Cars\&lt;Sim&gt;\&lt;CarKey&gt;_&lt;fnv1a8&gt;.json</c>).
/// Deterministic: the same keys always map to the same file, and keys that differ in any character (even ones
/// replaced by sanitization, or only in case) map to different files. Extracted from the haptics car profile store
/// so other modules use exactly the same names.
/// </summary>
internal static class CarFileNaming
{
    /// <summary>Sub folder of a module's data directory holding its per-car files.</summary>
    public const string CarsFolderName = "Cars";

    /// <summary>Extension of per-car files.</summary>
    public const string FileExtension = ".json";

    /// <summary>Longest readable part of a car file name (the hash suffix comes on top).</summary>
    public const int MaxFileNameStemLength = 80;

    private const string UnnamedSegment = "unnamed";
    private const char ReplacementChar = '_';
    private const string HashFormat = "x8";

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>DOS device names that cannot be used as a file or folder name on Windows, with or without extension.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly HashSet<char> InvalidFileNameChars = new HashSet<char>(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Path of a car's file below <paramref name="dataDirectory"/>:
    /// <c>&lt;dataDirectory&gt;\Cars\&lt;sanitized sim&gt;\&lt;stem&gt;.json</c>.
    /// </summary>
    /// <param name="dataDirectory">The module's data directory, e.g. <c>PluginsData\DLP\Haptics</c>.</param>
    /// <param name="simKey">SimHub game name.</param>
    /// <param name="carKey">Stable car key.</param>
    public static string GetCarFilePath(string dataDirectory, string simKey, string carKey) =>
        Path.Combine(dataDirectory, CarsFolderName, SanitizeSegment(simKey), BuildCarFileStem(carKey) + FileExtension);

    /// <summary>
    /// Makes one path segment safe for any Windows file system: invalid characters become '_', surrounding
    /// whitespace and trailing dots are removed, reserved device names are prefixed, empty becomes "unnamed".
    /// Used as-is for the sim folder (SimHub game names are fixed identifiers, so no hash is needed there).
    /// </summary>
    public static string SanitizeSegment(string key)
    {
        var builder = new StringBuilder(key?.Length ?? 0);
        if (key != null)
        {
            foreach (char c in key)
            {
                builder.Append(InvalidFileNameChars.Contains(c) ? ReplacementChar : c);
            }
        }

        // Windows silently drops trailing dots and spaces, which would make "A." and "A" collide.
        string segment = builder.ToString().Trim().TrimEnd('.', ' ');
        if (segment.Length == 0)
        {
            return UnnamedSegment;
        }

        int dot = segment.IndexOf('.');
        string deviceCandidate = dot < 0 ? segment : segment.Substring(0, dot);
        return ReservedDeviceNames.Contains(deviceCandidate) ? ReplacementChar + segment : segment;
    }

    /// <summary>
    /// Car file name without extension: the sanitized key (at most <see cref="MaxFileNameStemLength"/> characters)
    /// plus '_' and the 8-hex-digit FNV-1a hash of the original key, so distinct keys never share a file.
    /// </summary>
    public static string BuildCarFileStem(string carKey)
    {
        string stem = SanitizeSegment(carKey);
        if (stem.Length > MaxFileNameStemLength)
        {
            int length = MaxFileNameStemLength;

            // Never cut a surrogate pair in half.
            if (char.IsHighSurrogate(stem[length - 1]))
            {
                length--;
            }

            stem = stem.Substring(0, length).TrimEnd('.', ' ');
        }

        return stem + ReplacementChar + Fnv1a(carKey ?? string.Empty).ToString(HashFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>32-bit FNV-1a over the UTF-8 bytes of <paramref name="text"/> (stable across processes and versions).</summary>
    public static uint Fnv1a(string text)
    {
        uint hash = FnvOffsetBasis;
        foreach (byte b in Encoding.UTF8.GetBytes(text ?? string.Empty))
        {
            hash ^= b;
            hash = unchecked(hash * FnvPrime);
        }

        return hash;
    }
}
