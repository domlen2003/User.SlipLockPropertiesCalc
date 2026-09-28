using System.Collections.Generic;

namespace User.SlipLockPropertiesCalc.UI.ViewModels;

/// <summary>A titled group of <see cref="SliderItem"/>s rendered as a two-column slider grid.</summary>
public sealed class SliderGroup
{
    public SliderGroup(string title, IReadOnlyList<SliderItem> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }

    public IReadOnlyList<SliderItem> Items { get; }

    /// <summary>Shows every slider's default value (after a queued reset to defaults, before it is applied).</summary>
    public void ShowDefaults()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].ShowValue(Items[i].DefaultValue);
        }
    }
}
