namespace DivebombLogistics.Haptics.UI;

/// <summary>
/// Tabs of the debug view. Each <c>SHTabItem</c> carries its value as <c>Tag</c> and the tab control binds
/// <c>SelectedValue</c> (path "Tag"), so reordering the tabs in XAML cannot break which tab gets refreshed.
/// </summary>
public enum DebugTab
{
    SlipLock = 0,
    Balance,
    Diagnostics,
}
