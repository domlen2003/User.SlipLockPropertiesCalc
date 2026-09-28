using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace User.SlipLockPropertiesCalc.UI.Converters;

/// <summary>
/// Non-empty string → <see cref="Visibility.Visible"/>, null/empty → <see cref="Visibility.Collapsed"/>.
/// Lets hint and status texts disappear completely (no empty line) while they have nothing to say.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class TextToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
