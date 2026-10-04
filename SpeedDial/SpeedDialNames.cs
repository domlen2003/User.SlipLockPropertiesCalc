using System.Collections.Generic;
using System.Globalization;
using DivebombLogistics.Framework;
using DivebombLogistics.SpeedDial.Model;

namespace DivebombLogistics.SpeedDial;

/// <summary>
/// Names that tie SpeedDial to SimHub and to the file system: the module id (data folder
/// <c>PluginsData\DLP\SpeedDial</c>), the SimHub actions users bind buttons to and the exported properties, all
/// relative to the <c>DLP.</c> prefix (<see cref="DlpNames.FullName"/> gives the full name, e.g. for
/// <c>ControlsEditor.ActionName</c>). COMPATIBILITY: renaming any of them breaks user bindings, dashboards and data.
/// Registration order (pinned by tests): <see cref="AllActions"/> and <see cref="AllProperties"/>.
/// </summary>
internal static class SpeedDialNames
{
    /// <summary><c>IDlpModule.Id</c> of SpeedDial (data folder name).</summary>
    public const string ModuleId = "SpeedDial";

    /// <summary><c>IDlpModule.DisplayName</c> (tab header).</summary>
    public const string ModuleDisplayName = "Speed Dial";

    /// <summary>Prefix of every SpeedDial action and property (after <c>DLP.</c>).</summary>
    public const string Prefix = "SpeedDial.";

    /// <summary>Start of every status message that reports a failure (same convention as Haptics).</summary>
    public const string ErrorPrefix = "Error: ";

    // ---- Actions (button presses; the callbacks run on the data thread) ----

    /// <summary>Selects the next preset of the car (wraps around).</summary>
    public const string NextPresetAction = Prefix + "NextPreset";

    /// <summary>Selects the previous preset of the car (wraps around).</summary>
    public const string PreviousPresetAction = Prefix + "PreviousPreset";

    /// <summary>Dials the selected preset.</summary>
    public const string ApplySelectedPresetAction = Prefix + "ApplySelectedPreset";

    /// <summary>Cancels the running dial job.</summary>
    public const string CancelAction = Prefix + "Cancel";

    // ---- Properties (DLP.SpeedDial.*) ----

    /// <summary>bool: a dial job is running or paused.</summary>
    public const string BusyProperty = Prefix + "Busy";

    /// <summary>string: latest status text (dial progress, selection, "Slot 3 is empty", ...).</summary>
    public const string StatusProperty = Prefix + "Status";

    /// <summary>string: label of the running/last job (preset name or "Reset Pair 1").</summary>
    public const string ActivePresetProperty = Prefix + "ActivePreset";

    /// <summary>string: name of the selected preset (empty when none).</summary>
    public const string SelectedPresetProperty = Prefix + "SelectedPreset";

    /// <summary>int: 1-based position of the selected preset in the car's list, 0 = none.</summary>
    public const string SelectedIndexProperty = Prefix + "SelectedIndex";

    /// <summary>int: number of presets of the current car.</summary>
    public const string PresetCountProperty = Prefix + "PresetCount";

    /// <summary>string: result of the last finished job ("Completed", "Partial", "Failed", "Cancelled"; empty before the first).</summary>
    public const string LastResultProperty = Prefix + "LastResult";

    /// <summary>Start of the per-channel value properties: <c>SpeedDial.Value.&lt;channel id&gt;</c> (double, NaN exported as 0).</summary>
    public const string ValuePropertyPrefix = Prefix + "Value.";

    private static readonly string[] DialActionNames = BuildIndexed(Prefix + "Dial", SpeedDialSettings.MaxSlotCount);
    private static readonly string[] SetActionNames = BuildIndexed(Prefix + "Set", SpeedDialSettings.MaxPairCount);
    private static readonly string[] ResetActionNames = BuildIndexed(Prefix + "Reset", SpeedDialSettings.MaxPairCount);
    private static readonly string[] ValuePropertyNames = BuildValueProperties();
    private static readonly string[] ActionOrder = BuildActionOrder();
    private static readonly string[] PropertyOrder = BuildPropertyOrder();

    /// <summary>Every action in registration order: Dial1..Dial8, NextPreset, PreviousPreset, ApplySelectedPreset, Set1..Set4, Reset1..Reset4, Cancel.</summary>
    public static IReadOnlyList<string> AllActions => ActionOrder;

    /// <summary>
    /// Every property in registration order: Busy, Status, ActivePreset, SelectedPreset, SelectedIndex, PresetCount,
    /// Value.TC1, Value.TC2, Value.TC3, Value.ABS, Value.BB, LastResult.
    /// </summary>
    public static IReadOnlyList<string> AllProperties => PropertyOrder;

    /// <summary><c>SpeedDial.Dial1</c>..<c>SpeedDial.Dial8</c> for slot index 0..7 (null when out of range).</summary>
    public static string DialAction(int slotIndex) => Get(DialActionNames, slotIndex);

    /// <summary><c>SpeedDial.Set1</c>..<c>SpeedDial.Set4</c> for pair index 0..3 (null when out of range).</summary>
    public static string SetAction(int pairIndex) => Get(SetActionNames, pairIndex);

    /// <summary><c>SpeedDial.Reset1</c>..<c>SpeedDial.Reset4</c> for pair index 0..3 (null when out of range).</summary>
    public static string ResetAction(int pairIndex) => Get(ResetActionNames, pairIndex);

    /// <summary><c>SpeedDial.Value.TC1</c> ... <c>SpeedDial.Value.BB</c> (null for an invalid channel).</summary>
    public static string ValueProperty(DialChannel channel) =>
        DialChannels.IsValid(channel) ? ValuePropertyNames[(int)channel] : null;

    private static string Get(string[] names, int index) => index >= 0 && index < names.Length ? names[index] : null;

    private static string[] BuildIndexed(string stem, int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = stem + (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        return names;
    }

    private static string[] BuildValueProperties()
    {
        var names = new string[DialChannels.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = ValuePropertyPrefix + DialChannels.Id((DialChannel)i);
        }

        return names;
    }

    private static string[] BuildActionOrder()
    {
        var names = new List<string>(DialActionNames);
        names.Add(NextPresetAction);
        names.Add(PreviousPresetAction);
        names.Add(ApplySelectedPresetAction);
        names.AddRange(SetActionNames);
        names.AddRange(ResetActionNames);
        names.Add(CancelAction);
        return names.ToArray();
    }

    private static string[] BuildPropertyOrder()
    {
        var names = new List<string>
        {
            BusyProperty,
            StatusProperty,
            ActivePresetProperty,
            SelectedPresetProperty,
            SelectedIndexProperty,
            PresetCountProperty,
        };
        names.AddRange(ValuePropertyNames);
        names.Add(LastResultProperty);
        return names.ToArray();
    }
}
