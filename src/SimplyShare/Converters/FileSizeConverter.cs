using System.Globalization;
using System.Windows.Data;

namespace SimplyShare.Converters;

/// <summary>
/// 바이트 크기를 사람이 읽기 쉬운 형식으로 변환 (예: 1.5 MB)
/// </summary>
public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes)
            return "0 B";

        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:F1} {Units[unit]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
