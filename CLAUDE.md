# CLAUDE.md - SlipLock Properties Calc (v2)

Context for AI assistants. User and developer documentation: `PROJECT_KNOWLEDGE.md`. The code is the source of
truth; if this file disagrees with it, trust the code and fix this file.

## What the plugin does

A SimHub plugin (C#, .NET Framework 4.8, WPF, `LangVersion 10`, classic csproj without the .NET SDK) with two jobs:

1. **Slip / lock pipeline (v1, preserved).** Reads a base wheel-slip signal (the ShakeIT export, ACC native slip,
   rF2/LMU wheel rotation or per-wheel speeds), weights it by an estimated corner load, then applies envelopes,
   pedal blends and ABS/TC aggregation. The result is exported as per-wheel and mono haptic channels
   (`SlipLock.*`). With every sensitivity at 100 %, an unsigned source and no ShakeIT WheelLock, the output is
   bit-identical to v1.
2. **Balance (new in v2).** Understeer/oversteer detection exported as `Balance.Understeer` /
   `Balance.Oversteer` (0..1), plus tags and debug values. It compares the yaw rate a lagged bicycle model predicts
   from steering with the yaw rate the car actually delivers, adds countersteer, body-slip and spin detectors, and,
   where the sim reports tyre slip angles, a direct front/rear comparison. The vehicle model is learned per car.

Class `User.SlipLockPropertiesCalc.SlipLockPropertiesCalc`. SimHub publishes every property as
`SlipLockPropertiesCalc.<name>`, taking the prefix from the runtime class name.

## Layout and data flow

```
SlipLockPropertiesCalc.cs   SimHub shell (IPlugin, IDataPlugin, IWPFSettingsV2, ISlipLockHost): orchestration only
Integration/   SimHub glue: PluginManagerTelemetryReader, FrameContextBuilder, PropertyExporter, SimHubLog,
               DebugFileLog, PropertyDump
Core/          Wheels, MathUtil (Clamp/IsFinite for net48), ILog, LegacyEnvelope (v1 envelope)
Telemetry/     FrameContext, PropertyPaths, SlipSourceResolver, WheelSpeedModeDetector, CapabilityTracker,
               CarIdentityResolver
SlipLock/      SlipLockProcessor (v1 pipeline + changes A/B/C), ProxyLoad, MaxGTracker, GamePreset(s)
Balance/       BalanceEstimator, BalanceLearner, ParamResolver (+EffectiveParams), RecursiveLeastSquares,
               Histograms, YawLagEstimator, Filters, ClassPresets, BalanceTuning, SimCalibration, VehicleState
  Sources/     IRacingStateSource, RFactorStateSource (LMU/rF2), AccStateSource (ACC/AC/AC EVO/AC Rally),
               NullStateSource, VehicleStateSourceFactory, SourceField
  Recording/   BalanceRecorder (CSV ring buffer, background flush), BalanceCsv, BalanceRecord
Settings/      PluginSettings, GameCapabilities, CarProfile, CarProfileStore, SaveScheduler, SettingsWriter, JsonFile
Profiles/      ShakeItProfileGenerator (data export, haptic pedals, balance .siprofile)
UI/            SettingsControl.xaml + SettingsViewModel (MVVM), ViewModels/, Controls/ (WheelQuad, LevelMeter),
               Converters/, LiveSnapshot, ISlipLockHost
Tests/         console test runner; links every pure folder; Tests/Legacy/LegacyPipeline.cs = verbatim v1 oracle
```

"Pure" folders (Core, Telemetry, SlipLock, Balance, Settings, Profiles, plus `UI/LiveSnapshot.cs`) never reference
SimHub or WPF and are compiled into the Tests project too. Keep it that way.

Per frame (`DataUpdate`), each stage in its own try/catch; a failing stage zeroes its outputs:

1. Drain the UI command queue.
2. `FrameContextBuilder.Fill`. If the game is not running: on the first such frame, zero every output and reset the
   envelopes and filters, then return (v1 left stale values).
3. Game change: reset per-game state (v1 semantics), `GamePresets.Get`, `VehicleStateSourceFactory.Create`.
   Car change: resolve `CarIdentity`, save the old profile, load or create the new one, `estimator.LoadCar`.
   A new `SessionId` calls `estimator.Reset()`.
4. Slip/lock: `maxG.Update`, capabilities, `slipResolver.Probe` (rate-limited), `detector.ComputeBaseSlip`, ShakeIT
   WheelLock read, `processor.Process`.
5. Balance: `balanceSource.Read`, `estimator.Update`, `recorder.Record` (no-op unless recording).
6. Optional 1 Hz debug file log, `SaveScheduler.Tick`, UI snapshot (at most 20 Hz).

## Threading (important)

- **Data thread** (`DataUpdate`, 60 Hz or more) owns every processing object, the current `CarProfile` and the
  `SaveScheduler`. It must never block and never throw. Errors are logged on the first occurrence, then at most
  every 10 s with a count of suppressed errors, and are shown in the Diagnostics tab.
- **UI thread** never touches data-thread state directly:
  - Reads: `ISlipLockHost.CopySnapshot` copies a `LiveSnapshot` under a lock. The data thread fills it at up to
    20 Hz; the view model's 100 ms `DispatcherTimer` copies it while the page is visible.
  - Writes: numeric `PluginSettings` edits go through `ISlipLockHost.EditSettings` (queued `Action`, applied on the
    data thread). bool/enum settings may be written directly, followed by `NotifySettingsChanged()`. Per-car edits
    (sensitivities, overrides, class preset, learning lock, reset learning) go through host methods that enqueue
    onto `commands`. Export and import use `RunOnDataThread` (waits up to 3 s).
  - Reason: SimHub is a **32-bit process**. An 8-byte double written on one thread can be read torn on another.
- File IO is kept off the data thread. Settings: `SettingsWriter` deep-copies on the data thread and writes on the
  thread pool. Profiles: `CarProfileStore.SaveAsync` serializes on the data thread (a deliberate, rate-limited
  exception) and writes on the thread pool. `End()` drains the queue, then saves the profile and settings
  synchronously.
- Exports are registered once in `Init` via `PluginManager.AttachDelegate(name, GetType(), provider)`, in
  `PropertyExporter`. Never use `SetPropertyValue` per frame. Do not use the `IPluginExtensions.AttachDelegate`
  extension on an interface-typed receiver: it takes the prefix from the compile-time type. `Init` self-checks
  that `SlipLockPropertiesCalc.SlipLock.MaxSway` is readable.

## Exported properties (prefix `SlipLockPropertiesCalc.`)

**v1 names and value semantics are a compatibility contract**: user ShakeIT profiles and dashboards reference them.

- v1 (43): `SlipLock.{Slip|Lock|ABS|TC|SlipBlend|LockBlend|SlipTC|LockABS}.{FrontLeft|FrontRight|RearLeft|RearRight|Mono}`
  (0..100, rounded to 1 decimal like v1), plus `SlipLock.MaxSway`, `SlipLock.MaxSurge` and `SlipLock.MaxDecel`
  (start at 5, grow only, reset on a CarId change).
- Balance outputs: `Balance.Understeer` and `Balance.Oversteer` (0..1).
- Balance tags (bool): `Balance.PowerOversteer`, `LiftOrBrakeOversteer`, `EntryUndersteer`, `ExitUndersteer`,
  `Countersteer`, `Spin` and `Active`. `Balance.Confidence` is 0..1.
- Balance debug: `Balance.YawRate`, `YawRef` (rad/s), `YawRatio`, `BodySlipDeg`, `G` (1/m), `K` (s²/m²),
  `Theta0` (deg), `TauYaw` (s), `SamplesG` and `SamplesK` (long), `ParamSource` (source of G: `default`, `preset`,
  `learned`, `session` or `manual`) and `Path` (`none`, `model` or `direct`). NaN and infinity are exported as 0.

## Persistence

- Global settings: SimHub `ReadCommonSettings/SaveCommonSettings("GeneralSettings")` stores them in
  `<SimHub>\PluginsData\Common\SlipLockPropertiesCalc.GeneralSettings.json` (`PluginSettings`). Saved 2 s after the
  last change.
- **Compatibility rules for `PluginSettings`:** never rename or remove the v1 fields: `SlipThrottleBlend`,
  `TCThrottleBlend`, `LockBrakeBlend`, `ABSBrakeBlend`, `Slip/Lock/TC/ABSThreshold`, `GateSlipOnThrottle`,
  `GateLockOnBrake`, `{Slip|Lock|ABS|TC}{Attack|Release}Ms`, `SpeedWarningLevel` (unused SDK leftover, kept on
  purpose), and `GameCapabilities` (per game: `WheelSpeedMode` "Unknown/PerWheel/Mono", `ABSMode`/`TCMode`
  "Unknown/Available"). The settings key stays `"GeneralSettings"`. v2 adds `SchemaVersion`, `ShowDebugView`,
  `UseShakeItWheelLock` (default true), `DebugFileLog`, `Balance` (`BalanceTuning`) and `BalanceCalibration`
  (per-sim `SimCalibration`). `Normalize()` repairs whatever is loaded. Dictionaries are case-insensitive.
- Per-car profiles: `<SimHub>\PluginsData\SlipLockPropertiesCalc\Cars\<Sim>\<CarKey>_<fnv1a8>.json` (`CarProfile`:
  four sensitivities 10..500 % with 100 = v1, `Overrides`, `LearningLocked`, `Learned` = `BalanceLearnedState`).
  Writes are atomic. A corrupt file is renamed to `.bad-<timestamp>`. A file that exists but cannot be read makes
  that session's saves go to `<name>.unsaved.json`. Failed async writes are retried (250 ms, 1 s, 4 s, 15 s).
  Saved 2 s after a user edit, at most every 60 s while learning, on car change, and in `End()`.
- Car key (`CarIdentityResolver`): on LMU/rF2 the key is `PlayerNativeTelemetry.mVehicleModel`, because SimHub's
  CarId/CarModel are livery specific there. Elsewhere it is `CarModel`, then `CarId`. When LMU falls back to a
  livery key, resolution is retried once per second for up to 10 s; the provisional profile is never saved. Its
  state carries over to the native key if that key has no stored profile yet.
- Other files under `PluginsData\SlipLockPropertiesCalc\`: `Recordings\<sim>_<car>_<yyyyMMdd_HHmmss>.csv` and
  `property-dump-<timestamp>.txt`. Debug log: `<SimHub>\Logs\SlipLock_debug.log` (falls back to %TEMP%). Generated
  ShakeIT profiles: `Documents\SimHub\SlipLock_{DataExport|HapticPedals|Balance}.siprofile`.

## Slip/lock pipeline: deliberate v2 changes (`SlipLock/SlipLockProcessor.cs`)

- A: per-car Slip and Lock sensitivity multiply the raw slip first (a factor of 1.0 keeps v1's exact operation
  order).
- B: signed sources (rF2 rotation, per-wheel speed) always take lock from negative slip. v1 synthesized lock from
  positive slip for every source.
- C: lock = max(lock, ShakeIT `WheelLock` export) when the export exists and `UseShakeItWheelLock` is set.
- Game presets (`SlipLock/GamePresets.cs`) are code-defined, shown read-only, and looked up case-insensitively.
  v1 missed "AssettoCorsaEVO" and "BeamNgDrive", which fell back to Default.
- Edits must keep `Tests/SlipLockProcessorTests` (bit-equality against `Tests/Legacy/LegacyPipeline.cs`) green.

## Balance algorithm in short (`Balance/BalanceEstimator.cs`)

- Model: `r_ss = G·v·θeff / (1 + K·v²)` with `θeff = θ - θ0`, lagged by τ into `r_ref`. `ρ = r / r_ref`.
- Understeer (model) maps `(1-ρ)·sensitivity` from `UsOnset` to `UsFull`. Oversteer is the max of yaw excess
  `(ρ-1)`, countersteer (steering against the yaw, with `r_ref` also opposing `r`) and body slip beyond the learned
  envelope. |β| > 45° counts as a spin (OS = 1), and after `SpinTimeout` the outputs fade.
- Direct path (Auto mode, slip angles present, α_peak learned): front minus rear slip angle, normalized by the
  learned peak, so the unit does not matter. The sensitivity is the gain on the detector metric.
- Gates: hard gates (no data, frozen, replay, not on track, paused) skip the pipeline. Soft gates (pit lane,
  reverse, low speed ramp `VMin`..`VFull`, blanks after reset, contact or airborne, spin timeout) drive the outputs
  to 0. `Calibrating`: until `SimCalibration.SteeringSignVerified`, the model understeer, yaw excess and
  countersteer are held at 0; only body slip and spin reach the outputs.
- Shaping: hysteresis, speed ramp, attack/release envelope. Mutual exclusion: OS above 0.2 forces US to 0.
- Runtime sign calibration per sim (`SimCalibration`, persisted): forward sign (100 votes under throttle), steering
  sign (200 votes from clean cornering, plus a 600-sample monitor that can reopen it), and whether vertical
  acceleration includes gravity. "Reset learned model" and "Retest" call `ResetCalibration()`.
- Learner (`BalanceLearner.cs`): robust RLS for G/K (G after at least 50 samples, confidence n/300 × fit quality;
  K needs 800 samples and a 15 m/s speed spread), θ0 from straights (EMA, confidence n/300), τ from turn-in events
  (at least 10), ay_max as the p98 of |ay| (1500 samples), the β envelope per ay bin (p95, 200 samples per bin) and
  α_peak (p90, 300 samples). A session layer (G, θ0) can override the baseline and is never persisted.
  `ParamResolver.cs` precedence: Manual > Session > Learned (confidence ≥ 0.6) > class preset > default. Class
  presets and auto-detection are in `ClassPresets.cs`.
- Known deviations from the user's spec: no rally handbrake tag, no session-layer K, no wet ay_max adaptation;
  learning is disabled on kerbs, loose surfaces and wet surfaces.

## Sim adapters and raw paths (`Balance/Sources/`)

- iRacing `DataCorePlugin.GameRawData.Telemetry.`: `SessionTime, VelocityX, VelocityY, Speed, SteeringWheelAngle,
  SteeringWheelAngleMax, YawRate, LatAccel, LongAccel, VertAccel, OnPitRoad, IsInGarage, IsOnTrack|IsOnTrackCar,
  IsReplayPlaying`. A frame check falls back to `Speed` (β unavailable) if `VelocityX` is not car-frame. There are
  no slip angles, so iRacing runs on the model path only.
- LMU/rF2 `...CurrentPlayerTelemetry.`: `mElapsedTime, mLocalVel.{x,z}` (+z points rearward, so V = -z),
  `mLocalRot.y, mLocalAccel.{x,y,z}, mFilteredSteering` (Theta = -value × half-range),
  `mPhysicalSteeringWheelRange | mVisualSteeringWheelRange | 540°`, `mFilteredThrottle/Brake, mGear,
  mLastImpactET, mWheels0N.{mSurfaceType, mLateralPatchVel, mLongitudinalGroundVel}`. Scoring `...CurrentPlayer.`:
  `mInGarageStall, mInPits, mControl`. Also `...Data.mAvgPathWetness` (optional).
- ACC/AC family `...Physics.` (PascalCase, then camelCase for AC EVO): `SteerAngle` (-1..1 × assumed 270° half-lock),
  `LocalVelocity0N, LocalAngularVelocity0N`/`localAngularVel0N`, `AccG0N` (g), `slipAngle0N` (ACC, AC Rally).
  Arrays use 1-based two-digit suffixes (`01` = x).
- Slip sources (`Telemetry/PropertyPaths.cs`): `ShakeITBSV3Plugin.Export.WheelSlip.<Wheel>`, then `.proxyS.<Wheel>`,
  then `...Physics.WheelSlip0N`, then rF2 `mWheels0N.mRotation` (radius from `mStaticUndeflectedRadius`, cm). Lock:
  `ShakeITBSV3Plugin.Export.WheelLock.<Wheel>`. Capability flags come from `DataCorePlugin.GameData.ABSLevel` and
  `TCLevel`, which count as exported if they exist.
- Per-wheel speeds (`...Telemetry.LFspeed` etc.) do not exist live in iRacing, so detection settles on Mono.

## Build and test

```bash
MSB="/c/Users/domin/AppData/Local/Programs/Rider/tools/MSBuild/Current/Bin/amd64/MSBuild.exe"
cd "/c/Program Files (x86)/SimHub/PluginSdk/User.SlipLockPropertiesCalc"
"$MSB" User.SlipLockPropertiesCalc.csproj -p:Configuration=Release -p:DeployToSimHub=false -t:Rebuild -v:minimal
"$MSB" Tests/User.SlipLockPropertiesCalc.Tests.csproj -p:Configuration=Debug -v:minimal
Tests/bin/Debug/User.SlipLockPropertiesCalc.Tests.exe                  # all tests; exit code = failures
Tests/bin/Debug/User.SlipLockPropertiesCalc.Tests.exe --filter Balance # "Class.Method" substring
Tests/bin/Debug/User.SlipLockPropertiesCalc.Tests.exe --replay rec.csv # offline estimator replay summary
```

- `DeployToSimHub` defaults to **true** and copies the DLL and PDB into `%SIMHUB_INSTALL_PATH%`. Always pass
  `-p:DeployToSimHub=false`, including when building the `.sln`.
- Tests use the `[Test]` attribute (`Tests/TestFramework.cs`) and must be deterministic (seeded `Random`) and fast.
  A new file in a listed folder compiles automatically through the csproj wildcards.

## Hard rules

- **Never kill, close or restart SimHub.** Never write into `C:\Program Files (x86)\SimHub\` outside this repo.
  Deploying is the user's step.
- Do not rename the plugin class, the namespace, the `[PluginName("Slip Lock Properties Calc")]` attributes, the
  settings key, v1 settings fields, or any exported property name or its value semantics.
- **Zero allocations in the steady-state hot path** (`DataUpdate` and everything it calls per frame): no LINQ, no
  string building or formatting, no `new`, no per-frame closures, no exceptions for control flow, no file IO, no
  logging except rate-limited errors. Cache property path strings once. Allocations are fine only on game, car or
  session change.
- Never throw out of `DataUpdate`. Treat NaN and infinity as unknown.
- net48 gaps: use `MathUtil.Clamp` and `MathUtil.IsFinite`. Not available: `Span`, ranges or indices, `init`,
  `Dictionary.TryAdd`, `KeyValuePair` deconstruction, `string.Contains(char)`, `HashCode`,
  `Enum.GetValues<T>()`.
- Style: `internal sealed` by default, one type per file, namespaces mirror folders, named constants, XML docs on
  types and non-obvious members, comments explain why. English only.

## Known limitations and unverified assumptions

- rF2/LMU axis and steering sign conventions are best knowledge. The forward and steering signs are verified at
  runtime (auto-calibrated per sim), so the first corners in a new sim show "calibrating steering".
- ACC and AC Rally `slipAngle` units are unverified: the direct path normalizes by the learned peak and shows
  values in the sim's unit. AC's `SteerAngle` range is assumed to be 270° half-lock; G learning corrects for it.
  Original AC has no slip angles (model path only).
- iRacing: no surface type or wetness in SimHub's wrapper (surface Unknown), no slip angles, no per-wheel speeds.
  `SteeringWheelAngleMax` is treated as half-lock.
- rF2 `mAvgPathWetness` is optional and unverified. Wet learning exclusion otherwise relies on 3 or more wheels
  reporting a wet surface.
- Balance is supported for IRacing, LMU, RFactor2, AssettoCorsaCompetizione, AssettoCorsa, AssettoCorsaEVO and
  AssettoCorsaRally. Every other game gets `NullStateSource` (outputs 0).
- The slip/lock pipeline needs a slip source. For most games that is the ShakeIT data export profile.
- Exported doubles are read without locking, so a reader on another thread can rarely see a torn value for one
  frame. This is accepted because ShakeIT evaluates on the data thread.
