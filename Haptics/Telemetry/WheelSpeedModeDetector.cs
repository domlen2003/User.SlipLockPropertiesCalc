using System;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Haptics.Settings;

namespace DivebombLogistics.Haptics.Telemetry;

/// <summary>
/// v1 <c>GetBaseSlip</c> state machine: decides once per game whether the sim provides real per-wheel speeds
/// (then base slip = per-wheel speed vs vehicle speed, signed) or not (then base slip = the
/// <see cref="SlipSourceResolver"/> values), and persists the verdict in
/// <see cref="HapticsSettings.GameCapabilities"/><c>[game].WheelSpeedMode</c>.
/// <para>
/// States: <see cref="DetectionState.Loading"/> (first frame of a game: load the persisted verdict and the persisted
/// ABS/TC flags) → <see cref="DetectionState.Detecting"/> (drive dynamically; wheels differing by more than
/// <see cref="PerWheelDeltaThreshold"/> means per-wheel data, <see cref="MonoConfirmFrames"/> dynamic frames without
/// a difference means mono; no wheel speed at all means mono immediately) → <see cref="DetectionState.PerWheel"/> or
/// <see cref="DetectionState.Mono"/>.
/// </para>
/// Persistence only mutates the settings object and raises <see cref="SettingsChanged"/>; the shell saves.
/// Owned by the data thread; <see cref="ComputeBaseSlip"/> is allocation-free and exposes raw detection values (the
/// UI formats them).
/// </summary>
internal sealed class WheelSpeedModeDetector
{
    /// <summary>Largest wheel-speed difference (m/s) to wheel FL above which the data is per-wheel.</summary>
    public const double PerWheelDeltaThreshold = 0.05;

    /// <summary>Dynamic frames without a wheel-speed difference before deciding mono.</summary>
    public const int MonoConfirmFrames = 60;

    /// <summary>Detection only counts frames above this vehicle speed (m/s).</summary>
    public const double DetectMinSpeedMs = 5.0;

    /// <summary>... and while cornering above this |AccelerationSway| ...</summary>
    public const double DetectMinLateral = 0.3;

    /// <summary>... or braking above this percentage.</summary>
    public const double DetectMinBrake = 10.0;

    /// <summary>Per-wheel slip is forced to 0 below this vehicle speed (m/s) to avoid dividing by ~0.</summary>
    private const double PerWheelMinSpeedMs = 1.0;

    private const double SlipLimitPercent = 100.0;
    private const double Percent = 100.0;

    /// <summary>Seconds between wheel-speed path probes while in per-wheel mode without resolved paths.</summary>
    private const double WheelSpeedProbeIntervalSeconds = 0.5;

    private readonly HapticsSettings settings;
    private readonly SlipSourceResolver slipSource;
    private readonly CapabilityTracker capabilities;
    private readonly string[] wheelSpeedPaths = new string[Wheels.Count];
    private readonly double[] wheelSpeeds = new double[Wheels.Count];

    private string gameName = string.Empty;
    private bool wheelSpeedResolved;
    private double nextWheelSpeedProbeTime;
    private bool retestRequested;

    /// <param name="settings">Live settings; <see cref="HapticsSettings.GameCapabilities"/> is read and mutated.</param>
    /// <param name="slipSource">Slip source read in mono/detecting mode (probed by the shell).</param>
    /// <param name="capabilities">Receives the persisted ABS/TC flags when a game's verdict is loaded.</param>
    public WheelSpeedModeDetector(HapticsSettings settings, SlipSourceResolver slipSource, CapabilityTracker capabilities)
    {
        this.settings = settings;
        this.slipSource = slipSource;
        this.capabilities = capabilities;
        Reset(string.Empty);
    }

    /// <summary>Current state of the state machine.</summary>
    public DetectionState State { get; private set; }

    /// <summary>True while <see cref="State"/> is <see cref="DetectionState.Detecting"/>.</summary>
    public bool IsDetecting => State == DetectionState.Detecting;

    /// <summary>Signedness of the last <see cref="ComputeBaseSlip"/> output (positive = spin, negative = lock).</summary>
    public bool BaseSlipIsSigned { get; private set; }

    /// <summary>The source that produced the last base slip: per-wheel speed, or the resolver's source.</summary>
    public SlipSourceKind EffectiveSource => State == DetectionState.PerWheel ? SlipSourceKind.PerWheelSpeed : slipSource.Kind;

    /// <summary>The slip source could be read in the last frame (v1 "hasAnySlip"; not evaluated in per-wheel mode).</summary>
    public bool SlipSourceAvailable { get; private set; }

    /// <summary>Per-wheel speeds could be read in the last frame (not evaluated in mono mode).</summary>
    public bool WheelSpeedAvailable { get; private set; }

    /// <summary>Vehicle speed (m/s) of the last detecting frame.</summary>
    public double DetectSpeed { get; private set; }

