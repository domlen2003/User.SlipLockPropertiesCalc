namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>
/// What a preset row needs from the page view model: it forwards the row's user actions to the host (with the page's
/// confirmation and slot bookkeeping) and tells whether per-car edits are possible right now. UI thread only.
/// </summary>
internal interface IPresetRowOwner
{
    /// <summary>A car is loaded (preset edits possible).</summary>
    bool CanEditPresets { get; }

    /// <summary>Current telemetry value of <paramref name="channel"/> (NaN = unknown).</summary>
    double CurrentValue(DialChannel channel);

    /// <summary>Dials the preset now.</summary>
    void Apply(PresetItem preset);

    /// <summary>Overwrites the preset with the current values.</summary>
    void Capture(PresetItem preset);

    /// <summary>Renames the preset.</summary>
    void Rename(PresetItem preset, string name);

    /// <summary>Deletes the preset (after confirmation).</summary>
    void Delete(PresetItem preset);

    /// <summary>Makes the preset the one Next/Previous/Apply selected use.</summary>
    void Select(PresetItem preset);

    /// <summary>Moves the preset from slot <paramref name="previous"/> to <paramref name="next"/> (either may be "no slot").</summary>
    void AssignSlot(PresetItem preset, SlotOption previous, SlotOption next);

    /// <summary>Sets (value) or excludes (null) one channel of the preset.</summary>
    void SetValue(PresetItem preset, DialChannel channel, double? value);
}
