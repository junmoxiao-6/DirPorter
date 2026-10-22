using System.Windows.Data;
using System.Windows.Markup;
using DirPorter.App.Services;

namespace DirPorter.App.Markup;

/// <summary>
/// 本地化标记扩展：XAML 里写 <c>Text="{markup:Loc nav.scan}"</c>。
///
/// 返回的是对 <see cref="LocalizationService"/> 索引器的绑定，
/// 因此切换语言时（服务抛 <c>Item[]</c> 变更）界面自动刷新，无需重启。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
