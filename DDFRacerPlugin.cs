using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;

namespace DDFRacerPlugin
{
    [PluginDescription("Replicates DDF Racer v2.5.1 profile calculations")]
    [PluginAuthor("Your Name")]
    [PluginName("DDF Racer Physics")]
    public class DDFRacerPlugin : IPlugin, IDataPlugin, IWPFSettingsV2, INotifyPropertyChanged
    {
        public DataPluginDemoSettings Settings;

        /// <summary>
        /// Instance of the current plugin manager
        /// </summary>
        public PluginManager PluginManager { get; set; }

        /// <summary>
        /// Gets the left menu icon. Icon must be 24x24 and compatible with black and white display.
        /// </summary>
        public ImageSource PictureIcon => this.ToIcon(User.PluginSdkDemo.Properties.Resources.sdkmenuicon);

        /// <summary>
        /// Gets a short plugin title to show in left menu. Return null if you want to use the title as defined in PluginName attribute.
        /// </summary>
        public string LeftMenuTitle => "DDF Racer";

        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // UI Bindable Properties
        private double _maxSway = 5.0;
        public double MaxSway
        {
            get => _maxSway;
            set { _maxSway = value; OnPropertyChanged(); }
        }

        private double _maxSurge = 5.0;
        public double MaxSurge
        {
            get => _maxSurge;
            set { _maxSurge = value; OnPropertyChanged(); }
        }

        private double _maxDecel = 5.0;
        public double MaxDecel
        {
            get => _maxDecel;
            set { _maxDecel = value; OnPropertyChanged(); }
        }

        private string _currentCarId = "N/A";
        public string CurrentCarId
        {
            get => _currentCarId;
            set { _currentCarId = value; OnPropertyChanged(); }
        }

        private string _shakeITAvailable = "Checking...";
        public string ShakeITAvailable
        {
            get => _shakeITAvailable;
            set { _shakeITAvailable = value; OnPropertyChanged(); }
        }

        // Sway Throttle values
        private double _swayThrottleFL;
        public double SwayThrottleFL
        {
            get => _swayThrottleFL;
            set { _swayThrottleFL = value; OnPropertyChanged(); }
        }

        private double _swayThrottleFR;
        public double SwayThrottleFR
        {
            get => _swayThrottleFR;
            set { _swayThrottleFR = value; OnPropertyChanged(); }
        }

        private double _swayThrottleRL;
        public double SwayThrottleRL
        {
            get => _swayThrottleRL;
            set { _swayThrottleRL = value; OnPropertyChanged(); }
        }

        private double _swayThrottleRR;
        public double SwayThrottleRR
        {
            get => _swayThrottleRR;
            set { _swayThrottleRR = value; OnPropertyChanged(); }
        }

        // Surge Throttle values
        private double _surgeThrottleFL;
        public double SurgeThrottleFL
        {
            get => _surgeThrottleFL;
            set { _surgeThrottleFL = value; OnPropertyChanged(); }
        }

        private double _surgeThrottleFR;
        public double SurgeThrottleFR
        {
            get => _surgeThrottleFR;
            set { _surgeThrottleFR = value; OnPropertyChanged(); }
        }

        private double _surgeThrottleRL;
        public double SurgeThrottleRL
        {
            get => _surgeThrottleRL;
            set { _surgeThrottleRL = value; OnPropertyChanged(); }
        }

        private double _surgeThrottleRR;
        public double SurgeThrottleRR
        {
            get => _surgeThrottleRR;
            set { _surgeThrottleRR = value; OnPropertyChanged(); }
        }

        // Slip Brake values
        private double _slipBrakeFL;
        public double SlipBrakeFL
        {
            get => _slipBrakeFL;
            set { _slipBrakeFL = value; OnPropertyChanged(); }
        }

        private double _slipBrakeFR;
        public double SlipBrakeFR
        {
            get => _slipBrakeFR;
            set { _slipBrakeFR = value; OnPropertyChanged(); }
        }

        private double _slipBrakeRL;
        public double SlipBrakeRL
        {
            get => _slipBrakeRL;
            set { _slipBrakeRL = value; OnPropertyChanged(); }
        }

