using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace User.SlipLockPropertiesCalc.Balance.Recording;

/// <summary>
/// CSV format of balance recordings: one header row with the column names (VehicleState field names followed by
/// the recorded estimator outputs), then one row per tick. Invariant culture, comma separated, no quoting (no
/// value contains a comma). Doubles round-trip exactly; NaN is written as <c>NaN</c>, booleans as 0/1, enums by name.
/// </summary>
/// <remarks>
/// The reader maps columns by header name (case-insensitive), so files stay readable when columns are added,
/// removed or reordered in later versions. Missing numeric columns read as NaN.
/// </remarks>
internal static class BalanceCsv
{
    private const char Separator = ',';
    private const char CommentPrefix = '#';
    private const string True = "1";
    private const string False = "0";

    /// <summary>Column names in file order (declaration order of <see cref="Column"/>).</summary>
    private static readonly string[] ColumnNames = Enum.GetNames(typeof(Column));

    /// <summary>The header row.</summary>
    public static readonly string Header = string.Join(Separator.ToString(), ColumnNames);

    /// <summary>File columns. Names are written verbatim as the header; order defines the column order.</summary>
    private enum Column
    {
        Valid = 0,
        SimTime,
        WallTime,
        V,
        Vy,
        Theta,
        ThetaMax,
        R,
        Ay,
        Ax,
        Az,
        Throttle,
        Brake,
        Gear,
        Surface,
        OnPitRoad,
        OnTrack,
        InGarage,
        IsReplay,
        IsSpectating,
        IsPaused,
        Wetness,
        ContactCounter,
        HasSlipAngles,
        AlphaFront,
        AlphaRear,
        SlipAnglesInRadians,
        AlphaFL,
        AlphaFR,
        AlphaRL,
        AlphaRR,
        Understeer,
        Oversteer,
        YawRatio,
        YawRef,
        BodySlipDeg,
        Gate,
    }

    public static int ColumnCount => ColumnNames.Length;

    /// <summary>Writes the header row.</summary>
    public static void WriteHeader(TextWriter writer) => writer.WriteLine(Header);

    /// <summary>Writes one data row (allocates strings; writer thread / offline tools only).</summary>
    public static void WriteRow(TextWriter writer, ref BalanceRecord record)
    {
        for (int c = 0; c < ColumnNames.Length; c++)
        {
            if (c > 0)
            {
                writer.Write(Separator);
            }

            writer.Write(Format(ref record, (Column)c));
        }

        writer.WriteLine();
    }

