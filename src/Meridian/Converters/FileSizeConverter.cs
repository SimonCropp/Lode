using System.Globalization;
using System.Windows.Data;

namespace Meridian.Converters;

public class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long size || size == 0)
        {
            return string.Empty;
        }

        return size switch
        {
            < 1024 => $"{size} B",
            < 1024 * 1024 => $"{size / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{size / (1024.0 * 1024):F1} MB",
            _ => $"{size / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