        private double _slipBrakeRR;
        public double SlipBrakeRR
        {
            get => _slipBrakeRR;
            set { _slipBrakeRR = value; OnPropertyChanged(); }
        }

        private double _absBrake;
        public double ABSBrake
        {
            get => _absBrake;
            set { _absBrake = value; OnPropertyChanged(); }
        }

        // ShakeIT Raw Slip values (for comparison)
        private double _shakeITSlipFL;
        public double ShakeITSlipFL
        {
            get => _shakeITSlipFL;
            set { _shakeITSlipFL = value; OnPropertyChanged(); }
        }

        private double _shakeITSlipFR;
        public double ShakeITSlipFR
        {
            get => _shakeITSlipFR;
            set { _shakeITSlipFR = value; OnPropertyChanged(); }
        }

        private double _shakeITSlipRL;
        public double ShakeITSlipRL
        {
            get => _shakeITSlipRL;
            set { _shakeITSlipRL = value; OnPropertyChanged(); }
        }

        private double _shakeITSlipRR;
        public double ShakeITSlipRR
        {
            get => _shakeITSlipRR;
            set { _shakeITSlipRR = value; OnPropertyChanged(); }
        }

        // Custom Calculated Slip values (iRacing compatible)
        private double _customSlipFL;
        public double CustomSlipFL
        {
            get => _customSlipFL;
            set { _customSlipFL = value; OnPropertyChanged(); }
        }

        private double _customSlipFR;
        public double CustomSlipFR
        {
            get => _customSlipFR;
            set { _customSlipFR = value; OnPropertyChanged(); }
        }

        private double _customSlipRL;
        public double CustomSlipRL
        {
            get => _customSlipRL;
            set { _customSlipRL = value; OnPropertyChanged(); }
        }

        private double _customSlipRR;
        public double CustomSlipRR
        {
            get => _customSlipRR;
            set { _customSlipRR = value; OnPropertyChanged(); }
        }

        // Wheel Speed values for debugging (m/s)
        private double _wheelSpeedFL;
        public double WheelSpeedFL
        {
            get => _wheelSpeedFL;
            set { _wheelSpeedFL = value; OnPropertyChanged(); }
        }

        private double _wheelSpeedFR;
        public double WheelSpeedFR
        {
            get => _wheelSpeedFR;
            set { _wheelSpeedFR = value; OnPropertyChanged(); }
        }

        private double _wheelSpeedRL;
        public double WheelSpeedRL
        {
            get => _wheelSpeedRL;
            set { _wheelSpeedRL = value; OnPropertyChanged(); }
        }

        private double _wheelSpeedRR;
        public double WheelSpeedRR
        {
            get => _wheelSpeedRR;
            set { _wheelSpeedRR = value; OnPropertyChanged(); }
        }

        private double _vehicleSpeedMS;
        public double VehicleSpeedMS
        {
            get => _vehicleSpeedMS;
            set { _vehicleSpeedMS = value; OnPropertyChanged(); }
        }

        // Gear learning status display properties
        private int _currentGear;
        public int CurrentGear
        {
            get => _currentGear;
            set { _currentGear = value; OnPropertyChanged(); }
        }

        private string _gear1Status;
        public string Gear1Status { get => _gear1Status; set { _gear1Status = value; OnPropertyChanged(); } }

        private string _gear2Status;
        public string Gear2Status { get => _gear2Status; set { _gear2Status = value; OnPropertyChanged(); } }

        private string _gear3Status;
        public string Gear3Status { get => _gear3Status; set { _gear3Status = value; OnPropertyChanged(); } }

        private string _gear4Status;
        public string Gear4Status { get => _gear4Status; set { _gear4Status = value; OnPropertyChanged(); } }

        private string _gear5Status;
        public string Gear5Status { get => _gear5Status; set { _gear5Status = value; OnPropertyChanged(); } }

        private string _gear6Status;
        public string Gear6Status { get => _gear6Status; set { _gear6Status = value; OnPropertyChanged(); } }

        private string _gear7Status;
        public string Gear7Status { get => _gear7Status; set { _gear7Status = value; OnPropertyChanged(); } }

