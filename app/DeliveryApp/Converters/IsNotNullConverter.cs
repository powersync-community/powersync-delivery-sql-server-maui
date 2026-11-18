using System.Globalization;

namespace DeliveryApp.Converters;

/// <summary>True when the value is set (for strings: not null or empty). Used to hide empty labels.</summary>
public class IsNotNullConverter : IValueConverter
{
    public static readonly IsNotNullConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s.Length > 0 : value is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
