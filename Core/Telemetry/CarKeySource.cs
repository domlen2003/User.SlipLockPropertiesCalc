namespace DivebombLogistics.Core.Telemetry;

/// <summary>Where <see cref="CarIdentity.CarKey"/> came from, in decreasing order of stability.</summary>
internal enum CarKeySource
{
    /// <summary>No usable identity (no car loaded, or only empty/"N/A" values).</summary>
    None = 0,

    /// <summary>Sim-native car model name (LMU <c>PlayerNativeTelemetry.mVehicleModel</c>): livery independent.</summary>
    NativeModel,

    /// <summary>SimHub <c>CarModel</c>. Livery specific on LMU, the stable car name elsewhere.</summary>
    CarModel,

    /// <summary>SimHub <c>CarId</c> (last resort; livery specific on LMU).</summary>
    CarId,
}
