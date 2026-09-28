using System;
using System.Text;
using User.SlipLockPropertiesCalc.Core;
using User.SlipLockPropertiesCalc.Telemetry;

namespace User.SlipLockPropertiesCalc.Balance.Sources;

/// <summary>
/// Assetto Corsa family adapter (ACC, AC, AC Rally, AC EVO) over the <c>Physics</c> shared-memory page.
/// AC EVO uses lower-camel field names, so every field has a PascalCase and a camelCase candidate.
/// ACC/AC Rally expose per-wheel slip angles (unit not verified: the estimator normalizes them by the learned peak);
/// original AC has none and runs on the model path only.
/// </summary>
internal sealed class AccStateSource : IVehicleStateSource
{
    private const string Prefix = SourceCommon.RawDataPrefix + "Physics.";

    /// <summary>
    /// SteerAngle is normalized -1..1 without a reported steering range; 270° half-lock (540° lock-to-lock) is a
    /// typical GT value. Only the default G depends on it, and learning corrects G per car.
    /// </summary>
    private const double AssumedHalfLockRad = 270.0 * MathUtil.DegToRad;

    // AC arrays are exposed with 1-based two-digit suffixes: index 01 = x, 02 = y, 03 = z.
    private const string AxisX = "01";
    private const string AxisY = "02";
    private const string AxisZ = "03";

    private readonly SourceField steerAngle = new SourceField("Theta", Prefix + "SteerAngle", Prefix + "steerAngle");
    private readonly SourceField localVelocityX = Vector("Vy", "LocalVelocity", "localVelocity", AxisX);
    private readonly SourceField localVelocityZ = Vector("V", "LocalVelocity", "localVelocity", AxisZ);
    private readonly SourceField angularVelocityY = Vector("R", "LocalAngularVelocity", "localAngularVel", AxisY);
    private readonly SourceField accGX = Vector("Ay", "AccG", "accG", AxisX);
    private readonly SourceField accGY = Vector("Az", "AccG", "accG", AxisY);
    private readonly SourceField accGZ = Vector("Ax", "AccG", "accG", AxisZ);
    private readonly SourceField[] slipAngle = SourceCommon.PerWheel("SlipAngle", Prefix + "slipAngle", string.Empty);
    private readonly SourceField[] allFields;

    private double nextProbeWallTime = double.NegativeInfinity;

    public AccStateSource()
    {
        allFields = new[]
        {
            steerAngle, localVelocityX, localVelocityZ, angularVelocityY, accGX, accGY, accGZ,
            slipAngle[Wheels.FrontLeft], slipAngle[Wheels.FrontRight], slipAngle[Wheels.RearLeft], slipAngle[Wheels.RearRight],
        };
    }

    public string Name => "ACC/AC";

    public bool IsSupported => true;

    public bool Read(FrameContext ctx, ITelemetryReader reader, VehicleState state)
    {
        SourceCommon.BeginRead(ctx, state);
        bool probe = SourceCommon.ProbeDue(ctx.WallTime, ref nextProbeWallTime);

        // No sim clock in the physics page: SimTime stays NaN and the estimator uses wall time.
        state.V = localVelocityZ.Read(reader, probe);
        state.Vy = localVelocityX.Read(reader, probe);
        state.R = angularVelocityY.Read(reader, probe);
        state.Ay = accGX.Read(reader, probe) * MathUtil.Gravity;
        state.Ax = accGZ.Read(reader, probe) * MathUtil.Gravity;
        state.Az = accGY.Read(reader, probe) * MathUtil.Gravity;

        state.ThetaMax = AssumedHalfLockRad;
        state.Theta = steerAngle.Read(reader, probe) * AssumedHalfLockRad;

        state.OnTrack = !ctx.IsInPit;
        state.Surface = SurfaceKind.Unknown;
        ReadSlipAngles(reader, probe, state);

        return SourceCommon.FinishRead(state);
    }

    public void Reset()
    {
        SourceCommon.ResetAll(allFields);
        nextProbeWallTime = double.NegativeInfinity;
    }

    public string DescribeResolution()
    {
        var builder = new StringBuilder();
        builder.AppendLine("ACC/AC (DataCorePlugin.GameRawData.Physics)");
        SourceCommon.DescribeAll(builder, allFields);
        return builder.ToString();
    }

    private static SourceField Vector(string label, string pascalName, string camelName, string axis) =>
        new SourceField(label, Prefix + pascalName + axis, Prefix + camelName + axis);

    private void ReadSlipAngles(ITelemetryReader reader, bool probe, VehicleState state)
    {
        bool all = true;
        for (int i = 0; i < Wheels.Count; i++)
        {
            double alpha = slipAngle[i].Read(reader, probe);
            state.Alpha[i] = alpha;
            all &= MathUtil.IsFinite(alpha);
        }

        if (!all)
        {
            return;
        }

        state.HasSlipAngles = true;
        state.SlipAnglesInRadians = false;
        state.AlphaFront = (Math.Abs(state.Alpha[Wheels.FrontLeft]) + Math.Abs(state.Alpha[Wheels.FrontRight])) * 0.5;
        state.AlphaRear = (Math.Abs(state.Alpha[Wheels.RearLeft]) + Math.Abs(state.Alpha[Wheels.RearRight])) * 0.5;
    }
}
