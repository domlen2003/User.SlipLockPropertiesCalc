# DLP (Divebomb Logistics Plugin) v3: user and developer guide

DLP is the Divebomb Logistics team's SimHub plugin. It is built from **modules**; each module has its own tab on the
plugin's settings page (left menu **DLP**) and its own folder under `<SimHub>\PluginsData\DLP\`:

- **Haptics** (tab "Haptics"): the features of the former plugin "Slip Lock Properties Calc" (v1/v2), unchanged in
  behavior:
  - **Slip / lock / ABS / TC** per wheel and mono (`SlipLock.*`), for haptic pedals, bass shakers and motion
    devices. With default sensitivities the values are the same as in v1.
  - **Understeer / oversteer** (`Balance.Understeer`, `Balance.Oversteer`, 0..1). The vehicle model behind them is
    learned automatically for every car.
- **Speed Dial** (tab "Speed Dial", new in v3): named setup presets per car for TC, TC2 (TC Cut), TC3 (TC Slip), ABS
  and brake bias. A wheel button dials a preset in: DLP presses the in-car adjustment buttons through SimHub's
  Control Mapper and watches the game's telemetry until every value matches.

SimHub publishes every property and action of the plugin with the prefix `DLP.`, for example
`DLP.SlipLock.SlipTC.Mono`, `DLP.Balance.Understeer` or `DLP.SpeedDial.Dial1`. The old prefix
`SlipLockPropertiesCalc.` no longer exists (there are no aliases).

Contents: 1 Install and upgrade · 2 Haptics module · 3 Speed Dial module · 4 Troubleshooting · 5 Developer notes ·
6 Changelog v2 → v3 · 7 Changelog v1 → v2

---

## 1. Install and upgrade

### 1.1 Build

