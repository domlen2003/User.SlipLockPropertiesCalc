using System;
using System.Windows;
using System.Windows.Controls;
using SimHub.Plugins.Styles;

namespace DivebombLogistics.UI;

/// <summary>
/// The DLP settings page returned by <c>DLP.GetWPFSettingsControl</c>: a SimHub tab control with one tab per module.
/// The shell adds the tabs; the page itself has no logic (each module view manages its own refresh).
/// </summary>
public partial class MainView : UserControl
{
    private const double ErrorMargin = 12.0;

    /// <summary>Creates an empty page; add tabs with <see cref="AddTab"/>.</summary>
    public MainView()
    {
        InitializeComponent();
    }

    /// <summary>Number of tabs.</summary>
    public int TabCount => Tabs.Items.Count;

    /// <summary>Appends a tab showing <paramref name="content"/>.</summary>
    /// <param name="header">Tab header (the module's display name).</param>
    /// <param name="content">The module's view.</param>
    public void AddTab(string header, UIElement content)
    {
        if (content == null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        Tabs.Items.Add(new SHTabItem { Header = header ?? string.Empty, Content = content });
        if (Tabs.SelectedIndex < 0)
        {
            Tabs.SelectedIndex = 0;
        }
    }

    /// <summary>Content for a module that could not start or whose view failed to load.</summary>
    public static UIElement CreateErrorContent(string message) =>
        new TextBlock
        {
            Margin = new Thickness(ErrorMargin),
            TextWrapping = TextWrapping.Wrap,
            Text = message ?? string.Empty,
        };
}
