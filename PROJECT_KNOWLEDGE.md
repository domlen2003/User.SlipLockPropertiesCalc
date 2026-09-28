# SlipLock Properties Calc v2: user and developer guide

A SimHub plugin that turns sim telemetry into haptic channels:

- **Slip / lock / ABS / TC** per wheel and mono (`SlipLock.*`), for haptic pedals, bass shakers and motion
  devices. This is the v1 pipeline. With default sensitivities it produces the same values as v1.
- **Understeer / oversteer** (`Balance.Understeer`, `Balance.Oversteer`, 0..1), new in v2. The vehicle model it
  uses is learned automatically for every car.

SimHub publishes every property with the prefix `SlipLockPropertiesCalc.`, for example
`SlipLockPropertiesCalc.SlipLock.SlipTC.Mono`.

Balance detection is available for iRacing, Le Mans Ultimate, rFactor 2, Assetto Corsa Competizione, Assetto
Corsa, Assetto Corsa EVO and Assetto Corsa Rally. The slip/lock channels work in any game that provides a slip
source (see "Slip sources" below).

---

## 1. Quick start

### 1.1 Build

Requirements: MSBuild with .NET Framework 4.8 targeting (Visual Studio 2022, Rider or Build Tools) and the
environment variable `SIMHUB_INSTALL_PATH` pointing at the SimHub folder with a trailing backslash (for example
`C:\Program Files (x86)\SimHub\`). The project references SimHub's own DLLs from that folder.

```bash
MSB="/c/Users/domin/AppData/Local/Programs/Rider/tools/MSBuild/Current/Bin/amd64/MSBuild.exe"   # any MSBuild works
"$MSB" User.SlipLockPropertiesCalc.csproj -p:Configuration=Release -p:DeployToSimHub=false -t:Rebuild -v:minimal
```

The output is `bin\Release\User.SlipLockPropertiesCalc.dll` and `.pdb`.

Without `-p:DeployToSimHub=false`, the build copies the DLL and PDB into `%SIMHUB_INSTALL_PATH%` automatically.
That copy fails with a warning, not an error, while SimHub is running, because SimHub locks the DLL.

### 1.2 Install

1. **Close SimHub completely.** It loads plugins only at start-up and keeps the DLL locked.
2. Copy `User.SlipLockPropertiesCalc.dll` and `User.SlipLockPropertiesCalc.pdb` into the SimHub folder.
3. Start SimHub. If SimHub asks whether to enable the new plugin, confirm. Otherwise enable
   "Slip Lock Properties Calc" in SimHub's plugin settings. The plugin appears in the left menu as
   **Slip Lock Calc**.

Existing v1 settings and ShakeIT profiles keep working. The settings file and every property name are unchanged.

### 1.3 Create the ShakeIT data export profile (needed for most games)

The plugin's preferred slip source is ShakeIT's own wheel slip and wheel lock calculation, which ShakeIT publishes
only when an effect exports it.

1. On the plugin page, under **SETUP**, click **ShakeIT data export**. This writes
   `Documents\SimHub\SlipLock_DataExport.siprofile`.
2. In **ShakeIT Bass Shakers**, import the profile and make it active for your game. It holds a
   "SlipLock Data Export" group with a Wheel slip and a Wheel lock effect. Their outputs are disabled, so they drive
   no device; they only export `ShakeITBSV3Plugin.Export.WheelSlip.*` and `ShakeITBSV3Plugin.Export.WheelLock.*`.
3. Restart SimHub.

With the export active, the status line reads "Slip: ShakeIT". Without it, the plugin falls back to ACC's native
slip or, on LMU/rFactor 2, to wheel rotation. If no source exists at all, the page shows "No slip data found:
create the ShakeIT data export profile below...".

### 1.4 Map the channels to your devices

- **Haptic pedals:** click **Haptic pedal profile** to write `SlipLock_HapticPedals.siprofile`, then import it in
  **ShakeIT Motors**. It contains four custom effects:
  - `SlipTC Aggregate (throttle)`: `[SlipLockPropertiesCalc.SlipLock.SlipTC.Mono]` on the throttle channel (0),
    30 Hz. Enabled.
  - `LockABS Aggregate (brake)`: `[SlipLockPropertiesCalc.SlipLock.LockABS.Mono]` on the brake channel (1),
    25 Hz. Enabled.
  - `Slip*Throttle` and `Lock*Brake`: the `SlipBlend` and `LockBlend` alternatives. Disabled; enable them instead
    of the aggregates if you prefer.

  The channel layout (0 throttle, 1 brake, 2 clutch) matches typical haptic pedal devices. Re-assign channels in
  ShakeIT if yours differ.
- **Understeer / oversteer:** see section 4.
- **Your own effects or dashboards:** use any property from section 3 in a ShakeIT custom effect formula, for
  example `[SlipLockPropertiesCalc.SlipLock.Lock.FrontLeft]`. SlipLock values are 0..100. Balance values are 0..1,
  so multiply them by 100 in effect formulas.

---

## 2. The settings page

### 2.1 Simple view (always visible)

**Status line.** Shows game, car and class, then "Slip: <source>", "Lock: synth | from slip [+ ShakeIT]" and
"Balance: active (model|direct, confidence n %)", or the reason Balance is inactive, for example "not on track",
"low speed", "pit lane" or "calibrating steering, drive a few corners". A hint appears when no game is running or
no slip data was found.

**SENSITIVITY, saved for <car>.** Four sliders, 10 % to 500 % in steps of 5, reset value 100 %. Each has a live
meter (0-100).

| Slider | What it does | Meter |
|---|---|---|
| Slip | Multiplies the raw slip signal before corner-load weighting. Affects `Slip`, `SlipBlend` and `SlipTC` (while TC is not active). Raise it if wheelspin feels too weak; lower it if the effect buzzes all the time. | `SlipTC.Mono` (or `Slip.Mono` while TC is active) |
| Lock | The same for the lock channel (synthesized lock and ShakeIT WheelLock). Raise it for cars with weak lock feedback, such as the LMU LMP3. | `LockABS.Mono` (or `Lock.Mono` while ABS is active) |
| Understeer | Gain on the understeer detector metrics (yaw deficit, front-minus-rear slip angle). 200 % reaches onset and full intensity at half the deficit, so the front washing out is felt earlier. | `Balance.Understeer` × 100 |
| Oversteer | Gain on the oversteer detector metrics (yaw excess, countersteer yaw rate, body slip beyond the envelope, rear-minus-front slip angle). | `Balance.Oversteer` × 100 |

All channels stay capped at 100 (or 1.0 for Balance). While the car's TC or ABS is active, `SlipTC` and `LockABS`
carry the TC or ABS channel, which the sensitivities do not scale; the slider notes this and its meter switches to
`Slip.Mono` or `Lock.Mono`.

**Per-car memory.** Sensitivities are stored per car and switch automatically when you change car. A car you have
never driven starts at 100 %, which is v1 behavior. On LMU the car is identified by the model name rather than
the livery, so all liveries of one model share a profile. The sliders are disabled, with the hint "Load a car to
adjust", while no car is loaded.

**SETUP.** Three buttons write ShakeIT profiles to `Documents\SimHub` (import them in ShakeIT, then restart
SimHub):

- ShakeIT data export (`SlipLock_DataExport.siprofile`, Bass Shakers tab)
- Haptic pedal profile (`SlipLock_HapticPedals.siprofile`, Motors tab)
- Balance haptics profile (`SlipLock_Balance.siprofile`, Motors tab)

**Show debug view.** A switch, off by default and remembered, that reveals the three debug tabs.

### 2.2 Debug view

**Slip / Lock tab**
- *Data source and capabilities:* game, car id, preset, slip source, per-wheel mode, ShakeIT slip and lock status,
  lock source, game and car ABS/TC, the aggregate choice (TC/ABS or Slip/Lock) and max G. While per-wheel speed
  detection runs, it also shows the detection conditions (speed > 5 m/s, and lateral > 0.3 or brake > 10 %).
  - *Merge ShakeIT WheelLock into the lock channel* (default on).
  - *Retest per-wheel detection:* forgets the stored per-wheel result for this game, re-resolves the slip source
    (so a ShakeIT export activated later replaces rF2 rotation) and re-verifies the sim's steering direction for
    Balance.
- *Final outputs:* SlipTC and LockABS per wheel.
- *Envelope (attack / release):* 8 sliders in ms.
- *Pedal blend:* 4 sliders, plus two gates: "zero Slip/TC without throttle" and "zero Lock/ABS without brake". It
  also shows the Slip × throttle and Lock × brake per-wheel values.
- *Thresholds and preprocessor output:* 4 thresholds (values at or below are cut and the rest is rescaled to
  0..100), plus Slip, Lock, ABS and TC after the preprocessor.
- *Raw base input and corner load:* base slip (−100..100 for signed sources) and corner load (proxyL, 0..50 with
  25 = neutral).
- *Game preset (read-only):* the code-defined preset in use (`SlipLock/GamePresets.cs`).
- *Reset sliders to defaults:* restores envelopes, blends, thresholds, gates and the WheelLock toggle.

**Balance tab**
- *Outputs:* understeer, oversteer, confidence, state (gate), path, tags and class preset in use.
- *Detectors (raw, before shaping):* US model, US direct, OS model, OS yaw excess, OS countersteer, OS body slip,
  OS direct and the speed ramp.
- *Signals:* yaw rate r, reference r_ref, yaw ratio ρ, body slip β, steering, steering minus offset, and front and
  rear slip angle.
- *Vehicle model:* G, K, θ0, τ, ay max and α peak, each with value, source (default, preset, learned, session or
  manual), confidence and sample count.
- *Learning:* learning now, baseline locked, session override, steering and forward sign (with "verifying" until
  decided), gravity in vertical acceleration, and auto class preset.
  - *Lock learning for this car:* freezes the stored model; per-session adaptation still runs.
  - *Reset learned model...:* after a confirmation, forgets the car's learned model and re-verifies the steering
    direction.
- *Mode and per-car overrides:*
  - Estimation mode, for all cars: Auto (direct when available, else model), Model only, or Direct only.
  - Class preset for this car: Auto (detected from class and name), Formula / prototype, GT, Road / touring,
    Rally / loose surface, Oval, or None.
  - Overrides for this car: steering ratio, wheelbase, G, K, θ0 and τ. Tick a box to replace the learned or default
    value.
  - Export car profile... and Import car profile... (JSON).
- *Detector tuning (global, all cars):* an expander with every `BalanceTuning` value: speeds, onsets and full
  values, countersteer, body slip, spin, shaping, edge cases, direct path and learning confidence. It has its own
  "Reset tuning to defaults".

**Diagnostics tab**
- *Identity and data paths:* sim key, car key and its source (native model, car model or car id), profile file,
  resolved slip and lock paths, balance source, DataUpdate time in ms, frame count and UI errors.
- *Balance property resolution:* which raw property each balance input was read from, or "not found".
- *Last error.*
- *Tools:*
  - *Write debug log file:* a 1 Hz line in `<SimHub>\Logs\SlipLock_debug.log`, plus a SCAN and SELF-PROBE block on
    each game start.
  - *Record balance telemetry (CSV).*
  - *Dump property names:* writes every SimHub property plus the plugin's candidate paths with their current values
    to `PluginsData\SlipLockPropertiesCalc\property-dump-<timestamp>.txt`.

---

## 3. Exported properties

All names below carry the prefix `SlipLockPropertiesCalc.`. `<W>` is `FrontLeft`, `FrontRight`, `RearLeft`,
`RearRight` or `Mono`. Mono is the average of the four wheels.

### 3.1 Slip / lock (v1, names and values unchanged)

| Property | Range | Meaning |
|---|---|---|
| `SlipLock.Slip.<W>` | 0..100 | Wheelspin after corner load, speed fade and preprocessor, and after thresholds, gates and envelope. |
| `SlipLock.Lock.<W>` | 0..100 | Wheel lock (synthesized from slip while braking, or negative slip for signed sources, merged with ShakeIT WheelLock). |
| `SlipLock.ABS.<W>` | 0..100 | ABS activity weighted by corner load (the ABS flag is on/off). |
| `SlipLock.TC.<W>` | 0..100 | TC activity weighted by corner load. |
| `SlipLock.SlipBlend.<W>` | 0..100 | Slip blended with throttle (`SlipThrottleBlend` %, default 20). |
| `SlipLock.LockBlend.<W>` | 0..100 | Lock blended with brake (`LockBrakeBlend` %, default 20). |
| `SlipLock.SlipTC.<W>` | 0..100 | Aggregate: TC blended with throttle when the game exports TC and the TC level is > 0, else SlipBlend. Recommended throttle-pedal channel. |
| `SlipLock.LockABS.<W>` | 0..100 | Aggregate: ABS blended with brake when the game exports ABS and the ABS level is > 0, else LockBlend. Recommended brake-pedal channel. |
| `SlipLock.MaxSway` | ≥ 5 | Largest lateral acceleration seen with this car, in SimHub's `AccelerationSway` unit. Starts at 5, grows only, ignores jumps of 5 or more per frame, resets on car change. Normalizes the corner load. |
| `SlipLock.MaxSurge` | ≥ 5 | Same for `AccelerationSurge`, which is positive under braking in SimHub. |
| `SlipLock.MaxDecel` | ≥ 5 | Same for the negated surge. |

Values are rounded to one decimal. All outputs go to 0 when the game stops.

### 3.2 Balance (new)

| Property | Type / range | Meaning |
|---|---|---|
| `Balance.Understeer` | 0..1 | Understeer intensity after shaping (hysteresis, speed ramp, attack/release). |
| `Balance.Oversteer` | 0..1 | Oversteer intensity after shaping. 1 during a spin. |
| `Balance.PowerOversteer` | bool | Oversteer above the tag threshold (default 0.2) with throttle > 50 %. |
| `Balance.LiftOrBrakeOversteer` | bool | Oversteer above the threshold with throttle < 20 % or brake > 10 %. |
| `Balance.EntryUndersteer` | bool | Understeer above the threshold with brake > 10 %. |
| `Balance.ExitUndersteer` | bool | Understeer above the threshold with throttle > 30 %. |
| `Balance.Countersteer` | bool | The countersteer detector fires (steering against the yaw rotation). |
| `Balance.Spin` | bool | Body slip beyond 45°. |
| `Balance.Active` | bool | All gates are open (state "active"). False while the steering sign is still being calibrated on the model path. |
| `Balance.Confidence` | 0..1 | Trust in the vehicle model: manual 1.0, learned = learned confidence, session 0.7, class preset 0.3, default 0.2. Halved when the yaw rate is estimated from lateral acceleration. 0 for the model while the steering sign is being calibrated. On the direct path, the maximum of that and the α-peak confidence. |
| `Balance.YawRate` | rad/s | Filtered measured yaw rate r. |
| `Balance.YawRef` | rad/s | Yaw rate the vehicle model expects for the current steering and speed. |
| `Balance.YawRatio` | ratio | r / r_ref. Below 1 means understeer, above 1 oversteer. 0 when undefined. |
| `Balance.BodySlipDeg` | deg | Body slip angle β = atan2(v_lateral, v_forward). |
| `Balance.G` | 1/m | Steering gain in use. |
| `Balance.K` | s²/m² | Understeer factor in use. |
| `Balance.Theta0` | deg | Steering offset in use, in steering-wheel degrees. |
| `Balance.TauYaw` | s | Steering-to-yaw lag in use. |
| `Balance.SamplesG`, `Balance.SamplesK` | long | Learner sample counts. |
| `Balance.ParamSource` | text | Source of G: `default`, `preset`, `learned`, `session` or `manual`. |
| `Balance.Path` | text | `none`, `model` (yaw rate vs. steering) or `direct` (tyre slip angles). |

Unknown numeric values are exported as 0.

---

## 4. Understeer / oversteer in ShakeIT

**Generated profile.** Click **Balance haptics profile** to write `Documents\SimHub\SlipLock_Balance.siprofile`,
then import it in **ShakeIT Motors**. It contains two custom effects with aggregation mode "Corners" and no channel
map, so ShakeIT's corner assignment of your devices applies:

| Effect | Formulas | Tone |
|---|---|---|
| Understeer (front) | FrontLeft and FrontRight: `[SlipLockPropertiesCalc.Balance.Understeer] * 100` | 40 Hz |
| Oversteer (rear) | RearLeft and RearRight: `[SlipLockPropertiesCalc.Balance.Oversteer] * 100` | 35 Hz |

**Manual mapping** (bass shakers, seat movers, a single device). Create a custom effect and use
`[SlipLockPropertiesCalc.Balance.Understeer] * 100` or `[SlipLockPropertiesCalc.Balance.Oversteer] * 100` as the
formula for the corners or channel you want. The `*100` matters because ShakeIT expects 0..100 while the Balance
properties are 0..1. The tags, such as `Balance.PowerOversteer`, can gate or colour dashboard elements.

Tune the strength with the effect gain in ShakeIT, and the onset (how early the effect starts) with the
Understeer and Oversteer sensitivity sliders.

---

## 5. How balance detection and learning work

**Detection.** The plugin predicts the yaw rate from the steering angle and speed with a bicycle model,
`r_ss = G·v·(θ − θ0) / (1 + K·v²)`, lagged by τ, and compares it with the measured yaw rate:

- Understeer: the car rotates less than requested (ρ < 1) while steering beyond a small deadband.
- Oversteer: the maximum of yaw excess (ρ > 1), countersteer, and body slip beyond the car's learned normal envelope.
- Direct path (Auto mode on LMU/rF2 and ACC/AC Rally, once α_peak is learned): front minus rear slip angle for
  understeer, rear minus front for oversteer, normalized by the learned peak slip angle.
- A mutual exclusion forces understeer to 0 while oversteer is above 0.2. Body slip beyond 45° is a spin
  (oversteer = 1). After 1.5 s of spinning the outputs fade out.
- Outputs are 0 (fading with the release time) below `VMin` (8 m/s), in the pit lane, when reversing, off track,
  in replays or when paused, for 2 s after a reset or teleport, for 0.5 s after contact, and for 0.3 s after being
  airborne. They ramp to full weight at `VFull` (15 m/s). On loose surfaces (rF2/LMU only) the oversteer thresholds
  are widened by 2.5×.

**Steering calibration (first drive in a sim).** The steering and forward sign conventions are verified per sim at
runtime and stored in the settings (`BalanceCalibration`). Until the steering sign is verified, the status reads
"calibrating steering, drive a few corners". The model understeer, yaw-excess and countersteer detectors are then
held at 0, so a wrong sign can never produce false effects; body slip and spin still work. Verification needs 200
samples of clean cornering (about 3-4 s of steady cornering at SimHub's default 60 Hz) and happens once per sim.
Gravity handling of the vertical acceleration is detected the same way.

**Learning per car.** Everything is learned from normal driving, only on dry asphalt or an unknown surface, not in
the pit lane, not within 2 s of joining the track, and not within 1 s after contact or being airborne. Sample
counts are at the data rate (60 per second at SimHub's default):

| Parameter | What is learned | Used from (confidence ≥ 0.6 where applicable) |
|---|---|---|
| θ0 (steering offset) | EMA of steering on straights (> 20 m/s, |ay| < 0.5 m/s²) | about 180 straight samples (~3 s of straights) |
| G (steering gain) | Robust recursive least squares on steady, moderate cornering (|ay| below 4 m/s² or 35 % of ay max, no braking, settled yaw); needs θ0 first | about 200 accepted samples (confidence = n/300 × fit quality) |
| K (understeer factor) | The same fit, two-parameter | 800 samples with at least 15 m/s speed spread |
| τ (yaw lag) | Best-fit lag of quick turn-in events | 12 events (published after 10; confidence events/20) |
| ay max | 98th percentile of lateral acceleration | 1500 samples above 10 m/s (~25 s of driving) |
| Body-slip envelope | 95th percentile of β per lateral-load bin | 200 samples per bin (after ay max) |
| α peak (direct path) | 90th percentile of the larger front/rear slip angle near the grip limit | 300 samples above 70 % of ay max (confidence n/600) |

In practice the model becomes "learned" within the first laps (θ0 and G), and K and the direct path follow after a
few more laps with a range of corner speeds. Until then, the class preset or the defaults are used, with lower
confidence.

The precedence per parameter is **manual override > session > learned > class preset > default**. A session layer
tracks G and θ0 separately during a session; for example, damage or a changed setup makes G deviate by more than
15 %. It overrides the stored baseline for that session only and is never saved. The baseline is saved into the car
profile at most every 60 s while learning, on car change and when SimHub closes. *Lock learning for this car*
freezes the baseline. *Reset learned model* starts over.

---

## 6. Recording and offline replay (tuning)

1. In Diagnostics, enable **Record balance telemetry (CSV)** and drive. The file is
   `<SimHub>\PluginsData\SlipLockPropertiesCalc\Recordings\<sim>_<car>_<yyyyMMdd_HHmmss>.csv`; its path is shown
   under the toggle. It holds every vehicle-state input (speeds, yaw rate, steering, accelerations, pedals, flags,
   slip angles) plus the key outputs (US, OS, ρ, r_ref, β, gate).
2. Disable recording to flush and close the file.
3. Replay it offline through a fresh estimator (default tuning, empty car profile, unverified sign calibration):

   ```bash
   Tests/bin/Debug/User.SlipLockPropertiesCalc.Tests.exe --replay "path\to\recording.csv"
   ```

   The summary lists sample counts, active time, understeer and oversteer statistics, the gate distribution, the
   deviation from the recorded outputs, and the learned G, K, θ0 and τ with their confidences and the steering sign.
   Change detector code or `BalanceTuning` defaults, rebuild the tests and replay again to compare.

---

## 7. Troubleshooting

| Symptom | Check |
|---|---|
| Plugin missing or old behavior | Rebuild, close SimHub, copy the DLL and PDB, start SimHub, enable the plugin. SimHub must be restarted after every update. |
| "No slip data found" / "Slip: none" | Import the data export profile in ShakeIT Bass Shakers, make it active for the game, restart SimHub. Check "ShakeIT slip" in Slip / Lock → Data source. |
| Lock too weak (for example LMU LMP3) | Make sure "ShakeIT lock" shows Available and the merge toggle is on, then raise the Lock sensitivity for that car. |
| Balance "not available for this sim" | Only iRacing, LMU, rFactor 2 and the Assetto Corsa family are supported. |
| Balance "calibrating steering, drive a few corners" | Normal on the first drive in a sim. Drive some clean, steady corners. |
| Balance "no data" | The raw properties are missing. See Diagnostics → Balance property resolution, and use *Dump property names*. |
| Understeer or oversteer fires constantly, or not at all | Check the confidence and the vehicle model table. With low confidence, drive a few more laps. Otherwise adjust the per-car sensitivity. For a single car, try the class preset or an override. For all cars, use the detector tuning. |
| Model was learned under odd conditions | *Reset learned model...* for that car. Use *Lock learning* to keep a good model. |
| Wrong car profile on LMU | Diagnostics → car key source should read "native model". The first seconds may use a provisional key, which is not saved. |
| Values freeze or errors | Diagnostics → Last error, and the SimHub log (lines start with `SlipLock: `). Enable the debug log file for a 1 Hz trace. |

Files at a glance:
- Settings: `<SimHub>\PluginsData\Common\SlipLockPropertiesCalc.GeneralSettings.json`.
- Car profiles: `<SimHub>\PluginsData\SlipLockPropertiesCalc\Cars\<Sim>\<Car>_<hash>.json`. A corrupt file is
  kept as `.bad-<timestamp>`; if a file cannot be read, that session saves to `.unsaved.json` instead of replacing
  it.

---

## 8. Developer notes

- Architecture, threading rules and hard rules: see `CLAUDE.md`. In short, `DataUpdate` on the data thread owns
  all state and is allocation-free. The UI reads a snapshot and queues edits. Properties are registered once with
  `AttachDelegate`.
- Tests:

  ```bash
  "$MSB" Tests/User.SlipLockPropertiesCalc.Tests.csproj -p:Configuration=Debug -v:minimal
  Tests/bin/Debug/User.SlipLockPropertiesCalc.Tests.exe [--filter <Class.Method substring>] [--replay <csv>]
  ```

  The exit code is the number of failed tests. `Tests/Legacy/LegacyPipeline.cs` is a verbatim port of the v1 math.
  The slip/lock tests require bit-identical output against it at 100 % sensitivity.
- Slip sources, in resolution order: ShakeIT `WheelSlip`, ShakeIT `proxyS`, ACC native `Physics.WheelSlip0N` (×20),
  then rF2/LMU wheel rotation (signed). In iRacing, per-wheel speeds (`LFspeed` ...) are probed, but SimHub does
  not provide them live, so the per-wheel detection settles on Mono.
- Game presets (corner-load influence, speed fade, inverse load, pre-gain and pre-cut) are code-only, in
  `SlipLock/GamePresets.cs`.

---

## 9. Changelog v1 → v2

**New**
- Understeer/oversteer detection: 22 `Balance.*` properties, a per-car learned vehicle model, and runtime sign
  calibration per sim.
- Simple settings page (per-car sensitivities, live meters, setup buttons) with an optional debug view that holds
  all v1 displays and sliders, the balance internals and diagnostics.
- Per-car profiles (sensitivities, overrides, learned model) with export and import. LMU cars are keyed by model,
  not livery.
- The `SlipLock_Balance.siprofile` generator, CSV recording with offline replay, the property dump, and a
  Diagnostics tab.

**Behavior changes (deliberate)**
- **ShakeIT WheelLock merge:** lock = max(v1 lock, ShakeIT `WheelLock` export) when the export exists (toggle,
  default on). v1 synthesized lock only from ShakeIT slip, which reads 0 in heavy braking for some cars (weak LMP3
  lock).
- **Signed-source lock fix:** for signed slip sources (rF2/LMU wheel rotation, per-wheel speeds), lock now comes
  from negative slip (wheel slower than the car). v1 synthesized lock from positive slip for every source, which
  inverted lock for these sources. ShakeIT (unsigned) is unaffected.
- **Case-insensitive game presets:** SimHub reports `AssettoCorsaEVO` and `BeamNgDrive`, which never matched v1's
  `AssettoCorsaEvo` and `BeamNGdrive` entries. Those games now get their own preset instead of Default, so their
  slip/lock values change.
- **Outputs zeroed on game stop:** v1 kept the last values when the game stopped, which could leave an effect
  stuck. v2 clears every output and resets the envelopes.
- **Haptic pedal profile channel fix:** in v1 the disabled `Slip*Throttle` and `Lock*Brake` effects also had their
  pedal channel disabled, so enabling the effect produced nothing. Their channel is now enabled; only the effect
  itself is off.
- **Retest** also re-resolves the slip source, so a ShakeIT export activated after rF2 rotation was chosen is now
  picked up. It also re-verifies the balance sign calibration.
- Slip-source probing is rate-limited (every 0.5 s while unresolved; WheelLock every 2 s) instead of every frame.
- Out-of-range values in the settings file are clamped on load.

**Unchanged (compatibility)**
- Plugin class, namespace, name and property prefix; all 43 `SlipLock.*` property names and values (bit-identical
  at 100 % sensitivity); the settings key `GeneralSettings`; and all v1 settings fields, including the unused
  `SpeedWarningLevel`, which is kept so existing files round-trip.
- The v1 max-G reset rule, per-game capability persistence (`GameCapabilities`), the debug log location and the
  ShakeIT profile file names and contents (apart from the channel fix and fresh IDs).

**Internal**
- Rewrite into SimHub-independent modules with a console test suite. Properties are exported through
  `AttachDelegate` instead of per-frame `SetPropertyValue`. Settings and profiles are written off the data thread
  and debounced. Each processing stage is fault-isolated.