Requirements: MSBuild with .NET Framework 4.8 targeting (Visual Studio 2022, Rider or Build Tools) and the
environment variable `SIMHUB_INSTALL_PATH` pointing at the SimHub folder with a trailing backslash (for example
`C:\Program Files (x86)\SimHub\`). The project references SimHub's own DLLs from that folder.

```bash
MSB="/c/Users/domin/AppData/Local/Programs/Rider/tools/MSBuild/Current/Bin/amd64/MSBuild.exe"   # any MSBuild works
"$MSB" DivebombLogistics.Plugin.csproj -p:Configuration=Release -p:DeployToSimHub=false -t:Rebuild -v:minimal
```

The output is `bin\Release\DivebombLogistics.Plugin.dll` and `.pdb`.

**Deploy step.** Without `-p:DeployToSimHub=false`, the build also deploys: it copies `DivebombLogistics.Plugin.dll`
and `.pdb` into `%SIMHUB_INSTALL_PATH%` and then deletes the obsolete v1/v2 plugin files
`User.SlipLockPropertiesCalc.dll` and `User.SlipLockPropertiesCalc.pdb` there, so SimHub does not load both plugins.
The old files are deleted only when the new DLL was copied (or was already up to date). While SimHub is running it
locks the DLL: the copy then fails with a warning (not a build error) and the old plugin files are kept.

### 1.2 Install

1. **Close SimHub completely.** It loads plugins only at start-up and keeps the DLL locked.
2. Deploy: either build with the deploy step (section 1.1), or copy `DivebombLogistics.Plugin.dll` and
   `DivebombLogistics.Plugin.pdb` into the SimHub folder by hand. When copying by hand over a v1/v2 install, also
   delete `User.SlipLockPropertiesCalc.dll` and `User.SlipLockPropertiesCalc.pdb` from the SimHub folder; otherwise
   SimHub loads both plugins.
3. Start SimHub. DLP is a new plugin for SimHub (class `DivebombLogistics.DLP`): confirm when SimHub asks whether to
   enable it, or enable "Divebomb Logistics Plugin" in SimHub's plugin settings. The plugin appears in the left menu
   as **DLP**, with the tabs **Haptics** and **Speed Dial**.

SimHub must be restarted after every update of the DLL.

### 1.3 Upgrading from Slip Lock Properties Calc (v1/v2)

1. Close SimHub and deploy as in section 1.2. The deploy step removes the old `User.SlipLockPropertiesCalc.dll`.
2. Start SimHub and enable DLP. **Settings and car profiles are migrated automatically** on this first start,
   before any module loads:
   - `PluginsData\Common\SlipLockPropertiesCalc.GeneralSettings.json` becomes `PluginsData\DLP\Haptics\Settings.json`
     (all sliders, toggles, per-game capabilities, balance tuning and steering calibration).
   - `PluginsData\SlipLockPropertiesCalc\Cars\` is copied to `PluginsData\DLP\Haptics\Cars\` (per-car sensitivities,
     overrides and learned models).

   The old files are never changed or deleted; they stay as a backup. When the migration is complete, DLP writes
   `PluginsData\DLP\migration.json` and does not look at the old files again. If a part fails (for example a file
   is locked by a backup tool), SimHub's log shows an error, DLP starts with defaults for that part and records it in
   `PluginsData\DLP\migration-pending.json`. The next SimHub start retries exactly that part, even though DLP has
   written its own files in the meantime: the old settings then replace the defaults (the replaced file is kept as
   `Settings.json.pre-migration`), and the old car profiles are added without overwriting any profile DLP has saved
   since.
3. **Regenerate and re-import the ShakeIT profiles**, because every property name changed its prefix from
   `SlipLockPropertiesCalc.` to `DLP.`:
   1. On the Haptics tab, under **SETUP**, click the three profile buttons again. They now write
      `DLP_DataExport.siprofile`, `DLP_HapticPedals.siprofile` and `DLP_Balance.siprofile` (profiles
      "DLP Data Export", "DLP Haptic Pedals" and "DLP Balance") to `Documents\SimHub`.
   2. Import them in ShakeIT as described in sections 2.1 and 2.4, make them active, and remove or deactivate the old
      `SlipLock_*` profiles. The old pedal and balance profiles reference `[SlipLockPropertiesCalc.…]` and stay
      silent.
   3. In your own custom effects and dashboards, replace `SlipLockPropertiesCalc.` with `DLP.` in every formula, for
      example `[SlipLockPropertiesCalc.SlipLock.SlipTC.Mono]` becomes `[DLP.SlipLock.SlipTC.Mono]`.
   4. Restart SimHub.

Speed Dial is new, so there is nothing to migrate for it; set it up as described in section 3.

### 1.4 Files at a glance

Everything DLP stores lives under `<SimHub>\PluginsData\DLP\`, one folder per module:

| File | Content |
|---|---|
| `Haptics\Settings.json` | Haptics settings (global). |
| `Haptics\Cars\<Sim>\<Car>_<hash>.json` | Haptics car profiles (sensitivities, overrides, learned model). |
| `Haptics\Recordings\<sim>_<car>_<yyyyMMdd_HHmmss>.csv` | Balance recordings (section 2.6). |
| `Haptics\property-dump-<timestamp>.txt` | Property dumps (Haptics → Diagnostics). |
| `SpeedDial\Settings.json` | Speed Dial settings (global: roles, slot count, pairs, timing). |
| `SpeedDial\Cars\<Sim>\<Car>_<hash>.json` | Speed Dial data per car (presets, slots, selection, stored pair values, learning). |
| `migration.json` / `migration-pending.json` | Migration done / a part is retried at the next start (section 1.3). |

Robustness rules for every settings and car file: writes are atomic (a crash never leaves half a file). A corrupt file
is renamed to `<name>.bad-<timestamp>` and replaced by defaults. A file that exists but cannot be read (locked) is
left untouched; that session continues from and saves to `<name>.unsaved.json` instead (for example
`Settings.unsaved.json`). Side files are never merged back automatically; a left-over one is reported in SimHub's log.

Outside `PluginsData\DLP\`: the optional debug log `<SimHub>\Logs\DLP_debug.log` and the generated ShakeIT profiles
`Documents\SimHub\DLP_*.siprofile`. The v1/v2 files under `PluginsData\Common\` and `PluginsData\SlipLockPropertiesCalc\`
are the untouched backup.

---

## 2. Haptics module

Balance detection is available for iRacing, Le Mans Ultimate, rFactor 2, Assetto Corsa Competizione, Assetto
Corsa, Assetto Corsa EVO and Assetto Corsa Rally. The slip/lock channels work in any game that provides a slip
source (section 5, "Slip sources").

### 2.1 Quick start

**Create the ShakeIT data export profile (needed for most games).** The plugin's preferred slip source is
ShakeIT's own wheel slip and wheel lock calculation, which ShakeIT publishes only when an effect exports it.

1. On the Haptics tab, under **SETUP**, click **ShakeIT data export**. This writes
   `Documents\SimHub\DLP_DataExport.siprofile`.
2. In **ShakeIT Bass Shakers**, import the profile and make it active for your game. It holds a
   "SlipLock Data Export" group with a Wheel slip and a Wheel lock effect. Their outputs are disabled, so they drive
   no device; they only export `ShakeITBSV3Plugin.Export.WheelSlip.*` and `ShakeITBSV3Plugin.Export.WheelLock.*`.
3. Restart SimHub.

With the export active, the status line reads "Slip: ShakeIT". Without it, the plugin falls back to ACC's native
slip or, on LMU/rFactor 2, to wheel rotation. If no source exists at all, the page shows "No slip data found:
create the ShakeIT data export profile below...".

**Map the channels to your devices.**

- **Haptic pedals:** click **Haptic pedal profile** to write `DLP_HapticPedals.siprofile`, then import it in
  **ShakeIT Motors**. It contains four custom effects and a gear-shift effect:
  - `SlipTC Aggregate (throttle)`: `[DLP.SlipLock.SlipTC.Mono]` on the throttle channel (2), 30 Hz. Enabled.
  - `LockABS Aggregate (brake)`: `[DLP.SlipLock.LockABS.Mono]` on the brake channel (1), 25 Hz. Enabled.
  - `Slip*Throttle` and `Lock*Brake`: the `SlipBlend` and `LockBlend` alternatives. Disabled; enable them instead
    of the aggregates if you prefer.
  - Gear shift (ShakeIT's built-in gear effect): a 90 ms, 15 Hz pulse on brake and throttle when a gear engages,
    neutral ignored, gain 37 %. Enabled.

  The channel layout (0 clutch, 1 brake, 2 throttle) matches the author's pedal set, taken from their exported
  ShakeIT profile. Re-assign channels in ShakeIT if yours differ.
- **Understeer / oversteer:** see section 2.4.
- **Your own effects or dashboards:** use any property from section 2.3 in a ShakeIT custom effect formula, for
  example `[DLP.SlipLock.Lock.FrontLeft]`. SlipLock values are 0..100. Balance values are 0..1, so multiply them by
  100 in effect formulas.

### 2.2 The Haptics tab

#### Simple view (always visible)

**HAPTICS (status).** Shows game and car, then "Slip: <source>", "Lock: synth | from slip [+ ShakeIT]" and
"Balance: active (model|direct, confidence n %)", or the reason Balance is inactive, for example "not on track",
"low speed", "pit lane" or "calibrating steering, drive a few corners". A hint appears when no game is running or
no slip data was found.

**SENSITIVITY ("Saved for <car>").** Four sliders, 10 % to 500 % in steps of 5, reset value 100 %. Each has a live
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
the livery, so all liveries of one model share a profile. While no car is loaded the sliders are disabled, with the
hint "Load a car to adjust".

**SETUP.** Three buttons write ShakeIT profiles to `Documents\SimHub` (import them in ShakeIT, then restart
SimHub):

- ShakeIT data export (`DLP_DataExport.siprofile`, Bass Shakers)
- Haptic pedal profile (`DLP_HapticPedals.siprofile`, Motors)
- Balance haptics profile (`DLP_Balance.siprofile`, Motors)

**DEBUG → Show debug view.** A switch, off by default and remembered, that reveals the three debug tabs.

#### Debug view

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
- *Game preset (read-only):* the code-defined preset in use (`Haptics/SlipLock/GamePresets.cs`).
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
  - *Write debug log file:* a 1 Hz line in `<SimHub>\Logs\DLP_debug.log`, plus a SCAN and SELF-PROBE block on
    each game start.
  - *Record balance telemetry (CSV)* (section 2.6).
  - *Dump property names:* writes every SimHub property plus the plugin's candidate paths with their current values
    to `PluginsData\DLP\Haptics\property-dump-<timestamp>.txt`.

### 2.3 Exported properties

All names below carry the prefix `DLP.` (v1/v2: `SlipLockPropertiesCalc.`). `<W>` is `FrontLeft`, `FrontRight`,
`RearLeft`, `RearRight` or `Mono`. Mono is the average of the four wheels.

**Slip / lock** (v1; names after the prefix and values unchanged)

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

**Balance** (v2)

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

Unknown numeric values are exported as 0. The Speed Dial properties are listed in section 3.13.

### 2.4 Understeer / oversteer in ShakeIT

**Generated profile.** Click **Balance haptics profile** to write `Documents\SimHub\DLP_Balance.siprofile`,
then import it in **ShakeIT Motors**. It contains two custom effects with aggregation mode "Corners" and no channel
map, so ShakeIT's corner assignment of your devices applies:

| Effect | Formulas | Tone |
|---|---|---|
| Understeer (front) | FrontLeft and FrontRight: `[DLP.Balance.Understeer] * 100` | 40 Hz |
| Oversteer (rear) | RearLeft and RearRight: `[DLP.Balance.Oversteer] * 100` | 35 Hz |

**Manual mapping** (bass shakers, seat movers, a single device). Create a custom effect and use
`[DLP.Balance.Understeer] * 100` or `[DLP.Balance.Oversteer] * 100` as the formula for the corners or channel
you want. The `*100` matters because ShakeIT expects 0..100 while the Balance properties are 0..1. The tags, such as
`Balance.PowerOversteer`, can gate or colour dashboard elements.

Tune the strength with the effect gain in ShakeIT, and the onset (how early the effect starts) with the
Understeer and Oversteer sensitivity sliders.

### 2.5 How balance detection and learning work

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
| θ0 (steering offset) | EMA of steering on straights (> 20 m/s, \|ay\| < 0.5 m/s²) | about 180 straight samples (~3 s of straights) |
| G (steering gain) | Robust recursive least squares on steady, moderate cornering (\|ay\| below 4 m/s² or 35 % of ay max, no braking, settled yaw); needs θ0 first | about 200 accepted samples (confidence = n/300 × fit quality) |
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

### 2.6 Recording and offline replay (tuning)

1. In Diagnostics, enable **Record balance telemetry (CSV)** and drive. The file is
   `<SimHub>\PluginsData\DLP\Haptics\Recordings\<sim>_<car>_<yyyyMMdd_HHmmss>.csv`; its path is shown under the
   toggle. It holds every vehicle-state input (speeds, yaw rate, steering, accelerations, pedals, flags, slip
   angles) plus the key outputs (US, OS, ρ, r_ref, β, gate).
2. Disable recording to flush and close the file.
3. Replay it offline through a fresh estimator (default tuning, empty car profile, unverified sign calibration):

   ```bash
   Tests/bin/Debug/DivebombLogistics.Tests.exe --replay "path\to\recording.csv"
   ```

   The summary lists sample counts, active time, understeer and oversteer statistics, the gate distribution, the
   deviation from the recorded outputs, and the learned G, K, θ0 and τ with their confidences and the steering sign.
   Change detector code or `BalanceTuning` defaults, rebuild the tests and replay again to compare.

---

## 3. Speed Dial module

### 3.1 What it does

Speed Dial stores named **presets** per car, for example "Dry" (TC 4, TC2 3, ABS 5, BB 54.0 %) and "Wet" (TC 7,
TC2 6, ABS 8, BB 52.5 %). A preset contains absolute target values for the channels it includes; channels it does not
include are left alone. To **dial** a preset, DLP presses the in-car adjustment buttons for you, one press at a time:
it presses a channel's Increase or Decrease role, waits until the game's telemetry shows the new value, and presses
again until the value matches. Then it moves on to the next channel.

Channels: **TC**, **TC2 (Cut)** (LMU "TC Power Cut"), **TC3 (Slip)** (LMU "TC Slip Angle"), **ABS** and **Brake
bias** (front share in %). Which of them a sim reports is listed in section 3.12.

You trigger it with wheel buttons bound to DLP actions:

- **Dial 1..8:** each car assigns one of its presets to each Dial slot. "Dial 2" dials the preset in slot 2 of the
  car you are driving.
- **Next / Previous / Apply selected:** cycle through the car's presets and dial the selected one.
- **Set / Reset pairs:** "Set" stores the current values of a group of channels for this car, "Reset" dials back to
  them later (for example: note the brake bias before a stint, experiment, then return to it with one button).
- **Cancel:** stops a running dial.

Dashboards can show the current values, the selected preset and the dial status through the `DLP.SpeedDial.*`
properties (section 3.13).

### 3.2 Prerequisites: SimHub's Control Mapper

Speed Dial does not talk to the game directly. It "presses" **Control Mapper button roles**, and the Control Mapper
turns each role into an output the game sees as a button press. The chain for one direction of one channel is:

`DLP Speed Dial` → Control Mapper role (for example `TractionControl+`) → the role's output (for example a vJoy
button or a simulated key) → the game's control binding ("TC increase") → the in-car value changes → DLP reads it
from telemetry.

Before using Speed Dial:

1. **Enable SimHub's Control Mapper plugin.** Without it the Speed Dial tab shows the banner "SimHub's Control
   Mapper is not available..." and nothing can be pressed. DLP re-reads the Control Mapper's role list every 5 s, so
   roles added or removed there are picked up within seconds.
2. **Define one button role per channel and direction** in the Control Mapper (only for the channels you want to
   dial) and map each role's output to something the game can bind. The default role names in DLP are the author's:

   | Channel | Increase role | Decrease role |
   |---|---|---|
   | TC | `TractionControl+` | `TractionControl-` |
   | TC2 (Cut) | `TC_PowerCut+` | `TC_PowerCut-` |
   | TC3 (Slip) | `TC_SlipAngle+` | `TC_SlipAngle-` |
   | ABS | `ABS+` | `ABS-` |
   | Brake bias | `BrakeBalanceFront` | `BrakeBalanceRear` |

   Your own role names work too; enter them in Setup (section 3.3).
3. **Bind those outputs in the game** to the in-car adjustments (TC up/down, ABS up/down, brake bias forward/back,
   and so on), as you would bind wheel buttons.

DLP presses only roles the Control Mapper actually defines. A role name that does not exist there is refused at once
(the channel ends with "no role / Control Mapper") instead of waiting for a change that cannot come.

### 3.3 Setting up the roles (Setup → Control Mapper roles)

Open the **Speed Dial** tab, expand **CONFIGURATION → Setup**. Opening the expander reads the Control Mapper's button
roles for the role pickers ("n Control Mapper button roles found"); **Refresh role list** reads them again after you
add roles in the Control Mapper.

Per channel (these settings apply to all cars):

- **Enabled:** untick to make Speed Dial skip the channel, even when a preset contains it (result "skipped").
- **Increase role / Decrease role:** pick a role from the list or type its name. A typed name is stored on Enter or
  when the box loses the focus; a picked one at once. Clearing a role leaves that direction unbound.
- **Test press − / +:** sends a single press of the Decrease or Increase role. The game should show the value
  change. The answer appears under the row ("Last test: Pressed TractionControl+ (TC +)", or an error such as "no role
  bound" or "not a Control Mapper role"). Below the roles the row also shows the channel's current value and the
  car's maximum when the sim reports one ("now 3 · max 11"), or "not reported by this sim". The test buttons are
  disabled while the Control Mapper is unavailable or a dial is running.

Check every channel once with the test buttons in a running session: when "Pressed ..." appears but the game's value
does not move, the role's output is not bound in the game (or not mapped in the Control Mapper).

The Increase role does not have to raise the value: Speed Dial learns per car which way each role moves the value
(section 3.11). Still, keep Increase and Decrease consistent with each other.

### 3.4 Presets (PRESETS section)

The PRESETS section lists the presets of the car you are driving; a car must be loaded to create or edit them
("Load a car to create and edit its presets").

- **New preset from current values** creates a preset with every channel the game reports, at its current value.
  Type a name in "New preset name" first (empty gives "Preset n"); Enter in the name box does the same.
  **New empty preset** creates one without channels.
- Each preset row shows:
  - a **selection marker** (radio button): the preset that Next / Previous / Apply selected start from;
  - the **name**: **Rename** opens an inline box (Enter or leaving the box saves, Escape cancels); names are cut to 40
    characters;
  - a **slot** box: "no slot" or "Dial 1" .. "Dial n" (section 3.5);
  - **Apply:** dials the preset now (a running dial is replaced);
  - **Capture current:** after a confirmation, overwrites the preset's ticked channels with the current values
    (every channel the game reports when none is ticked);
  - **Delete...:** after a confirmation, deletes the preset (and frees its slot and selection);
  - one cell per channel: an **include** checkbox and the **value**. Ticking a channel includes it with its last
    value or, if it has none, the current value. Typing a value includes the channel; clearing the box excludes it.
    Values are rounded on entry (TC/ABS levels to whole numbers, brake bias to 0.01 %), clamped to the valid range,
    and a `%` sign or a decimal comma is accepted. A dimmed cell is a channel this sim does not report: dialing it
    fails with "no telemetry".
- **Export presets...** writes the car's Speed Dial data to a JSON file (suggested name `SpeedDial_<car>.json`).
  **Import presets...** adds the presets of such a file to the current car; existing presets stay, colliding ids get
  new ones, and slots, selection, stored pair values and learning of the file are ignored. A car holds at most 100
  presets.

Brake bias in ACC, AC EVO and AC Rally: the tab shows the hint "Brake bias here is the raw value of this sim...".
These sims report the brake bias without the car-specific offset their display adds, so the value differs from the
in-game number. Capture brake bias from the car instead of typing the in-game number.

### 3.5 Dial slots and preset cycling

- **Dial slots (Setup → Dial slots, all cars):** the number of Dial buttons in use, 1 to 8 (default 4). Each car
  assigns its own presets to the slots in the presets' slot boxes. A preset sits on at most one slot: assigning it to
  another slot moves it, and assigning another preset to an occupied slot replaces the old one. Lowering the slot
  count hides the higher slots without forgetting their assignments.
- **Dial n** (button action) dials the preset in slot n. An empty slot only reports "Slot n is empty"; a slot above
  the slot count reports "Slot n is not in use".
- **Next preset / Previous preset** move the selection through the car's presets in list order, with wrap-around.
  Without a selection Next picks the first preset and Previous the last. The status (and `DLP.SpeedDial.Status`)
  shows the new preset's name; `SelectedPreset` and `SelectedIndex` follow. Cycling needs a car but no running game.
- **Apply selected preset** dials the selected preset ("No preset selected" without one).
- Dial, Apply and Reset need a loaded car and a running game; otherwise they only report "No car loaded" or "No game
  running". Starting a dial while another one runs replaces it.

### 3.6 Set / Reset pairs

A pair is a named group of channels (Setup → Set / reset pairs, all cars). Default: "Pair 1" with every channel and
"Pair 2" with brake bias only. Up to 4 pairs: **Add pair** appends one, **Remove last pair** removes the last one
(only the last, because the stored values are kept per car by pair position). Each pair has a name and one checkbox
per channel.

- **Set** (button action or "Set now") stores the current values of the pair's channels for this car, with the
  time ("stored 14:32:10"). Only channels with a valid current value are stored; when none has one, the old values
  are kept ("Pair 1: no values to store"). Status: "Pair 1 stored".
- **Reset** (button action or "Reset now") dials back to the stored values. Only channels whose current value differs
  are dialed; when none differs the status reads "Pair 1 already set", and "Pair 1: nothing stored" when nothing was
  stored. The dial job is labeled "Reset Pair 1". Channels removed from the pair since the Set are ignored.

The SET / RESET section shows each pair with its channels, this car's stored values (or "nothing stored") and the
capture time. Stored values are saved per car.

### 3.7 Button bindings (CONFIGURATION → Button bindings)

The expander contains a SimHub control editor per action: **Dial 1** .. **Dial n** (n = slot count), **Next preset**,
**Previous preset**, **Apply selected preset**, **Set: <pair>** and **Reset: <pair>** for every defined pair, and
**Cancel dialing**. Click one and press the wheel button to bind it, as anywhere else in SimHub.

The actions are SimHub actions named `DLP.SpeedDial.Dial1` etc. (full list in section 3.13). DLP always registers all
8 Dial and all 4 Set/Reset actions, so a binding survives lowering and raising the slot or pair count; a binding to an
unused slot or undefined pair only reports a status.

### 3.8 Status and progress

**SPEED DIAL section (top of the tab).**

- Game and car ("(identifying...)" while LMU's car is still being resolved).
- **Value chips:** the current value of every channel ("TC 3 · TC2 2 · TC3 4 · ABS 5 · BB 54.2 %"); "-" when unknown,
  dimmed when the sim does not report the channel.
- A **banner** when something needs attention: Control Mapper not available, no game running ("Start a game to
  see the current values and dial presets"), "Waiting for the car...", or "Identifying the car: edits are kept and
  saved once the car is known".
- The **status line** (the same text as `DLP.SpeedDial.Status`): the result of the last button or edit, for example
  "Created Dry", "Pair 1 stored", "Slot 3 is empty", "Pressed ABS+ (ABS +)" or an "Error: ..." text.
- The **dial panel**, shown after the first dial of the session: state ("Dialing", "Paused (game paused, in a menu
  or replay)", "Completed", "Partly done", "Failed", "Cancelled") and job label, a progress bar, "2 of 5 channels ·
  presses: 7", the dialer's message, a **Cancel** button and one row per dialed channel: start → target, current
  value, presses and result.

**Dial messages** (`DLP.SpeedDial.Status` while and after a dial): "Starting", "Dialing TC2 (Cut)", "Paused: waiting
for the game", "Done", "Partly done: ABS no response" (the first channel that failed and why), "Failed: TC no
binding", "Nothing to dial", "Cancelled", "Cancelled: game paused too long", "Cancelled: game changed", "Cancelled:
car changed", "Cancelled: game stopped", "Cancelled: error", "Cancelled: SimHub is closing".

**Job result** (`DLP.SpeedDial.LastResult`): **Completed** when every dialed channel reached its target (or the
closest possible value, or was skipped), **Partial** when some did and some failed, **Failed** when none did,
**Cancelled**.

**Channel results** (dial panel):

| Result | Meaning |
|---|---|
| reached | The value matches the target. Levels must be equal; brake bias within 0.05 % (or half the learned step if larger). |
| closest possible | The target lies between two possible values (for example BB 54.3 % with 0.5 % steps): the closer one was kept. Counts as success. |
| skipped | The preset does not include the channel, or the channel is disabled in Setup. |
| no telemetry | The sim does not report the channel, or no valid value arrived within the confirm timeout. |
| no role / Control Mapper | The role needed is empty, not defined in the Control Mapper, or the Control Mapper is unavailable. |
| no response | Presses did not change the value (default: 3 presses in a row). Usually the role is not bound in the game. |
| limit reached | The value stopped at the car's limit (its reported maximum, 0, or where it stopped moving after responding) before the target. Also used when a value wraps around at its end. |
| direction error | The value kept moving the wrong way; the Increase/Decrease roles are most likely swapped or wrong. |
| too many presses | Safety stop (default 150 presses for one channel). |
| cancelled | The job was cancelled before this channel was finished. |

### 3.9 How dialing works, and safety

- A job dials one channel after the other in the order TC, TC2, TC3, ABS, brake bias. The target is first limited to
  the car's maximum when the sim reports one (LMU, AC EVO).
- For each press DLP picks Increase or Decrease from the learned direction, holds the role for the press duration,
  then waits until the telemetry value changes (at most the confirm timeout). After a change it waits the gap time
  before the next press. With the defaults one step takes about 160 ms or a little more.
- **Presses happen only while you can drive:** the game runs and is not paused, not in a menu, not in a replay and
  not spectating. Otherwise the job pauses ("Paused: waiting for the game") and resumes when you are back; it is
  cancelled after the pause timeout (default 10 s).
- **It stops on its own** when a channel does not respond (3 unanswered presses), sits at its limit, moves the wrong
  way twice, or reaches the safety limit of presses. A job is also cancelled when the game or the car changes, the
  game stops, an error occurs or SimHub closes, and by the Cancel action or button. Values reached so far stay.
- **Your own button presses during a dial are fine.** Speed Dial is closed-loop: it always reads the real value and
  presses toward the target from there. Large jumps never teach it anything, and a single contrary step after the
  direction was proven is taken for your press.
- Presses are queued to a background thread (at most 64 waiting), so SimHub's data processing never waits for the
  Control Mapper.

### 3.10 Timing settings (Setup → Timing, all cars)

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Press duration (ms) | 70 | 20..1000 | How long each role press is held. Raise it when the game misses presses. |
| Gap between presses (ms) | 90 | 0..2000 | Pause after the game showed the new value, before the next press. |
| Confirm timeout (ms) | 800 | 100..5000 | How long to wait for the value to change after a press before the press counts as unanswered (never shorter than the press duration). |
| Unanswered presses before giving up | 3 | 1..20 | After this many presses in a row without a change the channel stops ("no response" or "limit reached"). |
| Max presses per channel | 150 | 1..1000 | Safety stop for one channel. |
| Pause timeout (s) | 10 | 0..600 | A job paused by the game (pause, menu, replay) is cancelled after this time; 0 cancels at once. |

**Reset timing to defaults** restores all six.

### 3.11 Learned direction and step (Setup → Learned for this car)

Speed Dial learns from its own presses, per car and channel:

- **Direction:** whether the Increase role raises ("Increase raises") or lowers ("Increase lowers") the value;
  "(assumed)" until a press proved it. A first wrong-way step flips the direction tentatively; the flip is kept only
  when the other role then moves the value the right way. A second wrong move ends the channel with "direction error"
  and restores the previous direction, so a misconfigured binding never corrupts what was learned.
- **Step:** the smallest change one press caused (for example 1 for levels, 0.5 % for brake bias in some cars). It
  sets the brake bias tolerance and helps tell your own presses from DLP's.
- **Values seen:** the range observed while dialing.

**Reset** (per channel) or **Reset all learned values...** forgets it. Do this after you change a channel's roles or
the game's bindings. A stale (too small) step only costs a few extra presses; the job still ends.

**Diagnostics** (bottom of Setup): Control Mapper availability, the telemetry source in use, the telemetry details
(per channel the property path and whether it is present, missing or has no valid value, plus the maximum's path),
the car's data file and the last error.

### 3.12 Supported sims and channels

| Sim (SimHub game) | TC | TC2 (Cut) | TC3 (Slip) | ABS | Brake bias | Car maximum |
|---|---|---|---|---|---|---|
| Le Mans Ultimate (`LMU`) | `mTC` | `mTCCut` | `mTCSlip` | `mABS` | `mRearBrakeBias`, shown as front % | yes (`m*Max`) |
| iRacing | `dcTractionControl` | `dcTractionControl2` | - | `dcABS` | `dcBrakeBias` (front %) | no |
| ACC, AC Rally | `Graphics.TC` | `Graphics.TCCut` | - | `Graphics.ABS` | `Physics.BrakeBias` (raw, without the display offset) | no |
| AC EVO | `Graphics.electronics.tc_level` | `tc_cut_level` | - | `abs_level` | `Physics.brakeBias` | yes (`electronics_max_limit`) |
| Any other game (also rFactor 2, AC) | `GameData.TCLevel` | - | - | `GameData.ABSLevel` | `GameData.BrakeBias` | no |

Notes: LMU paths are under `DataCorePlugin.GameRawData.PlayerNativeTelemetry.`, iRacing under
`DataCorePlugin.GameRawData.Telemetry.`, the AC family under `DataCorePlugin.GameRawData.`, and the generic ones under
`DataCorePlugin.GameData.`. In iRacing a car without a control has no such variable ("no telemetry" for it). A brake
bias of 0 counts as "not reported". The AC EVO paths are unverified in a live session; check Setup → Diagnostics →
Telemetry details there.

### 3.13 Properties and actions

Properties (prefix `DLP.`):

| Property | Type | Meaning |
|---|---|---|
| `SpeedDial.Busy` | bool | A dial job is running or paused. |
| `SpeedDial.Status` | text | The latest status: dial progress or result, or the outcome of the last button/edit (section 3.8). |
| `SpeedDial.ActivePreset` | text | Label of the running or last job: the preset name or "Reset <pair>". |
| `SpeedDial.SelectedPreset` | text | Name of the selected preset; empty when none. |
| `SpeedDial.SelectedIndex` | int | 1-based position of the selected preset in the car's list; 0 = none. |
| `SpeedDial.PresetCount` | int | Number of presets of the current car. |
| `SpeedDial.Value.TC1`, `.TC2`, `.TC3`, `.ABS`, `.BB` | number | Current value per channel (levels; BB in front %). 0 when unknown or no game runs. |
| `SpeedDial.LastResult` | text | Result of the last finished job: `Completed`, `Partial`, `Failed` or `Cancelled`; empty before the first. |

Actions (prefix `DLP.`; bind them in the tab or wherever SimHub binds plugin actions):
`SpeedDial.Dial1` .. `SpeedDial.Dial8`, `SpeedDial.NextPreset`, `SpeedDial.PreviousPreset`,
`SpeedDial.ApplySelectedPreset`, `SpeedDial.Set1` .. `SpeedDial.Set4`, `SpeedDial.Reset1` .. `SpeedDial.Reset4`,
`SpeedDial.Cancel`.

### 3.14 Files and persistence

- `PluginsData\DLP\SpeedDial\Settings.json`: the global settings (role bindings, enabled flags, slot count, pairs,
  timing). Saved 2 s after the last change and when SimHub closes.
- `PluginsData\DLP\SpeedDial\Cars\<Sim>\<Car>_<hash>.json`: per car and sim the presets, slot assignments, the
  selected preset, the stored pair values and the learned direction/step. Saved 2 s after a change, on car change and
  when SimHub closes. The file path is shown under Setup → Diagnostics → Car file.
- The car is identified like in Haptics: on LMU by the model (all liveries share the data). For the first seconds LMU
  may use a provisional livery key ("(identifying...)"); edits made then are kept in memory, not saved under that
  key, and move to the model's file once it is known (if that file does not exist yet).
- The robustness rules of section 1.4 apply (atomic writes, `.bad-<timestamp>`, `.unsaved.json`).

---

## 4. Troubleshooting

### 4.1 General and Haptics

| Symptom | Check |
|---|---|
| Plugin missing or old behavior | Rebuild, close SimHub, deploy `DivebombLogistics.Plugin.dll` and `.pdb`, start SimHub, enable "Divebomb Logistics Plugin". SimHub must be restarted after every update. |
| Two plugins with the same channels | The old `User.SlipLockPropertiesCalc.dll` is still in the SimHub folder (for example the deploy ran while SimHub was open). Close SimHub, delete it and its `.pdb`, start SimHub. |
| Effects silent after the upgrade to DLP | The ShakeIT profile or formula still uses `SlipLockPropertiesCalc.`. Regenerate and re-import the profiles, and change custom formulas to `DLP.` (section 1.3). |
| Old settings or car profiles missing after the upgrade | Check SimHub's log for "Migration from Slip Lock Properties Calc". If `PluginsData\DLP\migration-pending.json` exists, the next SimHub start retries the failed part. |
| A tab shows "could not start" | The module failed at start-up; the other module keeps working. See SimHub's log (lines start with `DLP: ` or `DLP [<module>]: `). |
| "No slip data found" / "Slip: none" | Import the data export profile in ShakeIT Bass Shakers, make it active for the game, restart SimHub. Check "ShakeIT slip" in Slip / Lock → Data source. |
| Lock too weak (for example LMU LMP3) | Make sure "ShakeIT lock" shows Available and the merge toggle is on, then raise the Lock sensitivity for that car. |
| Balance "not available for this sim" | Only iRacing, LMU, rFactor 2 and the Assetto Corsa family are supported. |
| Balance "calibrating steering, drive a few corners" | Normal on the first drive in a sim. Drive some clean, steady corners. |
| Balance "no data" | The raw properties are missing. See Diagnostics → Balance property resolution, and use *Dump property names*. |
| Understeer or oversteer fires constantly, or not at all | Check the confidence and the vehicle model table. With low confidence, drive a few more laps. Otherwise adjust the per-car sensitivity. For a single car, try the class preset or an override. For all cars, use the detector tuning. |
| Model was learned under odd conditions | *Reset learned model...* for that car. Use *Lock learning* to keep a good model. |
| Wrong car profile on LMU | Diagnostics → car key source should read "native model". The first seconds may use a provisional key, which is not saved. |
| Values freeze or errors | Diagnostics → Last error, and the SimHub log (lines start with `DLP: ` or `DLP [Haptics]: `). Enable the debug log file for a 1 Hz trace. |

### 4.2 Speed Dial

| Symptom | Check |
|---|---|
| Banner "SimHub's Control Mapper is not available" / Diagnostics "not available" | Enable the Control Mapper plugin in SimHub and define at least one button role there. DLP re-checks every 5 s. |
| Test press: "Error: could not press X (not a Control Mapper role)" | The role name does not exist in the Control Mapper (typo, renamed role). Pick it from the list; use **Refresh role list** after adding roles. |
| Test press: "Error: no role bound for TC +" | The role box of that direction is empty. Enter a role. |
| Test press says "Pressed ..." but the game's value does not change; dials end with "no response" | The role's output is not bound in the game, or not mapped to an output in the Control Mapper. Bind it in the game's controls and test again. If the game misses short presses, raise **Press duration**. |
| Result "no telemetry", chip "-" or "not reported by this sim" | The sim does not report that channel (TC3 only exists in LMU; generic sims report only TC, ABS and BB; an iRacing car without the control has no variable), or no car is loaded yet. Check Setup → Diagnostics → Telemetry details: the path shows "missing" or "no valid value". |
| Result "direction error" | Increase and Decrease are swapped or point at different channels. Fix the roles, then **Reset** the channel under "Learned for this car". |
| Result "limit reached" | The target is outside what the car allows (for example TC 12 on a car with 11 levels). Edit the preset. |
| Dial stays "Paused: waiting for the game", then "Cancelled: game paused too long" | DLP does not press while the game is paused, in a menu or replay, or while spectating. Return to the car within the pause timeout (Setup → Timing). |
| A Dial button does nothing | Read the status line: "Slot n is empty" (assign a preset to the slot for this car), "Slot n is not in use" (raise Dial slots), "No car loaded", "No game running". Check the binding under Button bindings. |
| Brake bias in ACC / AC EVO / AC Rally differs from the in-game number | These sims report the raw value without the car-specific display offset. Capture the brake bias from the car instead of typing the in-game number. |
| Presets gone after changing livery (LMU) | They should not be: LMU cars are keyed by model. While the tab shows "(identifying...)" edits are not saved yet; they are saved once the car is known. Setup → Diagnostics → Car file shows the file in use. |
| A value is dialed with extra presses back and forth | The learned step may be stale. **Reset** that channel under "Learned for this car". |

---

## 5. Developer notes

- Architecture, threading rules and hard rules: see `CLAUDE.md`. In short, the shell `DLP` runs a module framework
  (`Framework/ModuleHost`); the v1/v2 features live in the `Haptics` module and the speed dial in the `SpeedDial`
  module. `DataUpdate` on the data thread owns all state and is allocation-free. The UI reads a snapshot and queues
  edits. Properties and actions are registered once at start-up (`AttachDelegate`, `AddAction`). Control Mapper
  presses run on a worker thread.
- Adding a module: implement `IDlpModule` (plus a host interface and a view), add one line to `DLP.CreateModules()`
  and the folder's `Compile`/`Page` wildcards in both csproj files.
- Tests:

  ```bash
  "$MSB" Tests/DivebombLogistics.Tests.csproj -p:Configuration=Debug -v:minimal
  Tests/bin/Debug/DivebombLogistics.Tests.exe [--filter <Class.Method substring>] [--replay <csv>] [--properties <file>]
  ```

  The exit code is the number of failed tests. `Tests/Legacy/LegacyPipeline.cs` is a verbatim port of the v1 math;
  the slip/lock tests require bit-identical output against it at 100 % sensitivity. The Speed Dial tests
  (`SpeedDial{Contract|Engine|Telemetry|Module|Persistence|EndToEnd}Tests`) dial against a simulated game
  (`Tests/Fakes/FakeDialGame.cs`).
- Slip sources, in resolution order: ShakeIT `WheelSlip`, ShakeIT `proxyS`, ACC native `Physics.WheelSlip0N` (×20),
  then rF2/LMU wheel rotation (signed). In iRacing, per-wheel speeds (`LFspeed` ...) are probed, but SimHub does
  not provide them live, so the per-wheel detection settles on Mono.
- Game presets (corner-load influence, speed fade, inverse load, pre-gain and pre-cut) are code-only, in
  `Haptics/SlipLock/GamePresets.cs`.

---

## 6. Changelog v2 → v3 (DLP)

**Renamed (one-time action needed, see section 1.3)**
- Plugin "Slip Lock Properties Calc" is now "Divebomb Logistics Plugin" (left menu **DLP**, class
  `DivebombLogistics.DLP`, assembly `DivebombLogistics.Plugin.dll`, version 3.0.0.0). SimHub sees it as a new plugin.
  The old DLL must be removed; the deploy step does it.
- Property prefix `SlipLockPropertiesCalc.` is now `DLP.`. The names after the prefix (all 65) and their values are
  unchanged. There are no aliases, so ShakeIT profiles and dashboards must be updated.
- Generated profiles: `DLP_DataExport`, `DLP_HapticPedals` and `DLP_Balance.siprofile` (were `SlipLock_*`).
- Files: everything lives under `PluginsData\DLP\<module>\`; the Haptics settings moved from SimHub's common settings
  file to `PluginsData\DLP\Haptics\Settings.json`; the debug log is `Logs\DLP_debug.log`; SimHub log lines start with
  `DLP`.

**New**
- **Speed Dial module** (section 3): per-car named presets for TC, TC2, TC3, ABS and brake bias, dialed closed-loop
  through Control Mapper role presses; Dial 1..8 slots, Next/Previous/Apply selected, Set/Reset pairs and Cancel as
  button actions; per-car learning of role direction and step; 12 `DLP.SpeedDial.*` properties and 20 actions;
  presets export/import. Telemetry readers for LMU, iRacing, ACC, AC EVO, AC Rally and a generic fallback.
- Automatic, retrying migration of the v1/v2 settings and car profiles (section 1.3).
- A module framework: the settings page has one tab per module, and a module that fails is isolated from the others.

**Unchanged**
- Every slip/lock and balance behavior, the settings JSON field names and the car profile format.

## 7. Changelog v1 → v2

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
- **Haptic pedal profile layout:** throttle effects moved from channel 0 to channel 2 (brake stays on 1) to match
  the author's pedals, and ShakeIT's gear-shift effect is included.
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