    /// <summary>Reads a recording file lazily. Truncated rows (e.g. the last row after a crash) are skipped.</summary>
    /// <exception cref="InvalidDataException">The file has no recognizable header (thrown on enumeration).</exception>
    public static IEnumerable<BalanceRecord> Read(string path)
    {
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            foreach (BalanceRecord record in Read(reader))
            {
                yield return record;
            }
        }
    }

    /// <summary>Reads recording rows from <paramref name="reader"/> lazily (see <see cref="Read(string)"/>).</summary>
    public static IEnumerable<BalanceRecord> Read(TextReader reader)
    {
        int[] columnMap = null;
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == CommentPrefix)
            {
                continue;
            }

            string[] fields = line.Split(Separator);
            if (columnMap == null)
            {
                columnMap = MapHeader(fields);
                continue;
            }

            if (fields.Length < columnMap.Length)
            {
                continue;
            }

            BalanceRecord record = BalanceRecord.CreateUnknown();
            for (int i = 0; i < columnMap.Length; i++)
            {
                if (columnMap[i] >= 0)
                {
                    Parse(ref record, (Column)columnMap[i], fields[i].Trim());
                }
            }

            yield return record;
        }
    }

    /// <summary>Maps each file column to a known <see cref="Column"/> index, or -1 for unknown columns.</summary>
    private static int[] MapHeader(string[] names)
    {
        var map = new int[names.Length];
        bool anyKnown = false;
        for (int i = 0; i < names.Length; i++)
        {
            map[i] = Array.FindIndex(ColumnNames, n => string.Equals(n, names[i].Trim(), StringComparison.OrdinalIgnoreCase));
            anyKnown |= map[i] >= 0;
        }

        if (!anyKnown)
        {
            throw new InvalidDataException("Not a balance recording: the header row has no known column.");
        }

        return map;
    }

    private static string Format(ref BalanceRecord r, Column column)
    {
        switch (column)
        {
            case Column.Valid: return FormatBool(r.Valid);
            case Column.SimTime: return FormatDouble(r.SimTime);
            case Column.WallTime: return FormatDouble(r.WallTime);
            case Column.V: return FormatDouble(r.V);
            case Column.Vy: return FormatDouble(r.Vy);
            case Column.Theta: return FormatDouble(r.Theta);
            case Column.ThetaMax: return FormatDouble(r.ThetaMax);
            case Column.R: return FormatDouble(r.R);
            case Column.Ay: return FormatDouble(r.Ay);
            case Column.Ax: return FormatDouble(r.Ax);
            case Column.Az: return FormatDouble(r.Az);
            case Column.Throttle: return FormatDouble(r.Throttle);
            case Column.Brake: return FormatDouble(r.Brake);
            case Column.Gear: return r.Gear.ToString(CultureInfo.InvariantCulture);
            case Column.Surface: return r.Surface.ToString();
            case Column.OnPitRoad: return FormatBool(r.OnPitRoad);
            case Column.OnTrack: return FormatBool(r.OnTrack);
            case Column.InGarage: return FormatBool(r.InGarage);
            case Column.IsReplay: return FormatBool(r.IsReplay);
            case Column.IsSpectating: return FormatBool(r.IsSpectating);
            case Column.IsPaused: return FormatBool(r.IsPaused);
            case Column.Wetness: return FormatDouble(r.Wetness);
            case Column.ContactCounter: return r.ContactCounter.ToString(CultureInfo.InvariantCulture);
            case Column.HasSlipAngles: return FormatBool(r.HasSlipAngles);
            case Column.AlphaFront: return FormatDouble(r.AlphaFront);
            case Column.AlphaRear: return FormatDouble(r.AlphaRear);
            case Column.SlipAnglesInRadians: return FormatBool(r.SlipAnglesInRadians);
            case Column.AlphaFL: return FormatDouble(r.AlphaFL);
            case Column.AlphaFR: return FormatDouble(r.AlphaFR);
            case Column.AlphaRL: return FormatDouble(r.AlphaRL);
            case Column.AlphaRR: return FormatDouble(r.AlphaRR);
            case Column.Understeer: return FormatDouble(r.Understeer);
            case Column.Oversteer: return FormatDouble(r.Oversteer);
            case Column.YawRatio: return FormatDouble(r.YawRatio);
            case Column.YawRef: return FormatDouble(r.YawRef);
            case Column.BodySlipDeg: return FormatDouble(r.BodySlipDeg);
            case Column.Gate: return r.Gate.ToString();
            default: return string.Empty;
        }
    }

    private static void Parse(ref BalanceRecord r, Column column, string text)
    {
        switch (column)
        {
            case Column.Valid: r.Valid = ParseBool(text); break;
            case Column.SimTime: r.SimTime = ParseDouble(text); break;
            case Column.WallTime: r.WallTime = ParseDouble(text); break;
            case Column.V: r.V = ParseDouble(text); break;
            case Column.Vy: r.Vy = ParseDouble(text); break;
            case Column.Theta: r.Theta = ParseDouble(text); break;
            case Column.ThetaMax: r.ThetaMax = ParseDouble(text); break;
            case Column.R: r.R = ParseDouble(text); break;
            case Column.Ay: r.Ay = ParseDouble(text); break;
            case Column.Ax: r.Ax = ParseDouble(text); break;
            case Column.Az: r.Az = ParseDouble(text); break;
            case Column.Throttle: r.Throttle = ParseDouble(text); break;
            case Column.Brake: r.Brake = ParseDouble(text); break;
            case Column.Gear: r.Gear = ParseInt(text); break;
            case Column.Surface: r.Surface = ParseEnum<SurfaceKind>(text); break;
            case Column.OnPitRoad: r.OnPitRoad = ParseBool(text); break;
            case Column.OnTrack: r.OnTrack = ParseBool(text); break;
            case Column.InGarage: r.InGarage = ParseBool(text); break;
            case Column.IsReplay: r.IsReplay = ParseBool(text); break;
            case Column.IsSpectating: r.IsSpectating = ParseBool(text); break;
            case Column.IsPaused: r.IsPaused = ParseBool(text); break;
            case Column.Wetness: r.Wetness = ParseDouble(text); break;
            case Column.ContactCounter: r.ContactCounter = ParseInt(text); break;
            case Column.HasSlipAngles: r.HasSlipAngles = ParseBool(text); break;
            case Column.AlphaFront: r.AlphaFront = ParseDouble(text); break;
            case Column.AlphaRear: r.AlphaRear = ParseDouble(text); break;
            case Column.SlipAnglesInRadians: r.SlipAnglesInRadians = ParseBool(text); break;
            case Column.AlphaFL: r.AlphaFL = ParseDouble(text); break;
            case Column.AlphaFR: r.AlphaFR = ParseDouble(text); break;
            case Column.AlphaRL: r.AlphaRL = ParseDouble(text); break;
            case Column.AlphaRR: r.AlphaRR = ParseDouble(text); break;
            case Column.Understeer: r.Understeer = ParseDouble(text); break;
            case Column.Oversteer: r.Oversteer = ParseDouble(text); break;
            case Column.YawRatio: r.YawRatio = ParseDouble(text); break;
            case Column.YawRef: r.YawRef = ParseDouble(text); break;
            case Column.BodySlipDeg: r.BodySlipDeg = ParseDouble(text); break;
            case Column.Gate: r.Gate = ParseEnum<BalanceGate>(text); break;
        }
    }

    private static string FormatBool(bool value) => value ? True : False;

    /// <summary>
    /// Shortest round-trip text. .NET Framework's "R" format is known to lose the last bit for a few values on
    /// 64-bit, so the result is verified and "G17" (always exact) is used when needed.
    /// </summary>
    private static string FormatDouble(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        if (double.IsNaN(value) || ParseDouble(text).Equals(value))
        {
            return text;
        }

        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static bool ParseBool(string text) =>
        text == True || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);

    private static TEnum ParseEnum<TEnum>(string text)
        where TEnum : struct =>
        Enum.TryParse(text, ignoreCase: true, out TEnum value) ? value : default;
}
