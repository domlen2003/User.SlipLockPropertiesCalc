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

            // Update ShakeIT raw values for UI display
            ShakeITSlipFL = shakeITSlipValues[0];
            ShakeITSlipFR = shakeITSlipValues[1];
            ShakeITSlipRL = shakeITSlipValues[2];
            ShakeITSlipRR = shakeITSlipValues[3];

            // iRacing-specific slip calculation
            // Get telemetry data
            double vehicleSpeed = data.NewData.SpeedKmh / 3.6; // Convert to m/s
            double latAccel = data.NewData.AccelerationSway ?? 0;  // Lateral G
            double longAccel = data.NewData.AccelerationSurge ?? 0; // Longitudinal G

            // Minimum speed threshold to avoid division by zero
            const double minSpeed = 0.1; // m/s (0.36 km/h)

            double[] customSlipValues = new double[4];

            if (vehicleSpeed < minSpeed)
            {
                // Below minimum speed, set all slip to zero
                for (int i = 0; i < 4; i++)
                {
                    customSlipValues[i] = 0.0;
                }
            }
            else
            {
                // Try to get iRacing wheel speeds
                // Property names might be: LFspeed, RFspeed, LRspeed, RRspeed
                string[] iRacingWheelProps = { "LFspeed", "RFspeed", "LRspeed", "RRspeed" };
                double[] wheelSpeeds = new double[4];
                bool hasWheelSpeeds = true;

                for (int i = 0; i < 4; i++)
                {
                    var wheelSpeed = pluginManager.GetPropertyValue(iRacingWheelProps[i]);
                    if (wheelSpeed != null)
                    {
                        try
                        {
                            wheelSpeeds[i] = Convert.ToDouble(wheelSpeed);
                        }
                        catch
                        {
                            hasWheelSpeeds = false;
                            break;
                        }
                    }
                    else
                    {
                        hasWheelSpeeds = false;
                        break;
                    }
                }

                if (hasWheelSpeeds)
                {
                    // Calculate slip for each wheel with Schnirbus modulation
                    // Modulation factors (tunable)
                    const double latFactor = 0.1;
                    const double longFactor = 0.1;

                    for (int i = 0; i < 4; i++)
                    {
                        // Base slip calculation: (wheel_speed - vehicle_speed) / vehicle_speed
                        double baseSlip = (wheelSpeeds[i] - vehicleSpeed) / vehicleSpeed;

                        // Apply Schnirbus modulation based on wheel position
                        // FL = base - lat - long
                        // FR = base + lat - long
                        // RL = base - lat + long
                        // RR = base + lat + long
                        double latMod = (i % 2 == 0) ? -latAccel : latAccel;  // Left=-1, Right=+1
                        double longMod = (i < 2) ? -longAccel : longAccel;     // Front=-1, Rear=+1

                        customSlipValues[i] = baseSlip + (latMod * latFactor) + (longMod * longFactor);

                        // Clamp to reasonable range
                        customSlipValues[i] = Math.Max(-2.0, Math.Min(2.0, customSlipValues[i]));
                    }
                }
                else
                {
                    // Fallback: no wheel speeds available, set to zero or use ShakeIT values
                    for (int i = 0; i < 4; i++)
                    {
                        customSlipValues[i] = 0.0;
                    }
                }
            }

            // Update UI properties
            CustomSlipFL = customSlipValues[0];
            CustomSlipFR = customSlipValues[1];
            CustomSlipRL = customSlipValues[2];
            CustomSlipRR = customSlipValues[3];

            // Export to SimHub
            for (int i = 0; i < 4; i++)
            {
                pluginManager.SetPropertyValue(
                    $"DDFRacer.CustomSlip.{wheelNames[i]}",
                    this.GetType(), customSlipValues[i]);
            }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"DDF Racer CalculateCustomSlip error: {ex.Message}");
                SimHub.Logging.Current.Error($"Stack trace: {ex.StackTrace}");
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