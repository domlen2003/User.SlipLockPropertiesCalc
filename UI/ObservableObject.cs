using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DivebombLogistics.UI;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base for view models and item models.
/// <see cref="SetProperty{T}"/> raises <see cref="PropertyChanged"/> only when the value actually changes, which keeps
/// the 10 Hz UI refresh cheap: unchanged values cause no binding work at all.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> and raises <see cref="PropertyChanged"/> when it differs.
    /// Uses <see cref="EqualityComparer{T}.Default"/>, so NaN compares equal to NaN (no endless updates for unknown values).
    /// </summary>
    /// <returns>True when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="propertyName"/>.</summary>
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
