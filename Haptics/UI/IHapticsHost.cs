using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.Settings;

namespace DivebombLogistics.Haptics.UI;

/// <summary>
/// What the Haptics tab needs from the haptics module. Implemented by <c>HapticsModule</c>.
/// Threading contract: every member may be called from the UI thread. Anything that mutates state owned by the
/// data thread (car profile, learner, sources, numeric settings) is queued and applied at the start of the next
/// <c>DataUpdate</c>. Only bool/int/enum fields of <see cref="Settings"/> (atomic even in SimHub's 32-bit process)
/// may be written directly, followed by <see cref="NotifySettingsChanged"/>; doubles go through
/// <see cref="EditSettings"/> so the data thread never reads a half-written value.
/// </summary>
public interface IHapticsHost
{
    /// <summary>Global settings (live object; read-only for the UI except bool/int/enum fields, see the type remarks).</summary>
    HapticsSettings Settings { get; }

    /// <summary>Thread-safe copy of the latest live values into <paramref name="target"/>.</summary>
    void CopySnapshot(HapticsSnapshot target);

    /// <summary>Schedules a debounced save of <see cref="Settings"/> after a UI edit.</summary>
    void NotifySettingsChanged();

    /// <summary>Applies <paramref name="edit"/> to <see cref="Settings"/> on the data thread (queued) and schedules the save.</summary>
    void EditSettings(System.Action<HapticsSettings> edit);

    /// <summary>Sets a per-car sensitivity (percent) for the current car; persisted (debounced). Ignored without a car.</summary>
    void SetSensitivity(SensitivityKind kind, double percent);

    /// <summary>Sets or clears (null) a per-car balance override for the current car.</summary>
    void SetOverride(BalanceOverrideKind kind, double? value);

    /// <summary>Sets or clears (null = auto) the class preset override for the current car.</summary>
    void SetClassPresetOverride(BalanceClassPreset? preset);

    /// <summary>Locks/unlocks learning for the current car.</summary>
    void SetLearningLocked(bool locked);

    /// <summary>Clears the learned vehicle model of the current car.</summary>
    void ResetLearning();

    /// <summary>Starts/stops CSV recording of balance telemetry.</summary>
    void SetRecording(bool enabled);

    /// <summary>Re-runs per-wheel speed detection for the current game (v1 "Retest").</summary>
    void RequestRetest();

    /// <summary>Writes the ShakeIT "data export" profile. Returns a status message for the UI.</summary>
    string GenerateShakeItDataExportProfile();

    /// <summary>Writes the haptic pedal profile. Returns a status message.</summary>
    string GenerateHapticPedalProfile();

    /// <summary>Writes a ShakeIT profile mapping Balance.Understeer/Oversteer to front/rear. Returns a status message.</summary>
    string GenerateBalanceProfile();

    /// <summary>Exports the current car profile JSON to <paramref name="filePath"/>. Returns a status message.</summary>
    string ExportCarProfile(string filePath);

    /// <summary>Imports a car profile JSON into the current car (queued). Returns a status message.</summary>
    string ImportCarProfile(string filePath);

    /// <summary>Writes all SimHub property names (raw game data included) to a text file. Returns a status message.</summary>
    string DumpPropertyNames();
}
