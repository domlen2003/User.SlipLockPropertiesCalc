namespace User.SlipLockPropertiesCalc.Core;

/// <summary>
/// Wheel indexing shared by every per-wheel array in the plugin.
/// Order is fixed: FL, FR, RL, RR (matches exported property names and SimHub's 01..04 raw suffixes).
/// </summary>
internal static class Wheels
{
    public const int Count = 4;

    public const int FrontLeft = 0;
    public const int FrontRight = 1;
    public const int RearLeft = 2;
    public const int RearRight = 3;

    /// <summary>Names used in exported property paths, e.g. <c>SlipLock.Slip.FrontLeft</c>.</summary>
    public static readonly string[] Names = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };

    /// <summary>Short labels for UI display.</summary>
    public static readonly string[] ShortNames = { "FL", "FR", "RL", "RR" };

    /// <summary>SimHub raw-data array suffixes (1-based, two digits), e.g. <c>mWheels01</c>.</summary>
    public static readonly string[] RawSuffixes = { "01", "02", "03", "04" };

    /// <summary>-1 for left wheels, +1 for right wheels.</summary>
    public static readonly int[] LateralSign = { -1, 1, -1, 1 };

    /// <summary>+1 for front wheels, -1 for rear wheels.</summary>
    public static readonly int[] LongitudinalSign = { 1, 1, -1, -1 };
}
