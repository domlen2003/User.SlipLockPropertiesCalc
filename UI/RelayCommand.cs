using System;
using System.Windows.Input;

namespace DivebombLogistics.UI;

/// <summary>
/// <see cref="ICommand"/> that delegates to an action. <see cref="CanExecuteChanged"/> is raised explicitly through
/// <see cref="RaiseCanExecuteChanged"/> (not via <c>CommandManager.RequerySuggested</c>), so the view model decides
/// when button states are re-evaluated instead of WPF polling on every input event.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    /// <param name="execute">Action run by the command.</param>
    /// <param name="canExecute">Optional availability check; null = always available.</param>
    public RelayCommand(Action execute, Func<bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

    /// <inheritdoc />
    public void Execute(object parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    /// <summary>Tells bound controls to re-query <see cref="CanExecute"/>.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