        private string _gear8Status;
        public string Gear8Status { get => _gear8Status; set { _gear8Status = value; OnPropertyChanged(); } }

        // Learning condition indicators
        private string _speedCondition;
        public string SpeedCondition { get => _speedCondition; set { _speedCondition = value; OnPropertyChanged(); } }

        private string _throttleCondition;
        public string ThrottleCondition { get => _throttleCondition; set { _throttleCondition = value; OnPropertyChanged(); } }

        private string _brakeCondition;
        public string BrakeCondition { get => _brakeCondition; set { _brakeCondition = value; OnPropertyChanged(); } }

        private string _straightCondition;
        public string StraightCondition { get => _straightCondition; set { _straightCondition = value; OnPropertyChanged(); } }

        private bool _isLearning;
        public bool IsLearning { get => _isLearning; set { _isLearning = value; OnPropertyChanged(); } }

        private string _learningStatusText;
        public string LearningStatusText { get => _learningStatusText; set { _learningStatusText = value; OnPropertyChanged(); } }

        private int _maxGears;
        public int MaxGears { get => _maxGears; set { _maxGears = value; OnPropertyChanged(); } }

        // Gear ratio learning state (session-based, not persistent)
        private double[] gearRatios = new double[10]; // Index 0-9 for gears (0=neutral, 1-8=gears, 9=reverse)
        private int[] gearRatioSampleCount = new int[10]; // How many samples we've collected
        private const int minSamplesForRatio = 10; // Need 10 good samples before we trust the ratio
        private const double wheelCircumference = 2.0; // Estimated, will be refined during learning (meters)
        private string currentCarId = "";
        private int logCounter = 0; // For throttled logging

        private readonly string[] wheelNames = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };
        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("Starting DDF Racer Plugin");

            this.PluginManager = pluginManager;

            // Load settings
            Settings = this.ReadCommonSettings<DataPluginDemoSettings>("GeneralSettings", () => new DataPluginDemoSettings());

