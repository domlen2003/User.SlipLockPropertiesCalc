using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;

namespace User.SlipLockPropertiesCalc
{
    [PluginDescription("Calculates wheel slip, lock, and physics properties for motion platforms")]
    [PluginAuthor("Your Name")]
    [PluginName("Slip Lock Properties Calc")]
    public class SlipLockPropertiesCalc : IPlugin, IDataPlugin, IWPFSettingsV2, INotifyPropertyChanged
    {
        public SlipLockSettings Settings;

        public PluginManager PluginManager { get; set; }
        public ImageSource PictureIcon => this.ToIcon(Properties.Resources.sdkmenuicon);
        public string LeftMenuTitle => "Slip Lock Calc";

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // ===================== Detection State Machine =====================

        private enum SlipDetectionState { Loading, Detecting, UsePerWheel, UseMono }
        private SlipDetectionState _detectionState = SlipDetectionState.Loading;
        private int _dynamicFrameCount = 0;
        private const int DetectionFrames = 60; // ~1s at 60Hz
        private bool _retestRequested = false;
        private string _lastGameName = "";
        private bool _absEverTriggered = false;
        private bool _tcEverTriggered = false;

        // ===================== Wheel speed property names =====================

        private static readonly string[] wheelSpeedProps = { "LFspeed", "RFspeed", "LRspeed", "RRspeed" };
        private readonly string[] wheelNames = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };

        // ===================== UI Bindable Properties =====================

        // Max G values
        private double _maxSway = 5.0;
        public double MaxSway { get => _maxSway; set { _maxSway = value; OnPropertyChanged(); } }

        private double _maxSurge = 5.0;
        public double MaxSurge { get => _maxSurge; set { _maxSurge = value; OnPropertyChanged(); } }

        private double _maxDecel = 5.0;
        public double MaxDecel { get => _maxDecel; set { _maxDecel = value; OnPropertyChanged(); } }

        // Status
        private string _currentCarId = "N/A";
        public string CurrentCarId { get => _currentCarId; set { _currentCarId = value; OnPropertyChanged(); } }

        private string _shakeITAvailable = "Checking...";
        public string ShakeITAvailable { get => _shakeITAvailable; set { _shakeITAvailable = value; OnPropertyChanged(); } }

        private string _currentGame = "";
        public string CurrentGame { get => _currentGame; set { _currentGame = value; OnPropertyChanged(); } }

        private string _slipSource = "Detecting...";
        public string SlipSource { get => _slipSource; set { _slipSource = value; OnPropertyChanged(); } }

        private string _perWheelSupport = "Unknown";
        public string PerWheelSupport { get => _perWheelSupport; set { _perWheelSupport = value; OnPropertyChanged(); } }

        private string _detectionStatus = "";
        public string DetectionStatus { get => _detectionStatus; set { _detectionStatus = value; OnPropertyChanged(); } }

        private bool _isDetecting = false;
        public bool IsDetecting { get => _isDetecting; set { _isDetecting = value; OnPropertyChanged(); } }

        // Detection condition indicators
        private string _detectSpeedCond = "";
        public string DetectSpeedCond { get => _detectSpeedCond; set { _detectSpeedCond = value; OnPropertyChanged(); } }

        private string _detectCornerCond = "";
        public string DetectCornerCond { get => _detectCornerCond; set { _detectCornerCond = value; OnPropertyChanged(); } }

        private string _detectBrakeCond = "";
        public string DetectBrakeCond { get => _detectBrakeCond; set { _detectBrakeCond = value; OnPropertyChanged(); } }

        // Sway Throttle values (4 corners)
        private double _swayThrottleFL; public double SwayThrottleFL { get => _swayThrottleFL; set { _swayThrottleFL = value; OnPropertyChanged(); } }
        private double _swayThrottleFR; public double SwayThrottleFR { get => _swayThrottleFR; set { _swayThrottleFR = value; OnPropertyChanged(); } }
        private double _swayThrottleRL; public double SwayThrottleRL { get => _swayThrottleRL; set { _swayThrottleRL = value; OnPropertyChanged(); } }
        private double _swayThrottleRR; public double SwayThrottleRR { get => _swayThrottleRR; set { _swayThrottleRR = value; OnPropertyChanged(); } }

