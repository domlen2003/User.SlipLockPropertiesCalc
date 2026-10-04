using System;
using System.Collections.Generic;
using DivebombLogistics.SpeedDial.Model;

namespace DivebombLogistics.SpeedDial.UI;

/// <summary>
/// What the Speed Dial tab needs from the SpeedDial module. Implemented by <c>SpeedDialModule</c>.
/// <para>
/// Threading contract: every member may be called from the UI thread. Reads go through <see cref="CopySnapshot"/>
/// (copied under a lock). Every mutation is posted to the module's data-thread dispatcher and applied at the start
/// of the next <c>DataUpdate</c>; its outcome appears in the snapshot (<see cref="SpeedDialSnapshot.Status"/>,
/// versions). Export/import run on the data thread synchronously (waits up to 3 s) and return a status text.
/// </para>
/// <para>
/// Indexing: <c>slotIndex</c> and <c>pairIndex</c> are 0-based (action names are 1-based: slot 0 = <c>Dial1</c>).
/// Preset ids are <see cref="DialPreset.Id"/> values taken from <see cref="SpeedDialSnapshot.Presets"/>. Per-car
/// operations are ignored when no car is loaded or the id is unknown (status text says why).
/// </para>
/// </summary>
public interface ISpeedDialHost
{
    /// <summary>True when SimHub's Control Mapper plugin is available (role presses possible). Any thread.</summary>
    bool ControlMapperAvailable { get; }

    /// <summary>Thread-safe copy of the latest state into <paramref name="target"/> (allocation-free).</summary>
    void CopySnapshot(SpeedDialSnapshot target);

    /// <summary>
    /// Applies <paramref name="edit"/> to the live global settings on the data thread (queued), then normalizes them,
    /// bumps <see cref="SpeedDialSnapshot.SettingsVersion"/> and schedules the debounced save. Never hand the live
    /// object to the UI: the snapshot carries a copy.
    /// </summary>
    void EditSettings(Action<SpeedDialSettings> edit);

    /// <summary>
    /// Creates a preset for the current car (name cleaned; "Preset n" when empty). With
    /// <paramref name="fromCurrentValues"/> it includes every channel with valid telemetry at its current value,
    /// otherwise it starts empty. It is appended to the list and becomes the selected preset when none was selected. Ignored beyond
    /// <see cref="SpeedDialCarData.MaxPresets"/>.
    /// </summary>
    void CreatePreset(string name, bool fromCurrentValues);

    /// <summary>Renames a preset of the current car (name cleaned; an empty name keeps the old one).</summary>
    void RenamePreset(string presetId, string name);

    /// <summary>Deletes a preset of the current car and clears its slot assignments and selection.</summary>
    void DeletePreset(string presetId);

    /// <summary>
    /// Sets one channel's target in a preset (sanitized with <c>DialChannels.Sanitize</c>), or excludes the channel
    /// from the preset with null.
    /// </summary>
    void SetPresetValue(string presetId, DialChannel channel, double? value);

    /// <summary>
    /// Overwrites a preset with the current telemetry values: the channels it includes (each one that has valid
    /// telemetry); when it includes none, every channel with valid telemetry.
    /// </summary>
    void CapturePresetFromCurrent(string presetId);

    /// <summary>Assigns a preset to Dial slot <paramref name="slotIndex"/> (0..7), or clears the slot with null.</summary>
    void AssignSlot(int slotIndex, string presetId);

    /// <summary>Selects the preset used by Next/Previous/Apply-selected (null clears the selection).</summary>
    void SelectPreset(string presetId);

    /// <summary>Dials a preset now (same as its slot button). Replaces a running job.</summary>
    void ApplyPreset(string presetId);

    /// <summary>"Set" of pair <paramref name="pairIndex"/> (0..3): stores the current values of the pair's channels for the current car.</summary>
    void SetPair(int pairIndex);

    /// <summary>"Reset" of pair <paramref name="pairIndex"/> (0..3): dials back to the stored values (status "already set" when nothing differs).</summary>
    void ResetPair(int pairIndex);

    /// <summary>Cancels the running dial job (no-op when idle).</summary>
    void CancelDial();

    /// <summary>Sends one press of the channel's Increase (<paramref name="increase"/> true) or Decrease role, for testing the binding. Ignored while a job runs.</summary>
    void TestRole(DialChannel channel, bool increase);

    /// <summary>Button roles defined in the Control Mapper, for the role pickers (may allocate; UI thread is fine; empty when unavailable).</summary>
    IReadOnlyList<string> GetButtonRoles();

    /// <summary>Forgets the learned direction/step/range of one channel of the current car, or of every channel with null.</summary>
    void ResetLearning(DialChannel? channel);

    /// <summary>Writes the current car's SpeedDial data (presets, slots, pairs, learning) as JSON to <paramref name="filePath"/>. Returns a status text (starting with "Error: " on failure).</summary>
    string ExportPresets(string filePath);

    /// <summary>
    /// Adds the presets of a SpeedDial JSON file to the current car (ids that collide get fresh ones; slots, pairs and
    /// learning of the file are ignored). Returns a status text (starting with "Error: " on failure).
    /// </summary>
    string ImportPresets(string filePath);
}
