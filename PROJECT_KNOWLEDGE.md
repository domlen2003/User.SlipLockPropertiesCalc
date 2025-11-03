# DDF Racer Physics Plugin - Project Knowledge

## Project Overview

**Name:** DDF Racer Physics Plugin
**Platform:** SimHub Plugin for Windows
**Language:** C# (.NET Framework 4.8)
**Purpose:** Calculate advanced physics effects for racing simulation motion platforms and haptic feedback

## Architecture

### Core Components

1. **DDFRacerPlugin.cs** - Main plugin class (640+ lines)
   - Implements `IPlugin`, `IDataPlugin`, `IWPFSettingsV2`, `INotifyPropertyChanged`
   - Handles real-time telemetry processing at 60+ Hz
   - Exports properties to SimHub for use in profiles

2. **SettingsControlDemo.xaml** - WPF UI for plugin dashboard
   - Shows real-time values in progress bars and labels
   - Data-bound to plugin properties via `DataContext`

3. **SettingsControlDemo.xaml.cs** - Code-behind for UI
   - Sets `DataContext = plugin` for binding

## Calculated Effects

### 1. Sway Throttle (Lateral + Slip + Throttle)
**Formula:**
```csharp
slip = (proxyL / 50.0) * (wheelSlip * 8.0)
sway = Math.Abs(accelSway) / Math.Max(shakeITMaxSway, 0.1)
multi = 0.2  // 20% throttle modulation

output_sway = sway * slip
blend_sway = (output_sway * 0.8) + (output_sway * throttleNorm * 0.2)
```

**Purpose:** Lateral G-forces modulated by wheel slip and throttle position
**Exported Properties:** `DDFRacer.SwayThrottle.FrontLeft/FrontRight/RearLeft/RearRight`

### 2. Surge Throttle (Longitudinal + Slip + Throttle)
**Formula:**
```csharp
surge = (0.0 - accelSurge) / Math.Max(shakeITMaxSurge, 0.1)
multi = 0.2

output_surge = surge * slip
blend_surge = (output_surge * 0.8) + (output_surge * throttleNorm * 0.2)
```

**Purpose:** Forward/backward acceleration modulated by slip and throttle
**Exported Properties:** `DDFRacer.SurgeThrottle.FrontLeft/FrontRight/RearLeft/RearRight`

### 3. Slip Brake (Slip + Deceleration + Brake)
**Formula:**
```csharp
decel = accelSurge / Math.Max(shakeITMaxDecel, 0.1)
multi_brake = 0.2

output_brake = slip * decel
blend_brake = (output_brake * 0.8) + (output_brake * brakeNorm * 0.2)
```

**Purpose:** Wheel slip during braking with brake pedal modulation
**Exported Properties:** `DDFRacer.SlipBrake.FrontLeft/FrontRight/RearLeft/RearRight`

### 4. ABS Brake
**Formula:**
```csharp
absBrake = absActive ? brake : 0.0
```

**Purpose:** Brake intensity when ABS is active
**Exported Property:** `DDFRacer.ABSBrake`

### 5. Custom Slip (iRacing Compatible)
**Formula (Schnirbus Method):**
```csharp
// Base slip from wheel speeds
baseSlip = (wheelSpeed - vehicleSpeed) / vehicleSpeed

// Per-wheel modulation
latMod = (isLeftWheel) ? -latAccel : latAccel
longMod = (isFrontWheel) ? -longAccel : longAccel

customSlip = baseSlip + (latMod * 0.1) + (longMod * 0.1)
// Clamped to [-2.0, 2.0]
```

**Purpose:** iRacing-compatible slip calculation using wheel speeds and G-force modulation
**Exported Properties:** `DDFRacer.CustomSlip.FrontLeft/FrontRight/RearLeft/RearRight`

## Dependencies

