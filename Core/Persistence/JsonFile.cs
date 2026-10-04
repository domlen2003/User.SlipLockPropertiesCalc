using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace DivebombLogistics.Core.Persistence;

/// <summary>Outcome of <see cref="JsonFile.TryRead{T}"/>.</summary>
internal enum JsonReadStatus
{
    /// <summary>The file was read and deserialized.</summary>
    Loaded = 0,

    /// <summary>The file does not exist.</summary>
    Missing,

    /// <summary>The file exists but is not valid JSON of the expected shape.</summary>
    Corrupt,

    /// <summary>The file could not be read (locked, access denied...). Its content is unknown, not bad.</summary>
    IoError,
}

/// <summary>
/// JSON file helpers shared by all plugin-owned files: one serializer configuration, crash-safe atomic
/// writes and quarantine of unreadable files.
/// <para>
/// Format: indented UTF-8 (no BOM), enums as strings, nulls written explicitly (so a cleared override is visible
/// in the file), invariant culture. NaN/∞ are written as the strings "NaN"/"Infinity" (valid JSON; read back
/// as doubles).
/// </para>
/// <para>
/// Reading is tolerant per member: a single unconvertible value (e.g. an enum name written by a newer plugin
/// version) keeps its default instead of discarding the whole file. Only syntactically broken files
/// (truncated, not JSON, wrong root type) count as corrupt.
/// </para>
/// </summary>
internal static class JsonFile
{
    /// <summary>Suffix of the temporary file an atomic write goes through.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>Infix of quarantined files: <c>&lt;name&gt;.bad-&lt;yyyyMMdd-HHmmss&gt;</c>.</summary>
    public const string QuarantineInfix = ".bad-";

    private const string QuarantineTimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Upper bound for quarantine name collisions within one second before giving up.</summary>
    private const int MaxQuarantineAttempts = 100;

