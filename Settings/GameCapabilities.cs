namespace User.SlipLockPropertiesCalc.Settings;

/// <summary>
/// Persisted per-game capability detection results (v1 format; field names and values must not change).
/// </summary>
public sealed class GameCapabilities
{
    public const string ModeUnknown = "Unknown";
    public const string ModePerWheel = "PerWheel";
    public const string ModeMono = "Mono";
    public const string Available = "Available";

    /// <summary>"Unknown", "PerWheel" or "Mono".</summary>
    public string WheelSpeedMode = ModeUnknown;

    /// <summary>"Unknown" or "Available".</summary>
    public string ABSMode = ModeUnknown;

    /// <summary>"Unknown" or "Available".</summary>
    public string TCMode = ModeUnknown;
}
