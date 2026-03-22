using System.Collections.Generic;

namespace User.SlipLockPropertiesCalc
{
    /// <summary>
    /// Per-game detected capabilities, persisted across sessions.
    /// </summary>
    public class GameCapabilities
    {
        public string WheelSpeedMode = "Unknown";  // "PerWheel", "Mono", "Unknown"
        public string ABSMode = "Unknown";          // "Available", "NotAvailable", "Unknown"
        public string TCMode = "Unknown";           // "Available", "NotAvailable", "Unknown"
    }

    /// <summary>
    /// Settings class, serialized as JSON by SimHub.
    /// </summary>
    public class SlipLockSettings
    {
        public int SpeedWarningLevel = 100;
        public Dictionary<string, GameCapabilities> GameCapabilities = new Dictionary<string, GameCapabilities>();
    }
}
