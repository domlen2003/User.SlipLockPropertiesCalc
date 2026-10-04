using DivebombLogistics.Core.Telemetry;
using GameReaderCommon;

namespace DivebombLogistics.Integration;

/// <summary>
/// Copies SimHub's normalized <see cref="GameData"/> into the reusable <see cref="FrameContext"/> so the processing
/// modules never see SimHub types. Allocation-free: string fields keep SimHub's instances.
/// </summary>
internal static class FrameContextBuilder
{
    /// <summary>
    /// Fills <paramref name="ctx"/> for this frame. <see cref="FrameContext.GameRunning"/> is true only when the game
    /// runs and a current data frame exists; the per-car/motion fields are left untouched otherwise.
    /// </summary>
    /// <param name="ctx">Reused context.</param>
    /// <param name="data">SimHub game data (may be null).</param>
    /// <param name="wallTime">Monotonic wall-clock seconds for this frame.</param>
    public static void Fill(FrameContext ctx, GameData data, double wallTime)
    {
        ctx.WallTime = wallTime;
        if (data == null)
        {
            ctx.GameRunning = false;
            return;
        }

        StatusDataBase status = data.NewData;
        ctx.GameRunning = data.GameRunning && status != null;
        ctx.GameName = data.GameName ?? string.Empty;
        ctx.GamePaused = data.GamePaused;
        ctx.GameInMenu = data.GameInMenu;
        ctx.SessionId = data.SessionId;
        ctx.FrameTimeUtc = data.FrameTimeUTC;
        if (status == null)
        {
            ctx.Spectating = false;
            ctx.IsReplay = data.GameReplay;
            return;
        }

        // GameData.Spectating is obsolete in the SDK; the per-frame flag is authoritative.
        ctx.Spectating = status.Spectating;
        ctx.IsReplay = data.GameReplay || status.IsGameReplay;
        ctx.IsSessionRestart = status.IsSessionRestart;

        ctx.CarId = status.CarId ?? string.Empty;
        ctx.CarModel = status.CarModel ?? string.Empty;
        ctx.CarClass = status.CarClass ?? string.Empty;

        ctx.SpeedKmh = status.SpeedKmh;
        ctx.Throttle = status.Throttle;
        ctx.Brake = status.Brake;

        // v1 mapped missing accelerations to 0.
        ctx.Sway = status.AccelerationSway ?? 0.0;
        ctx.Surge = status.AccelerationSurge ?? 0.0;
        ctx.Heave = status.AccelerationHeave ?? 0.0;
        ctx.YawVelocity = status.OrientationYawVelocity;

        ctx.Gear = status.Gear ?? string.Empty;
        ctx.GearNumber = FrameContext.ParseGear(ctx.Gear);

        ctx.AbsActive = status.ABSActive > 0;
        ctx.TcActive = status.TCActive > 0;
        ctx.IsInPitLane = status.IsInPitLane != 0;
        ctx.IsInPit = status.IsInPit != 0;
    }
}
