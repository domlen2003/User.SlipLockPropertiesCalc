using System;
using System.Collections.Generic;
using DivebombLogistics.Core;

namespace DivebombLogistics.Haptics.Balance.Sources;

/// <summary>Adapter family used for a SimHub game.</summary>
internal enum VehicleStateSourceKind
{
    /// <summary>No adapter (balance outputs stay 0).</summary>
    None = 0,
    IRacing,
    RFactor,
    AssettoCorsa,
}

/// <summary>Maps SimHub game names to vehicle-state adapters. Called on game change only (allocates).</summary>
internal static class VehicleStateSourceFactory
{
    /// <summary>SimHub game names (case-insensitive) per adapter.</summary>
    private static readonly Dictionary<string, VehicleStateSourceKind> KindsByGame =
        new Dictionary<string, VehicleStateSourceKind>(StringComparer.OrdinalIgnoreCase)
        {
            { "IRacing", VehicleStateSourceKind.IRacing },
            { "LMU", VehicleStateSourceKind.RFactor },
            { "RFactor2", VehicleStateSourceKind.RFactor },
            { "AssettoCorsaCompetizione", VehicleStateSourceKind.AssettoCorsa },
            { "AssettoCorsa", VehicleStateSourceKind.AssettoCorsa },
            { "AssettoCorsaEVO", VehicleStateSourceKind.AssettoCorsa },
            { "AssettoCorsaRally", VehicleStateSourceKind.AssettoCorsa },
        };

    /// <summary>Returns the adapter family for a SimHub game name (null/unknown → <see cref="VehicleStateSourceKind.None"/>).</summary>
    public static VehicleStateSourceKind GetKind(string gameName)
    {
        if (string.IsNullOrEmpty(gameName))
        {
            return VehicleStateSourceKind.None;
        }

        return KindsByGame.TryGetValue(gameName.Trim(), out VehicleStateSourceKind kind) ? kind : VehicleStateSourceKind.None;
    }

    /// <summary>Creates a fresh adapter for <paramref name="gameName"/>; unknown games get a <see cref="NullStateSource"/>.</summary>
    /// <param name="gameName">SimHub game name, e.g. "LMU" (case-insensitive).</param>
    /// <param name="log">Receives rare adapter notices (e.g. iRacing frame-check fallback). Optional.</param>
    public static IVehicleStateSource Create(string gameName, ILog log = null)
    {
        switch (GetKind(gameName))
        {
            case VehicleStateSourceKind.IRacing:
                return new IRacingStateSource(log);
            case VehicleStateSourceKind.RFactor:
                return new RFactorStateSource();
            case VehicleStateSourceKind.AssettoCorsa:
                return new AccStateSource();
            default:
                return new NullStateSource(gameName);
        }
    }
}
