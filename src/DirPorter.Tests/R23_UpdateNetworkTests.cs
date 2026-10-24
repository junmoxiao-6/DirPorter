using DirPorter.App.Services;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.15：更新下载的超时与错误文案。
/// 实测事故（0.3.2）：检查更新直接抛 `HttpClient.Timeout of 30 seconds`，用户完全看不懂；
/// 而且 30 秒总超时对 65 MB 的更新包**必然**不够 —— 检查能过、下载必挂。
/// </summary>
public class R23_UpdateNetworkTests
{
    [Fact]
    public void 超时异常被翻译成人话而不是堆原始文案()
    {
        var text = UpdateService.DescribeNetworkError(new TaskCanceledException("The request was canceled..."));

        Assert.Contains("超时", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HTTP_状态码单独呈现()
    {
        var text = UpdateService.DescribeNetworkError(
            new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound));

        Assert.Contains("404", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 连接失败与读取中断各有说法()
    {
        Assert.Contains("连接失败", UpdateService.DescribeNetworkError(new HttpRequestException("boom")), StringComparison.Ordinal);
        Assert.Contains("读取中断", UpdateService.DescribeNetworkError(new IOException("boom")), StringComparison.Ordinal);
    }

    [Fact]
    public void 下载通道不能设置固定总超时()
    {
        // 65 MB 的包在慢网络下需要几十分钟；设固定超时会让下载必然半途而废（0.3.2 实测）。
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "DirPorter.App")))
        {
            root = root.Parent;
        }

        var src = File.ReadAllText(Path.Combine(root!.FullName, "src", "DirPorter.App", "Services", "UpdateService.cs"));

        Assert.Contains("InfiniteTimeSpan", src, StringComparison.Ordinal);       // 下载 client 无总超时
        Assert.Contains("RangeHeaderValue", src, StringComparison.Ordinal);       // 断点续传
        Assert.DoesNotContain("FromSeconds(30)", src, StringComparison.Ordinal);  // 不能回到 30 秒
    }
}