### ShakeIT Plugin
**Required:** Yes (for most calculations)
**Properties Used:**
- `ShakeITBSV3Plugin.Export.WheelSlip.{wheel}` - Wheel slip ratio
- `ShakeITBSV3Plugin.Export.proxyL.{wheel}` - Wheel load proxy
- `ShakeITBSV3Plugin.Export.maxsway.All` - Max lateral G
- `ShakeITBSV3Plugin.Export.maxsurge.All` - Max forward G
- `ShakeITBSV3Plugin.Export.maxdecel.All` - Max decel G

**Availability Check:**
```csharp
var testProp = pluginManager.GetPropertyValue("ShakeITBSV3Plugin.Export.WheelSlip.FrontLeft");
bool hasShakeIT = (testProp != null);
```

### iRacing Telemetry (for Custom Slip)
**Properties:**
- `LFspeed`, `RFspeed`, `LRspeed`, `RRspeed` - Wheel speeds (m/s)
- `SpeedKmh` - Vehicle speed (converted to m/s)
- `AccelerationSway` - Lateral G-force
- `AccelerationSurge` - Longitudinal G-force

**Note:** iRacing doesn't provide true per-wheel slip, so custom calculation uses Schnirbus modulation method.

## Key Configuration Values

### Blending Factors
- `multi = 0.2` - Throttle blending (20% modulated, 80% raw)
- `multi_brake = 0.2` - Brake blending (20% modulated, 80% raw)
- `latFactor = 0.1` - Lateral G modulation for custom slip
- `longFactor = 0.1` - Longitudinal G modulation for custom slip

### Thresholds
- `minSpeed = 0.1 m/s` - Minimum speed to calculate slip (prevents division by zero)
- `slipClamp = [-2.0, 2.0]` - Custom slip value limits

### Max Value Tracking
- `MaxSway = 5.0 G` (initial)
- `MaxSurge = 5.0 G` (initial)
- `MaxDecel = 5.0 G` (initial)
- **Reset:** When car changes (detected via `CarId`)

## Data Flow

```
GameData (60+ Hz)
    ↓
DataUpdate()
    ↓
1. Check game running & car changes
2. Update max values
3. Check ShakeIT availability
4. Calculate 4 wheels × 3 effects = 12 values
5. Calculate custom slip (4 wheels)
6. Export all properties to SimHub
7. Update UI-bound properties
    ↓
UI Updates (via INotifyPropertyChanged)
```

## UI Sections

### 1. Maximum G-Force Values
- Shows MaxSway, MaxSurge, MaxDecel
- Updates dynamically during session
- Resets on car change

### 2. SWAY * THROTTLE % MIXED HALF
- 4 vertical progress bars (FL, FR, RL, RR)
- Shows blended lateral G with slip and throttle

### 3. SURGE * THROTTLE % MIXED HALF
- 4 vertical progress bars
- Shows blended longitudinal G with slip and throttle

### 4. SLIP * SURGE (BRAKES ONLY)
- 4 vertical progress bars
- Shows slip during braking with brake modulation

### 5. ABS * BRAKE %
- Single horizontal progress bar
- Shows brake intensity when ABS active

### 6. WHEEL SLIP COMPARISON (iRacing)
**NEW SECTION - Side-by-side comparison:**
- **Left:** ShakeIT Raw Slip (4 wheels)
- **Right:** Custom Calculated Slip (4 wheels)
- Purpose: Compare ShakeIT values vs custom iRacing-compatible calculation

### 7. Plugin Status
- ShakeIT availability indicator
- Current car ID

## iRacing Limitations & Workarounds

### The Problem
iRacing intentionally withholds per-wheel slip data to prevent competitive advantages. All simulators get the same slip value for all 4 wheels.

### Our Solution: Schnirbus Method
Modulate the base slip value with G-forces to create realistic per-corner variation:

```
FL = baseSlip - latAccel×0.1 - longAccel×0.1  (inside front during right turn)
FR = baseSlip + latAccel×0.1 - longAccel×0.1  (outside front during right turn)
RL = baseSlip - latAccel×0.1 + longAccel×0.1  (inside rear)
RR = baseSlip + latAccel×0.1 + longAccel×0.1  (outside rear)
```