    /// <summary>|AccelerationSway| of the last detecting frame.</summary>
    public double DetectLat { get; private set; }

    /// <summary>Brake (0..100) of the last detecting frame.</summary>
    public double DetectBrake { get; private set; }

    public bool DetectSpeedOk { get; private set; }

    public bool DetectCornerOk { get; private set; }

    public bool DetectBrakeOk { get; private set; }

    /// <summary>Dynamic frames counted without a wheel-speed difference (towards <see cref="MonoConfirmFrames"/>).</summary>
    public int DetectFrames { get; private set; }

    /// <summary>Largest wheel-speed difference of the last dynamic detecting frame (m/s).</summary>
    public double DetectMaxDelta { get; private set; }

    /// <summary>True after <see cref="HapticsSettings.GameCapabilities"/> was modified; the shell saves and clears it.</summary>
    public bool SettingsChanged { get; private set; }

    public void ClearSettingsChanged() => SettingsChanged = false;

    /// <summary>Game change (v1): back to Loading, detection counters and wheel-speed paths forgotten.</summary>
    public void Reset(string game)
    {
        gameName = game ?? string.Empty;
        State = DetectionState.Loading;
        retestRequested = false;
        ForgetWheelSpeed();
        ResetDetectionTelemetry();
        BaseSlipIsSigned = false;
        SlipSourceAvailable = false;
    }

    /// <summary>
    /// v1 "Retest": at the next <see cref="ComputeBaseSlip"/> the game's persisted entry is removed and detection
    /// restarts. The persisted ABS/TC flags go with the entry; the in-memory ever-active flags are kept (v1).
    /// </summary>
    public void RequestRetest() => retestRequested = true;

    /// <summary>
    /// Advances the state machine by one frame and writes the base slip of all wheels into <paramref name="baseSlip"/>
    /// (zeros when no data). Call after <see cref="SlipSourceResolver.Probe"/> and <see cref="CapabilityTracker.Update"/>.
    /// </summary>
    /// <param name="reader">Property access.</param>
    /// <param name="vehicleSpeedMs">Vehicle speed in m/s (SimHub <c>SpeedKmh / 3.6</c>).</param>
    /// <param name="sway">SimHub AccelerationSway.</param>
    /// <param name="brake">Brake 0..100.</param>
    /// <param name="wallTime">Monotonic wall-clock seconds (rate-limits wheel-speed probing).</param>
    /// <param name="baseSlip">Length-4 destination.</param>
    public void ComputeBaseSlip(ITelemetryReader reader, double vehicleSpeedMs, double sway, double brake, double wallTime, double[] baseSlip)
    {
        if (retestRequested)
        {
            StartRetest();
        }

        if (State == DetectionState.Loading)
        {
            LoadPersistedMode();
        }

        switch (State)
        {
            case DetectionState.Detecting:
                // At most one frame can pass without wheel speeds (it decides mono), so probing is not rate limited.
                WheelSpeedAvailable = ReadWheelSpeeds(reader, wallTime, rateLimited: false);
                Detect(vehicleSpeedMs, sway, brake);
                ReadSlipSource(reader, vehicleSpeedMs, baseSlip);
                break;

            case DetectionState.PerWheel:
                WheelSpeedAvailable = ReadWheelSpeeds(reader, wallTime, rateLimited: true);
                ComputePerWheelSlip(vehicleSpeedMs, baseSlip);
                BaseSlipIsSigned = true;
                break;

            default:
                // Mono: v1 also read wheel speeds here, but never used them.
                ReadSlipSource(reader, vehicleSpeedMs, baseSlip);
                break;
        }
    }

    private void StartRetest()
    {
        retestRequested = false;
        State = DetectionState.Detecting;
        ForgetWheelSpeed();
        ResetDetectionTelemetry();
        if (gameName.Length > 0 && settings.GameCapabilities.Remove(gameName))
        {
            SettingsChanged = true;
        }
    }

    /// <summary>v1 Loading: persisted verdict ("PerWheel"/"Mono", anything else re-detects) + ABS/TC flags.</summary>
    private void LoadPersistedMode()
    {
        if (gameName.Length > 0
            && settings.GameCapabilities.TryGetValue(gameName, out GameCapabilities persisted)
            && persisted != null)
        {
            State = persisted.WheelSpeedMode == GameCapabilities.ModePerWheel
                ? DetectionState.PerWheel
                : persisted.WheelSpeedMode == GameCapabilities.ModeMono ? DetectionState.Mono : DetectionState.Detecting;
            capabilities.LoadPersisted(persisted);
        }
        else
        {
            State = DetectionState.Detecting;
            DetectFrames = 0;
        }
    }

