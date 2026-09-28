using System;

namespace User.SlipLockPropertiesCalc.Telemetry;

// The fields are assigned by Integration/FrameContextBuilder, which the SimHub-independent Tests project does not
// compile; without this the Tests build reports CS0649 ("never assigned") for the fields no test sets.
#pragma warning disable CS0649

/// <summary>
/// SimHub-normalized per-frame data, copied out of <c>GameReaderCommon.GameData</c> by the plugin shell so the
/// processing modules stay independent of SimHub assemblies (and unit-testable).
/// A single instance is reused every frame (no per-frame allocation); string fields keep SimHub's instances.
/// </summary>
internal sealed class FrameContext
{
    // ---- Session / game state ----
    public bool GameRunning;
    public string GameName = string.Empty;
    public bool GamePaused;
    public bool GameInMenu;
    public bool Spectating;
    public bool IsReplay;
    public Guid SessionId;
    public bool IsSessionRestart;

    /// <summary>Monotonic wall-clock seconds (Stopwatch based) at the start of this DataUpdate.</summary>
    public double WallTime;

    /// <summary>SimHub frame timestamp (UTC).</summary>
    public DateTime FrameTimeUtc;

    // ---- Car identity (SimHub normalized; LMU values are livery specific, see CarIdentityResolver) ----
    public string CarId = string.Empty;
    public string CarModel = string.Empty;
    public string CarClass = string.Empty;

    // ---- Driver inputs and motion (SimHub normalized) ----
    /// <summary>Vehicle speed in km/h.</summary>
    public double SpeedKmh;

    /// <summary>Throttle 0..100.</summary>
    public double Throttle;

    /// <summary>Brake 0..100.</summary>
    public double Brake;

    /// <summary><c>AccelerationSway</c> (null mapped to 0 like the legacy code). SimHub units (m/s² for LMU).</summary>
    public double Sway;

    /// <summary><c>AccelerationSurge</c> (null mapped to 0). Positive under braking in SimHub's convention.</summary>
    public double Surge;

    /// <summary><c>AccelerationHeave</c> (null mapped to 0).</summary>
    public double Heave;

    /// <summary><c>OrientationYawVelocity</c> as reported by SimHub.</summary>
    public double YawVelocity;

    /// <summary>SimHub gear string ("R", "N", "1", ...).</summary>
    public string Gear = string.Empty;

    /// <summary>Parsed gear: -1 reverse, 0 neutral/unknown, n forward.</summary>
    public int GearNumber;

    public bool AbsActive;
    public bool TcActive;
    public bool IsInPitLane;
    public bool IsInPit;

    /// <summary>Parses a SimHub gear string without allocating.</summary>
    public static int ParseGear(string gear)
    {
        if (string.IsNullOrEmpty(gear))
        {
            return 0;
        }

        char first = gear[0];
        if (first == 'R' || first == 'r' || first == '-')
        {
            return -1;
        }

        int value = 0;
        bool any = false;
        for (int i = 0; i < gear.Length; i++)
        {
            char c = gear[i];
            if (c < '0' || c > '9')
            {
                break;
            }

            value = (value * 10) + (c - '0');
            any = true;
        }

        return any ? value : 0;
    }
}
