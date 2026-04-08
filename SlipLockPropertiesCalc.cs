using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;

namespace User.SlipLockPropertiesCalc
{
    [PluginDescription("Per-wheel slip/lock channels with corner load estimation for haptic devices")]
    [PluginAuthor("Dominik Lenz")]
    [PluginName("Slip Lock Properties Calc")]
    public class SlipLockPropertiesCalc : IPlugin, IDataPlugin, IWPFSettingsV2, INotifyPropertyChanged
    {
        public SlipLockSettings Settings;
        public PluginManager PluginManager { get; set; }
        public ImageSource PictureIcon => this.ToIcon(Properties.Resources.sdkmenuicon);
        public string LeftMenuTitle => "Slip Lock Calc";

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private static readonly string[] W = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };
        private static readonly int[] LatSign = { -1, 1, -1, 1 };
        private static readonly int[] LongSign = { 1, 1, -1, -1 };

        // Property name variants — per-wheel speed
        private static readonly string[][] WheelSpeedV = {
            new[] { "DataCorePlugin.GameRawData.Telemetry.LFspeed", "LFspeed" },
            new[] { "DataCorePlugin.GameRawData.Telemetry.RFspeed", "RFspeed" },
            new[] { "DataCorePlugin.GameRawData.Telemetry.LRspeed", "LRspeed" },
            new[] { "DataCorePlugin.GameRawData.Telemetry.RRspeed", "RRspeed" },
        };
        // Slip sources: ShakeIT first (works for all games when profile active), native fallbacks
        private static readonly string[][] SlipV = {
            new[] { "ShakeITBSV3Plugin.Export.WheelSlip.FrontLeft", "ShakeITBSV3Plugin.Export.proxyS.FrontLeft",   // ShakeIT (all games)
                    "DataCorePlugin.GameRawData.Physics.WheelSlip01" },                                            // ACC native fallback
            new[] { "ShakeITBSV3Plugin.Export.WheelSlip.FrontRight", "ShakeITBSV3Plugin.Export.proxyS.FrontRight",
                    "DataCorePlugin.GameRawData.Physics.WheelSlip02" },
            new[] { "ShakeITBSV3Plugin.Export.WheelSlip.RearLeft", "ShakeITBSV3Plugin.Export.proxyS.RearLeft",
                    "DataCorePlugin.GameRawData.Physics.WheelSlip03" },
            new[] { "ShakeITBSV3Plugin.Export.WheelSlip.RearRight", "ShakeITBSV3Plugin.Export.proxyS.RearRight",
                    "DataCorePlugin.GameRawData.Physics.WheelSlip04" },
        };
        // LMU/rF2: wheel rotation (rad/s) — need to calculate slip from this + vehicle speed
        private static readonly string[] LMURotationProps = {
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels01.mRotation",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels02.mRotation",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels03.mRotation",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels04.mRotation",
        };
        private static readonly string[] LMURadiusProps = {
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels01.mStaticUndeflectedRadius",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels02.mStaticUndeflectedRadius",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels03.mStaticUndeflectedRadius",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels04.mStaticUndeflectedRadius",
        };
        private bool _hasLMURotation = false;
        private double[] _tireRadii = new double[4];
        // Track which slip source was resolved (for logging/display)
        private string _slipSourceType = "unknown";

        // State
        private enum DetState { Loading, Detecting, PerWheel, Mono }
        private DetState _det = DetState.Loading;
        private int _dynFrames = 0;
        private bool _retestReq = false;
        private string _lastGame = "";
        private bool _absEver = false, _tcEver = false;
        private string[] _wsProps, _slipProps;
        private bool _scanned = false;
        private bool _selfProbed = false;
        private string _propPrefix = ""; // resolved prefix for our own properties
        private GamePreset _preset;

