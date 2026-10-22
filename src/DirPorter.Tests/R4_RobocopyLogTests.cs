using System.Text;
using DirPorter.Core.Copy;
using DirPorter.Core.FileSystem;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R4 附带修复的回归：robocopy 日志解析。
///
/// 背景（本轮实测发现）：robocopy 的 <c>/LOG</c> 文件是**系统 ANSI 单字节编码**，
/// <c>/UNICODE</c> 只影响控制台。此前解析器按 UTF-16 读，导致
/// **引擎 A 的"已复制文件数/字节数"恒为 0、进度恒为 0、中文错误行也检不出**。
/// </summary>
public class R4_RobocopyLogTests
{
    /// <summary>真实的 zh-CN 汇总块（从 robocopy 日志原样摘录，仅保留关键行）。</summary>
    private static readonly string[] ChineseSummary =
    {
        "",
        "------------------------------------------------------------------------------",
        "",
        "                  总数        复制        跳过       不匹配        失败        其他",
        "       目录:         2         2         1         0         0         0",
        "       文件:         3         3         0         0         0         0",
        "       字节:    123456    123456         0         0         0         0",
        "       时间:   0:00:00   0:00:00                       0:00:00   0:00:00",
        "",
        "       速度:             9,000 字节/秒。",
    };

    /// <summary>英文 robocopy 日志（标签带空格与冒号），用于验证解析不依赖文案。</summary>
    private static readonly string[] EnglishSummary =
    {
        "",
        "               Total    Copied   Skipped  Mismatch    FAILED    Extras",
        "    Dirs :         2         2         1         0         0         0",
        "   Files :         3         3         0         0         0         0",
        "   Bytes :    123456    123456         0         0         0         0",
        "   Times :   0:00:00   0:00:00                       0:00:00   0:00:00",
    };

    [Fact]
    public void 中文汇总块_可解析出文件与字节计数()
    {
        Assert.True(RobocopyCopier.TryParseSummary(ChineseSummary, out var summary));

        Assert.Equal(3, summary.FilesTotal);
        Assert.Equal(3, summary.FilesCopied);
        Assert.Equal(0, summary.FilesSkipped);
        Assert.Equal(123456, summary.BytesCopied);
        Assert.Equal(2, summary.DirectoriesCopied);
    }

    [Fact]
    public void 英文汇总块_同样可解析_证明不依赖本地化文案()
    {
        Assert.True(RobocopyCopier.TryParseSummary(EnglishSummary, out var summary));

        Assert.Equal(3, summary.FilesCopied);
        Assert.Equal(123456, summary.BytesCopied);
    }

    [Fact]
    public void 没有汇总块时不误判()
    {
        Assert.False(RobocopyCopier.TryParseSummary(new[] { "   ROBOCOPY     开始", "      5\tC:\\a.txt" }, out _));
    }

    [Fact]
    public void ANSI解码_ASCII与UTF8BOM都能正确处理()
    {
        // ASCII 在 ANSI / UTF-8 下一致
        Assert.Equal("C:\\Users\\a.txt", AnsiText.Decode(Encoding.ASCII.GetBytes("C:\\Users\\a.txt")));

        // UTF-8 BOM 自动识别
        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("中文路径")).ToArray();
        Assert.Equal("中文路径", AnsiText.Decode(utf8));

        // UTF-16LE BOM 自动识别
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("中文路径")).ToArray();
        Assert.Equal("中文路径", AnsiText.Decode(utf16));
    }

    /// <summary>端到端：引擎 A 的计数必须是真实值（修复前恒为 0）。</summary>
    [Fact]
    public async Task 引擎A_报告的复制文件数与字节数为真实值()
    {
        using var ws = new TempWorkspace("r4-rc-count");
        var source = ws.CreateDirectory("Source");
        var a = ws.CreateFile(@"Source\a.txt", new string('a', 1000));
        var b = ws.CreateFile(@"Source\sub\b.txt", new string('b', 2000));
        var c = ws.CreateFile(@"Source\sub\c.txt", new string('c', 3000));

        var expectedBytes = new FileInfo(a).Length + new FileInfo(b).Length + new FileInfo(c).Length;
        var target = ws.PathOf("Target");

        var copier = new RobocopyCopier();
        var result = await copier.CopyAsync(
            new CopyRequest(source, target, new CopyOptions { Threads = 2 }), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, result.Outcome);
        Assert.Equal(3, result.FilesCopied);
        Assert.Equal(expectedBytes, result.BytesCopied);
        Assert.Equal(0, result.FailedCount);

        // 再跑一次：目标已是最新，应全部"跳过"
        var second = await copier.CopyAsync(
            new CopyRequest(source, target, new CopyOptions { Threads = 2 }), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, second.Outcome);
        Assert.Equal(3, second.FilesSkipped);
    }

    [Fact]
    public void 中文路径的文件行可被解析()
    {
        // 该行是 ANSI 解码后才能得到的形态（测试编码转换 + 行解析的配合）
        var line = "  \t\t       5\tC:\\Users\\31125\\AppData\\Local\\Temp\\probe\\中文 目录\\文件.txt";
        Assert.True(RobocopyCopier.TryParseFileLine(line, out var size, out var path));
        Assert.Equal(5, size);
        Assert.Contains("中文 目录", path);
    }
}
