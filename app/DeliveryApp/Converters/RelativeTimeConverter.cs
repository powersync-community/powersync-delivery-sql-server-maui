using System.Globalization;

namespace DeliveryApp.Converters;

/// <summary>Formats a UTC timestamp as "just now", "5 min ago", "3 h ago", "yesterday" or a date.</summary>
public class RelativeTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime time)
            return "";

        var local = time.ToLocalTime();
        var age = DateTime.Now - local;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (local.Date == DateTime.Today) return $"{(int)age.TotalHours} h ago";
        if (local.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        return local.ToString(local.Year == DateTime.Today.Year ? "MMM d" : "MMM d, yyyy", culture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