            // Export SWAY * THROTTLE % MIXED HALF values
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty(
                    $"DDFRacer.SwayThrottle.{wheel}",
                    this.GetType(), 0.0);
            }

            // Export SURGE * THROTTLE % MIXED HALF values
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty(
                    $"DDFRacer.SurgeThrottle.{wheel}",
                    this.GetType(), 0.0);
            }

            // Export SLIP * SURGE (BRAKES ONLY) values
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty(
                    $"DDFRacer.SlipBrake.{wheel}",
                    this.GetType(), 0.0);
            }

            // Export max values
            pluginManager.AddProperty("DDFRacer.MaxSway",
                                     this.GetType(), 5.0);
            pluginManager.AddProperty("DDFRacer.MaxSurge",
                                     this.GetType(), 5.0);
            pluginManager.AddProperty("DDFRacer.MaxDecel",
                                     this.GetType(), 5.0);

            // Export ABS * BRAKE %
            pluginManager.AddProperty("DDFRacer.ABSBrake",
                                     this.GetType(), 0.0);

            // Export Custom Slip values (iRacing compatible)
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty(
                    $"DDFRacer.CustomSlip.{wheel}",
                    this.GetType(), 0.0);
            }

            SimHub.Logging.Current.Info("DDF Racer Plugin initialized");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            try
            {
                if (!data.GameRunning || data.NewData == null)
                {
                    // Reset all values when game not running
                    ShakeITAvailable = "Game not running";
                    return;
                }

                // Check if car changed - reset max values
                string carId = data.NewData.CarId ?? "N/A";
                if (carId != CurrentCarId && !string.IsNullOrEmpty(carId) && carId != "N/A")
                {
                    MaxSway = 5.0;
                    MaxSurge = 5.0;
                    MaxDecel = 5.0;
                    CurrentCarId = carId;
                    SimHub.Logging.Current.Info($"Car changed to {carId}, reset max values");
                }

            // Get standard telemetry
            double throttle = data.NewData.Throttle;
            double brake = data.NewData.Brake;
            double accelSway = data.NewData.AccelerationSway ?? 0;
            double accelSurge = data.NewData.AccelerationSurge ?? 0;

            // Update max values
            UpdateMaxValues(pluginManager, accelSway, accelSurge);

            // Try to get ShakeIT data
            bool hasShakeIT = CheckShakeITAvailable(pluginManager);
            ShakeITAvailable = hasShakeIT ? "✓ Available" : "✗ Not Found - Enable ShakeIT plugin!";

            if (!hasShakeIT)
            {
                return;
            }

            // Get ShakeIT max values (use ours as fallback)
            double shakeITMaxSway = GetShakeITDouble(pluginManager,
                "ShakeITBSV3Plugin.Export.maxsway.All", MaxSway);
            double shakeITMaxSurge = GetShakeITDouble(pluginManager,
                "ShakeITBSV3Plugin.Export.maxsurge.All", MaxSurge);
            double shakeITMaxDecel = GetShakeITDouble(pluginManager,
                "ShakeITBSV3Plugin.Export.maxdecel.All", MaxDecel);

            // Calculate each wheel
            double[] swayThrottleValues = new double[4];
            double[] surgeThrottleValues = new double[4];
            double[] slipBrakeValues = new double[4];

            for (int i = 0; i < 4; i++)
            {
                string wheel = wheelNames[i];

                // Get ShakeIT values for this wheel
                double proxyL = GetShakeITDouble(pluginManager,
                    $"ShakeITBSV3Plugin.Export.proxyL.{wheel}", 0);
                double wheelSlip = GetShakeITDouble(pluginManager,
                    $"ShakeITBSV3Plugin.Export.WheelSlip.{wheel}", 0);

                // ===== SWAY * THROTTLE % MIXED HALF =====
                double slip = (proxyL / 50.0) * (wheelSlip * 8.0);
                double throttleNorm = throttle / 100.0;
                double sway = Math.Abs(accelSway) / Math.Max(shakeITMaxSway, 0.1);
                double multi = 0.2;

                double output_sway = sway * slip;
                double blend_sway = (output_sway * (1.0 - multi)) +
                                   ((output_sway * throttleNorm) * multi);

                swayThrottleValues[i] = blend_sway;
                pluginManager.SetPropertyValue(
                    $"DDFRacer.SwayThrottle.{wheel}",
                    this.GetType(), blend_sway);

                // ===== SURGE * THROTTLE % MIXED HALF =====
                double surge = (0.0 - accelSurge) / Math.Max(shakeITMaxSurge, 0.1);

                double output_surge = surge * slip;
                double blend_surge = (output_surge * (1.0 - multi)) +
                                    ((output_surge * throttleNorm) * multi);

                surgeThrottleValues[i] = blend_surge;
                pluginManager.SetPropertyValue(
                    $"DDFRacer.SurgeThrottle.{wheel}",
                    this.GetType(), blend_surge);

                // ===== SLIP * SURGE (BRAKES ONLY) =====
                double brakeNorm = brake / 100.0;
                double decel = accelSurge / Math.Max(shakeITMaxDecel, 0.1);
                double multi_brake = 0.2;

                double output_brake = slip * decel;
                double blend_brake = (output_brake * (1.0 - multi_brake)) +
                                    ((output_brake * brakeNorm) * multi_brake);

                double slipBrake = blend_brake;

                slipBrakeValues[i] = slipBrake;
                pluginManager.SetPropertyValue(
                    $"DDFRacer.SlipBrake.{wheel}",
                    this.GetType(), slipBrake);
            }

            // Update UI bindable properties
            SwayThrottleFL = swayThrottleValues[0];
            SwayThrottleFR = swayThrottleValues[1];
            SwayThrottleRL = swayThrottleValues[2];
            SwayThrottleRR = swayThrottleValues[3];

            SurgeThrottleFL = surgeThrottleValues[0];
            SurgeThrottleFR = surgeThrottleValues[1];
            SurgeThrottleRL = surgeThrottleValues[2];
            SurgeThrottleRR = surgeThrottleValues[3];

            SlipBrakeFL = slipBrakeValues[0];
            SlipBrakeFR = slipBrakeValues[1];
            SlipBrakeRL = slipBrakeValues[2];
            SlipBrakeRR = slipBrakeValues[3];

            // ===== ABS * BRAKE % =====
            bool absActive = data.NewData.ABSActive > 0;
            double absBrake = absActive ? brake : 0.0;

            ABSBrake = absBrake;
            pluginManager.SetPropertyValue(
                "DDFRacer.ABSBrake",
                this.GetType(), absBrake);

            // ===== CUSTOM SLIP CALCULATION (iRacing Compatible) =====
            CalculateCustomSlip(pluginManager, data);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"DDF Racer DataUpdate error: {ex.Message}");
                SimHub.Logging.Current.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        private bool CheckShakeITAvailable(PluginManager pluginManager)
        {
            var testProp = pluginManager.GetPropertyValue(
                "ShakeITBSV3Plugin.Export.WheelSlip.FrontLeft");
            return testProp != null;
        }

        private double GetShakeITDouble(PluginManager pluginManager,
                                        string propertyName,
                                        double defaultValue)
        {
            var value = pluginManager.GetPropertyValue(propertyName);
            if (value == null) return defaultValue;

            try
            {
                return Convert.ToDouble(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private void UpdateMaxValues(PluginManager pluginManager,
                                     double accelSway,
                                     double accelSurge)
        {
            // Update MaxSway
            double sway = Math.Abs(accelSway);
            if (sway < MaxSway + 5.0)
            {
                if (MaxSway <= sway)
                {
                    MaxSway = sway;
                }
            }

            // Update MaxSurge
            double surge = accelSurge;
            if (surge < MaxSurge + 5.0)
            {
                if (surge > MaxSurge || Math.Abs(surge - MaxSurge) < 0.001)
                {
                    MaxSurge = surge;
                }
            }

            // Update MaxDecel
            double decel = accelSurge;
            if (decel < MaxDecel + 5.0)
            {
                if (decel <= MaxDecel || Math.Abs(decel - MaxDecel) < 0.001)
                {
                    MaxDecel = decel;
                }
            }

            // Export to SimHub
            pluginManager.SetPropertyValue("DDFRacer.MaxSway",
                                          this.GetType(), MaxSway);
            pluginManager.SetPropertyValue("DDFRacer.MaxSurge",
                                          this.GetType(), MaxSurge);
            pluginManager.SetPropertyValue("DDFRacer.MaxDecel",
                                          this.GetType(), MaxDecel);
        }

        private void CalculateCustomSlip(PluginManager pluginManager, GameData data)
        {
            try
            {
                // Get ShakeIT raw slip values for comparison
                double[] shakeITSlipValues = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    shakeITSlipValues[i] = GetShakeITDouble(pluginManager,
                        $"ShakeITBSV3Plugin.Export.WheelSlip.{wheelNames[i]}", 0);
                }

            // Update ShakeIT raw values for UI display (already in 0-100 range)
            ShakeITSlipFL = shakeITSlipValues[0];
            ShakeITSlipFR = shakeITSlipValues[1];
            ShakeITSlipRL = shakeITSlipValues[2];
            ShakeITSlipRR = shakeITSlipValues[3];

            // Reset gear ratios if car changed
            string carId = data.NewData.CarId ?? "";
            if (carId != currentCarId && !string.IsNullOrEmpty(carId))
            {
                SimHub.Logging.Current.Info($"DDF Racer: Car changed to {carId}, resetting gear ratio learning and max gears");
                Array.Clear(gearRatios, 0, gearRatios.Length);
                Array.Clear(gearRatioSampleCount, 0, gearRatioSampleCount.Length);
                MaxGears = 0; // Will be determined by highest gear seen during driving
                currentCarId = carId;
            }

            // RPM-based wheel speed calculation with gear ratio learning
            double vehicleSpeed = data.NewData.SpeedKmh / 3.6; // Convert to m/s
            VehicleSpeedMS = vehicleSpeed;

            // Get RPM from game raw data telemetry
            double rpm = 0;
            try
            {
                var rpmValue = pluginManager.GetPropertyValue("DataCorePlugin.GameRawData.Telemetry.RPM");
                if (rpmValue != null)
                {
                    rpm = Convert.ToDouble(rpmValue);
                }
            }
            catch { rpm = 0; }

            int gear = 0;
            try
            {
                var gearValue = pluginManager.GetPropertyValue("Gear");
                if (gearValue != null)
                {
                    gear = Convert.ToInt32(gearValue);
                }
            }
            catch { gear = 0; }

            // Determine max gears from highest gear seen during driving
            if (gear > 0 && gear > MaxGears)
            {
                MaxGears = gear;
                SimHub.Logging.Current.Info($"DDF Racer: Max gears updated to {MaxGears} (highest gear seen)");
            }

            double throttle = data.NewData.Throttle;
            double brake = data.NewData.Brake;
            double latAccel = Math.Abs(data.NewData.AccelerationSway ?? 0);

            // Minimum speed for learning and calculation
            const double minSpeed = 5.0; // m/s (18 km/h)

            double[] wheelSpeeds = new double[4];

            // Check if we're in good conditions to learn gear ratios
            bool speedOk = vehicleSpeed > minSpeed;
            bool rpmOk = rpm > 1000;
            bool gearOk = gear > 0 && gear <= MaxGears; // Valid forward gear
            bool throttleOk = throttle > 15 && throttle < 85;
            bool brakeOk = brake < 5;
            bool straightOk = latAccel < 3.0; // Note: AccelerationSway units not in G, but works for straightness check

            bool isLearningCondition = speedOk && rpmOk && gearOk && throttleOk && brakeOk && straightOk;

            // Update condition indicators
            SpeedCondition = speedOk ? $"✓ {vehicleSpeed:F1} m/s" : $"✗ {vehicleSpeed:F1} m/s";
            ThrottleCondition = throttleOk ? $"✓ {throttle:F0}%" : $"✗ {throttle:F0}%";
            BrakeCondition = brakeOk ? $"✓ {brake:F0}%" : $"✗ {brake:F0}%";
            StraightCondition = straightOk ? $"✓ {latAccel:F2}" : $"✗ {latAccel:F2}";
            IsLearning = isLearningCondition;
            LearningStatusText = isLearningCondition ? "LEARNING" : "WAITING";

            // Detailed logging for why learning isn't happening (once per second)
            logCounter++;
            if (logCounter >= 60 && !isLearningCondition)
            {
                logCounter = 0;
                string reasons = "";
                if (!speedOk) reasons += $"Speed too low ({vehicleSpeed:F1} < {minSpeed}), ";
                if (!rpmOk) reasons += $"RPM too low ({rpm:F0} < 1000), ";
                if (!gearOk) reasons += $"Invalid gear (gear={gear}, maxGears={MaxGears}), ";
                if (!throttleOk) reasons += $"Throttle wrong ({throttle:F0}% not in 15-85%), ";
                if (!brakeOk) reasons += $"Braking ({brake:F0}% >= 5%), ";
                if (!straightOk) reasons += $"Turning ({latAccel:F2} >= 3.0), ";
                SimHub.Logging.Current.Info($"DDF Racer: NOT LEARNING - {reasons.TrimEnd(',', ' ')}");
            }
            else if (logCounter >= 60 && isLearningCondition)
            {
                logCounter = 0;
            }

            // Learn gear ratio if conditions are good
            if (isLearningCondition)
            {
                // Calculate effective ratio: RPM per m/s of vehicle speed
                // This combines gear ratio, final drive, and wheel circumference
                double effectiveRatio = rpm / vehicleSpeed;

                // Running average of gear ratios
                if (gearRatioSampleCount[gear] == 0)
                {
                    gearRatios[gear] = effectiveRatio;
                    gearRatioSampleCount[gear] = 1;
                    SimHub.Logging.Current.Info($"DDF Racer: Started learning gear {gear}, ratio={effectiveRatio:F1}");
                }
                else
                {
                    // Weighted average (new sample gets 10% weight)
                    gearRatios[gear] = gearRatios[gear] * 0.9 + effectiveRatio * 0.1;
                    gearRatioSampleCount[gear]++;

                    // Log when gear is fully learned
                    if (gearRatioSampleCount[gear] == minSamplesForRatio)
                    {
                        SimHub.Logging.Current.Info($"DDF Racer: Gear {gear} LEARNED! Ratio={gearRatios[gear]:F1} (samples={minSamplesForRatio})");
                    }
                }
            }

            // Calculate theoretical wheel speed if we have learned the ratio
            if (gear > 0 && gear <= MaxGears && gearRatioSampleCount[gear] >= minSamplesForRatio)
            {
                // Theoretical speed based on RPM and learned ratio
                double theoreticalSpeed = rpm / gearRatios[gear];

                // For now, display theoretical speed for all wheels
                // (Later we can add per-wheel variation using suspension/G-forces)
                for (int i = 0; i < 4; i++)
                {
                    wheelSpeeds[i] = theoreticalSpeed;
                }
            }
            else
            {
                // Still learning or invalid gear - show actual vehicle speed
                for (int i = 0; i < 4; i++)
                {
                    wheelSpeeds[i] = vehicleSpeed;
                }
            }

            // Update wheel speed UI properties (theoretical speeds from RPM)
            WheelSpeedFL = wheelSpeeds[0];
            WheelSpeedFR = wheelSpeeds[1];
            WheelSpeedRL = wheelSpeeds[2];
            WheelSpeedRR = wheelSpeeds[3];

            // Calculate slip (difference between theoretical and actual)
            double[] slipValues = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (vehicleSpeed > 1.0) // Only calculate slip above 1 m/s
                {
                    slipValues[i] = ((wheelSpeeds[i] - vehicleSpeed) / vehicleSpeed) * 100.0; // Percentage
                    slipValues[i] = Math.Max(-100.0, Math.Min(100.0, slipValues[i])); // Clamp
                }
                else
                {
                    slipValues[i] = 0.0;
                }
            }

            // Update UI properties (show slip percentage)
            CustomSlipFL = slipValues[0];
            CustomSlipFR = slipValues[1];
            CustomSlipRL = slipValues[2];
            CustomSlipRR = slipValues[3];

            // Update current gear display
            CurrentGear = gear;

            // Update gear learning status strings
            UpdateGearStatus(1, ref _gear1Status);
            UpdateGearStatus(2, ref _gear2Status);
            UpdateGearStatus(3, ref _gear3Status);
            UpdateGearStatus(4, ref _gear4Status);
            UpdateGearStatus(5, ref _gear5Status);
            UpdateGearStatus(6, ref _gear6Status);
            UpdateGearStatus(7, ref _gear7Status);
            UpdateGearStatus(8, ref _gear8Status);

            // Trigger property changed for gear statuses
            OnPropertyChanged(nameof(Gear1Status));
            OnPropertyChanged(nameof(Gear2Status));
            OnPropertyChanged(nameof(Gear3Status));
            OnPropertyChanged(nameof(Gear4Status));
            OnPropertyChanged(nameof(Gear5Status));
            OnPropertyChanged(nameof(Gear6Status));
            OnPropertyChanged(nameof(Gear7Status));
            OnPropertyChanged(nameof(Gear8Status));

            // Export to SimHub
            for (int i = 0; i < 4; i++)
            {
                pluginManager.SetPropertyValue(
                    $"DDFRacer.CustomSlip.{wheelNames[i]}",
                    this.GetType(), slipValues[i]);
            }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"DDF Racer CalculateCustomSlip error: {ex.Message}");
                SimHub.Logging.Current.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        private void UpdateGearStatus(int gearNum, ref string statusString)
        {
            int samples = gearRatioSampleCount[gearNum];
            bool learned = samples >= minSamplesForRatio;

            if (learned)
            {
                // Learned - show ratio
                statusString = $"✓ {gearRatios[gearNum]:F1}";
            }
            else if (samples > 0)
            {
                // Learning - show progress
                int percent = (samples * 100) / minSamplesForRatio;
                statusString = $"⋯ {percent}%";
            }
            else
            {
                // Not started
                statusString = "—";
            }
        }

        public void End(PluginManager pluginManager)
        {
            // Save settings
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info("DDF Racer Plugin stopped");
        }

        /// <summary>
        /// Returns the settings control for the plugin
        /// </summary>
        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("DDF Racer GetWPFSettingsControl called");
            return new SettingsControl(this);
        }
    }

    /// <summary>
    /// Settings class
    /// </summary>
    public class DataPluginDemoSettings
    {
        public int Unused = 0;
    }
}