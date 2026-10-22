using System.Windows;

namespace DirPorter.App.Services;

/// <summary>
/// 深色 / 浅色主题切换（需求「通用」）。
///
/// 做法：把主题字典作为**一个可替换的节**挂在 <see cref="Application.Resources"/> 上，
/// 视图里一律用 <c>DynamicResource</c> 引用画笔键，切换时整体替换即可生效
/// （不需要重启、也不需要重建窗口）。
/// </summary>
public static class ThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    private static ResourceDictionary? _current;

    /// <summary>
    /// 本程序集名（= <c>dirporter</c>，与根命名空间 <c>DirPorter.App</c> **不同**）。
    ///
    /// ⚠️ 惨痛教训：pack URI 的 authority 段必须是**程序集名**而不是命名空间。
    /// 早期写成 <c>pack://application:,,,/DirPorter.App;component/...</c> 时，
    /// 运行期抛 FileNotFoundException（找不到程序集 DirPorter.App），
    /// 主题字典加载失败 → 界面丢掉全部配色。这里改为从运行时反射取，改名也不会再踩。
    /// </summary>
    private static readonly string AssemblyName =
        typeof(ThemeService).Assembly.GetName().Name ?? "dirporter";

    public static string Current { get; private set; } = Light;

    public static string Normalize(string? theme)
        => string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;

    public static void Apply(string? theme)
    {
        var normalized = Normalize(theme);
        Current = normalized;

        var application = Application.Current;
        if (application is null) return;   // 设计期/测试环境无 Application

        var merged = application.Resources.MergedDictionaries;

        if (_current is not null) merged.Remove(_current);

        _current = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{AssemblyName};component/Themes/{normalized}.xaml",
                UriKind.Absolute),
        };

        merged.Add(_current);
    }
}