    /// <summary>Plugin files are a few hundred KB at most; anything far larger is not ours.</summary>
    private const long MaxReadBytes = 16L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Serializes <paramref name="value"/> with the plugin's JSON conventions.</summary>
    public static string Serialize(object value)
    {
        var builder = new StringBuilder(4096);
        using (var writer = new StringWriter(builder, CultureInfo.InvariantCulture))
        {
            CreateSerializer().Serialize(writer, value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Deserializes a JSON object into <typeparamref name="T"/>.
    /// </summary>
    /// <param name="json">JSON text; the root must be an object.</param>
    /// <param name="skippedMembers">Number of member values that could not be converted and kept their default.</param>
    /// <returns>The deserialized instance (never null).</returns>
    /// <exception cref="JsonException">Syntax error, truncated input or a root that is not an object.</exception>
    public static T Deserialize<T>(string json, out int skippedMembers)
        where T : class
    {
        JObject root = ParseObject(json);
        return ToObject<T>(root, out skippedMembers);
    }

    /// <summary>
    /// Parses JSON text that must be an object. Date-like strings stay strings (no implicit DateTime conversion,
    /// which would otherwise reformat text fields such as car names).
    /// </summary>
    /// <exception cref="JsonException">Syntax error, truncated input or a root that is not an object.</exception>
    public static JObject ParseObject(string json)
    {
        if (json == null)
        {
            throw new JsonReaderException("No JSON content.");
        }

        using (var reader = new JsonTextReader(new StringReader(json)))
        {
            reader.DateParseHandling = DateParseHandling.None;
            reader.FloatParseHandling = FloatParseHandling.Double;
            reader.Culture = CultureInfo.InvariantCulture;

            JToken token = JToken.ReadFrom(reader);

            // JToken.ReadFrom stops after the first complete value; trailing garbage means a damaged file.
            if (reader.Read())
            {
                throw new JsonReaderException("Unexpected content after the JSON value.");
            }

            if (token is JObject root)
            {
                return root;
            }

            throw new JsonReaderException("The JSON root is not an object.");
        }
    }

    /// <summary>Converts a parsed object with the tolerant member error handling described on the class.</summary>
    public static T ToObject<T>(JObject root, out int skippedMembers)
        where T : class
    {
        int skipped = 0;
        JsonSerializer serializer = CreateSerializer();
        serializer.Error += (sender, args) =>
        {
            // The syntax was validated by ParseObject, so every error here is the conversion of a single value
            // (wrong type, unknown enum name...): recoverable, the member keeps its default. Handle it only at the
            // innermost object; the event is raised again for every enclosing object.
            if (args.CurrentObject != null && args.CurrentObject == args.ErrorContext.OriginalObject)
            {
                args.ErrorContext.Handled = true;
                skipped++;
            }
        };

        T value = root.ToObject<T>(serializer);
        skippedMembers = skipped;
        if (value == null)
        {
            throw new JsonSerializationException("The JSON object deserialized to null.");
        }

        return value;
    }

    /// <summary>
    /// Reads and deserializes a file. Never throws.
    /// </summary>
    /// <param name="path">File to read.</param>
    /// <param name="value">The deserialized value when <see cref="JsonReadStatus.Loaded"/>, otherwise null.</param>
    /// <param name="skippedMembers">Values that could not be converted (see <see cref="Deserialize{T}"/>).</param>
    /// <param name="error">Failure description for logging; null when loaded or missing.</param>
    public static JsonReadStatus TryRead<T>(string path, out T value, out int skippedMembers, out string error)
        where T : class
    {
        value = null;
        skippedMembers = 0;
        error = null;

        string json;
        try
        {
            if (!File.Exists(path))
            {
                return JsonReadStatus.Missing;
            }

            if (new FileInfo(path).Length > MaxReadBytes)
            {
                error = "file is larger than " + MaxReadBytes.ToString(CultureInfo.InvariantCulture) + " bytes";
                return JsonReadStatus.Corrupt;
            }

            json = File.ReadAllText(path, Utf8NoBom);
        }
        catch (Exception ex) when (IsIoException(ex))
        {
            error = ex.Message;
            return JsonReadStatus.IoError;
        }

        try
        {
            value = Deserialize<T>(json, out skippedMembers);
            return JsonReadStatus.Loaded;
        }
        catch (Exception ex)
        {
            // Mostly JsonException; anything else thrown while converting file content is equally a content problem.
            error = ex.Message;
            return JsonReadStatus.Corrupt;
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="contents"/> so that readers (and a crash at any
    /// point) see either the complete old or the complete new file, never a partial one: the text is written and
    /// flushed to <c>&lt;path&gt;.tmp</c>, then swapped in with <see cref="File.Replace(string, string, string)"/>
    /// (or moved when the target does not exist yet). Creates the directory if needed.
    /// On failure the temporary file is removed and the exception propagates; the old file is untouched.
    /// Callers must not write the same path from two threads at once (the temp file is shared).
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + TempSuffix;
        try
        {
            byte[] bytes = Utf8NoBom.GetBytes(contents ?? string.Empty);

            // FileMode.Create also overwrites a stale temp file left behind by a crash.
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);

                // Flush to disk before the swap; otherwise a power loss could leave a renamed but empty file.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                ReplaceExisting(tempPath, path);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Renames an unreadable file to <c>&lt;path&gt;.bad-&lt;timestamp&gt;</c> so it is kept for inspection but no
    /// longer loaded (and not overwritten by the next save). Never throws.
    /// </summary>
    /// <returns>The new file path, or null if the file could not be moved.</returns>
    public static string Quarantine(string path, DateTime timestamp)
    {
        try
        {
            string baseName = path + QuarantineInfix + timestamp.ToString(QuarantineTimestampFormat, CultureInfo.InvariantCulture);
            string target = baseName;
            for (int attempt = 1; File.Exists(target); attempt++)
            {
                if (attempt >= MaxQuarantineAttempts)
                {
                    return null;
                }

                target = baseName + "-" + attempt.ToString(CultureInfo.InvariantCulture);
            }

            File.Move(path, target);
            return target;
        }
        catch (Exception ex) when (IsIoException(ex))
        {
            return null;
        }
    }

    /// <summary>True for the exception types file system calls throw for environmental reasons.</summary>
    public static bool IsIoException(Exception ex) =>
        ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException
        || ex is NotSupportedException || ex is ArgumentException;

    private static JsonSerializer CreateSerializer()
    {
        var serializer = new JsonSerializer
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
            Culture = CultureInfo.InvariantCulture,
            DateParseHandling = DateParseHandling.None,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            FloatFormatHandling = FloatFormatHandling.String,
            MissingMemberHandling = MissingMemberHandling.Ignore,

            // Replace (not merge into) collections that field initializers already created; merging would
            // append file contents to the defaults.
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };
        serializer.Converters.Add(new StringEnumConverter());
        return serializer;
    }

    private static void ReplaceExisting(string tempPath, string path)
    {
        try
        {
            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            // File.Replace needs NTFS-like semantics; fall back to delete + move (not atomic, still no partial file).
            File.Delete(path);
            File.Move(tempPath, path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsIoException(ex))
        {
            // Best effort: a leftover temp file is overwritten by the next write.
        }
    }
}
