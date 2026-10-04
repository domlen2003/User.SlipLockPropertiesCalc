using System;
using System.Collections.Generic;
using System.Globalization;

namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// A named per-car setup preset: absolute target values for the channels it includes (no bindings). Part of
/// <see cref="SpeedDialCarData.Presets"/>. Plain JSON DTO (public fields); owned by the data thread.
/// </summary>
public sealed class DialPreset
{
    /// <summary>Longest preset or pair name; longer names are cut.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Format of <see cref="Id"/> (<see cref="Guid.ToString(string)"/>, 32 lowercase hex digits).</summary>
    public const string IdFormat = "N";

    /// <summary>Stable unique id (a GUID string) referenced by slots and the selection. Never shown to the user.</summary>
    public string Id = string.Empty;

    /// <summary>User-visible name.</summary>
    public string Name = string.Empty;

    /// <summary>Target values of the included channels, keyed by <see cref="DialChannels.Id"/>; a missing key = channel not included.</summary>
    public Dictionary<string, double> Values = ChannelValues.Create();

    /// <summary>A new preset with a fresh <see cref="Id"/> and no values.</summary>
    public static DialPreset Create(string name) => new DialPreset
    {
        Id = NewId(),
        Name = CleanName(name, "Preset"),
    };

    /// <summary>A fresh unique preset id.</summary>
    public static string NewId() => Guid.NewGuid().ToString(IdFormat, CultureInfo.InvariantCulture);

    /// <summary>Trims, cuts to <see cref="MaxNameLength"/> and replaces an empty name with <paramref name="fallback"/>.</summary>
    public static string CleanName(string name, string fallback)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            trimmed = (fallback ?? string.Empty).Trim();
        }

        return trimmed.Length > MaxNameLength ? trimmed.Substring(0, MaxNameLength).TrimEnd() : trimmed;
    }

    /// <summary>The target of <paramref name="channel"/>; false (NaN) when the preset does not include it. Allocation-free.</summary>
    public bool TryGetValue(DialChannel channel, out double value) => ChannelValues.TryGet(Values, channel, out value);

    /// <summary>True when the preset includes <paramref name="channel"/>.</summary>
    public bool Includes(DialChannel channel) => ChannelValues.TryGet(Values, channel, out _);

    /// <summary>Includes the channel with the sanitized value, or excludes it (null / non-finite).</summary>
    public void SetValue(DialChannel channel, double? value)
    {
        Values ??= ChannelValues.Create();
        ChannelValues.Set(Values, channel, value);
    }

    /// <summary>Number of included channels.</summary>
    public int IncludedCount() => ChannelValues.CountFinite(Values);

    /// <summary>
    /// Repairs a deserialized instance. An empty <see cref="Id"/> gets a fresh one (duplicates are resolved by
    /// <see cref="SpeedDialCarData.Normalize"/>); the name is cleaned ("Preset n" when empty, n = index + 1); values
    /// are normalized (<see cref="ChannelValues.Normalize"/>).
    /// </summary>
    /// <param name="index">Position in the preset list (for the fallback name).</param>
    public void Normalize(int index)
    {
        Id = (Id ?? string.Empty).Trim();
        if (Id.Length == 0)
        {
            Id = NewId();
        }

        Name = CleanName(Name, "Preset " + (index + 1).ToString(CultureInfo.InvariantCulture));
        Values = ChannelValues.Normalize(Values);
    }

    /// <summary>Independent copy (same <see cref="Id"/>).</summary>
    public DialPreset DeepCopy() => new DialPreset
    {
        Id = Id,
        Name = Name,
        Values = ChannelValues.Copy(Values),
    };
}