**Logic:**
- Positive lateral G (right turn) → outside wheels (+) get more slip
- Negative lateral G (left turn) → inside wheels (+) get more slip
- Positive longitudinal G (acceleration) → rear wheels (+) get more slip
- Negative longitudinal G (braking) → front wheels (+) get more slip

### Alternative: Direct Wheel Speed Calculation
```csharp
slipRatio = (wheelSpeed - vehicleSpeed) / vehicleSpeed
```
- Positive values: wheel spinning (traction loss)
- Negative values: wheel locking (braking)
- Zero: free rolling

## Exported SimHub Properties

### Per-Wheel Effects (12 properties)
- `DDFRacer.SwayThrottle.FrontLeft/FrontRight/RearLeft/RearRight`
- `DDFRacer.SurgeThrottle.FrontLeft/FrontRight/RearLeft/RearRight`
- `DDFRacer.SlipBrake.FrontLeft/FrontRight/RearLeft/RearRight`

### Global Values (3 properties)
- `DDFRacer.MaxSway`
- `DDFRacer.MaxSurge`
- `DDFRacer.MaxDecel`

### ABS (1 property)
- `DDFRacer.ABSBrake`

### Custom Slip (4 properties)
- `DDFRacer.CustomSlip.FrontLeft/FrontRight/RearLeft/RearRight`

**Total: 20 exported properties**

## Build & Deployment

### Build Process
```bash
msbuild User.PluginSdkDemo.csproj -p:Configuration=Release -t:Rebuild
```

### Post-Build Event
```batch
XCOPY /Y /R "$(TargetDir)\$(TargetName)*" "%SIMHUB_INSTALL_PATH%"
```
Automatically copies DLL and PDB to SimHub installation directory.

