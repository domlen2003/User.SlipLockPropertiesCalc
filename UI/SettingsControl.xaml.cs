using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace User.SlipLockPropertiesCalc.UI;

/// <summary>
/// Plugin settings page. All logic lives in <see cref="SettingsViewModel"/>; the code-behind only ties the view
/// model's live refresh to the page's visibility, so no timer runs while SimHub shows another page, and scrolls the
/// debug view into sight when the user switches it on (it opens below the simple view, out of the viewport).
/// </summary>
public partial class SettingsControl : UserControl
{
    /// <summary>Designer constructor (the XAML designer supplies a design-time data context).</summary>
    public SettingsControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>Creates the page for the running plugin.</summary>
    public SettingsControl(SettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private SettingsViewModel ViewModel => DataContext as SettingsViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsVisible)
        {
            ViewModel?.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel?.Stop();

    /// <summary>User switched the debug view on (not the initial binding while the page loads).</summary>
    private void OnShowDebugViewChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        // After the layout pass that makes the tabs visible, otherwise there is nothing to bring into view yet.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => DebugSection.BringIntoView()));
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ViewModel?.Start();
        }
        else
        {
            ViewModel?.Stop();
        }
    }
}
