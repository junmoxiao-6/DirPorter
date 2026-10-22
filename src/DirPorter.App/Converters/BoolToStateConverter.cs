using System.Globalization;
using System.Windows.Data;
using DirPorter.App.Services;

namespace DirPorter.App.Converters;

/// <summary>
/// bool → 本地化"可用 / 不可用"。用于能力矩阵的一行状态。
/// （只用于静态展示；语言切换后需要重新求值的场景一律用 ViewModel 上的文本属性，
///   避免转换器不刷新导致的"界面卡在旧语言"。）
/// </summary>
public sealed class BoolToStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => LocalizationService.Instance.Get(value is true ? "common.supported" : "common.unsupported");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
