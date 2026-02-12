using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SimplyShare.Converters;

/// <summary>
/// Boolean 값을 색상으로 변환 (온라인: 녹색, 오프라인: 회색)
/// </summary>
public sealed class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true
            ? new SolidColorBrush(Colors.LimeGreen)
            : new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
