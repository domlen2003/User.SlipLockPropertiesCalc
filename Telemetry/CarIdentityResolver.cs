using System;

namespace User.SlipLockPropertiesCalc.Telemetry;

/// <summary>
/// Derives a stable per-car key (DESIGN 5.4). SimHub's <c>CarId</c>/<c>CarModel</c> are livery specific on LMU
/// ("GT3_Iron Lynx 2026_61", "296GT3 Custom Team 2025"), which would give every livery its own sensitivities and
/// learned model, so rFactor-engine sims prefer the native model name (<c>PlayerNativeTelemetry.mVehicleModel</c>,
/// LMU only). Everything else uses <c>CarModel</c>, then <c>CarId</c>.
/// <para>Allocates (text decoding, trimming): call only when the car id/model changes or on a bounded retry.</para>
/// </summary>
internal static class CarIdentityResolver
{
    /// <summary>SimHub's placeholder for "no value".</summary>
    public const string NotAvailable = "N/A";

    /// <summary>LMU <c>IP_VehicleClass</c> member meaning "no class".</summary>
    private const string UnknownVehicleClass = "Unknown";

    private static readonly string[] NativeModelGames = { "LMU", "RFactor2" };

    /// <summary>Resolves the identity of the player's car from the current frame.</summary>
    public static CarIdentity Resolve(FrameContext ctx, ITelemetryReader reader)
    {
        string simKey = Clean(ctx.GameName);
        bool nativeExpected = IsNativeModelGame(simKey);

        string carClass = Clean(ctx.CarClass);
        string key = null;
        CarKeySource source = CarKeySource.None;

        if (nativeExpected)
        {
            key = Clean(reader.GetText(PropertyPaths.LmuVehicleModel));
            if (key.Length > 0)
            {
                source = CarKeySource.NativeModel;
            }

            string nativeClass = Clean(reader.GetText(PropertyPaths.LmuVehicleClass));
            if (IsClassName(nativeClass))
            {
                carClass = nativeClass;
            }
        }

        if (source == CarKeySource.None)
        {
            key = Clean(ctx.CarModel);
            source = key.Length > 0 ? CarKeySource.CarModel : CarKeySource.None;
        }

        if (source == CarKeySource.None)
        {
            key = Clean(ctx.CarId);
            source = key.Length > 0 ? CarKeySource.CarId : CarKeySource.None;
        }

        if (source == CarKeySource.None)
        {
            return new CarIdentity(simKey, string.Empty, string.Empty, CarKeySource.None, nativeExpected);
        }

        return new CarIdentity(simKey, key, carClass, source, nativeExpected);
    }

    /// <summary>True for sims whose native telemetry carries a livery-independent model name.</summary>
    public static bool IsNativeModelGame(string gameName)
    {
        for (int i = 0; i < NativeModelGames.Length; i++)
        {
            if (string.Equals(gameName, NativeModelGames[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Trims and maps null/"N/A" to empty.</summary>
    private static string Clean(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        return string.Equals(trimmed, NotAvailable, StringComparison.OrdinalIgnoreCase) ? string.Empty : trimmed;
    }

    /// <summary>
    /// Accepts real class names only: an unconverted enum value (a bare number) or "Unknown" would make a worse class
    /// than SimHub's own <c>CarClass</c>.
    /// </summary>
    private static bool IsClassName(string value)
    {
        if (value.Length == 0 || string.Equals(value, UnknownVehicleClass, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return true;
            }
        }

        return false;
    }
}
