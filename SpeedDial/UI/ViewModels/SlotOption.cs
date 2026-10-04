using System.Globalization;

namespace DivebombLogistics.SpeedDial.UI.ViewModels;

/// <summary>A choice of the per-preset Dial slot dropdown: "no slot" or "Dial n".</summary>
public sealed class SlotOption
{
    /// <summary>Text of the "no slot" choice.</summary>
    public const string NoneText = "no slot";

    private const string SlotTextPrefix = "Dial ";

    private SlotOption(int slotIndex, string text)
    {
        SlotIndex = slotIndex;
        Text = text;
    }

    /// <summary>0-based slot index; -1 for "no slot".</summary>
    public int SlotIndex { get; }

    /// <summary>Display text.</summary>
    public string Text { get; }

    /// <summary>True for the "no slot" choice.</summary>
    public bool IsNone => SlotIndex < 0;

    /// <summary>The "no slot" choice.</summary>
    public static SlotOption CreateNone() => new SlotOption(-1, NoneText);

    /// <summary>The choice for slot <paramref name="slotIndex"/> (0-based; shown 1-based like the action names).</summary>
    public static SlotOption CreateSlot(int slotIndex) =>
        new SlotOption(slotIndex, SlotTextPrefix + (slotIndex + 1).ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => Text;
}
