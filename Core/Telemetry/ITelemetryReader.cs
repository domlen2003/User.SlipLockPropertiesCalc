using System;
using System.Globalization;
using System.Text;

namespace DivebombLogistics.Core.Telemetry;

/// <summary>
/// Read access to SimHub properties by full path (e.g. <c>DataCorePlugin.GameRawData.Telemetry.YawRate</c>).
/// Production implementation wraps <c>PluginManager.GetPropertyValue</c>; tests use a dictionary fake.
/// Must return <c>null</c> for missing properties and must not throw.
/// </summary>
internal interface ITelemetryReader
{
    object GetValue(string propertyPath);
}

/// <summary>Typed, allocation-conscious helpers over <see cref="ITelemetryReader"/>.</summary>
internal static class TelemetryReaderExtensions
{
    /// <summary>True if the property exists (non-null value).</summary>
    public static bool Exists(this ITelemetryReader reader, string path) => path != null && reader.GetValue(path) != null;

    /// <summary>Reads a numeric (or bool) property. Returns false and NaN when missing or not convertible.</summary>
    public static bool TryGetDouble(this ITelemetryReader reader, string path, out double value)
    {
        if (path == null)
        {
            value = double.NaN;
            return false;
        }

        return TryConvertToDouble(reader.GetValue(path), out value);
    }

    /// <summary>Reads a numeric property or returns NaN.</summary>
    public static double GetDoubleOrNaN(this ITelemetryReader reader, string path)
    {
        TryGetDouble(reader, path, out double value);
        return value;
    }

    /// <summary>Reads a boolean-like property (bool or non-zero number). Returns false when missing.</summary>
    public static bool TryGetBool(this ITelemetryReader reader, string path, out bool value)
    {
        if (TryGetDouble(reader, path, out double d))
        {
            value = d != 0.0;
            return true;
        }

        value = false;
        return false;
    }

    /// <summary>
    /// Reads a text property. Handles <see cref="string"/>, NUL-terminated ASCII/UTF-8 <see cref="byte"/> arrays
    /// (rFactor/LMU fixed-size char buffers), enums and other objects (ToString). Returns null when missing.
    /// Allocates: call only on state changes (car/game change), never per frame.
    /// </summary>
    public static string GetText(this ITelemetryReader reader, string path)
    {
        object raw = path == null ? null : reader.GetValue(path);
        switch (raw)
        {
            case null:
                return null;
            case string s:
                return s.Trim();
            case byte[] bytes:
                int length = Array.IndexOf(bytes, (byte)0);
                if (length < 0)
                {
                    length = bytes.Length;
                }

                return Encoding.UTF8.GetString(bytes, 0, length).Trim();
            default:
                return Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim();
        }
    }

    /// <summary>Returns the first candidate path whose value is non-null, or null.</summary>
    public static string ResolveFirst(this ITelemetryReader reader, string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (reader.GetValue(candidates[i]) != null)
            {
                return candidates[i];
            }
        }

        return null;
    }

    /// <summary>Converts boxed SimHub values (double, float, integer types, bool, IConvertible) to double.</summary>
    public static bool TryConvertToDouble(object raw, out double value)
    {
        switch (raw)
        {
            case null:
                value = double.NaN;
                return false;
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case short s:
                value = s;
                return true;
            case byte b:
                value = b;
                return true;
            case sbyte sb:
                value = sb;
                return true;
            case uint ui:
                value = ui;
                return true;
            case ushort us:
                value = us;
                return true;
            case bool flag:
                value = flag ? 1.0 : 0.0;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            case IConvertible convertible:
                try
                {
                    value = convertible.ToDouble(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (FormatException)
                {
                }
                catch (InvalidCastException)
                {
                }
                catch (OverflowException)
                {
                }

                value = double.NaN;
                return false;
            default:
                value = double.NaN;
                return false;
        }
    }
}