        // Surge Throttle values (4 corners)
        private double _surgeThrottleFL; public double SurgeThrottleFL { get => _surgeThrottleFL; set { _surgeThrottleFL = value; OnPropertyChanged(); } }
        private double _surgeThrottleFR; public double SurgeThrottleFR { get => _surgeThrottleFR; set { _surgeThrottleFR = value; OnPropertyChanged(); } }
        private double _surgeThrottleRL; public double SurgeThrottleRL { get => _surgeThrottleRL; set { _surgeThrottleRL = value; OnPropertyChanged(); } }
        private double _surgeThrottleRR; public double SurgeThrottleRR { get => _surgeThrottleRR; set { _surgeThrottleRR = value; OnPropertyChanged(); } }

        // Slip Brake values (4 corners)
        private double _slipBrakeFL; public double SlipBrakeFL { get => _slipBrakeFL; set { _slipBrakeFL = value; OnPropertyChanged(); } }
        private double _slipBrakeFR; public double SlipBrakeFR { get => _slipBrakeFR; set { _slipBrakeFR = value; OnPropertyChanged(); } }
        private double _slipBrakeRL; public double SlipBrakeRL { get => _slipBrakeRL; set { _slipBrakeRL = value; OnPropertyChanged(); } }
        private double _slipBrakeRR; public double SlipBrakeRR { get => _slipBrakeRR; set { _slipBrakeRR = value; OnPropertyChanged(); } }

        private double _absBrake; public double ABSBrake { get => _absBrake; set { _absBrake = value; OnPropertyChanged(); } }

        // ShakeIT Raw Slip (reference)
        private double _shakeITSlipFL; public double ShakeITSlipFL { get => _shakeITSlipFL; set { _shakeITSlipFL = value; OnPropertyChanged(); } }
        private double _shakeITSlipFR; public double ShakeITSlipFR { get => _shakeITSlipFR; set { _shakeITSlipFR = value; OnPropertyChanged(); } }
        private double _shakeITSlipRL; public double ShakeITSlipRL { get => _shakeITSlipRL; set { _shakeITSlipRL = value; OnPropertyChanged(); } }
        private double _shakeITSlipRR; public double ShakeITSlipRR { get => _shakeITSlipRR; set { _shakeITSlipRR = value; OnPropertyChanged(); } }

        // Custom Slip output
        private double _customSlipFL; public double CustomSlipFL { get => _customSlipFL; set { _customSlipFL = value; OnPropertyChanged(); } }
        private double _customSlipFR; public double CustomSlipFR { get => _customSlipFR; set { _customSlipFR = value; OnPropertyChanged(); } }
        private double _customSlipRL; public double CustomSlipRL { get => _customSlipRL; set { _customSlipRL = value; OnPropertyChanged(); } }
        private double _customSlipRR; public double CustomSlipRR { get => _customSlipRR; set { _customSlipRR = value; OnPropertyChanged(); } }

        // Dual channel: WheelSlipTC (traction loss)
        private string _slipTCMode = "Detecting...";
        public string SlipTCMode { get => _slipTCMode; set { _slipTCMode = value; OnPropertyChanged(); } }
        private double _wheelSlipTCFL; public double WheelSlipTCFL { get => _wheelSlipTCFL; set { _wheelSlipTCFL = value; OnPropertyChanged(); } }
        private double _wheelSlipTCFR; public double WheelSlipTCFR { get => _wheelSlipTCFR; set { _wheelSlipTCFR = value; OnPropertyChanged(); } }
        private double _wheelSlipTCRL; public double WheelSlipTCRL { get => _wheelSlipTCRL; set { _wheelSlipTCRL = value; OnPropertyChanged(); } }
        private double _wheelSlipTCRR; public double WheelSlipTCRR { get => _wheelSlipTCRR; set { _wheelSlipTCRR = value; OnPropertyChanged(); } }

