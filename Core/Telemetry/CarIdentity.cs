using System;

namespace DivebombLogistics.Core.Telemetry;

/// <summary>
/// Immutable identity of the player's car, used to select the per-car profile (sensitivities, learned model).
/// Two identities denote the same profile when <see cref="SimKey"/> and <see cref="CarKey"/> match (ordinal).
/// </summary>
internal sealed class CarIdentity
{
    /// <summary>Identity used while no car is known.</summary>
    public static readonly CarIdentity None = new CarIdentity(string.Empty, string.Empty, string.Empty, CarKeySource.None, false);

    public CarIdentity(string simKey, string carKey, string carClass, CarKeySource keySource, bool nativeModelExpected)
    {
        SimKey = simKey ?? string.Empty;
        CarKey = carKey ?? string.Empty;
        DisplayName = CarKey;
        CarClass = carClass ?? string.Empty;
        KeySource = keySource;
        NativeModelExpected = nativeModelExpected;
    }

    /// <summary>SimHub game name, e.g. "LMU".</summary>
    public string SimKey { get; }

    /// <summary>Stable car key, e.g. "Ligier JS P325" (empty when <see cref="HasCar"/> is false).</summary>
    public string CarKey { get; }

    /// <summary>Name shown in the UI (currently the car key).</summary>
    public string DisplayName { get; }

    /// <summary>Car class, e.g. "LMP3" (may be empty).</summary>
    public string CarClass { get; }

    public CarKeySource KeySource { get; }

    /// <summary>True for sims that normally provide a native model name (LMU, rFactor 2).</summary>
    public bool NativeModelExpected { get; }

    public bool HasCar => KeySource != CarKeySource.None;

    /// <summary>
    /// True when the sim normally provides a native model name but the key came from a (livery-specific) fallback,
    /// e.g. during LMU's first frames. The shell should resolve again later (bounded retries).
    /// </summary>
    public bool ShouldRetry => NativeModelExpected && (KeySource == CarKeySource.CarModel || KeySource == CarKeySource.CarId);

    /// <summary>True when both identities select the same car profile.</summary>
    public bool SameCarAs(CarIdentity other) =>
        other != null
        && string.Equals(SimKey, other.SimKey, StringComparison.Ordinal)
        && string.Equals(CarKey, other.CarKey, StringComparison.Ordinal);

    public override string ToString() => HasCar ? SimKey + "/" + CarKey + " (" + KeySource + ")" : SimKey + "/<no car>";
}
