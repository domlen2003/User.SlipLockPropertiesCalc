# CLAUDE.md - DLP, Divebomb Logistics Plugin (v3)

Context for AI assistants. User and developer documentation: `PROJECT_KNOWLEDGE.md`. The code is the source of truth;
if this file disagrees with it, trust the code and fix this file.

## What the plugin does

A SimHub plugin (C#, .NET Framework 4.8, WPF, `LangVersion 10`, classic csproj without the .NET SDK) built as a module
framework. v3 renamed the former "Slip Lock Properties Calc" (v1/v2) to **DLP** and moved its features into the
**Haptics** module. Modules:

1. **Haptics** (`Haptics/`, v1/v2 features, unchanged behavior):
   - **Slip / lock pipeline (v1, preserved).** Reads a base wheel-slip signal (the ShakeIT export, ACC native slip,
     rF2/LMU wheel rotation or per-wheel speeds), weights it by an estimated corner load, then applies envelopes,
     pedal blends and ABS/TC aggregation. Exported as per-wheel and mono haptic channels (`DLP.SlipLock.*`). With
     every sensitivity at 100 %, an unsigned source and no ShakeIT WheelLock, the output is bit-identical to v1.
   - **Balance (v2).** Understeer/oversteer detection exported as `DLP.Balance.Understeer` / `DLP.Balance.Oversteer`
     (0..1), plus tags and debug values. It compares the yaw rate a lagged bicycle model predicts from steering with
     the yaw rate the car actually delivers, adds countersteer, body-slip and spin detectors, and, where the sim
     reports tyre slip angles, a direct front/rear comparison. The vehicle model is learned per car.
2. **SpeedDial** (`SpeedDial/`, tab "Speed Dial"): per-car named setup presets (TC, TC2 = TC Cut, TC3 = TC Slip, ABS,
   brake bias) dialed in by Control Mapper role presses, closed-loop on the game's telemetry: press, wait for the value
   to change, press again until it matches. Button actions: Dial 1..8 slots, next/previous/apply-selected, Set/Reset
   pairs (store the current values of a channel subset per car, dial back to them later), cancel.

Plugin class `DivebombLogistics.DLP` (`[PluginName("Divebomb Logistics Plugin")]`, `LeftMenuTitle` "DLP"). SimHub
publishes every property and action as `DLP.<name>`, taking the prefix from the runtime class name. Assembly
`DivebombLogistics.Plugin.dll`, version 3.0.0.0.

## Layout and data flow

Namespace = `DivebombLogistics.<folder path>`.

```
DLP.cs           SimHub shell (IPlugin, IDataPlugin, IWPFSettingsV2): SimHub services, migration, module list
                 (CreateModules), settings page. Orchestration itself is Framework/ModuleHost.
Core/            pure shared: MathUtil (Clamp/IsFinite for net48), Wheels, ILog + NullLog
  Telemetry/     ITelemetryReader (+ TelemetryReaderExtensions), FrameContext, CarIdentity, CarKeySource,
                 CarIdentityResolver (owns the LMU native model/class paths)
  Persistence/   JsonFile (atomic writes, quarantine), AsyncJsonWriter<T> (off-thread JSON file writer),
                 SaveScheduler, CarFileNaming (Cars\<Sim>\<CarKey>_<fnv1a8>.json, shared by all modules),
                 UnsavedSideFile (<name>.unsaved.json while the real file is unreadable)
Framework/       pure module framework: IDlpModule, ModuleContext, ModuleHost (+ModuleSlot), IPropertyRegistry,
                 IActionRegistry, IRoleOutput, IDataThreadDispatcher + DataThreadDispatcher, ErrorReporter,
                 CarIdentityTracker (+ICarIdentityState), ShellDiagnostics, DlpNames ("DLP." prefix, "DLP" folder)
Integration/     SimHub glue: PluginManagerTelemetryReader, FrameContextBuilder, SimHubLog, SimHubPropertyRegistry,
                 SimHubActionRegistry, ControlMapperRoleOutput, ModuleRegistration (module + view factory),
                 LegacyMigration (+MigrationResult, MigrationMarker, MigrationPending; pure, unit-tested)
Haptics/         HapticsModule (IDlpModule + IHapticsHost: all v2 orchestration), HapticsPropertyExporter
  SlipLock/      SlipLockProcessor (v1 pipeline + changes A/B/C), LegacyEnvelope, ProxyLoad, MaxGTracker, GamePreset(s)
  Balance/       BalanceEstimator, BalanceLearner, ParamResolver, RecursiveLeastSquares, Histograms, YawLagEstimator,
                 Filters, ClassPresets, BalanceTuning, SimCalibration, VehicleState, ...
    Sources/     IRacingStateSource, RFactorStateSource (LMU/rF2), AccStateSource (ACC/AC/AC EVO/AC Rally),
                 NullStateSource, VehicleStateSourceFactory, SourceField
    Recording/   BalanceRecorder (CSV ring buffer, background flush), BalanceCsv, BalanceRecord
  Telemetry/     PropertyPaths, SlipSourceResolver, WheelSpeedModeDetector, CapabilityTracker, TelemetryEnums
  Settings/      HapticsSettings (v1/v2 PluginSettings), HapticsSettingsStore, GameCapabilities, CarProfile,
                 CarProfileStore
  Profiles/      ShakeItProfileGenerator (data export, haptic pedals, balance .siprofile)
  Diagnostics/   DebugFileLog (Logs\DLP_debug.log), PropertyDump (pure; names come from the context)
  UI/            HapticsView.xaml + HapticsViewModel (MVVM), IHapticsHost, HapticsSnapshot, DebugTab, DisplayText,
                 ViewModels/
SpeedDial/       SpeedDialModule (IDlpModule + ISpeedDialHost: actions, properties, car switching, persistence),
                 DialChannel (+DialChannels registry: ids, names, kinds, Sanitize, Matches), SpeedDialNames
  Model/         JSON DTOs: SpeedDialSettings, ChannelBinding, DialTiming, SetResetPairDefinition, SpeedDialCarData,
                 DialPreset, DialSnapshot (stored pair values), ChannelLearning, ChannelValues
  Engine/        Dialer (closed-loop state machine) + ChannelRun, ChannelPhase, DialerMessages (cached texts);
                 DialContracts (DialRequest, DialStatus, ChannelProgress, DialState, ChannelResult), IDialTelemetry
  Telemetry/     DialTelemetryFactory (by DialGameNames) -> LmuDialTelemetry, IRacingDialTelemetry,
                 AccDialTelemetry (ACC, AC EVO, AC Rally), GenericDialTelemetry; shared engine DialChannelSet/
                 DialChannelSource (+DialValueTransform, DialPathState for diagnostics); DialPropertyPaths
  Persistence/   SpeedDialStore (settings + per-car files, export/import), OrderedFileWriter (ordered async writes)
  UI/            SpeedDialView.xaml + SpeedDialViewModel, ViewModels/ (row items), SpeedDialText,
                 DesignTimeSpeedDialHost; ISpeedDialHost + SpeedDialSnapshot (pure, linked into the tests)
UI/              shared WPF: MainView (SHTabControl, one tab per module), SharedStyles.xaml (text/slider styles of
                 both tabs), ObservableObject, RelayCommand, ViewModels/SliderItem, Controls/ (WheelQuad, LevelMeter),
                 Converters/
Tests/           DivebombLogistics.Tests.csproj console runner; links every pure folder; Fakes/ (ModuleTestRig, fake
                 registries/modules, FakeDialGame = simulated game for dialer tests); Tests/Legacy/LegacyPipeline.cs =
                 verbatim v1 oracle; SpeedDial{Contract|Engine|Telemetry|Module|Persistence|EndToEnd}Tests
```

"Pure" code never references SimHub or WPF and is compiled into the Tests project too: Core, Framework, Haptics
(except `Haptics/UI`, of which only `HapticsSnapshot.cs` and `IHapticsHost.cs` are pure and linked) and
SpeedDial (except `SpeedDial/UI`, of which only `ISpeedDialHost.cs` and `SpeedDialSnapshot.cs` are pure and linked),
`Integration/LegacyMigration.cs` + `MigrationResult.cs` + `MigrationMarker.cs` + `MigrationPending.cs` (listed
explicitly in the Tests csproj). Keep it that way. `HapticsModule` and `SpeedDialModule` are pure: everything
SimHub-specific reaches them through `ModuleContext`.

### Module framework

- `IDlpModule`: `Id` (data folder `PluginsData\DLP\<Id>\`, log prefix), `DisplayName` (tab header), `Init(ModuleContext)`,
  `OnGameChanged(FrameContext)`, `OnCarChanged(CarIdentity)`, `OnSessionChanged(FrameContext)`, `Update(FrameContext)`,
  `OnGameStopped()`, `Tick(now, gameRunning)`, `OnFault(Exception)`, `End()`.
- `ModuleContext` (per module): `Id, Log, Reader, Frame` (the shell's reused FrameContext), `Car` (ICarIdentityState),
  `Properties` (shared IPropertyRegistry), `Actions` (per-module IActionRegistry), `Roles` (shared IRoleOutput),
  `Dispatcher` (the module's own DataThreadDispatcher), `Errors` (the module's ErrorReporter), `Diagnostics`
  (ShellDiagnostics), `DataDirectory`, `LogDirectory` (`<SimHub>\Logs`), `Clock` (monotonic seconds),
  `ListPropertyNames` (all SimHub property names, diagnostics only).
- Modules in `DLP.CreateModules()` (processing and tab order): Haptics, SpeedDial.
- Adding a module: one line in `DLP.CreateModules()`:
  `ModuleRegistration.Create(new XModule(), m => new XView(new XViewModel(m)))`, plus `Compile`/`Page` wildcards for
  the new folder in both csproj files.

Per frame (`DLP.DataUpdate` -> `ModuleHost`):

1. `BeginFrame`: bind every module dispatcher to the data thread and drain it (each action guarded; errors go to the
   module's ErrorReporter).
2. `FrameContextBuilder.Fill`, then `PrepareFrame`. Game running: on a game-name change `OnGameChanged`, then the car
   resets (`OnCarChanged(CarIdentity.None)`); `CarIdentityTracker.Update` (car change or LMU retry) ->
   `OnCarChanged(identity)`; a new `SessionId` -> `OnSessionChanged`. Game not running: `OnGameStopped` once on the
   first such frame (zero outputs, reset envelopes/filters; v1 left stale values).
3. `EndFrame`: `Update` for every active module (game running), then `Tick` for every module.
4. `ShellDiagnostics.RecordFrame` (frame count, smoothed DataUpdate ms).

Fault isolation: every module call is guarded; a throwing module gets its error reported (rate-limited) and
`OnFault` (zero outputs), the other modules keep running. If filling the frame or the shell stage throws, every module
gets `ErrorReporter.Record` + `OnFault` and skips `Update` that frame. A module whose `Init` throws is disabled and its
tab shows the error. Inside Haptics, `Update` keeps the v2 stage isolation (slip/lock, balance, debug log), and
`Tick` runs `SaveScheduler.Tick` and the UI snapshot (at most 20 Hz).

Haptics per frame: max-G CarId rule (v1), `maxG.Update`, capabilities, `slipResolver.Probe` (rate-limited),
`detector.ComputeBaseSlip`, ShakeIT WheelLock read, `processor.Process`; then `balanceSource.Read`,
`estimator.Update`, `recorder.Record` (no-op unless recording); optional 1 Hz debug file log.

SpeedDial per frame: read the five channel values (`IDialTelemetry.TryRead`, NaN = unknown), compute the press gate
(`GameRunning && !GamePaused && !GameInMenu && !IsReplay && !Spectating`), `dialer.Update(now, gate, telemetry,
settings, carData)`, persist learning changes (2 s debounce), mirror the dial status into the exported strings when
`DialStatus.Version` changed. `Tick`: `SaveScheduler.Tick`, snapshot at most 20 Hz (also reads the per-car maxima and
the cached `Describe()` text). Game change: new `IDialTelemetry` from `DialTelemetryFactory` and cancel. Car change:
cancel (except LMU's placeholder key resolving to the native key of the same vehicle), flush the old car, load or
create the new one. Game stop / fault: cancel and export 0.

## Threading (important)

- **Data thread** (`DataUpdate`, 60 Hz or more) owns every module's processing objects, the current car profiles and
  the save schedulers. It must never block and never throw. Errors are logged on the first occurrence, then at most
  every 10 s with a count of suppressed errors (`ErrorReporter`), and are shown in the module's diagnostics.
- **UI thread** never touches data-thread state directly:
  - Reads: `IHapticsHost.CopySnapshot` / `ISpeedDialHost.CopySnapshot` copy the module's snapshot under a lock
    (LastError comes straight from the module's ErrorReporter). Each view model's 100 ms `DispatcherTimer` copies it
    while its tab is visible.
  - Writes: numeric `HapticsSettings` edits go through `IHapticsHost.EditSettings` (posted to the module's
    dispatcher). bool/enum settings may be written directly, followed by `NotifySettingsChanged()`. Per-car edits go
    through host methods that post to the dispatcher. Export and import use `IDataThreadDispatcher.Run` (waits up to
    3 s). SpeedDial: every `ISpeedDialHost` mutation (settings via `EditSettings(s => ...)` with captured values,
    presets, slots, selection, pairs, test presses, learning resets) is posted; the UI never mutates
    `snapshot.Settings`. Row view models are rebuilt only on `PresetsVersion`/`PairsVersion`/`SettingsVersion`/
    `CarDataVersion` changes; Control Mapper roles are listed only when the Setup expander opens or Refresh is clicked.
  - SimHub actions (`IActionRegistry`) are invoked on any thread; their callbacks are posted to the module's
    dispatcher and run at the start of the next frame.
  - `IRoleOutput.Press` only enqueues; `ControlMapperRoleOutput` runs `StartStopRole` on its own worker thread
    (unknown whether it blocks), never on the data thread. The worker lists `GetAvailableButtonRoles()` at start,
    every 5 s and after a rejected press, and publishes them as an immutable set: `IsAvailable` = at least one role,
    and `Press` returns false at once for an empty role, a role the Control Mapper does not define, a full queue
    (64) or after `Dispose` (the dialer then reports NoBinding). `GetButtonRoles()` (UI pickers) calls the Control
    Mapper on the calling thread.
  - Reason: SimHub is a **32-bit process**. An 8-byte double written on one thread can be read torn on another.
- File IO is kept off the data thread. Settings: `AsyncJsonWriter` deep-copies on the data thread and writes on the
  thread pool. Profiles: `CarProfileStore.SaveAsync` serializes on the data thread (a deliberate, rate-limited
  exception) and writes on the thread pool. `End()` drains each dispatcher, then each module saves synchronously.
- Properties are registered once in `Init` through `IPropertyRegistry` (`SimHubPropertyRegistry` calls
  `PluginManager.AttachDelegate(name, GetType() of DLP, provider)`); actions through `SimHubActionRegistry`
  (`PluginManager.AddAction(name, GetType(), start, end)`). Never use `SetPropertyValue` per frame. Do not use the
  `IPluginExtensions.AttachDelegate`/`AddAction<T>` extensions on an interface-typed receiver: they take the prefix from
  the compile-time type. `Init` self-checks that `DLP.SlipLock.MaxSway` is readable and that the class name matches
  the prefix.

## Exported properties (prefix `DLP.`)

**Relative names and value semantics are a compatibility contract** (user ShakeIT profiles and dashboards). v3 only
changed the prefix from `SlipLockPropertiesCalc.` to `DLP.` (no aliases); `Tests/HapticsModuleTests` pins all 65
names and their order.

- SlipLock (43): `SlipLock.{Slip|Lock|ABS|TC|SlipBlend|LockBlend|SlipTC|LockABS}.{FrontLeft|FrontRight|RearLeft|RearRight|Mono}`
  (0..100, rounded to 1 decimal like v1), plus `SlipLock.MaxSway`, `SlipLock.MaxSurge` and `SlipLock.MaxDecel`
  (start at 5, grow only, reset on a CarId change).
- Balance outputs: `Balance.Understeer` and `Balance.Oversteer` (0..1).
- Balance tags (bool): `Balance.PowerOversteer`, `LiftOrBrakeOversteer`, `EntryUndersteer`, `ExitUndersteer`,
  `Countersteer`, `Spin` and `Active`. `Balance.Confidence` is 0..1.
- Balance debug: `Balance.YawRate`, `YawRef` (rad/s), `YawRatio`, `BodySlipDeg`, `G` (1/m), `K` (s²/m²),
  `Theta0` (deg), `TauYaw` (s), `SamplesG` and `SamplesK` (long), `ParamSource` (`default`, `preset`, `learned`,
  `session` or `manual`) and `Path` (`none`, `model` or `direct`). NaN and infinity are exported as 0.
- SpeedDial (`DLP.SpeedDial.*`; `Tests/SpeedDialModuleTests` pins the names and order):
  - Properties (12): `Busy` (bool), `Status` (string: last action or dial message, e.g. "Dialing TC2 (Cut)",
    "Done", "Partly done: ABS no response", "Pair 1 stored", "Slot 3 is empty"), `ActivePreset` (string: label of
    the last job, preset name or "Reset <pair>"), `SelectedPreset` (string), `SelectedIndex` (int, 1-based, 0 = none),
    `PresetCount` (int), `Value.TC1`, `Value.TC2`, `Value.TC3`, `Value.ABS`, `Value.BB` (double; levels, BB = front
    %; unknown exported as 0), `LastResult` (string: "Completed", "Partial", "Failed", "Cancelled" or empty).
  - Actions (20, always registered at the maximum counts so bindings survive slot/pair count changes):
    `SpeedDial.Dial1`..`Dial8`, `SpeedDial.NextPreset`, `SpeedDial.PreviousPreset`, `SpeedDial.ApplySelectedPreset`,
    `SpeedDial.Set1`..`Set4`, `SpeedDial.Reset1`..`Reset4`, `SpeedDial.Cancel`. Users bind them in the tab through
    `ui:ControlsEditor ActionName="DLP.SpeedDial.Dial1"`.

## Persistence

All DLP files live under `<SimHub>\PluginsData\DLP\` (`DlpNames.DataFolderName`), one folder per module.

- Haptics global settings: `PluginsData\DLP\Haptics\Settings.json` (`HapticsSettings`, loaded by
  `HapticsSettingsStore`: missing = defaults, corrupt = quarantined `.bad-<timestamp>` + defaults, unreadable =
  continue from `Settings.unsaved.json` (or defaults) and save there; `UnsavedSideFile` never overwrites an unreadable
  side file either, it falls back to a timestamped `<name>.unsaved-<yyyyMMdd_HHmmss>.json`). Written by `AsyncJsonWriter` 2 s after the last change
  and synchronously in `End()`.
- **Compatibility rules for `HapticsSettings`** (JSON field names unchanged since v1): never rename or remove
  `SlipThrottleBlend`, `TCThrottleBlend`, `LockBrakeBlend`, `ABSBrakeBlend`, `Slip/Lock/TC/ABSThreshold`,
  `GateSlipOnThrottle`, `GateLockOnBrake`, `{Slip|Lock|ABS|TC}{Attack|Release}Ms`, `SpeedWarningLevel` (unused SDK
  leftover, kept on purpose) and `GameCapabilities` (per game: `WheelSpeedMode` "Unknown/PerWheel/Mono",
  `ABSMode`/`TCMode` "Unknown/Available"). v2 added `SchemaVersion`, `ShowDebugView`, `UseShakeItWheelLock`
  (default true), `DebugFileLog`, `Balance` (`BalanceTuning`) and `BalanceCalibration` (per-sim `SimCalibration`).
  `Normalize()` repairs whatever is loaded. Dictionaries are case-insensitive.
- Per-car profiles: `PluginsData\DLP\Haptics\Cars\<Sim>\<CarKey>_<fnv1a8>.json` (`CarFileNaming`; `CarProfile`: four
  sensitivities 10..500 % with 100 = v1, `Overrides`, `LearningLocked`, `Learned` = `BalanceLearnedState`). Writes are
  atomic. A corrupt file is renamed to `.bad-<timestamp>`. A file that exists but cannot be read makes that session's
  saves go to `<name>.unsaved.json`. Failed async writes are retried (250 ms, 1 s, 4 s, 15 s). Saved 2 s after a user
  edit, at most every 60 s while learning, on car change, and in `End()`.
- Car key (`CarIdentityResolver`, resolved by the shell's `CarIdentityTracker` and shared by all modules): on LMU/rF2
  the key is `PlayerNativeTelemetry.mVehicleModel`, because SimHub's CarId/CarModel are livery specific there.
  Elsewhere it is `CarModel`, then `CarId`. When LMU falls back to a livery key, resolution is retried once per second
  for up to 10 s (`ICarIdentityState.IsProvisional`); modules must not save per-car data under a provisional key.
  Haptics carries the provisional profile's state over to the native key if that key has no stored profile yet.
- Other Haptics files: `PluginsData\DLP\Haptics\Recordings\<sim>_<car>_<yyyyMMdd_HHmmss>.csv` and
  `PluginsData\DLP\Haptics\property-dump-<timestamp>.txt`. Debug log: `<SimHub>\Logs\DLP_debug.log` (falls back to
  %TEMP%). Generated ShakeIT profiles: `Documents\SimHub\DLP_{DataExport|HapticPedals|Balance}.siprofile` (profile
  names "DLP Data Export", "DLP Haptic Pedals", "DLP Balance"; formulas use `[DLP.…]`; users re-import once).
- SpeedDial global settings: `PluginsData\DLP\SpeedDial\Settings.json` (`SpeedDialSettings`: `SchemaVersion`,
  `Channels` id -> `ChannelBinding` {`Enabled`, `IncreaseRole`, `DecreaseRole`; defaults TC1 `TractionControl+/-`, TC2
  `TC_PowerCut+/-`, TC3 `TC_SlipAngle+/-`, ABS `ABS+/-`, BB `BrakeBalanceFront/Rear`}, `SlotCount` 1..8 (4), `Pairs`
  1..4 (`Name`, channel ids; default "Pair 1" all, "Pair 2" BB), `Timing` (`PressMs` 70, `GapMs` 90,
  `ConfirmTimeoutMs` 800, `MaxStallPresses` 3, `MaxPressesPerChannel` 150, `GatePauseTimeoutMs` 10000),
  `ChannelOrder`). Missing = defaults, corrupt = `.bad-<timestamp>` + defaults, unreadable = continue from and save to
  `Settings.unsaved.json`. Saved 2 s after an edit and in `End()` (only if edited this session).
- SpeedDial per-car data: `PluginsData\DLP\SpeedDial\Cars\<Sim>\<CarKey>_<fnv1a8>.json` (`SpeedDialCarData`:
  `Presets` {`Id` guid "N", `Name`, `Values` id -> value, included channels only}, `SlotPresetIds[8]`,
  `SelectedPresetId`, `PairSnapshots[4]` {`Values`, `CapturedUtc`}, `Learning` id -> `ChannelLearning` {`Direction`,
  `DirectionConfirmed`, `Step`, `ObservedMin/Max`}). Channel ids `TC1, TC2, TC3, ABS, BB` are JSON keys: never
  rename. Same robustness as the Haptics profiles (atomic, quarantine, `<name>.unsaved.json`, retries) through
  `OrderedFileWriter`, which keeps queued content visible to loads until it is on disk. Saved 2 s after a change
  (presets, slots, selection, pair values, learning), on car change and in `End()` (only if changed); never under a
  provisional key (the data is carried over to the native key if that has no file yet). Export/import of presets:
  a `SpeedDialCarData` JSON file; import appends presets to the current car (colliding ids renewed, at most 100).
- **Migration from v1/v2** (`Integration/LegacyMigration`, runs in `DLP.Init` before the modules): copies
  `PluginsData\Common\SlipLockPropertiesCalc.GeneralSettings.json` (old SimHub common settings, Newtonsoft defaults)
  to `Haptics\Settings.json` if that does not exist, and the tree `PluginsData\SlipLockPropertiesCalc\Cars\` to
  `Haptics\Cars\` if that does not exist (via a `Cars.migrating` staging folder; skips `*.tmp`, `*.bad-*`,
  `*.unsaved.json`). Old files are never modified or deleted. Marker `PluginsData\DLP\migration.json`
  (`FromSlipLockPropertiesCalc`, `Settings`, `CarProfiles`, totals over all runs) skips later runs. Each part has its
  own try/catch (any exception). A failed part writes `PluginsData\DLP\migration-pending.json` (`MigrationPending`:
  `SettingsFailed`, `CarProfilesFailed`, plus what earlier runs already did) instead of the marker, because the
  Haptics session that follows creates `Settings.json` and `Cars\` with defaults. The next start retries exactly the
  failed parts even though their targets exist: settings overwrite `Settings.json` (old copy kept as
  `Settings.json.pre-migration`), cars merge file by file and never overwrite an existing profile. An unreadable
  pending record retries both parts. On completion the marker is written and the pending record deleted.

## SpeedDial: how dialing works (`SpeedDial/Engine/Dialer.cs`)

- A job (`DialRequest`: label, preset id, target per channel, NaN = skip) is dialed one channel at a time in
  `settings.ChannelOrder`. `Start` replaces a running job (no Cancelled report) and never presses; `Cancel(reason)`
  ends it. All per-frame work is allocation-free; status texts are cached (`DialerMessages`).
- Per channel: read the value (missing: wait one confirm window, then NoTelemetry; unsupported: NoTelemetry at once);
  clamp the target to [0, `TryGetMax`]; done when `DialChannels.Matches` (integers equal after rounding; BB within
  max(0.05, step/2)). Otherwise press the Increase role when `(target - value) * learning.Direction > 0`, else
  Decrease (empty role or `Press` false = NoBinding, disabled binding = Skipped), then wait for a change or
  max(`ConfirmTimeoutMs`, `PressMs`); the next press waits `GapMs` after the change and `PressMs + GapMs` after the
  previous press started (about 160 ms per step with defaults).
- Learning (per car, persisted): on a change it records `Step` (smallest |delta|), the observed range and
  `DirectionConfirmed`. The first wrong-way single step flips `Direction`; a second gives DirectionError and restores
  the direction the run started with. Jumps (|delta| > 1.5 x step, > 2 before a step is known) never teach anything;
  one contrary step after the run proved its direction counts as a manual press (manual presses during a dial are
  fine); the same wrong-way jump twice from the same value is a wrap-around (dial back, LimitReached); three other
  wrong-way jumps give DirectionError. A reversed binding stuck at the far limit tries the other role once.
- Results: `MaxStallPresses` unanswered presses = LimitReached (at the reported max, the min, or after moving that way
  in this run) or NoResponse; crossing the target without matching = ClosestPossible (one press back first if the
  previous value was closer); `MaxPressesPerChannel` = MaxPresses. Job state: Completed (every targeted channel
  Reached/ClosestPossible/Skipped), Partial (some reached, some failed), Failed (none reached), Cancelled.
- Gate: no presses while the gate is closed; the job is Paused, resumes (restarting the confirm wait) when it opens
  and is Cancelled after `GatePauseTimeoutMs` (at once for 0). A press still in flight when a job is replaced settles
  before the new job reads that channel.
- Module rules: Dial/Apply/Reset need a car and a running game (otherwise only a status text); a slot beyond
  `SlotCount` or an empty slot only sets a status. Set k stores pair k's channels that have valid telemetry (keeps the
  old values when none has). Reset k dials only channels whose current value does not `Match` the stored one
  ("<pair> already set" when none differs). Next/Previous wrap around (no selection: Next = first, Previous = last)
  and need only a car. A new job replaces a running one. A UI test press is ignored while a job runs ("Busy
  dialing"). A preset sits on at most one slot (assigning it frees the others); the first preset created becomes
  the selection when none is selected; deleting a preset clears its slots and the selection.

## Haptics slip/lock pipeline: deliberate v2 changes (`Haptics/SlipLock/SlipLockProcessor.cs`)

- A: per-car Slip and Lock sensitivity multiply the raw slip first (a factor of 1.0 keeps v1's exact operation
  order).
- B: signed sources (rF2 rotation, per-wheel speed) always take lock from negative slip. v1 synthesized lock from
  positive slip for every source.
- C: lock = max(lock, ShakeIT `WheelLock` export) when the export exists and `UseShakeItWheelLock` is set.
- Game presets (`Haptics/SlipLock/GamePresets.cs`) are code-defined, shown read-only, and looked up
  case-insensitively. v1 missed "AssettoCorsaEVO" and "BeamNgDrive", which fell back to Default.
- Edits must keep `Tests/SlipLockProcessorTests` (bit-equality against `Tests/Legacy/LegacyPipeline.cs`) green.

## Balance algorithm in short (`Haptics/Balance/BalanceEstimator.cs`)

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

## Sim adapters and raw paths (`Haptics/Balance/Sources/`, `Haptics/Telemetry/PropertyPaths.cs`)

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
- Slip sources: `ShakeITBSV3Plugin.Export.WheelSlip.<Wheel>`, then `.proxyS.<Wheel>`, then `...Physics.WheelSlip0N`,
  then rF2 `mWheels0N.mRotation` (radius from `mStaticUndeflectedRadius`, cm). Lock:
  `ShakeITBSV3Plugin.Export.WheelLock.<Wheel>`. Capability flags come from `DataCorePlugin.GameData.ABSLevel` and
  `TCLevel`, which count as exported if they exist.
- Per-wheel speeds (`...Telemetry.LFspeed` etc.) do not exist live in iRacing, so detection settles on Mono.
- Car identity (Core): LMU `DataCorePlugin.GameRawData.PlayerNativeTelemetry.mVehicleModel` / `mVehicleClass`.
- SpeedDial dial telemetry (`SpeedDial/Telemetry/DialPropertyPaths.cs`; levels must be >= 0; BB only when the front
  share is strictly between 0 and 100 %, because 0 means "not reported"):
  - LMU `...PlayerNativeTelemetry.`: `mTC/mTCMax`, `mTCCut/mTCCutMax`, `mTCSlip/mTCSlipMax`, `mABS/mABSMax` (bytes,
    max 0 = no max), `mRearBrakeBias` (rear fraction, front % = (1 - x)·100). Verified by reflection on
    `RfactorReader.LMU.TelemInfoV01`. All five channels.
  - iRacing `DataCorePlugin.GameRawData.Telemetry.`: `dcTractionControl`, `dcTractionControl2`, `dcABS`,
    `dcBrakeBias` (front %); no TC3, no maxima (checked against SimHub's iRacing `SampleData.json`).
  - ACC / AC Rally: `...Graphics.TC`, `TCCut`, `ABS` (int), `...Physics.BrakeBias` (raw front fraction x 100, no
    in-game offset); no TC3, no maxima. Verified on the ACSharedMemory ACC/ACR models.
  - AC EVO: `...Graphics.electronics.tc_level`, `tc_cut_level`, `abs_level` (-1 = car lacks it), maxima
    `...Graphics.electronics_max_limit.*`; fallback `Physics.tc`, `Physics.abs`; BB `Physics.brakeBias` x 100, then
    `Graphics.electronics.brake_bias` (fraction if < 1, else percent). The nested dotted paths assume SimHub flattens
    structs like `mWheels01.mSurfaceType`: unverified live.
  - Every other game (also RFactor2, AssettoCorsa): `DataCorePlugin.GameData.TCLevel`, `ABSLevel`, `BrakeBias`.

## Build and test

```bash
MSB="/c/Users/domin/AppData/Local/Programs/Rider/tools/MSBuild/Current/Bin/amd64/MSBuild.exe"
cd "/c/Program Files (x86)/SimHub/PluginSdk/User.SlipLockPropertiesCalc"   # repo folder keeps its old name
"$MSB" DivebombLogistics.Plugin.csproj -p:Configuration=Release -p:DeployToSimHub=false -t:Rebuild -v:minimal
"$MSB" Tests/DivebombLogistics.Tests.csproj -p:Configuration=Debug -v:minimal
Tests/bin/Debug/DivebombLogistics.Tests.exe                    # all tests; exit code = failures
Tests/bin/Debug/DivebombLogistics.Tests.exe --filter Balance   # "Class.Method" substring
Tests/bin/Debug/DivebombLogistics.Tests.exe --replay rec.csv   # offline estimator replay summary
Tests/bin/Debug/DivebombLogistics.Tests.exe --properties f.txt # exported property names (v2 -> DLP)
```

- `DeployToSimHub` defaults to **true**: it copies `DivebombLogistics.Plugin.dll/.pdb` into `%SIMHUB_INSTALL_PATH%`
  and deletes the obsolete `User.SlipLockPropertiesCalc.dll/.pdb` there (ContinueOnError) so SimHub does not load both
  plugins. The delete runs only if the Copy task's `CopiedFiles` contains the new DLL (copied or up to date) and it
  exists in SimHub; otherwise a warning says the old plugin was kept. Always pass `-p:DeployToSimHub=false`, including when building `DivebombLogistics.Plugin.sln`.
- Tests use the `[Test]` attribute (`Tests/TestFramework.cs`) and must be deterministic (seeded `Random`) and fast.
  A new file in a linked folder compiles automatically through the csproj wildcards. Module tests run the real
  `ModuleHost` with fakes through `Tests/Fakes/ModuleTestRig.cs`.

## Hard rules

- **Never kill, close or restart SimHub.** Never write into `C:\Program Files (x86)\SimHub\` outside this repo.
  Deploying is the user's step.
- Do not rename the plugin class `DivebombLogistics.DLP`, the namespace, the plugin attributes, the data folder
  names (`PluginsData\DLP\<module Id>`), the `HapticsSettings`/`CarProfile`/`SpeedDialSettings`/`SpeedDialCarData`
  JSON field names, the SpeedDial channel ids (`TC1, TC2, TC3, ABS, BB`; new channels are appended to the enum
  only), module ids, or any exported property/action name or its value semantics.
- **Zero allocations in the steady-state hot path** (`DataUpdate` and everything it calls per frame, i.e. every
  module's `Update`/`Tick`): no LINQ, no string building or formatting, no `new`, no per-frame closures, no exceptions
  for control flow, no file IO, no logging except rate-limited errors. Cache property path strings once. Allocations
  are fine only on game, car or session change. `FrameworkTests.Host_SteadyStateFrameDoesNotAllocate` guards the shell.
- Never throw out of `DataUpdate`. Treat NaN and infinity as unknown.
- net48 gaps: use `MathUtil.Clamp` and `MathUtil.IsFinite`. Not available: `Span`, ranges or indices, `init`,
  `Dictionary.TryAdd`, `KeyValuePair` deconstruction, `string.Contains(char)`, `HashCode`,
  `Enum.GetValues<T>()`.
- Style: `internal sealed` by default, one type per file, namespaces mirror folders, usings sorted (System first),
  named constants, XML docs on types and non-obvious members, comments explain why. English only.

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
- SpeedDial: AC EVO paths and units are unverified (check the telemetry details under Setup > Diagnostics in a live
  session); ACC/AC Rally brake bias is raw (no in-game display offset). A learned step that is too small (stale) only
  clears with "Reset learning"; it can cost a few extra presses but the job still ends.
- Unknown whether Control Mapper's `StartStopRole` blocks for the press duration (hence the worker thread).
  `GetControlMapperInterface()` never returns null (it wraps a plugin lookup), so availability is judged by
  `GetAvailableButtonRoles()` returning at least one role; a disabled Control Mapper is noticed within 5 s.
- SpeedDial cannot see whether a role is bound in the game: a press the game ignores shows up only as NoResponse
  after `MaxStallPresses` confirm timeouts (about 2.4 s with defaults).
