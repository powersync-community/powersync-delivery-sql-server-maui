using System.Globalization;

namespace DeliveryApp.Converters;

/// <summary>True (e.g. a canceled stop) → struck-through text.</summary>
public class StrikethroughConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TextDecorations.Strikethrough : TextDecorations.None;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
