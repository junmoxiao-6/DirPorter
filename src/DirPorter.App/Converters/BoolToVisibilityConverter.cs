using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DirPorter.App.Converters;

/// <summary>
/// bool → <see cref="Visibility"/>。<c>ConverterParameter="invert"</c> 时反转（true → Collapsed）。
/// 语言无关、无状态，因此不像文本转换器那样有"切换语言后不刷新"的问题。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string text && string.Equals(text, "invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