    /// <summary>One v1 detection step.</summary>
    private void Detect(double vehicleSpeedMs, double sway, double brake)
    {
        double lateral = Math.Abs(sway);
        DetectSpeed = vehicleSpeedMs;
        DetectLat = lateral;
        DetectBrake = brake;
        DetectSpeedOk = vehicleSpeedMs > DetectMinSpeedMs;
        DetectCornerOk = lateral > DetectMinLateral;
        DetectBrakeOk = brake > DetectMinBrake;
        bool dynamic = DetectSpeedOk && (DetectCornerOk || DetectBrakeOk);

        if (!WheelSpeedAvailable)
        {
            Decide(DetectionState.Mono, GameCapabilities.ModeMono);
            return;
        }

        if (!dynamic)
        {
            return;
        }

        double maxDelta = 0.0;
        for (int i = 1; i < Wheels.Count; i++)
        {
            double delta = Math.Abs(wheelSpeeds[i] - wheelSpeeds[Wheels.FrontLeft]);

            // Guarded so one non-finite sample cannot poison the comparison (v1's Math.Max would return NaN).
            if (delta > maxDelta)
            {
                maxDelta = delta;
            }
        }

        DetectMaxDelta = maxDelta;
        if (maxDelta > PerWheelDeltaThreshold)
        {
            Decide(DetectionState.PerWheel, GameCapabilities.ModePerWheel);
            return;
        }

        DetectFrames++;
        if (DetectFrames >= MonoConfirmFrames)
        {
            Decide(DetectionState.Mono, GameCapabilities.ModeMono);
        }
    }

    /// <summary>v1 <c>Save</c>: switch state and persist the verdict (skipped without a game name).</summary>
    private void Decide(DetectionState state, string mode)
    {
        State = state;
        if (gameName.Length == 0)
        {
            return;
        }

        if (!settings.GameCapabilities.TryGetValue(gameName, out GameCapabilities entry) || entry == null)
        {
            entry = new GameCapabilities();
            settings.GameCapabilities[gameName] = entry;
        }

        entry.WheelSpeedMode = mode;
        SettingsChanged = true;
    }

    /// <summary>Mono/detecting base slip: the resolver's values, zeros when unavailable (v1).</summary>
    private void ReadSlipSource(ITelemetryReader reader, double vehicleSpeedMs, double[] baseSlip)
    {
        SlipSourceAvailable = slipSource.TryRead(reader, vehicleSpeedMs, baseSlip);
        if (!SlipSourceAvailable)
        {
            Array.Clear(baseSlip, 0, Wheels.Count);
        }

        BaseSlipIsSigned = slipSource.IsSigned;
    }

    /// <summary>Per-wheel base slip: (wheel speed - vehicle speed) / vehicle speed in percent, clamped to ±100 (v1).</summary>
    private void ComputePerWheelSlip(double vehicleSpeedMs, double[] baseSlip)
    {
        if (!WheelSpeedAvailable || !(vehicleSpeedMs > PerWheelMinSpeedMs))
        {
            Array.Clear(baseSlip, 0, Wheels.Count);
            return;
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            double slip = (wheelSpeeds[i] - vehicleSpeedMs) / vehicleSpeedMs * Percent;
            baseSlip[i] = MathUtil.IsFinite(slip) ? MathUtil.Clamp(slip, -SlipLimitPercent, SlipLimitPercent) : 0.0;
        }
    }

    /// <summary>v1 <c>TryWS</c>: resolve the candidate paths once (all wheels required), then read all four.</summary>
    private bool ReadWheelSpeeds(ITelemetryReader reader, double wallTime, bool rateLimited)
    {
        if (!wheelSpeedResolved)
        {
            if (rateLimited && wallTime < nextWheelSpeedProbeTime)
            {
                return false;
            }

            nextWheelSpeedProbeTime = wallTime + WheelSpeedProbeIntervalSeconds;
            if (!ResolveWheelSpeedPaths(reader))
            {
                return false;
            }
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            if (!reader.TryGetDouble(wheelSpeedPaths[i], out double speed))
            {
                return false;
            }

            wheelSpeeds[i] = speed;
        }

        return true;
    }

    private bool ResolveWheelSpeedPaths(ITelemetryReader reader)
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            string path = reader.ResolveFirst(PropertyPaths.WheelSpeedCandidates[i]);
            if (path == null)
            {
                return false;
            }

            wheelSpeedPaths[i] = path;
        }

        wheelSpeedResolved = true;
        return true;
    }

    private void ForgetWheelSpeed()
    {
        wheelSpeedResolved = false;
        nextWheelSpeedProbeTime = double.NegativeInfinity;
        WheelSpeedAvailable = false;
        for (int i = 0; i < Wheels.Count; i++)
        {
            wheelSpeedPaths[i] = null;
            wheelSpeeds[i] = 0.0;
        }
    }

    private void ResetDetectionTelemetry()
    {
        DetectFrames = 0;
        DetectMaxDelta = 0.0;
        DetectSpeed = 0.0;
        DetectLat = 0.0;
        DetectBrake = 0.0;
        DetectSpeedOk = false;
        DetectCornerOk = false;
        DetectBrakeOk = false;
    }
}
