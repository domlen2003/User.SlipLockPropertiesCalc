using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Haptics.Balance.Sources;

/// <summary>
/// Source for sims without a balance adapter: every sample is invalid, so the estimator keeps its outputs at 0.
/// Still fills the common flags so diagnostics and recordings show consistent pedal/state data.
/// </summary>
internal sealed class NullStateSource : IVehicleStateSource
{
    private readonly string gameName;

    /// <param name="gameName">SimHub game name, only used for the diagnostics text.</param>
    public NullStateSource(string gameName = null)
    {
        this.gameName = string.IsNullOrEmpty(gameName) ? "this game" : gameName;
    }

    public string Name => "Unsupported";

    public bool IsSupported => false;

    public bool Read(FrameContext ctx, ITelemetryReader reader, VehicleState state)
    {
        SourceCommon.BeginRead(ctx, state);
        state.Valid = false;
        return false;
    }

    public void Reset()
    {
    }

    public string DescribeResolution() => "Understeer/oversteer detection is not supported for " + gameName + ".";
}