        // Debug
        private string _logPath;
        private DateTime _lastLog = DateTime.MinValue;
        private int _logFrames = 0;
        private void Log(string m, bool f = false) { try { _logFrames++; if (!f && (DateTime.Now - _lastLog).TotalSeconds < 1) return; _lastLog = DateTime.Now; File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] (f#{_logFrames}) {m}\r\n"); _logFrames = 0; } catch { } }

        // ==================== UI Properties ====================
        private double _maxSway = 5; public double MaxSway { get => _maxSway; set { _maxSway = value; OnPropertyChanged(); } }
        private double _maxSurge = 5; public double MaxSurge { get => _maxSurge; set { _maxSurge = value; OnPropertyChanged(); } }
        private double _maxDecel = 5; public double MaxDecel { get => _maxDecel; set { _maxDecel = value; OnPropertyChanged(); } }

        private string _currentCarId = "N/A"; public string CurrentCarId { get => _currentCarId; set { _currentCarId = value; OnPropertyChanged(); } }
        private string _currentGame = ""; public string CurrentGame { get => _currentGame; set { _currentGame = value; OnPropertyChanged(); } }
        private string _shakeITStatus = "..."; public string ShakeITStatus { get => _shakeITStatus; set { _shakeITStatus = value; OnPropertyChanged(); } }
        private string _slipSource = "..."; public string SlipSource { get => _slipSource; set { _slipSource = value; OnPropertyChanged(); } }
        private string _perWheelSupport = "?"; public string PerWheelSupport { get => _perWheelSupport; set { _perWheelSupport = value; OnPropertyChanged(); } }
        private string _detectionStatus = ""; public string DetectionStatus { get => _detectionStatus; set { _detectionStatus = value; OnPropertyChanged(); } }
        private bool _isDetecting = false; public bool IsDetecting { get => _isDetecting; set { _isDetecting = value; OnPropertyChanged(); } }
        private string _detectSpeedCond = ""; public string DetectSpeedCond { get => _detectSpeedCond; set { _detectSpeedCond = value; OnPropertyChanged(); } }
        private string _detectCornerCond = ""; public string DetectCornerCond { get => _detectCornerCond; set { _detectCornerCond = value; OnPropertyChanged(); } }
        private string _detectBrakeCond = ""; public string DetectBrakeCond { get => _detectBrakeCond; set { _detectBrakeCond = value; OnPropertyChanged(); } }
        private string _profileStatus = ""; public string ProfileStatus { get => _profileStatus; set { _profileStatus = value; OnPropertyChanged(); } }
        private string _activePreset = "?"; public string ActivePreset { get => _activePreset; set { _activePreset = value; OnPropertyChanged(); } }

        // ABS/TC capability
        private string _gameExportsABS = "?"; public string GameExportsABS { get => _gameExportsABS; set { _gameExportsABS = value; OnPropertyChanged(); } }
        private string _gameExportsTC = "?"; public string GameExportsTC { get => _gameExportsTC; set { _gameExportsTC = value; OnPropertyChanged(); } }
        private string _carHasABS = "?"; public string CarHasABS { get => _carHasABS; set { _carHasABS = value; OnPropertyChanged(); } }
        private string _carHasTC = "?"; public string CarHasTC { get => _carHasTC; set { _carHasTC = value; OnPropertyChanged(); } }
        private string _absEnabled = "?"; public string ABSEnabled { get => _absEnabled; set { _absEnabled = value; OnPropertyChanged(); } }
        private string _tcEnabled = "?"; public string TCEnabled { get => _tcEnabled; set { _tcEnabled = value; OnPropertyChanged(); } }
        private string _aggSlipUsing = "?"; public string AggregateSlipUsing { get => _aggSlipUsing; set { _aggSlipUsing = value; OnPropertyChanged(); } }
        private string _aggLockUsing = "?"; public string AggregateLockUsing { get => _aggLockUsing; set { _aggLockUsing = value; OnPropertyChanged(); } }

        private bool _isBaseMono = true; public bool IsBaseMono { get => _isBaseMono; set { _isBaseMono = value; OnPropertyChanged(); } }
        private bool _isBasePerWheel = false; public bool IsBasePerWheel { get => _isBasePerWheel; set { _isBasePerWheel = value; OnPropertyChanged(); } }

        // Settings sliders (4 pedal blends)
        private double _slipThrottleBlend; public double SlipThrottleBlend { get => _slipThrottleBlend; set { _slipThrottleBlend = value; OnPropertyChanged(); Settings.SlipThrottleBlend = value; } }
        private double _tcThrottleBlend; public double TCThrottleBlend { get => _tcThrottleBlend; set { _tcThrottleBlend = value; OnPropertyChanged(); Settings.TCThrottleBlend = value; } }
        private double _lockBrakeBlend; public double LockBrakeBlend { get => _lockBrakeBlend; set { _lockBrakeBlend = value; OnPropertyChanged(); Settings.LockBrakeBlend = value; } }
        private double _absBrakeBlend; public double ABSBrakeBlend { get => _absBrakeBlend; set { _absBrakeBlend = value; OnPropertyChanged(); Settings.ABSBrakeBlend = value; } }
        private double _slipThreshold; public double SlipThreshold { get => _slipThreshold; set { _slipThreshold = value; OnPropertyChanged(); Settings.SlipThreshold = value; } }
        private double _lockThreshold; public double LockThreshold { get => _lockThreshold; set { _lockThreshold = value; OnPropertyChanged(); Settings.LockThreshold = value; } }
        private double _tcThreshold; public double TCThreshold { get => _tcThreshold; set { _tcThreshold = value; OnPropertyChanged(); Settings.TCThreshold = value; } }
        private double _absThreshold; public double ABSThreshold { get => _absThreshold; set { _absThreshold = value; OnPropertyChanged(); Settings.ABSThreshold = value; } }
        private bool _gateSlipOnThrottle; public bool GateSlipOnThrottle { get => _gateSlipOnThrottle; set { _gateSlipOnThrottle = value; OnPropertyChanged(); Settings.GateSlipOnThrottle = value; } }
        private bool _gateLockOnBrake; public bool GateLockOnBrake { get => _gateLockOnBrake; set { _gateLockOnBrake = value; OnPropertyChanged(); Settings.GateLockOnBrake = value; } }

        // Envelope shaping
        private double _slipAttackMs; public double SlipAttackMs { get => _slipAttackMs; set { _slipAttackMs = value; OnPropertyChanged(); Settings.SlipAttackMs = value; } }
        private double _slipReleaseMs; public double SlipReleaseMs { get => _slipReleaseMs; set { _slipReleaseMs = value; OnPropertyChanged(); Settings.SlipReleaseMs = value; } }
        private double _lockAttackMs; public double LockAttackMs { get => _lockAttackMs; set { _lockAttackMs = value; OnPropertyChanged(); Settings.LockAttackMs = value; } }
        private double _lockReleaseMs; public double LockReleaseMs { get => _lockReleaseMs; set { _lockReleaseMs = value; OnPropertyChanged(); Settings.LockReleaseMs = value; } }
        private double _absAttackMs; public double ABSAttackMs { get => _absAttackMs; set { _absAttackMs = value; OnPropertyChanged(); Settings.ABSAttackMs = value; } }
        private double _absReleaseMs; public double ABSReleaseMs { get => _absReleaseMs; set { _absReleaseMs = value; OnPropertyChanged(); Settings.ABSReleaseMs = value; } }
        private double _tcAttackMs; public double TCAttackMs { get => _tcAttackMs; set { _tcAttackMs = value; OnPropertyChanged(); Settings.TCAttackMs = value; } }
        private double _tcReleaseMs; public double TCReleaseMs { get => _tcReleaseMs; set { _tcReleaseMs = value; OnPropertyChanged(); Settings.TCReleaseMs = value; } }

        // Envelope runtime state (not persisted)
        private double[] _envSlip = new double[4], _envLock = new double[4], _envABS = new double[4], _envTC = new double[4];
        private DateTime _lastFrameTime = DateTime.MinValue;

        // Pipeline display values
        private double _baseSlipMono; public double BaseSlipMono { get => _baseSlipMono; set { _baseSlipMono = value; OnPropertyChanged(); } }
        private double _baseLockMono; public double BaseLockMono { get => _baseLockMono; set { _baseLockMono = value; OnPropertyChanged(); } }
        private double _baseABSMono; public double BaseABSMono { get => _baseABSMono; set { _baseABSMono = value; OnPropertyChanged(); } }
        private double _baseTCMono; public double BaseTCMono { get => _baseTCMono; set { _baseTCMono = value; OnPropertyChanged(); } }
        private double _baseSlipFL; public double BaseSlipFL { get => _baseSlipFL; set { _baseSlipFL = value; OnPropertyChanged(); } }
        private double _baseSlipFR; public double BaseSlipFR { get => _baseSlipFR; set { _baseSlipFR = value; OnPropertyChanged(); } }
        private double _baseSlipRL; public double BaseSlipRL { get => _baseSlipRL; set { _baseSlipRL = value; OnPropertyChanged(); } }
        private double _baseSlipRR; public double BaseSlipRR { get => _baseSlipRR; set { _baseSlipRR = value; OnPropertyChanged(); } }

        // Preprocessor output (after proxyL, normalized 0-100)
        private double _slipFL; public double SlipFL { get => _slipFL; set { _slipFL = value; OnPropertyChanged(); } }
        private double _slipFR; public double SlipFR { get => _slipFR; set { _slipFR = value; OnPropertyChanged(); } }
        private double _slipRL; public double SlipRL { get => _slipRL; set { _slipRL = value; OnPropertyChanged(); } }
        private double _slipRR; public double SlipRR { get => _slipRR; set { _slipRR = value; OnPropertyChanged(); } }
        private double _lockFL; public double LockFL { get => _lockFL; set { _lockFL = value; OnPropertyChanged(); } }
        private double _lockFR; public double LockFR { get => _lockFR; set { _lockFR = value; OnPropertyChanged(); } }
        private double _lockRL; public double LockRL { get => _lockRL; set { _lockRL = value; OnPropertyChanged(); } }
        private double _lockRR; public double LockRR { get => _lockRR; set { _lockRR = value; OnPropertyChanged(); } }
        private double _absFL; public double ABSFL { get => _absFL; set { _absFL = value; OnPropertyChanged(); } }
        private double _absFR; public double ABSFR { get => _absFR; set { _absFR = value; OnPropertyChanged(); } }
        private double _absRL; public double ABSRL { get => _absRL; set { _absRL = value; OnPropertyChanged(); } }
        private double _absRR; public double ABSRR { get => _absRR; set { _absRR = value; OnPropertyChanged(); } }
        private double _tcFL; public double TCFL { get => _tcFL; set { _tcFL = value; OnPropertyChanged(); } }
        private double _tcFR; public double TCFR { get => _tcFR; set { _tcFR = value; OnPropertyChanged(); } }
        private double _tcRL; public double TCRL { get => _tcRL; set { _tcRL = value; OnPropertyChanged(); } }
        private double _tcRR; public double TCRR { get => _tcRR; set { _tcRR = value; OnPropertyChanged(); } }

        // Blended
        private double _slipBFL; public double SlipBFL { get => _slipBFL; set { _slipBFL = value; OnPropertyChanged(); } }
        private double _slipBFR; public double SlipBFR { get => _slipBFR; set { _slipBFR = value; OnPropertyChanged(); } }
        private double _slipBRL; public double SlipBRL { get => _slipBRL; set { _slipBRL = value; OnPropertyChanged(); } }
        private double _slipBRR; public double SlipBRR { get => _slipBRR; set { _slipBRR = value; OnPropertyChanged(); } }
        private double _lockBFL; public double LockBFL { get => _lockBFL; set { _lockBFL = value; OnPropertyChanged(); } }
        private double _lockBFR; public double LockBFR { get => _lockBFR; set { _lockBFR = value; OnPropertyChanged(); } }
        private double _lockBRL; public double LockBRL { get => _lockBRL; set { _lockBRL = value; OnPropertyChanged(); } }
        private double _lockBRR; public double LockBRR { get => _lockBRR; set { _lockBRR = value; OnPropertyChanged(); } }

        // Aggregates
        private double _aggSFL; public double AggSFL { get => _aggSFL; set { _aggSFL = value; OnPropertyChanged(); } }
        private double _aggSFR; public double AggSFR { get => _aggSFR; set { _aggSFR = value; OnPropertyChanged(); } }
        private double _aggSRL; public double AggSRL { get => _aggSRL; set { _aggSRL = value; OnPropertyChanged(); } }
        private double _aggSRR; public double AggSRR { get => _aggSRR; set { _aggSRR = value; OnPropertyChanged(); } }
        private double _aggLFL; public double AggLFL { get => _aggLFL; set { _aggLFL = value; OnPropertyChanged(); } }
        private double _aggLFR; public double AggLFR { get => _aggLFR; set { _aggLFR = value; OnPropertyChanged(); } }
        private double _aggLRL; public double AggLRL { get => _aggLRL; set { _aggLRL = value; OnPropertyChanged(); } }
        private double _aggLRR; public double AggLRR { get => _aggLRR; set { _aggLRR = value; OnPropertyChanged(); } }
        private string _slipTCMode = "..."; public string SlipTCMode { get => _slipTCMode; set { _slipTCMode = value; OnPropertyChanged(); } }
        private string _lockABSMode = "..."; public string LockABSMode { get => _lockABSMode; set { _lockABSMode = value; OnPropertyChanged(); } }

        // Corner load display
        private double _loadFL; public double LoadFL { get => _loadFL; set { _loadFL = value; OnPropertyChanged(); } }
        private double _loadFR; public double LoadFR { get => _loadFR; set { _loadFR = value; OnPropertyChanged(); } }
        private double _loadRL; public double LoadRL { get => _loadRL; set { _loadRL = value; OnPropertyChanged(); } }
        private double _loadRR; public double LoadRR { get => _loadRR; set { _loadRR = value; OnPropertyChanged(); } }

        // ==================== Public API ====================
        public void RequestRetest() { _retestReq = true; }

        public void GenerateShakeITProfile()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimHub");
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "SlipLock_DataExport.siprofile");
                string g(int _) => Guid.NewGuid().ToString();
                string j = $@"{{""CarChoices"":[],""IncludeOutputSettingsInProfile"":false,""UnmuteEffectsAfterSimhubRestart"":true,""EffectsContainers"":[{{""ContainerType"":""GroupContainer"",""IsEnabled"":true,""Gain"":100.0,""Description"":""SlipLock Data Export"",""EffectsContainers"":[{{""ContainerType"":""WheelsSlipContainer"",""IsEnabled"":true,""Gain"":50.0,""BrakeFilter"":10,""MuteWhenLockEffectIsActive"":false,""ThrottleFilter"":10,""UseBrakeFilter"":false,""UseThrottleFilter"":false,""UseLegacyIracingAlgorythm"":false,""ContainerId"":""{g(0)}"",""AggregationMode"":""Corners"",""Filter"":{{""GammaValue"":1.0,""InputGain"":100.0,""MinimumForce"":0,""Threshold"":0,""FilterType"":""GammaFilter""}},""Output"":{{""UseHighFrequency"":false,""HighFrequency"":50,""WhiteNoise"":10,""UseWhiteNoise"":false,""FrequencyBasedOnPreFilter"":false,""UsePrehemptiveMode"":false,""Frequency"":50,""PropertyName"":""WheelSlip"",""ExportProperty"":true,""DisableOutput"":true,""OutputType"":""ToneOutput""}}}},{{""ContainerType"":""WheelsLockContainer"",""IsEnabled"":true,""Gain"":50.0,""IsLock"":true,""UseLegacyIracingAlgorythm"":false,""LockSensibility"":50.0,""BrakeFilter"":20,""ContainerId"":""{g(1)}"",""AggregationMode"":""Corners"",""Filter"":{{""GammaValue"":1.0,""InputGain"":100.0,""MinimumForce"":0,""Threshold"":0,""FilterType"":""GammaFilter""}},""Output"":{{""UseHighFrequency"":false,""HighFrequency"":50,""WhiteNoise"":10,""UseWhiteNoise"":false,""FrequencyBasedOnPreFilter"":false,""UsePrehemptiveMode"":false,""Frequency"":50,""PropertyName"":""WheelLock"",""ExportProperty"":true,""DisableOutput"":true,""OutputType"":""ToneOutput""}}}}],""ContainerId"":""{g(2)}"",""Filter"":null,""Output"":null}}],""AutoCalibrationRatio2"":100,""OutputMode"":1,""GlobalGain"":50.0,""UseProfileGain"":false,""Name"":""SlipLock Data Export"",""ProfileId"":""{g(3)}"",""GameCode"":null,""CarChoice"":null}}";
                File.WriteAllText(p, j);
                ProfileStatus = "Saved! Import in ShakeIT and restart.";
            }
            catch (Exception ex) { ProfileStatus = $"Error: {ex.Message}"; }
        }

        public void GenerateHapticPedalProfile()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimHub");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "SlipLock_HapticPedals.siprofile");

                string G() => Guid.NewGuid().ToString();

                // Channel mapping: 0=throttle, 1=brake, 2=clutch (typical Simagic/SimNet layout)
                // ch0=true,ch1=false,ch2=false → throttle only
                // ch0=false,ch1=true,ch2=false → brake only
                string ChMap(bool ch0, bool ch1, bool ch2)
                {
                    string c0 = ch0 ? "true" : "false", c1 = ch1 ? "true" : "false", c2 = ch2 ? "true" : "false";
                    return "\"SettingsStore\":{\"Settings\":[{\"Channels\":{\"All\":{\"Channels\":{\"0\":{\"IsEnabled\":" + c0 + "},\"1\":{\"IsEnabled\":" + c1 + "},\"2\":{\"IsEnabled\":" + c2 + "}}}},\"TypeName\":\"DeviceChannelActivationSettings\"}]}";
                }

                // Motors custom effect using NCalc — reads pre-averaged mono export
                string pfx = "SlipLockPropertiesCalc.";

                string MotorEffect(string desc, string prop, bool throttleCh, bool brakeCh, int freq) =>
