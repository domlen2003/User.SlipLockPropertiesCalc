using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DivebombLogistics.SpeedDial.UI.ViewModels;

namespace DivebombLogistics.SpeedDial.UI;

/// <summary>
/// Content of the "Speed Dial" tab of the DLP settings page (<c>UI/MainView</c>). All logic lives in
/// <see cref="SpeedDialViewModel"/>; the code-behind only ties the view model's live refresh to the page's visibility
/// (no timer runs while SimHub shows another page), commits edit boxes and role boxes on Enter or when the page is
/// hidden (so a typed value is never lost because the box kept the focus), commits a role picked from the list at
/// once, and focuses the inline rename box (saving the name when the box loses the focus).
/// </summary>
public partial class SpeedDialView : UserControl
{
    /// <summary>Designer constructor (the XAML designer supplies a design-time data context).</summary>
    public SpeedDialView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>Creates the page for the running plugin.</summary>
    public SpeedDialView(SpeedDialViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private SpeedDialViewModel ViewModel => DataContext as SpeedDialViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsVisible)
        {
            ViewModel?.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CommitFocusedTextBox();
        ViewModel?.Stop();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ViewModel?.Start();
        }
        else
        {
            CommitFocusedTextBox();
            ViewModel?.Stop();
        }
    }

    /// <summary>Enter in an edit box commits its binding (the boxes otherwise commit on focus loss).</summary>
    private void OnCommitKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>Enter in a role box commits the typed role (the box otherwise commits on focus loss).</summary>
    private void OnRoleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ComboBox box)
        {
            box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>A role picked from the list is stored at once (the box's Text follows the selection after this event).</summary>
    private void OnRoleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.SelectedItem != null)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource()));
        }
    }

    /// <summary>The inline rename box appeared: focus it with the name selected, so typing replaces it at once.</summary>
    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            // After layout: a box that just became visible cannot take the focus in the same pass.
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    if (box.IsVisible)
                    {
                        box.Focus();
                        box.SelectAll();
                    }
                }));
        }
    }

    /// <summary>Leaving the rename box saves the name (Escape cancels before the box loses the focus).</summary>
    private void OnRenameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is PresetItem preset && preset.IsRenaming
            && preset.CommitRenameCommand.CanExecute(null))
        {
            preset.CommitRenameCommand.Execute(null);
        }
    }

    /// <summary>Commits the edit box (or editable role box) of this page that has the keyboard focus (page hidden or unloaded).</summary>
    private void CommitFocusedTextBox()
    {
        if (!(Keyboard.FocusedElement is TextBox box) || !IsAncestorOf(box))
        {
            return;
        }

        if (box.TemplatedParent is ComboBox combo)
        {
            // The text part of an editable ComboBox: its own Text is template-bound, the binding sits on the ComboBox.
            combo.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            return;
        }

        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
