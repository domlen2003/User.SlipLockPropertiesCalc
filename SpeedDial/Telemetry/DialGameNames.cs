namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>SimHub game names (<c>FrameContext.GameName</c>) the dial telemetry factory distinguishes. Compared case-insensitively.</summary>
internal static class DialGameNames
{
    /// <summary>Le Mans Ultimate.</summary>
    public const string Lmu = "LMU";

    /// <summary>rFactor 2 (no native dial fields in SimHub's wrapper: generic reader).</summary>
    public const string RFactor2 = "RFactor2";

    /// <summary>iRacing.</summary>
    public const string IRacing = "IRacing";

    /// <summary>Assetto Corsa Competizione.</summary>
    public const string Acc = "AssettoCorsaCompetizione";

    /// <summary>Assetto Corsa EVO (lower-camel physics fields).</summary>
    public const string AcEvo = "AssettoCorsaEVO";

    /// <summary>Assetto Corsa Rally (ACC-style shared memory).</summary>
    public const string AcRally = "AssettoCorsaRally";

    /// <summary>Original Assetto Corsa (no TC/ABS levels in its shared memory: generic reader).</summary>
    public const string AssettoCorsa = "AssettoCorsa";
}
