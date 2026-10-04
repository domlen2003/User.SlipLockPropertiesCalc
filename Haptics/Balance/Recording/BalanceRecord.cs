using DivebombLogistics.Core;

namespace DivebombLogistics.Haptics.Balance.Recording;

/// <summary>
/// One recorded tick: every <see cref="VehicleState"/> field plus the key estimator outputs.
/// A value type so the recorder's ring can be preallocated and filled without allocations.
/// </summary>
internal struct BalanceRecord
{
    // ---- VehicleState ----
    public bool Valid;
    public double SimTime;
    public double WallTime;
    public double V;
    public double Vy;
    public double Theta;
    public double ThetaMax;
    public double R;
    public double Ay;
    public double Ax;
    public double Az;
    public double Throttle;
    public double Brake;
    public int Gear;
    public SurfaceKind Surface;
    public bool OnPitRoad;
    public bool OnTrack;
    public bool InGarage;
    public bool IsReplay;
    public bool IsSpectating;
    public bool IsPaused;
    public double Wetness;
    public int ContactCounter;
    public bool HasSlipAngles;
    public double AlphaFront;
    public double AlphaRear;
    public bool SlipAnglesInRadians;
    public double AlphaFL;
    public double AlphaFR;
    public double AlphaRL;
    public double AlphaRR;

    // ---- Estimator outputs at the time of recording ----
    public double Understeer;
    public double Oversteer;

    /// <summary>rho = r / r_ref.</summary>
    public double YawRatio;

    /// <summary>r_ref (rad/s).</summary>
    public double YawRef;

    /// <summary>beta (deg).</summary>
    public double BodySlipDeg;

    public BalanceGate Gate;

    /// <summary>Copies the state and outputs of one tick (allocation-free). <paramref name="outputs"/> may be null.</summary>
    public void CopyFrom(VehicleState state, BalanceOutputs outputs)
    {
        Valid = state.Valid;
        SimTime = state.SimTime;
        WallTime = state.WallTime;
        V = state.V;
        Vy = state.Vy;
        Theta = state.Theta;
        ThetaMax = state.ThetaMax;
        R = state.R;
        Ay = state.Ay;
        Ax = state.Ax;
        Az = state.Az;
        Throttle = state.Throttle;
        Brake = state.Brake;
        Gear = state.Gear;
        Surface = state.Surface;
        OnPitRoad = state.OnPitRoad;
        OnTrack = state.OnTrack;
        InGarage = state.InGarage;
        IsReplay = state.IsReplay;
        IsSpectating = state.IsSpectating;
        IsPaused = state.IsPaused;
        Wetness = state.Wetness;
        ContactCounter = state.ContactCounter;
        HasSlipAngles = state.HasSlipAngles;
        AlphaFront = state.AlphaFront;
        AlphaRear = state.AlphaRear;
        SlipAnglesInRadians = state.SlipAnglesInRadians;
        AlphaFL = state.Alpha[Wheels.FrontLeft];
        AlphaFR = state.Alpha[Wheels.FrontRight];
        AlphaRL = state.Alpha[Wheels.RearLeft];
        AlphaRR = state.Alpha[Wheels.RearRight];

        if (outputs == null)
        {
            ClearOutputs();
            return;
        }

        Understeer = outputs.Understeer;
        Oversteer = outputs.Oversteer;
        YawRatio = outputs.YawRatio;
        YawRef = outputs.YawRef;
        BodySlipDeg = outputs.BodySlipDeg;
        Gate = outputs.Gate;
    }

    /// <summary>Writes the recorded vehicle state into <paramref name="target"/> (every field is overwritten).</summary>
    public void ToVehicleState(VehicleState target)
    {
        target.Valid = Valid;
        target.SimTime = SimTime;
        target.WallTime = WallTime;
        target.V = V;
        target.Vy = Vy;
        target.Theta = Theta;
        target.ThetaMax = ThetaMax;
        target.R = R;
        target.Ay = Ay;
        target.Ax = Ax;
        target.Az = Az;
        target.Throttle = Throttle;
        target.Brake = Brake;
        target.Gear = Gear;
        target.Surface = Surface;
        target.OnPitRoad = OnPitRoad;
        target.OnTrack = OnTrack;
        target.InGarage = InGarage;
        target.IsReplay = IsReplay;
        target.IsSpectating = IsSpectating;
        target.IsPaused = IsPaused;
        target.Wetness = Wetness;
        target.ContactCounter = ContactCounter;
        target.HasSlipAngles = HasSlipAngles;
        target.AlphaFront = AlphaFront;
        target.AlphaRear = AlphaRear;
        target.SlipAnglesInRadians = SlipAnglesInRadians;
        target.Alpha[Wheels.FrontLeft] = AlphaFL;
        target.Alpha[Wheels.FrontRight] = AlphaFR;
        target.Alpha[Wheels.RearLeft] = AlphaRL;
        target.Alpha[Wheels.RearRight] = AlphaRR;
    }

    /// <summary>A record with every numeric field unknown (NaN), used as the starting point when parsing.</summary>
    public static BalanceRecord CreateUnknown()
    {
        var record = new BalanceRecord
        {
            SimTime = double.NaN,
            WallTime = double.NaN,
            V = double.NaN,
            Vy = double.NaN,
            Theta = double.NaN,
            ThetaMax = double.NaN,
            R = double.NaN,
            Ay = double.NaN,
            Ax = double.NaN,
            Az = double.NaN,
            Wetness = double.NaN,
            AlphaFront = double.NaN,
            AlphaRear = double.NaN,
            AlphaFL = double.NaN,
            AlphaFR = double.NaN,
            AlphaRL = double.NaN,
            AlphaRR = double.NaN,
        };
        record.ClearOutputs();
        return record;
    }

    private void ClearOutputs()
    {
        Understeer = 0.0;
        Oversteer = 0.0;
        YawRatio = double.NaN;
        YawRef = double.NaN;
        BodySlipDeg = double.NaN;
        Gate = BalanceGate.NoData;
    }
}