        // Dual channel: WheelLockABS (wheel locking)
        private string _lockABSMode = "Detecting...";
        public string LockABSMode { get => _lockABSMode; set { _lockABSMode = value; OnPropertyChanged(); } }
        private double _wheelLockABSFL; public double WheelLockABSFL { get => _wheelLockABSFL; set { _wheelLockABSFL = value; OnPropertyChanged(); } }
        private double _wheelLockABSFR; public double WheelLockABSFR { get => _wheelLockABSFR; set { _wheelLockABSFR = value; OnPropertyChanged(); } }
        private double _wheelLockABSRL; public double WheelLockABSRL { get => _wheelLockABSRL; set { _wheelLockABSRL = value; OnPropertyChanged(); } }
        private double _wheelLockABSRR; public double WheelLockABSRR { get => _wheelLockABSRR; set { _wheelLockABSRR = value; OnPropertyChanged(); } }

        // ===================== Public Methods =====================

        public void RequestRetest()
        {
            _retestRequested = true;
        }

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("Starting SlipLock Plugin");
            this.PluginManager = pluginManager;
            Settings = this.ReadCommonSettings<SlipLockSettings>("GeneralSettings", () => new SlipLockSettings());
            if (Settings.GameCapabilities == null)
                Settings.GameCapabilities = new Dictionary<string, GameCapabilities>();

