using System;
using System.Globalization;
using System.Windows.Data;

namespace User.SlipLockPropertiesCalc.UI.Converters;

/// <summary>
/// NaN-safe number formatting for bindings. Single binding: the format comes from <c>ConverterParameter</c>.
/// Multi binding: <c>values[0]</c> is the number and <c>values[1]</c> the format (lets reusable controls expose a
/// <c>ValueFormat</c> property). Unknown values (NaN, infinity, null, non-numbers) render as <see cref="DisplayText.Missing"/>.
/// </summary>
public sealed class NumberFormatConverter : IValueConverter, IMultiValueConverter
{
    /// <summary>Used when no format is supplied.</summary>
    public const string DefaultFormat = "0.0";

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Format(value, parameter as string);

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length == 0)
        {
            return DisplayText.Missing;
        }

        string format = values.Length > 1 ? values[1] as string : parameter as string;
        return Format(values[0], format);
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Format(object value, string format)
    {
        // IConvertible covers double/float/int/long; strings and DependencyProperty.UnsetValue fall through.
        if (value is IConvertible convertible && !(value is string))
        {
            double number = convertible.ToDouble(DisplayText.Culture);
            return DisplayText.Number(number, string.IsNullOrEmpty(format) ? DefaultFormat : format);
        }

        return DisplayText.Missing;
    }
}
