# CLAUDE.md - AI Assistant Context for DDF Racer Physics Plugin

## Quick Project Summary

This is a **SimHub plugin** (C# .NET 4.8 WPF) that calculates advanced physics effects for racing simulation motion platforms. It replicates DDF Racer v2.5.1 profile calculations and adds custom iRacing-compatible wheel slip calculations.

## Critical Files

- **DDFRacerPlugin.cs** (640 lines) - Main plugin, all calculation logic
- **SettingsControlDemo.xaml** (390 lines) - WPF UI with data bindings
- **SettingsControlDemo.xaml.cs** - Sets DataContext for UI binding
- **User.PluginSdkDemo.csproj** - Build configuration

## Current State (as of 2025-11-03)

### What Works
✅ Sway Throttle calculation (lateral G + slip + throttle, 4 wheels)
✅ Surge Throttle calculation (longitudinal G + slip + throttle, 4 wheels)
✅ Slip Brake calculation (slip + deceleration + brake, 4 wheels)
✅ ABS Brake calculation (brake intensity when ABS active)
✅ Custom slip calculation for iRacing (Schnirbus method)
✅ Side-by-side slip comparison UI (ShakeIT vs Custom)
✅ Error handling with SimHub logging
✅ Max G-force tracking (resets on car change)

### Recent Changes
- Fixed throttle blending: `multi = 0.2` (was 0 with TODO comment)
- Fixed SlipBrake: now uses `blend_brake` instead of raw slip
- Fixed ABS: simplified redundant multiplication
- Added 8 UI properties for slip comparison
- Added error handling with try-catch + logging

### Known Limitations
⚠️ **Requires ShakeIT plugin** for most calculations (proxyL, WheelSlip, max values)
⚠️ **iRacing-only** for custom slip (uses iRacing-specific wheel speed properties)
⚠️ Custom slip is **display-only** (not yet connected to effects/outputs)

## Key Calculations Explained

### 1. Blending Formula (used in 3 effects)
```csharp
multi = 0.2  // 20% modulation
blend = (output * 0.8) + (output * inputPercent * 0.2)
```
This creates 80% raw effect + 20% modulated by driver input (throttle or brake).

### 2. Custom Slip (Schnirbus Method)
```csharp
// Base: standard slip ratio
baseSlip = (wheelSpeed - vehicleSpeed) / vehicleSpeed

// Per-wheel modulation using G-forces
// FL: -lat -long | FR: +lat -long
// RL: -lat +long | RR: +lat +long
customSlip = baseSlip + (latMod * 0.1) + (longMod * 0.1)
```

**Why?** iRacing gives identical slip for all 4 wheels. This creates realistic per-corner variation by adding/subtracting G-forces based on wheel position.

### 3. Minimum Speed Threshold
```csharp
if (vehicleSpeed < 0.1) // 0.36 km/h
    slip = 0.0  // Prevent division by zero
```

## Exported Properties (20 total)

### Per-Wheel (16 properties)
- `DDFRacer.SwayThrottle.{FrontLeft|FrontRight|RearLeft|RearRight}`
- `DDFRacer.SurgeThrottle.{FrontLeft|FrontRight|RearLeft|RearRight}`
- `DDFRacer.SlipBrake.{FrontLeft|FrontRight|RearLeft|RearRight}`
- `DDFRacer.CustomSlip.{FrontLeft|FrontRight|RearLeft|RearRight}`

### Global (4 properties)
- `DDFRacer.MaxSway` / `MaxSurge` / `MaxDecel`
- `DDFRacer.ABSBrake`

## Dependencies & Data Sources

### Required: ShakeIT Plugin
- `ShakeITBSV3Plugin.Export.WheelSlip.{wheel}` - Slip ratio
- `ShakeITBSV3Plugin.Export.proxyL.{wheel}` - Wheel load proxy
- `ShakeITBSV3Plugin.Export.maxsway/maxsurge/maxdecel.All` - Max G values

### Optional: iRacing Wheel Speeds
- `LFspeed`, `RFspeed`, `LRspeed`, `RRspeed` - Wheel speeds in m/s
- Used for custom slip calculation only

### Standard SimHub Telemetry
- `data.NewData.Throttle` / `Brake` - Pedal positions (0-100)
- `data.NewData.AccelerationSway` - Lateral G
- `data.NewData.AccelerationSurge` - Longitudinal G
- `data.NewData.SpeedKmh` - Vehicle speed
- `data.NewData.ABSActive` - ABS status
- `data.NewData.CarId` - Car identifier (for reset logic)

## Build & Deploy

```bash
# Build (from project directory)
msbuild User.PluginSdkDemo.csproj -p:Configuration=Release -t:Rebuild

# Post-build automatically copies to:
%SIMHUB_INSTALL_PATH%\User.PluginSdkDemo.dll
%SIMHUB_INSTALL_PATH%\User.PluginSdkDemo.pdb

# Requires SimHub restart to reload plugin
```

## Debugging

### Log File Location
`C:\Program Files (x86)\SimHub\Logs\` (check latest log file)

### Log Messages to Look For
```
"Starting DDF Racer Plugin"
"DDF Racer Plugin initialized"
"DDF Racer GetWPFSettingsControl called"
"Car changed to {carId}, reset max values"
"DDF Racer DataUpdate error: ..."
"DDF Racer CalculateCustomSlip error: ..."
```

### Common Issues

**UI shows nothing:**
1. Restart SimHub after building
2. Check plugin enabled in Settings → Plugins
3. Check game is running
4. Look for errors in SimHub log

**ShakeIT values all zero:**
- Enable ShakeIT plugin
- Configure ShakeIT for current game
- Check "ShakeIT Available" status in UI

**Custom slip all zero:**
- Only works with iRacing
- Requires speed > 0.1 m/s
- Check if `LFspeed` properties exist in telemetry

## Code Architecture

### Plugin Lifecycle
```
Init() → registers properties
   ↓
DataUpdate() → called every frame at 60+ Hz
   ↓
   ├─ Update max values
   ├─ Check ShakeIT availability
   ├─ Calculate 12 per-wheel effects
   ├─ Calculate 4 custom slip values
   └─ Export 20 properties + update UI
   ↓
End() → save settings
```

### UI Binding
```
DDFRacerPlugin (this)
   ↓ DataContext
SettingsControl (UserControl)
   ↓ XAML Bindings
{Binding MaxSway}
{Binding SwayThrottleFL}
{Binding CustomSlipFL}
etc.
```

All properties implement `INotifyPropertyChanged` for live UI updates.

## Important Constants

```csharp
// Blending factors (tunable)
multi = 0.2           // Throttle blending
multi_brake = 0.2     // Brake blending
latFactor = 0.1       // Lateral G modulation
longFactor = 0.1      // Longitudinal G modulation

// Thresholds
minSpeed = 0.1        // m/s (0.36 km/h)
slipClamp = [-2.0, 2.0]

// Initial max values
MaxSway = 5.0 G
MaxSurge = 5.0 G
MaxDecel = 5.0 G
```

## iRacing Limitation (IMPORTANT)

iRacing **intentionally withholds** per-wheel slip data to prevent competitive advantages. The telemetry API provides the **same slip value for all 4 wheels**.

### Our Workaround
Use the **Schnirbus method**: modulate base slip with G-forces to create realistic per-corner variation. This is an **estimation**, not true per-wheel slip, but provides reasonable haptic feedback.

### Formula Logic
- Right turn (positive lateral G) → outside wheels get more slip
- Acceleration (positive longitudinal G) → rear wheels get more slip
- Braking (negative longitudinal G) → front wheels get more slip

## Future Enhancement Ideas

1. **Make custom slip configurable** - Add UI controls for `latFactor`, `longFactor`
2. **Multi-sim support** - Detect sim and use appropriate telemetry
3. **Connect custom slip to effects** - Actually use it in calculations, not just display
4. **Loaded wheel slip** - Weight amplitude by wheel load (use suspension deflection)
5. **Export as separate profile** - Allow users to choose between ShakeIT and custom
6. **Tire temp integration** - Reduce slip when tires cold
7. **Gamma curves** - Add output shaping for haptics

## Testing the Plugin

### Quick Test
1. Build project
2. Restart SimHub
3. Enable plugin in Settings → Plugins
4. Go to plugin settings page
5. Start iRacing and drive
6. Watch values update in real-time

### What to Check
- Max G values increase during driving
- Max G values reset when changing car
- Sway values change during cornering
- Surge values change during acceleration/braking
- Slip Brake activates when braking
- ABS Brake shows when ABS triggers
- ShakeIT slip displays (left column)
- Custom slip displays (right column)
- Custom slip shows per-corner variation

## Performance Characteristics

- **Update frequency:** 60+ Hz
- **Per-frame cost:** ~30 property reads + 20 property writes + 20 UI updates
- **Performance impact:** Negligible (simple arithmetic only, no complex math)

## Git Repository

```bash
# Current branch: master
# Commits:
# 1. Initial: Added slip comparison, fixed calculations
# 2. Error handling: Added try-catch and logging
```

## Quick Reference: Property Names

### Wheel Name Mapping
```csharp
wheelNames[0] = "FrontLeft"   // FL
wheelNames[1] = "FrontRight"  // FR
wheelNames[2] = "RearLeft"    // RL
wheelNames[3] = "RearRight"   // RR
```

### iRacing Wheel Speed Properties
```csharp
"LFspeed"  // Left Front
"RFspeed"  // Right Front
"LRspeed"  // Left Rear
"RRspeed"  // Right Rear
```

## Key Methods

### DDFRacerPlugin.cs

**Init(PluginManager pluginManager)** - Lines 231-285
- Registers all 20 properties
- Loads settings

**DataUpdate(PluginManager pluginManager, ref GameData data)** - Lines 287-422
- Main calculation loop (60+ Hz)
- Wrapped in try-catch with logging

**CalculateCustomSlip(PluginManager pluginManager, GameData data)** - Lines 491-613
- Reads ShakeIT slip for comparison
- Calculates iRacing custom slip with Schnirbus method
- Wrapped in try-catch with logging

**UpdateMaxValues(...)** - Lines 469-489
- Tracks maximum G-forces seen
- Uses threshold logic to prevent spikes

**GetWPFSettingsControl(...)** - Lines 630-634
- Returns UI control
- Logs when called (for debugging)

## Typical Session Flow

1. SimHub starts → `Init()` called → properties registered
2. Game launches → telemetry starts flowing
3. Every frame (60+ Hz):
   - `DataUpdate()` called
   - Max values updated
   - ShakeIT availability checked
   - 12 wheel effects calculated
   - 4 custom slip values calculated
   - 20 properties exported
   - UI updates automatically via bindings
4. Car changes → Max values reset to 5.0 G
5. Game closes → values stop updating
6. SimHub closes → `End()` called → settings saved

## For Future AI Assistants

### When User Reports "Nothing shows in UI"
1. Ask if they rebuilt the project
2. Ask if they restarted SimHub
3. Check if game is running
4. Check SimHub log for errors
5. Verify plugin is enabled in Settings → Plugins

### When Modifying Calculations
- All calculations happen in `DataUpdate()` (lines 287-422)
- Custom slip in `CalculateCustomSlip()` (lines 491-613)
- Always update both the exported property AND the UI property
- Example:
```csharp
customSlipValues[i] = calculatedValue;
CustomSlipFL = customSlipValues[0];  // UI binding
pluginManager.SetPropertyValue("DDFRacer.CustomSlip.FrontLeft", this.GetType(), customSlipValues[0]);  // Export
```

### When Adding New Properties
1. Add private field + public property with `INotifyPropertyChanged`
2. Register in `Init()` with `pluginManager.AddProperty()`
3. Calculate in `DataUpdate()` or `CalculateCustomSlip()`
4. Export with `pluginManager.SetPropertyValue()`
5. Add to XAML with `{Binding PropertyName}`

### Build Issues
- Requires Visual Studio 2022 or MSBuild
- Requires .NET Framework 4.8
- Requires `SIMHUB_INSTALL_PATH` environment variable
- Post-build event may fail if SimHub is running (file locked)

## Important Notes

⚠️ **DO NOT rename the plugin class** - SimHub uses the class name
⚠️ **DO NOT change namespace** - Breaks existing profiles
⚠️ **DO restart SimHub** after rebuilding - DLL is locked while running
⚠️ **DO preserve property names** - Users may reference them in profiles
✅ **DO add logging** to new code for debugging
✅ **DO use try-catch** around calculations to prevent crashes
✅ **DO clamp values** to prevent extreme outputs

## Contact/References

- **SimHub SDK:** github.com/SHWotever/SimHub/wiki
- **Reference Plugin:** github.com/viper4gh/SimHub-Plugin-CalcLngWheelSlip
- **Community:** simhubdash.com/community-2/