### Environment Variable
`SIMHUB_INSTALL_PATH` must be set to SimHub directory (typically `C:\Program Files (x86)\SimHub\`)

### Files Copied
- `User.PluginSdkDemo.dll` - Plugin binary
- `User.PluginSdkDemo.pdb` - Debug symbols

## Error Handling

All critical methods wrapped in try-catch with logging:

```csharp
try {
    // Calculation logic
} catch (Exception ex) {
    SimHub.Logging.Current.Error($"DDF Racer error: {ex.Message}");
    SimHub.Logging.Current.Error($"Stack trace: {ex.StackTrace}");
}
```

**Logged Operations:**
- Plugin initialization
- DataUpdate main loop
- CalculateCustomSlip method
- GetWPFSettingsControl calls
- Car changes

## Debugging Tips

### Check Plugin is Loaded
Look for in SimHub log:
```
"Starting DDF Racer Plugin"
"DDF Racer Plugin initialized"
```

### Check UI is Loading
Look for:
```
"DDF Racer GetWPFSettingsControl called"
```

### Check for Errors
Search log for:
```
"DDF Racer DataUpdate error:"
"DDF Racer CalculateCustomSlip error:"
```

### Common Issues

**1. Nothing shows in UI**
- Restart SimHub after building
- Check plugin is enabled in Settings → Plugins
- Verify game is running and sending telemetry

**2. ShakeIT values all zero**
- Enable ShakeIT plugin
- Verify ShakeIT is configured for current game
- Check "ShakeIT Available" status in plugin UI

**3. Custom slip all zero**
- Only works with iRacing (uses iRacing-specific wheel speed properties)
- Requires minimum speed > 0.1 m/s
- Check if `LFspeed`, `RFspeed`, etc. properties exist

## Future Enhancement Ideas

1. **Configurable modulation factors** - Allow user to tune `latFactor`, `longFactor`
2. **Multi-sim support** - Extend custom slip to other simulators
3. **Loaded wheel slip** - Weight amplitude by estimated wheel load
4. **Tire temperature integration** - Modulate effects by tire temp
5. **Gamma curves** - Add user-configurable output curves
6. **Export profiles** - Save/load effect configurations

## References

### Formula Sources
- **DDF Racer v2.5.1** - Original profile calculations
- **Schnirbus Method** - Community iRacing workaround for per-wheel variation
- **SAE J670** - Standard slip ratio formula
- **viper4gh/SimHub-Plugin-CalcLngWheelSlip** - Open source slip calculation reference

### Key Concepts
- **Slip Ratio:** (wheel_speed - vehicle_speed) / vehicle_speed
- **Slip Angle:** arctan(lateral_velocity / longitudinal_velocity)
- **Load Transfer:** Weight shifting during cornering/braking affects wheel grip
- **Schnirbus Modulation:** Using G-forces to estimate per-wheel load and slip variation

## File Structure

```
User.SlipLockPropertiesCalc/
├── .gitignore                      # Git ignore patterns
├── DDFRacerPlugin.cs              # Main plugin logic (640 lines)
├── DataPluginDemoSettings.cs      # Settings class (unused currently)
├── SettingsControlDemo.xaml       # UI layout (390 lines)
├── SettingsControlDemo.xaml.cs    # UI code-behind
├── CustomDialog.xaml              # Unused dialog
├── CustomDialog.xaml.cs           # Unused dialog code
├── User.PluginSdkDemo.csproj     # Project file
├── User.PluginSdkDemo.sln        # Solution file
├── GettingStarted.txt            # SimHub plugin SDK notes
├── sdkmenuicon.png               # Plugin icon (24×24)
├── Properties/
│   ├── AssemblyInfo.cs           # Assembly metadata
│   ├── DesignTimeResources.xaml  # Design-time resources
│   ├── Resources.resx            # Embedded resources
│   └── Resources.Designer.cs     # Generated resource code
├── bin/                          # Build output (ignored)
├── obj/                          # Build intermediates (ignored)
└── .vs/                          # Visual Studio cache (ignored)
```

## Git History

### Commit 1: Initial commit
- Fixed throttle/brake blending (multi = 0.2)
- Fixed SlipBrake to use blend_brake
- Added ShakeIT slip display properties (8)
- Added custom slip calculation
- Implemented side-by-side comparison UI
- Added .gitignore

### Commit 2: Error handling
- Added try-catch with logging to DataUpdate
- Added try-catch with logging to CalculateCustomSlip
- Added "Game not running" status
- Added logging to GetWPFSettingsControl

## Testing Checklist

### Basic Functionality
- [ ] Plugin loads in SimHub
- [ ] UI displays in plugin dashboard
- [ ] Max G values update during driving
- [ ] Max G values reset on car change
- [ ] ShakeIT status shows correctly

### Effects Calculation
- [ ] Sway Throttle values change during cornering
- [ ] Surge Throttle values change during acceleration
- [ ] Slip Brake values activate during braking
- [ ] ABS Brake activates when ABS triggers

### iRacing Custom Slip
- [ ] ShakeIT slip values display (left side)
- [ ] Custom slip values display (right side)
- [ ] Custom slip shows per-corner variation
- [ ] Custom slip responds to cornering (lateral G)
- [ ] Custom slip responds to acceleration/braking (longitudinal G)

### Edge Cases
- [ ] No errors when game not running
- [ ] No errors when ShakeIT disabled
- [ ] No errors at very low speeds (< 0.1 m/s)
- [ ] No division by zero errors
- [ ] Values clamp correctly to reasonable ranges

## Performance Notes

- **Update Rate:** 60+ Hz (called every frame)
- **Per-Frame Operations:**
  - 4 ShakeIT property reads (WheelSlip)
  - 4 ShakeIT property reads (proxyL)
  - 3 ShakeIT property reads (max values)
  - 4 iRacing property reads (wheel speeds)
  - 20 property writes (exports)
  - 20 UI property updates (INotifyPropertyChanged)
- **Optimization:** All calculations use simple arithmetic (no complex math)

## Version History

**v1.0** (2025-11-03)
- Initial implementation with DDF Racer profile calculations
- Added iRacing custom slip with Schnirbus modulation
- Side-by-side slip comparison UI
- Error handling and logging
