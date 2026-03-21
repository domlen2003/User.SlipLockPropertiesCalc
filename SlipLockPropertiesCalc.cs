using GameReaderCommon;
using SimHub.Plugins;
using System;
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

        /// <summary>
        /// Instance of the current plugin manager
        /// </summary>
        public PluginManager PluginManager { get; set; }

        /// <summary>
        /// Gets the left menu icon. Icon must be 24x24 and compatible with black and white display.
        /// </summary>
        public ImageSource PictureIcon => this.ToIcon(User.SlipLockPropertiesCalc.Properties.Resources.sdkmenuicon);

        /// <summary>
        /// Gets a short plugin title to show in left menu. Return null if you want to use the title as defined in PluginName attribute.
        /// </summary>
        public string LeftMenuTitle => "Slip Lock Calc";

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

        // ShakeIT Raw Slip values (for reference/fallback)
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

        // Custom Slip output values
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

        // Slip source indicator
        private string _slipSource = "Detecting...";
        public string SlipSource
        {
            get => _slipSource;
            set { _slipSource = value; OnPropertyChanged(); }
        }

        // Per-wheel speed property names (iRacing format)
        private static readonly string[] wheelSpeedProps = { "LFspeed", "RFspeed", "LRspeed", "RRspeed" };
        private readonly string[] wheelNames = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("Starting SlipLock Plugin");

            this.PluginManager = pluginManager;

            // Load settings
            Settings = this.ReadCommonSettings<SlipLockSettings>("GeneralSettings", () => new SlipLockSettings());

            // Export SWAY * THROTTLE % MIXED HALF values
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty(
                    $"SlipLock.SwayThrottle.{wheel}",
                    this.GetType(), 0.0);
            }

            // Export SURGE * THROTTLE % MIXED HALF values
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty(
                    $"SlipLock.SurgeThrottle.{wheel}",
                    this.GetType(), 0.0);
            }

            // Export SLIP * SURGE (BRAKES ONLY) values
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty(
                    $"SlipLock.SlipBrake.{wheel}",
                    this.GetType(), 0.0);
            }

            // Export max values
            pluginManager.AddProperty("SlipLock.MaxSway",
                                     this.GetType(), 5.0);
            pluginManager.AddProperty("SlipLock.MaxSurge",
                                     this.GetType(), 5.0);
            pluginManager.AddProperty("SlipLock.MaxDecel",
                                     this.GetType(), 5.0);

            // Export ABS * BRAKE %
            pluginManager.AddProperty("SlipLock.ABSBrake",
                                     this.GetType(), 0.0);

            // Export Custom Slip values
            foreach (string wheel in wheelNames)
            {
                pluginManager.AddProperty(
                    $"SlipLock.CustomSlip.{wheel}",
                    this.GetType(), 0.0);
            }

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
                ShakeITAvailable = hasShakeIT ? "Available" : "Not Found - Enable ShakeIT plugin!";

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
                        $"SlipLock.SwayThrottle.{wheel}",
                        this.GetType(), blend_sway);

                    // ===== SURGE * THROTTLE % MIXED HALF =====
                    double surge = (0.0 - accelSurge) / Math.Max(shakeITMaxSurge, 0.1);

                    double output_surge = surge * slip;
                    double blend_surge = (output_surge * (1.0 - multi)) +
                                        ((output_surge * throttleNorm) * multi);

                    surgeThrottleValues[i] = blend_surge;
                    pluginManager.SetPropertyValue(
                        $"SlipLock.SurgeThrottle.{wheel}",
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
                        $"SlipLock.SlipBrake.{wheel}",
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
                    "SlipLock.ABSBrake",
                    this.GetType(), absBrake);

                // ===== WHEEL SLIP (Input Adapter) =====
                CalculateSlip(pluginManager, data);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"SlipLock DataUpdate error: {ex.Message}");
                SimHub.Logging.Current.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Input adapter for wheel slip: auto-detects per-wheel speeds, falls back to ShakeIT mono.
        /// </summary>
        private void CalculateSlip(PluginManager pluginManager, GameData data)
        {
            try
            {
                double vehicleSpeed = data.NewData.SpeedKmh / 3.6; // m/s

                // Always read ShakeIT slip for reference
                double[] shakeITSlip = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    shakeITSlip[i] = GetShakeITDouble(pluginManager,
                        $"ShakeITBSV3Plugin.Export.WheelSlip.{wheelNames[i]}", 0);
                }
                ShakeITSlipFL = shakeITSlip[0];
                ShakeITSlipFR = shakeITSlip[1];
                ShakeITSlipRL = shakeITSlip[2];
                ShakeITSlipRR = shakeITSlip[3];

                // Try to read per-wheel speeds from game telemetry
                double[] wheelSpeeds = new double[4];
                bool hasWheelSpeeds = true;
                for (int i = 0; i < 4; i++)
                {
                    var val = pluginManager.GetPropertyValue(wheelSpeedProps[i]);
                    if (val == null)
                    {
                        hasWheelSpeeds = false;
                        break;
                    }
                    try { wheelSpeeds[i] = Convert.ToDouble(val); }
                    catch { hasWheelSpeeds = false; break; }
                }

                // Check if wheel speeds actually differ (at least 2 distinct values)
                bool wheelSpeedsDiffer = false;
                if (hasWheelSpeeds && vehicleSpeed > 1.0)
                {
                    for (int i = 1; i < 4; i++)
                    {
                        if (Math.Abs(wheelSpeeds[i] - wheelSpeeds[0]) > 0.01)
                        {
                            wheelSpeedsDiffer = true;
                            break;
                        }
                    }
                }

                double[] slipValues = new double[4];

                if (hasWheelSpeeds && wheelSpeedsDiffer && vehicleSpeed > 1.0)
                {
                    // Direct per-wheel slip from wheel speed vs vehicle speed
                    SlipSource = "Per-Wheel Speeds (direct)";
                    for (int i = 0; i < 4; i++)
                    {
                        slipValues[i] = ((wheelSpeeds[i] - vehicleSpeed) / vehicleSpeed) * 100.0;
                        slipValues[i] = Math.Max(-100.0, Math.Min(100.0, slipValues[i]));
                    }
                }
                else
                {
                    // Fall back to ShakeIT mono slip
                    SlipSource = hasWheelSpeeds ? "ShakeIT (mono - wheel speeds identical)" : "ShakeIT (mono)";
                    for (int i = 0; i < 4; i++)
                    {
                        slipValues[i] = shakeITSlip[i];
                    }
                }

                // Update UI
                CustomSlipFL = slipValues[0];
                CustomSlipFR = slipValues[1];
                CustomSlipRL = slipValues[2];
                CustomSlipRR = slipValues[3];

                // Export to SimHub
                for (int i = 0; i < 4; i++)
                {
                    pluginManager.SetPropertyValue(
                        $"SlipLock.CustomSlip.{wheelNames[i]}",
                        this.GetType(), slipValues[i]);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"SlipLock CalculateSlip error: {ex.Message}");
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
            pluginManager.SetPropertyValue("SlipLock.MaxSway",
                                          this.GetType(), MaxSway);
            pluginManager.SetPropertyValue("SlipLock.MaxSurge",
                                          this.GetType(), MaxSurge);
            pluginManager.SetPropertyValue("SlipLock.MaxDecel",
                                          this.GetType(), MaxDecel);
        }

        public void End(PluginManager pluginManager)
        {
            // Save settings
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info("SlipLock Plugin stopped");
        }

        /// <summary>
        /// Returns the settings control for the plugin
        /// </summary>
        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("SlipLock GetWPFSettingsControl called");
            return new SettingsControl(this);
        }
    }
}