            var t = this.GetType();
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty($"SlipLock.SwayThrottle.{wheel}", t, 0.0);
                pluginManager.AddProperty($"SlipLock.SurgeThrottle.{wheel}", t, 0.0);
                pluginManager.AddProperty($"SlipLock.SlipBrake.{wheel}", t, 0.0);
                pluginManager.AddProperty($"SlipLock.CustomSlip.{wheel}", t, 0.0);
                pluginManager.AddProperty($"SlipLock.WheelSlipTC.{wheel}", t, 0.0);
                pluginManager.AddProperty($"SlipLock.WheelLockABS.{wheel}", t, 0.0);
            }
            pluginManager.AddProperty("SlipLock.MaxSway", t, 5.0);
            pluginManager.AddProperty("SlipLock.MaxSurge", t, 5.0);
            pluginManager.AddProperty("SlipLock.MaxDecel", t, 5.0);
            pluginManager.AddProperty("SlipLock.ABSBrake", t, 0.0);

            SimHub.Logging.Current.Info("SlipLock Plugin initialized");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            try
            {
                if (!data.GameRunning || data.NewData == null)
                {
                    ShakeITAvailable = "Game not running";
                    SlipSource = "No game";
                    IsDetecting = false;
                    return;
                }

                // Track game name for detection
                string gameName = data.GameName ?? "";
                if (gameName != _lastGameName)
                {
                    _lastGameName = gameName;
                    _detectionState = SlipDetectionState.Loading;
                    _dynamicFrameCount = 0;
                    _absEverTriggered = false;
                    _tcEverTriggered = false;
                    SimHub.Logging.Current.Info($"SlipLock: Game changed to '{gameName}', reloading capabilities");
                }
                CurrentGame = gameName;

                // Check if car changed - reset max values
                string carId = data.NewData.CarId ?? "N/A";
                if (carId != CurrentCarId && !string.IsNullOrEmpty(carId) && carId != "N/A")
                {
                    MaxSway = 5.0;
                    MaxSurge = 5.0;
                    MaxDecel = 5.0;
                    CurrentCarId = carId;
                }

                double throttle = data.NewData.Throttle;
                double brake = data.NewData.Brake;
                double accelSway = data.NewData.AccelerationSway ?? 0;
                double accelSurge = data.NewData.AccelerationSurge ?? 0;

                UpdateMaxValues(pluginManager, accelSway, accelSurge);

                bool hasShakeIT = CheckShakeITAvailable(pluginManager);
                ShakeITAvailable = hasShakeIT ? "Available" : "Not Found - Enable ShakeIT plugin!";

                if (!hasShakeIT) return;

                // ShakeIT-based effect calculations (unchanged)
                CalculateEffects(pluginManager, data, throttle, brake, accelSway, accelSurge);

                // Wheel slip detection + dual channels
                CalculateSlip(pluginManager, data, throttle, brake, accelSway);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"SlipLock DataUpdate error: {ex.Message}");
                SimHub.Logging.Current.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        // ===================== Effect Calculations =====================

        private void CalculateEffects(PluginManager pluginManager, GameData data,
            double throttle, double brake, double accelSway, double accelSurge)
        {
            double shakeITMaxSway = GetDouble(pluginManager, "ShakeITBSV3Plugin.Export.maxsway.All", MaxSway);
            double shakeITMaxSurge = GetDouble(pluginManager, "ShakeITBSV3Plugin.Export.maxsurge.All", MaxSurge);
            double shakeITMaxDecel = GetDouble(pluginManager, "ShakeITBSV3Plugin.Export.maxdecel.All", MaxDecel);

            double[] swayVals = new double[4], surgeVals = new double[4], brakeVals = new double[4];
            var t = this.GetType();

            for (int i = 0; i < 4; i++)
            {
                string wheel = wheelNames[i];
                double proxyL = GetDouble(pluginManager, $"ShakeITBSV3Plugin.Export.proxyL.{wheel}", 0);
                double wheelSlip = GetDouble(pluginManager, $"ShakeITBSV3Plugin.Export.WheelSlip.{wheel}", 0);

                double slip = (proxyL / 50.0) * (wheelSlip * 8.0);
                double throttleNorm = throttle / 100.0;
                double multi = 0.2;

                // Sway
                double sway = Math.Abs(accelSway) / Math.Max(shakeITMaxSway, 0.1);
                double outSway = sway * slip;
                swayVals[i] = (outSway * (1.0 - multi)) + (outSway * throttleNorm * multi);
                pluginManager.SetPropertyValue($"SlipLock.SwayThrottle.{wheel}", t, swayVals[i]);

                // Surge
                double surge = -accelSurge / Math.Max(shakeITMaxSurge, 0.1);
                double outSurge = surge * slip;
                surgeVals[i] = (outSurge * (1.0 - multi)) + (outSurge * throttleNorm * multi);
                pluginManager.SetPropertyValue($"SlipLock.SurgeThrottle.{wheel}", t, surgeVals[i]);

                // SlipBrake
                double brakeNorm = brake / 100.0;
                double decel = accelSurge / Math.Max(shakeITMaxDecel, 0.1);
                double outBrake = slip * decel;
                brakeVals[i] = (outBrake * (1.0 - multi)) + (outBrake * brakeNorm * multi);
                pluginManager.SetPropertyValue($"SlipLock.SlipBrake.{wheel}", t, brakeVals[i]);
            }

            SwayThrottleFL = swayVals[0]; SwayThrottleFR = swayVals[1];
            SwayThrottleRL = swayVals[2]; SwayThrottleRR = swayVals[3];
            SurgeThrottleFL = surgeVals[0]; SurgeThrottleFR = surgeVals[1];
            SurgeThrottleRL = surgeVals[2]; SurgeThrottleRR = surgeVals[3];
            SlipBrakeFL = brakeVals[0]; SlipBrakeFR = brakeVals[1];
            SlipBrakeRL = brakeVals[2]; SlipBrakeRR = brakeVals[3];

            // ABS
            bool absActive = data.NewData.ABSActive > 0;
            double absBrake = absActive ? brake : 0.0;
            ABSBrake = absBrake;
            pluginManager.SetPropertyValue("SlipLock.ABSBrake", this.GetType(), absBrake);
        }

        // ===================== Slip Detection State Machine =====================

        private void CalculateSlip(PluginManager pluginManager, GameData data,
            double throttle, double brake, double accelSway)
        {
            try
            {
                var t = this.GetType();
                double vehicleSpeed = data.NewData.SpeedKmh / 3.6;

                // Always read ShakeIT slip
                double[] shakeITSlip = new double[4];
                for (int i = 0; i < 4; i++)
                    shakeITSlip[i] = GetDouble(pluginManager, $"ShakeITBSV3Plugin.Export.WheelSlip.{wheelNames[i]}", 0);
                ShakeITSlipFL = shakeITSlip[0]; ShakeITSlipFR = shakeITSlip[1];
                ShakeITSlipRL = shakeITSlip[2]; ShakeITSlipRR = shakeITSlip[3];

                // Handle retest request
                if (_retestRequested)
                {
                    _retestRequested = false;
                    _detectionState = SlipDetectionState.Detecting;
                    _dynamicFrameCount = 0;
                    _absEverTriggered = false;
                    _tcEverTriggered = false;
                    if (!string.IsNullOrEmpty(_lastGameName) && Settings.GameCapabilities.ContainsKey(_lastGameName))
                        Settings.GameCapabilities.Remove(_lastGameName);
                    SimHub.Logging.Current.Info($"SlipLock: Retest requested for '{_lastGameName}'");
                }

                // LOADING: check saved capabilities
                if (_detectionState == SlipDetectionState.Loading)
                {
                    if (!string.IsNullOrEmpty(_lastGameName) && Settings.GameCapabilities.ContainsKey(_lastGameName))
                    {
                        var caps = Settings.GameCapabilities[_lastGameName];
                        if (caps.WheelSpeedMode == "PerWheel")
                            _detectionState = SlipDetectionState.UsePerWheel;
                        else if (caps.WheelSpeedMode == "Mono")
                            _detectionState = SlipDetectionState.UseMono;
                        else
                            _detectionState = SlipDetectionState.Detecting;

                        _absEverTriggered = caps.ABSMode == "Available";
                        _tcEverTriggered = caps.TCMode == "Available";
                        SimHub.Logging.Current.Info($"SlipLock: Loaded capabilities for '{_lastGameName}': WheelSpeed={caps.WheelSpeedMode}, ABS={caps.ABSMode}, TC={caps.TCMode}");
                    }
                    else
                    {
                        _detectionState = SlipDetectionState.Detecting;
                        _dynamicFrameCount = 0;
                    }
                }

                // Read per-wheel speeds
                double[] wheelSpeeds = new double[4];
                bool hasWheelSpeeds = true;
                for (int i = 0; i < 4; i++)
                {
                    var val = pluginManager.GetPropertyValue(wheelSpeedProps[i]);
                    if (val == null) { hasWheelSpeeds = false; break; }
                    try { wheelSpeeds[i] = Convert.ToDouble(val); }
                    catch { hasWheelSpeeds = false; break; }
                }

                double[] slipValues = new double[4];

                // DETECTING state
                if (_detectionState == SlipDetectionState.Detecting)
                {
                    IsDetecting = true;

                    bool speedOk = vehicleSpeed > 5.0;
                    bool cornerOk = Math.Abs(accelSway) > 0.3;
                    bool brakeOk = brake > 10;
                    bool dynamicOk = speedOk && (cornerOk || brakeOk);

                    DetectSpeedCond = speedOk ? $"OK {vehicleSpeed:F1} m/s" : $"-- {vehicleSpeed:F1} m/s";
                    DetectCornerCond = cornerOk ? $"OK {Math.Abs(accelSway):F2}G" : $"-- {Math.Abs(accelSway):F2}G";
                    DetectBrakeCond = brakeOk ? $"OK {brake:F0}%" : $"-- {brake:F0}%";

                    if (!hasWheelSpeeds)
                    {
                        // No wheel speed properties at all
                        SaveCapabilities("Mono");
                        _detectionState = SlipDetectionState.UseMono;
                        DetectionStatus = "No wheel speed data found";
                        SimHub.Logging.Current.Info($"SlipLock: '{_lastGameName}' detected as Mono (no wheel speed properties)");
                    }
                    else if (dynamicOk)
                    {
                        // Check if any wheel speeds differ
                        bool differ = false;
                        for (int i = 1; i < 4; i++)
                        {
                            if (Math.Abs(wheelSpeeds[i] - wheelSpeeds[0]) > 0.05)
                            { differ = true; break; }
                        }

                        if (differ)
                        {
                            SaveCapabilities("PerWheel");
                            _detectionState = SlipDetectionState.UsePerWheel;
                            DetectionStatus = "Per-wheel speeds detected!";
                            SimHub.Logging.Current.Info($"SlipLock: '{_lastGameName}' detected as PerWheel");
                        }
                        else
                        {
                            _dynamicFrameCount++;
                            DetectionStatus = $"Testing... ({_dynamicFrameCount}/{DetectionFrames} frames)";

                            if (_dynamicFrameCount >= DetectionFrames)
                            {
                                SaveCapabilities("Mono");
                                _detectionState = SlipDetectionState.UseMono;
                                DetectionStatus = "Wheel speeds identical under load";
                                SimHub.Logging.Current.Info($"SlipLock: '{_lastGameName}' detected as Mono (identical after {DetectionFrames} frames)");
                            }
                        }
                    }
                    else
                    {
                        DetectionStatus = "Waiting for conditions... (drive and corner/brake)";
                    }

                    // During detection, use ShakeIT as interim
                    for (int i = 0; i < 4; i++) slipValues[i] = shakeITSlip[i];
                    SlipSource = "ShakeIT (detecting...)";
                    PerWheelSupport = "Detecting...";
                }

                // USE_PERWHEEL state
                if (_detectionState == SlipDetectionState.UsePerWheel)
                {
                    IsDetecting = false;
                    PerWheelSupport = "Per-Wheel";
                    SlipSource = "Per-Wheel Speeds (direct)";

                    if (hasWheelSpeeds && vehicleSpeed > 1.0)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            slipValues[i] = ((wheelSpeeds[i] - vehicleSpeed) / vehicleSpeed) * 100.0;
                            slipValues[i] = Math.Max(-100.0, Math.Min(100.0, slipValues[i]));
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 4; i++) slipValues[i] = shakeITSlip[i];
                    }
                }

                // USE_MONO state
                if (_detectionState == SlipDetectionState.UseMono)
                {
                    IsDetecting = false;
                    PerWheelSupport = "Mono";
                    SlipSource = "ShakeIT (mono)";
                    for (int i = 0; i < 4; i++) slipValues[i] = shakeITSlip[i];
                }

                // Update CustomSlip
                CustomSlipFL = slipValues[0]; CustomSlipFR = slipValues[1];
                CustomSlipRL = slipValues[2]; CustomSlipRR = slipValues[3];
                for (int i = 0; i < 4; i++)
                    pluginManager.SetPropertyValue($"SlipLock.CustomSlip.{wheelNames[i]}", t, slipValues[i]);

                // ===================== Dual Channels =====================

                // Track ABS/TC availability
                bool absActive = data.NewData.ABSActive > 0;
                bool tcActive = data.NewData.TCActive > 0;
                if (absActive) _absEverTriggered = true;
                if (tcActive) _tcEverTriggered = true;

                // Save TC/ABS detection to capabilities
                if (!string.IsNullOrEmpty(_lastGameName) && Settings.GameCapabilities.ContainsKey(_lastGameName))
                {
                    var caps = Settings.GameCapabilities[_lastGameName];
                    if (_absEverTriggered && caps.ABSMode != "Available")
                    {
                        caps.ABSMode = "Available";
                        SimHub.Logging.Current.Info($"SlipLock: ABS detected for '{_lastGameName}'");
                    }
                    if (_tcEverTriggered && caps.TCMode != "Available")
                    {
                        caps.TCMode = "Available";
                        SimHub.Logging.Current.Info($"SlipLock: TC detected for '{_lastGameName}'");
                    }
                }

                double brakeNorm = brake / 100.0;
                double[] slipTC = new double[4];
                double[] lockABS = new double[4];

                // Channel 1: WheelSlipTC
                if (_tcEverTriggered && tcActive)
                {
                    SlipTCMode = "TC";
                    // TC active: use slip values as TC intensity indicator
                    for (int i = 0; i < 4; i++)
                        slipTC[i] = Math.Max(0, slipValues[i]); // positive slip = wheelspin
                }
                else
                {
                    SlipTCMode = _tcEverTriggered ? "Slip (TC off)" : "Slip (no TC)";
                    for (int i = 0; i < 4; i++)
                        slipTC[i] = Math.Max(0, slipValues[i]);
                }

                WheelSlipTCFL = slipTC[0]; WheelSlipTCFR = slipTC[1];
                WheelSlipTCRL = slipTC[2]; WheelSlipTCRR = slipTC[3];
                for (int i = 0; i < 4; i++)
                    pluginManager.SetPropertyValue($"SlipLock.WheelSlipTC.{wheelNames[i]}", t, slipTC[i]);

                // Channel 2: WheelLockABS
                if (_absEverTriggered && absActive)
                {
                    LockABSMode = "ABS";
                    // ABS active: brake intensity as lock indicator
                    for (int i = 0; i < 4; i++)
                        lockABS[i] = brake;
                }
                else
                {
                    LockABSMode = _absEverTriggered ? "Lock (ABS off)" : "Lock (no ABS)";
                    // Brake-weighted negative slip
                    for (int i = 0; i < 4; i++)
                        lockABS[i] = Math.Abs(Math.Min(0, slipValues[i])) * brakeNorm;
                }

                WheelLockABSFL = lockABS[0]; WheelLockABSFR = lockABS[1];
                WheelLockABSRL = lockABS[2]; WheelLockABSRR = lockABS[3];
                for (int i = 0; i < 4; i++)
                    pluginManager.SetPropertyValue($"SlipLock.WheelLockABS.{wheelNames[i]}", t, lockABS[i]);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"SlipLock CalculateSlip error: {ex.Message}");
                SimHub.Logging.Current.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        // ===================== Helpers =====================

        private void SaveCapabilities(string wheelSpeedMode)
        {
            if (string.IsNullOrEmpty(_lastGameName)) return;
            if (!Settings.GameCapabilities.ContainsKey(_lastGameName))
                Settings.GameCapabilities[_lastGameName] = new GameCapabilities();
            Settings.GameCapabilities[_lastGameName].WheelSpeedMode = wheelSpeedMode;
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        private bool CheckShakeITAvailable(PluginManager pm)
        {
            return pm.GetPropertyValue("ShakeITBSV3Plugin.Export.WheelSlip.FrontLeft") != null;
        }

        private double GetDouble(PluginManager pm, string prop, double fallback)
        {
            var val = pm.GetPropertyValue(prop);
            if (val == null) return fallback;
            try { return Convert.ToDouble(val); }
            catch { return fallback; }
        }

        private void UpdateMaxValues(PluginManager pm, double accelSway, double accelSurge)
        {
            double sway = Math.Abs(accelSway);
            if (sway < MaxSway + 5.0 && MaxSway <= sway) MaxSway = sway;

            if (accelSurge < MaxSurge + 5.0 && (accelSurge > MaxSurge || Math.Abs(accelSurge - MaxSurge) < 0.001))
                MaxSurge = accelSurge;

            if (accelSurge < MaxDecel + 5.0 && (accelSurge <= MaxDecel || Math.Abs(accelSurge - MaxDecel) < 0.001))
                MaxDecel = accelSurge;

            var t = this.GetType();
            pm.SetPropertyValue("SlipLock.MaxSway", t, MaxSway);
            pm.SetPropertyValue("SlipLock.MaxSurge", t, MaxSurge);
            pm.SetPropertyValue("SlipLock.MaxDecel", t, MaxDecel);
        }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info("SlipLock Plugin stopped");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }
    }
}
