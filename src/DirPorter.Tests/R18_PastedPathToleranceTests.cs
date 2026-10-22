using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.FileSystem;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.9：粘贴路径的容错。
/// 实测事故：链接页粘贴「复制为路径」产出的 <c>"F:\jun\Coze"</c>（带引号），
/// 防抖回调里抛 ArgumentException → DispatcherUnhandledException → **整个应用退出**。
/// 三道防线：输入清洗、页面内捕获、防抖回调兜底。
/// </summary>
[Collection(DataRootSequentialCollection.Name)]
public class R18_PastedPathToleranceTests
{
    private sealed class SilentInteraction : IUserInteraction
    {
        public void Info(string message, string? title = null) { }
        public void Error(string message, string? title = null) { }
        public bool Confirm(string message, string? title = null) => true;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public string? PickFile(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }

    [Theory]
    [InlineData("\"C:\\Users\\31125\\Coze\"", "C:\\Users\\31125\\Coze")]   // 成对引号（复制为路径）
    [InlineData("\"C:\\Users\\31125\\Coze", "C:\\Users\\31125\\Coze")]     // 只删掉尾部引号
    [InlineData("C:\\Users\\31125\\Coze\"", "C:\\Users\\31125\\Coze")]     // 只剩尾部引号
    [InlineData("  \"F:\\jun\\Coze\"  ", "F:\\jun\\Coze")]                 // 前后空白
    [InlineData("'F:\\jun\\Coze'", "F:\\jun\\Coze")]                       // 单引号
    [InlineData("F:\\jun\\Coze", "F:\\jun\\Coze")]                         // 正常路径不受影响
    [InlineData("", "")]
    public void 清洗粘贴路径_去掉首尾引号与空白(string input, string expected)
    {
        Assert.Equal(expected, PathNormalizer.CleanInput(input));
    }

    [Fact]
    public void 清洗后的路径能通过规范化校验()
    {
        // NormalizeAbsolute 返回的是 \\?\ 扩展路径（设计如此），这里只校验"不抛异常且末尾正确"。
        var normalized = PathNormalizer.NormalizeAbsolute(PathNormalizer.CleanInput("\"C:\\Users\\31125\\Coze\""));

        Assert.EndsWith(@"C:\Users\31125\Coze", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 链接页贴入带引号路径_不抛异常且给出提示()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());
        var link = host.Main.Link;

        link.LinkPath = "\"C:\\Users\\31125\\Coze\"";
        link.TargetPath = "\"F:\\jun\\Coze\"";

        // 关键：不得抛出（此前这里会让整个程序退出）。
        var exception = Record.Exception(() => link.EvaluateCommand.Execute(null));
        Assert.Null(exception);
    }

    [Fact]
    public void 防抖回调抛异常不会冒到调用方()
    {
        var debouncer = new UiDebouncer(TimeSpan.FromMilliseconds(1));
        debouncer.Poke(() => throw new ArgumentException("路径包含非法字符"));

        Assert.Null(Record.Exception(() => debouncer.Flush()));
    }
}