$@"{{
  ""ContainerType"":""CustomEffectContainer"",
  ""IsEnabled"":true,
  ""Gain"":100.0,
  ""Description"":""{desc}"",
  ""FrontLeftFormula"":{{""Expression"":""[{pfx}{prop}.Mono]""}},
  ""FrontRightFormula"":{{""Expression"":""""}},
  ""RearLeftFormula"":{{""Expression"":""""}},
  ""RearRightFormula"":{{""Expression"":""""}},
  ""ForceFrequencies"":false,
  ""FrontLeftFrequencyFormula"":{{""Expression"":""""}},
  ""FrontRightFrequencyFormula"":{{""Expression"":""""}},
  ""RearLeftFrequencyFormula"":{{""Expression"":""""}},
  ""RearRightFrequencyFormula"":{{""Expression"":""""}},
  ""AlwaysExecute"":false,
  {ChMap(throttleCh, brakeCh, false)},
  ""ContainerId"":""{G()}"",
  ""AggregationMode"":""Mono"",
  ""Filter"":{{""GammaValue"":1.0,""InputGain"":100.0,""MinimumForce"":0,""Threshold"":0,""FilterType"":""GammaFilter""}},
  ""Output"":{{""UseHighFrequency"":false,""HighFrequency"":50,""WhiteNoise"":10,""UseWhiteNoise"":false,""FrequencyBasedOnPreFilter"":false,""UsePrehemptiveMode"":false,""Frequency"":{freq},""OutputType"":""ToneOutput""}}
}}";

                // SlipTC aggregate → throttle pedal (channel 0)
                string slipTC = MotorEffect("SlipTC Aggregate (throttle)", "SlipLock.SlipTC", true, false, 30);
                // LockABS aggregate → brake pedal (channel 1)
                string lockABS = MotorEffect("LockABS Aggregate (brake)", "SlipLock.LockABS", false, true, 25);
                // Raw slip blend → throttle pedal (disabled by default, user can enable)
                string slipBlend = MotorEffect("Slip*Throttle (throttle)", "SlipLock.SlipBlend", true, false, 30)
                    .Replace("\"IsEnabled\":true", "\"IsEnabled\":false");
                // Raw lock blend → brake pedal (disabled by default)
                string lockBlend = MotorEffect("Lock*Brake (brake)", "SlipLock.LockBlend", false, true, 25)
                    .Replace("\"IsEnabled\":true", "\"IsEnabled\":false");

                string json = $@"{{
  ""CarChoices"":[],
  ""IncludeOutputSettingsInProfile"":false,
  ""UnmuteEffectsAfterSimhubRestart"":true,
  ""EffectsContainers"":[
    {slipTC},
    {lockABS},
    {slipBlend},
    {lockBlend}
  ],
  ""AutoCalibrationRatio2"":100,
  ""OutputMode"":3,
  ""GlobalGain"":50.0,
  ""UseProfileGain"":false,
  ""LastLoaded"":""0001-01-01T00:00:00"",
  ""Name"":""SlipLock Haptic Pedals"",
  ""ProfileId"":""{G()}"",
  ""GameCode"":null,
  ""CarChoice"":null
}}";
                File.WriteAllText(path, json);
                ProfileStatus = "Haptic pedal profile saved! Import in ShakeIT Motors tab.";
                SimHub.Logging.Current.Info($"SlipLock: Generated haptic pedal profile at {path}");
            }
            catch (Exception ex) { ProfileStatus = $"Error: {ex.Message}"; }
        }

        // ==================== Init ====================
        public void Init(PluginManager pm)
        {
            SimHub.Logging.Current.Info("Starting SlipLock Plugin");
            this.PluginManager = pm;
            _logPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "Logs", "SlipLock_debug.log");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_logPath)); File.WriteAllText(_logPath, $"=== SlipLock {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n"); } catch { _logPath = Path.Combine(Path.GetTempPath(), "SlipLock_debug.log"); }

            Settings = this.ReadCommonSettings<SlipLockSettings>("GeneralSettings", () => new SlipLockSettings());
            if (Settings.GameCapabilities == null) Settings.GameCapabilities = new Dictionary<string, GameCapabilities>();
            _slipThrottleBlend = Settings.SlipThrottleBlend; _tcThrottleBlend = Settings.TCThrottleBlend;
            _lockBrakeBlend = Settings.LockBrakeBlend; _absBrakeBlend = Settings.ABSBrakeBlend;
            _slipThreshold = Settings.SlipThreshold; _lockThreshold = Settings.LockThreshold;
            _tcThreshold = Settings.TCThreshold; _absThreshold = Settings.ABSThreshold;
            _gateSlipOnThrottle = Settings.GateSlipOnThrottle; _gateLockOnBrake = Settings.GateLockOnBrake;
            _slipAttackMs = Settings.SlipAttackMs; _slipReleaseMs = Settings.SlipReleaseMs;
            _lockAttackMs = Settings.LockAttackMs; _lockReleaseMs = Settings.LockReleaseMs;
            _absAttackMs = Settings.ABSAttackMs; _absReleaseMs = Settings.ABSReleaseMs;
            _tcAttackMs = Settings.TCAttackMs; _tcReleaseMs = Settings.TCReleaseMs;

            var t = this.GetType();
            foreach (string w in W)
            {
                pm.AddProperty($"SlipLock.Slip.{w}", t, 0.0); pm.AddProperty($"SlipLock.Lock.{w}", t, 0.0);
                pm.AddProperty($"SlipLock.ABS.{w}", t, 0.0); pm.AddProperty($"SlipLock.TC.{w}", t, 0.0);
                pm.AddProperty($"SlipLock.SlipBlend.{w}", t, 0.0); pm.AddProperty($"SlipLock.LockBlend.{w}", t, 0.0);
                pm.AddProperty($"SlipLock.SlipTC.{w}", t, 0.0); pm.AddProperty($"SlipLock.LockABS.{w}", t, 0.0);
            }
            // Mono averages (for haptic pedals — single value per channel)
            pm.AddProperty("SlipLock.Slip.Mono", t, 0.0); pm.AddProperty("SlipLock.Lock.Mono", t, 0.0);
            pm.AddProperty("SlipLock.ABS.Mono", t, 0.0); pm.AddProperty("SlipLock.TC.Mono", t, 0.0);
            pm.AddProperty("SlipLock.SlipBlend.Mono", t, 0.0); pm.AddProperty("SlipLock.LockBlend.Mono", t, 0.0);
            pm.AddProperty("SlipLock.SlipTC.Mono", t, 0.0); pm.AddProperty("SlipLock.LockABS.Mono", t, 0.0);
            pm.AddProperty("SlipLock.MaxSway", t, 5.0); pm.AddProperty("SlipLock.MaxSurge", t, 5.0); pm.AddProperty("SlipLock.MaxDecel", t, 5.0);
            SimHub.Logging.Current.Info("SlipLock Plugin initialized");
        }

        // ==================== DataUpdate ====================
        public void DataUpdate(PluginManager pm, ref GameData data)
        {
            try
            {
                if (!data.GameRunning || data.NewData == null) { SlipSource = "No game"; IsDetecting = false; return; }

                string game = data.GameName ?? "";
                if (game != _lastGame)
                {
                    _lastGame = game; _det = DetState.Loading; _dynFrames = 0;
                    _absEver = false; _tcEver = false; _wsProps = null; _slipProps = null; _hasLMURotation = false; _scanned = false; _selfProbed = false;
                    _preset = GamePresets.Table.ContainsKey(game) ? GamePresets.Table[game] : GamePresets.Default;
                    ActivePreset = GamePresets.Table.ContainsKey(game) ? game : "Default";
                }
                CurrentGame = game;
                string carId = data.NewData.CarId ?? "N/A";
                if (carId != CurrentCarId && !string.IsNullOrEmpty(carId) && carId != "N/A") { MaxSway = 5; MaxSurge = 5; MaxDecel = 5; CurrentCarId = carId; }
                if (!_scanned) { _scanned = true; ScanProps(pm); }

                // Self-probe: find what prefix SimHub uses for our properties
                if (!_selfProbed)
                {
                    _selfProbed = true;
                    string testProp = "SlipLock.MaxSway";
                    string[] prefixes = { "", "SlipLockPropertiesCalc.", "User.SlipLockPropertiesCalc.SlipLockPropertiesCalc.", "DataCorePlugin.GameData." };
                    var sb = new StringBuilder("=== SELF-PROBE ===");
                    foreach (var pfx in prefixes)
                    {
                        var v = pm.GetPropertyValue(pfx + testProp);
                        sb.Append($"\r\n  [{pfx}{testProp}] = {(v == null ? "NULL" : v)}");
                        if (v != null && _propPrefix == "") _propPrefix = pfx;
                    }
                    sb.Append($"\r\n  RESOLVED PREFIX: '{_propPrefix}'");
                    Log(sb.ToString(), true);
                }

                double throttle = data.NewData.Throttle, brake = data.NewData.Brake;
                double sway = data.NewData.AccelerationSway ?? 0, surge = data.NewData.AccelerationSurge ?? 0;
                double vs = data.NewData.SpeedKmh / 3.6;
                bool absActive = data.NewData.ABSActive > 0, tcActive = data.NewData.TCActive > 0;

                double tcLevel = -1, absLevel = -1; bool gExpTC = false, gExpABS = false;
                try { var v = pm.GetPropertyValue("DataCorePlugin.GameData.TCLevel"); if (v != null) { tcLevel = Convert.ToDouble(v); gExpTC = true; } } catch { }
                try { var v = pm.GetPropertyValue("DataCorePlugin.GameData.ABSLevel"); if (v != null) { absLevel = Convert.ToDouble(v); gExpABS = true; } } catch { }

                UpdMax(pm, sway, surge);
                if (absActive) _absEver = true;
                if (tcActive) _tcEver = true;
                PersistDetection();
                ProbeSlip(pm);

                // Capability display
                GameExportsABS = gExpABS ? "Yes" : "No"; GameExportsTC = gExpTC ? "Yes" : "No";
                CarHasABS = _absEver ? "Yes" : (gExpABS ? "Unknown" : "No data");
                CarHasTC = _tcEver ? "Yes" : (gExpTC ? "Unknown" : "No data");
                ABSEnabled = !gExpABS ? "N/A" : (absLevel > 0 ? $"Yes (lvl {absLevel:F0})" : "Off");
                TCEnabled = !gExpTC ? "N/A" : (tcLevel > 0 ? $"Yes (lvl {tcLevel:F0})" : "Off");

                // Fixed aggregate decision
                bool aggUsesTC = gExpTC && tcLevel > 0;
                bool aggUsesABS = gExpABS && absLevel > 0;

                var t = this.GetType();
                double tN = throttle / 100, bN = brake / 100;

                // ===== PREPROCESSOR: get raw base + apply proxyL + normalize =====
                double[] rawSlip = GetBaseSlip(pm, data, vs, sway, brake);
                bool perW = _det == DetState.PerWheel;
                IsBaseMono = !perW; IsBasePerWheel = perW;

                // Base display
                BaseSlipMono = rawSlip[0]; BaseLockMono = _preset.SynthLockFromSlip && brake > 5 && surge > 0.1 ? rawSlip[0] : 0;
                BaseABSMono = absActive ? 1.0 : 0; BaseTCMono = tcActive ? 1.0 : 0;
                BaseSlipFL = rawSlip[0]; BaseSlipFR = rawSlip[1]; BaseSlipRL = rawSlip[2]; BaseSlipRR = rawSlip[3];

                // Compute proxyL loads for each channel type
                double[] slipL = CalcL(sway, surge, _preset.SlipLat / 100, _preset.SlipLong / 100);
                double[] lockL = CalcL(sway, surge, _preset.LockLat / 100, _preset.LockLong / 100);
                double[] absL = CalcL(sway, surge, _preset.ABSLat / 100, _preset.ABSLong / 100);
                double[] tcL = CalcL(sway, surge, _preset.TCLat / 100, _preset.TCLong / 100);

                // Display slip loads
                LoadFL = slipL[0]; LoadFR = slipL[1]; LoadRL = slipL[2]; LoadRR = slipL[3];

                // Speed fade: linearly reduce slip/lock below preset speed threshold
                double speedFade = 1.0;
                if (_preset.SpeedFadeKmh > 0)
                {
                    double speedKmh = data.NewData.SpeedKmh;
                    speedFade = Math.Min(1.0, Math.Max(0, speedKmh / _preset.SpeedFadeKmh));
                }

                // Apply proxyL and normalize
                double[] slip = new double[4], lockV = new double[4], absV = new double[4], tcV = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    // Slip: positive direction, load-adjusted, speed-faded
                    double slipRaw = Math.Max(0, rawSlip[i]);
                    if (_preset.InverseSlipLoad)
                        // Inverse: divide by load to counteract load-dependent slip in rotation-based data
                        // load range 0-50, neutral=25. Inverse: 25/load, so loaded wheel (35) gets reduced, unloaded (15) gets boosted
                        slip[i] = Norm(slipRaw * 25.0 / Math.Max(1, slipL[i]) * speedFade);
                    else
                        slip[i] = Norm(slipRaw * slipL[i] / 25.0 * speedFade);

                    // Lock: either synthesized from slip (iRacing) or negative direction, speed-faded
                    if (_preset.SynthLockFromSlip)
                        lockV[i] = (brake > 5 && surge > 0.1) ? Norm(Math.Max(0, rawSlip[i]) * lockL[i] / 25.0 * speedFade) : 0;
                    else
                        lockV[i] = Norm(Math.Abs(Math.Min(0, rawSlip[i])) * lockL[i] / 25.0 * speedFade);

                    // ABS: mono flag → 100 when active → distributed by proxyL → normalized
                    absV[i] = absActive ? Norm(100.0 * absL[i] / 25.0) : 0;

                    // TC: mono flag → 100 when active → distributed by proxyL → normalized
                    tcV[i] = tcActive ? Norm(100.0 * tcL[i] / 25.0) : 0;
                }

                // Preprocessor gain + cut (applied to slip and lock only, ABS/TC are binary-derived)
                if (_preset.PreCut > 0 || _preset.PreGain < 100)
                {
                    double gain = _preset.PreGain / 100.0;
                    double cut = _preset.PreCut;
                    for (int i = 0; i < 4; i++)
                    {
                        // Apply gain first, then cut with remap
                        slip[i] = slip[i] * gain;
                        slip[i] = slip[i] <= cut ? 0 : (slip[i] - cut) / (100.0 - cut) * 100.0;
                        slip[i] = Norm(slip[i]);

                        lockV[i] = lockV[i] * gain;
                        lockV[i] = lockV[i] <= cut ? 0 : (lockV[i] - cut) / (100.0 - cut) * 100.0;
                        lockV[i] = Norm(lockV[i]);
                    }
                }

                // Log preprocessor output
                Log($"PREPROC slip=[{slip[0]:F2},{slip[1]:F2},{slip[2]:F2},{slip[3]:F2}] lock=[{lockV[0]:F2},{lockV[1]:F2},{lockV[2]:F2},{lockV[3]:F2}] loads=[{slipL[0]:F1},{slipL[1]:F1},{slipL[2]:F1},{slipL[3]:F1}]");
                // Display preprocessor output (rounded)
                SlipFL = R(slip[0]); SlipFR = R(slip[1]); SlipRL = R(slip[2]); SlipRR = R(slip[3]);
                LockFL = R(lockV[0]); LockFR = R(lockV[1]); LockRL = R(lockV[2]); LockRR = R(lockV[3]);
                ABSFL = R(absV[0]); ABSFR = R(absV[1]); ABSRL = R(absV[2]); ABSRR = R(absV[3]);
                TCFL = R(tcV[0]); TCFR = R(tcV[1]); TCRL = R(tcV[2]); TCRR = R(tcV[3]);
                for (int i = 0; i < 4; i++)
                {
                    pm.SetPropertyValue($"SlipLock.Slip.{W[i]}", t, R(slip[i]));
                    pm.SetPropertyValue($"SlipLock.Lock.{W[i]}", t, R(lockV[i]));
                    pm.SetPropertyValue($"SlipLock.ABS.{W[i]}", t, R(absV[i]));
                    pm.SetPropertyValue($"SlipLock.TC.{W[i]}", t, R(tcV[i]));
                }
                pm.SetPropertyValue("SlipLock.Slip.Mono", t, R((slip[0]+slip[1]+slip[2]+slip[3])/4));
                pm.SetPropertyValue("SlipLock.Lock.Mono", t, R((lockV[0]+lockV[1]+lockV[2]+lockV[3])/4));
                pm.SetPropertyValue("SlipLock.ABS.Mono", t, R((absV[0]+absV[1]+absV[2]+absV[3])/4));
                pm.SetPropertyValue("SlipLock.TC.Mono", t, R((tcV[0]+tcV[1]+tcV[2]+tcV[3])/4));

                // ===== POSTPROCESSOR =====

                // Thresholds
                for (int i = 0; i < 4; i++)
                {
                    slip[i] = Thresh(slip[i], _slipThreshold);
                    lockV[i] = Thresh(lockV[i], _lockThreshold);
                    absV[i] = Thresh(absV[i], _absThreshold);
                    tcV[i] = Thresh(tcV[i], _tcThreshold);
                }

                // Gates
                bool slipGated = _gateSlipOnThrottle && throttle <= 0;
                bool lockGated = _gateLockOnBrake && brake <= 0;
                if (slipGated) for (int i = 0; i < 4; i++) { slip[i] = 0; tcV[i] = 0; }
                if (lockGated) for (int i = 0; i < 4; i++) { lockV[i] = 0; absV[i] = 0; }

                // Envelope shaping
                var now = DateTime.Now;
                double dt = _lastFrameTime == DateTime.MinValue ? 0.016 : (now - _lastFrameTime).TotalSeconds;
                _lastFrameTime = now;
                dt = Math.Max(0.001, Math.Min(0.1, dt));

                for (int i = 0; i < 4; i++)
                {
                    slip[i] = ApplyEnvelope(ref _envSlip[i], slip[i], dt, _slipAttackMs, _slipReleaseMs);
                    lockV[i] = ApplyEnvelope(ref _envLock[i], lockV[i], dt, _lockAttackMs, _lockReleaseMs);
                    absV[i] = ApplyEnvelope(ref _envABS[i], absV[i], dt, _absAttackMs, _absReleaseMs);
                    tcV[i] = ApplyEnvelope(ref _envTC[i], tcV[i], dt, _tcAttackMs, _tcReleaseMs);
                }

                // 4 separate pedal blends
                double stB = _slipThrottleBlend / 100, tcB = _tcThrottleBlend / 100;
                double lbB = _lockBrakeBlend / 100, abB = _absBrakeBlend / 100;
                double[] slipB = new double[4], lockB = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    slipB[i] = R(slip[i] * (1 - stB) + slip[i] * tN * stB);
                    lockB[i] = R(lockV[i] * (1 - lbB) + lockV[i] * bN * lbB);
                    pm.SetPropertyValue($"SlipLock.SlipBlend.{W[i]}", t, slipB[i]);
                    pm.SetPropertyValue($"SlipLock.LockBlend.{W[i]}", t, lockB[i]);
                }
                SlipBFL = slipB[0]; SlipBFR = slipB[1]; SlipBRL = slipB[2]; SlipBRR = slipB[3];
                LockBFL = lockB[0]; LockBFR = lockB[1]; LockBRL = lockB[2]; LockBRR = lockB[3];
                pm.SetPropertyValue("SlipLock.SlipBlend.Mono", t, R((slipB[0]+slipB[1]+slipB[2]+slipB[3])/4));
                pm.SetPropertyValue("SlipLock.LockBlend.Mono", t, R((lockB[0]+lockB[1]+lockB[2]+lockB[3])/4));

                // Fixed aggregates
                double[] aggS = new double[4], aggL = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    if (aggUsesTC)
                        aggS[i] = R(tcV[i] * (1 - tcB) + tcV[i] * tN * tcB);
                    else
                        aggS[i] = slipB[i];

                    if (aggUsesABS)
                        aggL[i] = R(absV[i] * (1 - abB) + absV[i] * bN * abB);
                    else
                        aggL[i] = lockB[i];

                    pm.SetPropertyValue($"SlipLock.SlipTC.{W[i]}", t, aggS[i]);
                    pm.SetPropertyValue($"SlipLock.LockABS.{W[i]}", t, aggL[i]);
                }
                AggSFL = aggS[0]; AggSFR = aggS[1]; AggSRL = aggS[2]; AggSRR = aggS[3];
                AggLFL = aggL[0]; AggLFR = aggL[1]; AggLRL = aggL[2]; AggLRR = aggL[3];
                pm.SetPropertyValue("SlipLock.SlipTC.Mono", t, R((aggS[0]+aggS[1]+aggS[2]+aggS[3])/4));
                pm.SetPropertyValue("SlipLock.LockABS.Mono", t, R((aggL[0]+aggL[1]+aggL[2]+aggL[3])/4));

                // Fixed mode labels
                SlipTCMode = aggUsesTC ? "TC" : "Slip";
                LockABSMode = aggUsesABS ? "ABS" : "Lock";
                AggregateSlipUsing = aggUsesTC ? $"TC (lvl {tcLevel:F0})" : (gExpTC ? "Slip (TC off)" : "Slip (no TC)");
                AggregateLockUsing = aggUsesABS ? $"ABS (lvl {absLevel:F0})" : (gExpABS ? "Lock (ABS off)" : "Lock (no ABS)");

                pm.SetPropertyValue("SlipLock.MaxSway", t, MaxSway);
                pm.SetPropertyValue("SlipLock.MaxSurge", t, MaxSurge);
                pm.SetPropertyValue("SlipLock.MaxDecel", t, MaxDecel);
            }
            catch (Exception ex) { SimHub.Logging.Current.Error($"SlipLock error: {ex.Message}\n{ex.StackTrace}"); }
        }

        // ==================== ProxyL ====================
        private double[] CalcL(double sway, double surge, double latI, double longI)
        {
            double nL = Math.Min(100, Math.Abs(sway) / Math.Max(MaxSway, 0.1) * 100) * latI;
            double nG = Math.Min(100, Math.Abs(surge) / Math.Max(MaxSurge, 0.1) * 100) * longI;
            var r = new double[4];
            for (int i = 0; i < 4; i++)
            {
                double la = (sway < 0) == (LatSign[i] < 0) ? nL : -nL;
                double lo = (surge > 0) == (LongSign[i] > 0) ? nG : -nG;
                r[i] = 25 + 25 * la / 100; r[i] += r[i] * lo / 100;
                r[i] = Math.Max(0, Math.Min(50, r[i]));
            }
            return r;
        }

        private static double Norm(double v) => Math.Max(0, Math.Min(100, v));
        private static double Thresh(double v, double t) => v <= t ? 0 : (v - t) / (100 - t) * 100;
        private static double R(double v) => Math.Round(v, 1); // round to 1 decimal for clean export

        private static double ApplyEnvelope(ref double state, double target, double dt, double attackMs, double releaseMs)
        {
            if (target >= state)
            {
                // Attack: constant rate (fast ramp up)
                if (attackMs < 1) { state = target; }
                else
                {
                    double rate = 1.0 - Math.Exp(-dt / (attackMs / 1000.0));
                    state += (target - state) * rate;
                }
            }
            else
            {
                // Release: concave curve — slow linger at top, fast snap at bottom
                // Scale releaseMs by (state/100)^0.5 so low values decay faster
                double level = Math.Max(0.01, state / 100.0);
                double effectiveMs = releaseMs * Math.Sqrt(level); // sqrt: gentle curve
                if (effectiveMs < 1) { state = target; }
                else
                {
                    double rate = 1.0 - Math.Exp(-dt / (effectiveMs / 1000.0));
                    state += (target - state) * rate;
                }
            }
            // Let the concave curve handle the final approach naturally
            // Only snap truly negligible values to avoid float dust
            if (state < 0.001) state = 0;
            return Math.Max(0, Math.Min(100, state));
        }

        // ==================== Base Slip (detection) ====================
        private double[] GetBaseSlip(PluginManager pm, GameData d, double vs, double sway, double brake)
        {
            var r = new double[4];
            if (_retestReq) { _retestReq = false; _det = DetState.Detecting; _dynFrames = 0; _wsProps = null; if (!string.IsNullOrEmpty(_lastGame) && Settings.GameCapabilities.ContainsKey(_lastGame)) Settings.GameCapabilities.Remove(_lastGame); }
            if (_det == DetState.Loading)
            {
                if (!string.IsNullOrEmpty(_lastGame) && Settings.GameCapabilities.ContainsKey(_lastGame))
                { var c = Settings.GameCapabilities[_lastGame]; _det = c.WheelSpeedMode == "PerWheel" ? DetState.PerWheel : c.WheelSpeedMode == "Mono" ? DetState.Mono : DetState.Detecting; _absEver = c.ABSMode == "Available"; _tcEver = c.TCMode == "Available"; }
                else { _det = DetState.Detecting; _dynFrames = 0; }
            }
            var ws = new double[4]; bool hasWS = TryWS(pm, ws);
            var ss = new double[4]; bool hasSI = TrySI(pm, ss);
            // Try LMU rotation-based slip if standard sources failed
            bool hasLMU = false;
            if (!hasSI) { hasLMU = TryReadLMUSlip(pm, ss, vs); }
            bool hasAnySlip = hasSI || hasLMU;
            // Log raw slip values every second for debugging
            if (hasAnySlip) Log($"RAW slip[{_slipSourceType}]: [{ss[0]:F4},{ss[1]:F4},{ss[2]:F4},{ss[3]:F4}] speed={vs:F1} brake={brake:F0} surge={sway:F2}");
            ShakeITStatus = hasSI ? "Available" : (hasLMU ? "N/A (LMU native)" : "Not found");

            if (_det == DetState.Detecting)
            {
                IsDetecting = true;
                bool sp = vs > 5, co = Math.Abs(sway) > 0.3, br = brake > 10, dy = sp && (co || br);
                DetectSpeedCond = sp ? $"OK {vs:F1}" : $"-- {vs:F1}"; DetectCornerCond = co ? $"OK {Math.Abs(sway):F2}G" : $"-- {Math.Abs(sway):F2}G"; DetectBrakeCond = br ? $"OK {brake:F0}%" : $"-- {brake:F0}%";
                if (!hasWS) { Save("Mono"); _det = DetState.Mono; DetectionStatus = "No wheel speed"; }
                else if (dy) { double md = 0; for (int i = 1; i < 4; i++) md = Math.Max(md, Math.Abs(ws[i] - ws[0])); if (md > 0.05) { Save("PerWheel"); _det = DetState.PerWheel; DetectionStatus = $"PerWheel (d={md:F3})"; } else { _dynFrames++; DetectionStatus = $"Testing ({_dynFrames}/60)"; if (_dynFrames >= 60) { Save("Mono"); _det = DetState.Mono; DetectionStatus = "Mono"; } } }
                else DetectionStatus = "Drive + corner/brake...";
                for (int i = 0; i < 4; i++) r[i] = hasAnySlip ? ss[i] : 0;
                SlipSource = hasAnySlip ? $"{_slipSourceType} (detecting)" : "Detecting..."; PerWheelSupport = "Detecting..."; return r;
            }
            if (_det == DetState.PerWheel) { IsDetecting = false; PerWheelSupport = "Per-Wheel"; SlipSource = "Per-Wheel"; if (hasWS && vs > 1) for (int i = 0; i < 4; i++) r[i] = Math.Max(-100, Math.Min(100, (ws[i] - vs) / vs * 100)); return r; }
            IsDetecting = false; PerWheelSupport = hasLMU ? "Per-Wheel" : "Mono"; SlipSource = hasAnySlip ? _slipSourceType : "No data"; if (hasAnySlip) for (int i = 0; i < 4; i++) r[i] = ss[i]; return r;
        }

        // ==================== Helpers ====================
        private void ScanProps(PluginManager pm) { var sb = new StringBuilder("=== SCAN ==="); foreach (var p in new[] {
            "ShakeITBSV3Plugin.Export.WheelSlip.FrontLeft", "ShakeITBSV3Plugin.Export.WheelLock.FrontLeft",
            "DataCorePlugin.GameRawData.Telemetry.LFspeed", "LFspeed",
            // ACC
            "DataCorePlugin.GameRawData.Physics.WheelSlip01", "DataCorePlugin.GameRawData.Physics.WheelSlip02",
            "DataCorePlugin.GameRawData.Physics.WheelAngularSpeed01",
            // LMU / rF2
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels01.mGripFract",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels01.mRotation",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels01.mStaticUndeflectedRadius",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mWheels01.mTireLoad",
            // Common
            "DataCorePlugin.GameData.ABSActive", "DataCorePlugin.GameData.TCActive",
            "DataCorePlugin.GameData.TCLevel", "DataCorePlugin.GameData.ABSLevel"
        }) { var v = pm.GetPropertyValue(p); sb.Append($"\r\n  {p}={v ?? "NULL"}"); } Log(sb.ToString(), true); }
        private void ProbeSlip(PluginManager pm)
        {
            if (_slipProps == null && !_hasLMURotation)
            {
                // Try standard slip sources first (ACC native, ShakeIT)
                var r = Res(pm, SlipV);
                if (r != null)
                {
                    _slipProps = r;
                    if (r[0].Contains("ShakeIT")) _slipSourceType = "ShakeIT";
                    else if (r[0].Contains("Physics.WheelSlip")) _slipSourceType = "ACC-native";
                    else _slipSourceType = r[0];
                    Log($"Slip resolved: {_slipSourceType} [{string.Join(", ", r)}]", true);
                    return;
                }

                // Try LMU/rF2 mRotation as fallback
                bool allRot = true;
                for (int i = 0; i < 4; i++)
                {
                    var v = pm.GetPropertyValue(LMURotationProps[i]);
                    if (v == null) { allRot = false; break; }
                }
                if (allRot)
                {
                    _hasLMURotation = true;
                    _slipSourceType = "rF2/LMU-rotation";
                    // Try to read tire radii
                    for (int i = 0; i < 4; i++)
                    {
                        _tireRadii[i] = 0.33; // default ~330mm
                        try
                        {
                            var v = pm.GetPropertyValue(LMURadiusProps[i]);
                            if (v != null)
                            {
                                double raw = Convert.ToDouble(v);
                                // rF2/LMU reports radius in cm, convert to meters
                                _tireRadii[i] = raw > 1.0 ? raw / 100.0 : raw;
                            }
                        }
                        catch { }
                    }
                    Log($"Slip resolved: rF2/LMU-rotation radii=[{_tireRadii[0]:F3},{_tireRadii[1]:F3},{_tireRadii[2]:F3},{_tireRadii[3]:F3}]", true);
                }
            }
        }

        private bool TryReadLMUSlip(PluginManager pm, double[] r, double vehicleSpeed)
        {
            if (!_hasLMURotation) return false;
            if (vehicleSpeed < 1.0) { for (int i = 0; i < 4; i++) r[i] = 0; return true; }
            for (int i = 0; i < 4; i++)
            {
                var v = pm.GetPropertyValue(LMURotationProps[i]);
                if (v == null) return false;
                try
                {
                    double rotRadPerSec = Convert.ToDouble(v);
                    double wheelSpeed = Math.Abs(rotRadPerSec) * _tireRadii[i]; // linear speed m/s
                    r[i] = (wheelSpeed - vehicleSpeed) / vehicleSpeed * 100.0;
                    r[i] = Math.Max(-100, Math.Min(100, r[i]));
                }
                catch { return false; }
            }
            return true;
        }
        private string[] Res(PluginManager pm, string[][] v) { var r = new string[4]; for (int i = 0; i < 4; i++) { bool f = false; foreach (var n in v[i]) if (pm.GetPropertyValue(n) != null) { r[i] = n; f = true; break; } if (!f) return null; } return r; }
        private bool TryWS(PluginManager pm, double[] r) { if (_wsProps == null) { var x = Res(pm, WheelSpeedV); if (x != null) _wsProps = x; else return false; } for (int i = 0; i < 4; i++) { var v = pm.GetPropertyValue(_wsProps[i]); if (v == null) return false; try { r[i] = Convert.ToDouble(v); } catch { return false; } } return true; }
        private bool TrySI(PluginManager pm, double[] r)
        {
            if (_slipProps == null) return false;
            for (int i = 0; i < 4; i++)
            {
                var v = pm.GetPropertyValue(_slipProps[i]);
                if (v == null) return false;
                try { r[i] = Convert.ToDouble(v); } catch { return false; }
            }
            // Normalize to 0-100 scale based on source type
            if (_slipSourceType == "rF2/LMU-native")
            {
                // mGripFract: 0-1 fraction (0=no slide, 1=full slide)
                for (int i = 0; i < 4; i++) r[i] = r[i] * 100.0;
            }
            else if (_slipSourceType == "ACC-native")
            {
                // ACC WheelSlip: can range 0-~5+ (ratio), scale to 0-100
                for (int i = 0; i < 4; i++) r[i] = Math.Min(100, r[i] * 20.0);
            }
            // ShakeIT: already 0-100 range (SimHub normalizes it)
            return true;
        }
        private void Save(string m) { if (string.IsNullOrEmpty(_lastGame)) return; if (!Settings.GameCapabilities.ContainsKey(_lastGame)) Settings.GameCapabilities[_lastGame] = new GameCapabilities(); Settings.GameCapabilities[_lastGame].WheelSpeedMode = m; this.SaveCommonSettings("GeneralSettings", Settings); }
        private void PersistDetection() { if (string.IsNullOrEmpty(_lastGame) || !Settings.GameCapabilities.ContainsKey(_lastGame)) return; var c = Settings.GameCapabilities[_lastGame]; bool ch = false; if (_absEver && c.ABSMode != "Available") { c.ABSMode = "Available"; ch = true; } if (_tcEver && c.TCMode != "Available") { c.TCMode = "Available"; ch = true; } if (ch) this.SaveCommonSettings("GeneralSettings", Settings); }
        private void UpdMax(PluginManager pm, double sw, double su) { double s = Math.Abs(sw); if (s > MaxSway && s < MaxSway + 5) MaxSway = s; if (su > MaxSurge && su < MaxSurge + 5) MaxSurge = su; double d = -su; if (d > MaxDecel && d < MaxDecel + 5) MaxDecel = d; }
        public void End(PluginManager pm) { this.SaveCommonSettings("GeneralSettings", Settings); }
        public Control GetWPFSettingsControl(PluginManager pm) => new SettingsControl(this);
    }
}
