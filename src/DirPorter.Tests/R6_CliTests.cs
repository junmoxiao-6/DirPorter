using System.Xml.Linq;
using DirPorter.Core.Cli;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R6：完整 CLI 与打包契约。
///
/// 本组用例的由来很具体：
///  · 清单里注释写了命令行参数的**双连字符**形式，导致 XML 非法；
///    编译照过，exe 一启动就报"应用程序的并行配置不正确"（SxS 拒绝）。
///    于是有了"清单必须是合法 XML"的用例 —— 这类故障只在运行时炸，必须用测试兜住。
///  · link / unlink / linkdir 是**只经 ILinkProvider** 的写操作，值得端到端跑一遍。
/// </summary>
[Collection(DataRootSequentialCollection.Name)]
public class R6_CliTests
{
    // ============================ 打包契约 ============================

    [Fact]
    public void 应用清单_必须是合法XML_且注释里不能有双连字符()
    {
        foreach (var name in new[] { "app.manifest", "app.admin.manifest" })
        {
            var path = Path.Combine(RepoRoot(), "src", "DirPorter.Cli", name);
            Assert.True(File.Exists(path), $"清单文件缺失：{path}");

            var text = File.ReadAllText(path);

            // ① XML 必须可解析（双连字符等非法注释会在这里直接失败）
            var document = XDocument.Parse(text, LoadOptions.None);
            Assert.NotNull(document.Root);

            // ② 注释里出现 "X--Y" 是 XML 规范硬禁止的；单独断言一次并给出可读原因
            var commentIndex = text.IndexOf("<!--", StringComparison.Ordinal);
            if (commentIndex >= 0)
            {
                var end = text.IndexOf("-->", commentIndex + 4, StringComparison.Ordinal);
                var comment = end > 0 ? text[commentIndex..(end + 3)] : text[commentIndex..];
                Assert.False(comment.Contains("--", StringComparison.Ordinal) && !comment.StartsWith("<!--", StringComparison.Ordinal) && !comment.EndsWith("-->", StringComparison.Ordinal),
                    "XML 注释内不得出现两个连续的连字符（例如命令行参数的双连字符写法）。");
            }

            var ns = XNamespace.Get("urn:schemas-microsoft-com:asm.v1");
            Assert.NotNull(document.Root!.Element(ns + "assemblyIdentity"));

            // ③ 清单必须声明权限级别
            var trust = document.Root.Element(XNamespace.Get("urn:schemas-microsoft-com:asm.v2") + "trustInfo");
            Assert.NotNull(trust);
            Assert.Contains("requestedExecutionLevel", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 发布脚本_必须开启单文件与原生库自解压()
    {
        var script = Path.Combine(RepoRoot(), "publish.ps1");
        Assert.True(File.Exists(script), $"缺少发布脚本：{script}");

        var text = File.ReadAllText(script);

        // Microsoft.Data.Sqlite 的原生 e_sqlite3 必须能被单文件模式找到，
        // 否则"条目数超过阈值自动切 SQLite"这条路径会在发布版里静默失效。
        Assert.Contains("PublishSingleFile=true", text, StringComparison.Ordinal);
        Assert.Contains("IncludeNativeLibrariesForSelfExtract=true", text, StringComparison.Ordinal);

        // 发布后必须自检（脚本自带 selfcheck 调用）
        Assert.Contains("selfcheck", text, StringComparison.Ordinal);
    }

    // ============================ 参数解析 ============================

    [Theory]
    [InlineData("100MB", 104857600L)]
    [InlineData("1GB", 1073741824L)]
    [InlineData("1.5GB", 1610612736L)]
    [InlineData("1048576", 1048576L)]
    [InlineData("512KB", 524288L)]
    [InlineData("100mb", 104857600L)]
    public void 尺寸解析_支持人类可读写法(string text, long expected)
    {
        Assert.True(CliHost.TryParseSize(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5MB")]
    [InlineData("")]
    public void 尺寸解析_非法输入返回false(string text)
    {
        Assert.False(CliHost.TryParseSize(text, out _));
    }

    [Theory]
    [InlineData("auto", null)]
    [InlineData("junction", "Junction")]
    [InlineData("/J", null)]              // 带斜杠的 mklink 写法不被接受（避免与 shell 拼接混用）
    [InlineData("symlink-dir", "DirectorySymlink")]
    [InlineData("symlink-file", "FileSymlink")]
    [InlineData("hardlink", "HardLink")]
    [InlineData("H", "HardLink")]
    public void 链接类型解析(string text, string? expected)
    {
        if (expected is null)
        {
            // "auto" 合法但解析为 null；"/J" 非法
            var ok = LinkCommands.TryParseKind(text, out var kind, out _);
            if (text == "auto")
            {
                Assert.True(ok);
                Assert.Null(kind);
            }
            else
            {
                Assert.False(ok);
            }

            return;
        }

        Assert.True(LinkCommands.TryParseKind(text, out var parsed, out _));
        Assert.Equal(expected, parsed?.ToString());
    }

    [Fact]
    public void 链接类型解析_非法值给出可读错误()
    {
        Assert.False(LinkCommands.TryParseKind("dragon", out _, out var error));
        Assert.Contains("无法识别的链接类型", error);
    }

    // ============================ 端到端：link / unlink / linkdir ============================

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CliHost.Run(args, stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void link_干跑不写入_并且列出其余类型的可用性()
    {
        using var ws = new TempWorkspace("r6-link-dry");
        var target = ws.CreateDirectory("real-target");
        var link = ws.PathOf("link-old");

        var (code, stdout, _) = Run("link", "--source", link, "--target", target, "--dry-run");

        Assert.Equal(ExitCodes.Success, code);
        Assert.False(Directory.Exists(link));                     // 干跑零写入
        Assert.Contains("将执行的操作", stdout);
        Assert.Contains("其余三种类型的可用性", stdout);
    }

    [Fact]
    public void link_创建后inspect可见_且unlink后目标数据完好()
    {
        using var ws = new TempWorkspace("r6-link-roundtrip");
        var target = ws.CreateDirectory("real-target");
        var sentinel = ws.CreateFile(@"real-target\sentinel.txt", "must-survive");
        var link = ws.PathOf("link-old");

        var create = Run("link", "--source", link, "--target", target, "--yes");
        Assert.Equal(ExitCodes.Success, create.ExitCode);
        Assert.Contains("创建成功", create.Stdout);

        var inspect = Run("inspect", "--path", link, "--json");
        Assert.Equal(ExitCodes.Success, inspect.ExitCode);
        Assert.Contains("Junction", inspect.Stdout);

        var unlink = Run("unlink", "--path", link, "--yes");
        Assert.Equal(ExitCodes.Success, unlink.ExitCode);
        Assert.False(Directory.Exists(link));

        // ★ 安全红线：目标数据必须完好
        Assert.True(File.Exists(sentinel));
        Assert.Equal("must-survive", File.ReadAllText(sentinel));
        Assert.Contains("目标数据未被触碰", unlink.Stdout);
    }

    [Fact]
    public void unlink_对非链接一律拒绝()
    {
        using var ws = new TempWorkspace("r6-unlink-refuse");
        var plain = ws.CreateDirectory("plain-directory");
        ws.CreateFile(@"plain-directory\data.txt", "important");

        var (code, _, stderr) = Run("unlink", "--path", plain, "--yes");

        Assert.Equal(ExitCodes.PrecheckFailed, code);
        Assert.Contains("不是链接", stderr);
        Assert.True(Directory.Exists(plain));                 // 真实目录必须原封不动
        Assert.True(File.Exists(ws.PathOf(@"plain-directory\data.txt")));
    }

    [Fact]
    public void unlink_不存在的路径视为幂等成功()
    {
        using var ws = new TempWorkspace("r6-unlink-missing");

        var (code, stdout, _) = Run("unlink", "--path", ws.PathOf("nope"), "--yes");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("无需解除链接", stdout);
    }

    [Fact]
    public void linkdir_建立硬链接并支持增量跳过()
    {
        using var ws = new TempWorkspace("r6-linkdir");
        var source = ws.CreateDirectory("src");
        ws.CreateFile(@"src\a.txt", "alpha");
        ws.CreateFile(@"src\sub\b.txt", "beta");
        var target = ws.PathOf("dst");

        var first = Run("linkdir", "--source", source, "--target", target, "--type", "hardlink", "--yes");
        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Contains("创建 2 个链接", first.Stdout);

        // 相对子目录结构必须保持
        Assert.True(File.Exists(ws.PathOf(@"dst\a.txt")));
        Assert.True(File.Exists(ws.PathOf(@"dst\sub\b.txt")));

        // 增量：重复执行不应再建
        var second = Run("linkdir", "--source", source, "--target", target, "--type", "hardlink", "--incremental", "--yes");
        Assert.Equal(ExitCodes.Success, second.ExitCode);
        Assert.Contains("创建 0 个链接", second.Stdout);
        Assert.Contains("跳过 2 个", second.Stdout);
    }

    [Fact]
    public void linkdir_拒绝目录级类型()
    {
        using var ws = new TempWorkspace("r6-linkdir-reject");
        var source = ws.CreateDirectory("src");

        var (code, _, stderr) = Run("linkdir", "--source", source, "--target", ws.PathOf("dst"), "--type", "junction", "--yes");

        Assert.Equal(ExitCodes.InvalidArguments, code);
        Assert.Contains("只支持", stderr);
    }

    // ============================ scan --root / --min-size ============================

    [Fact]
    public void scan_支持root别名与最小体积过滤()
    {
        using var ws = new TempWorkspace("r6-scan-root");
        var root = ws.CreateDirectory("root");
        ws.CreateFile(@"root\big\payload.bin", new string('x', 5000));
        ws.CreateFile(@"root\small\tiny.txt", "x");

        var (code, stdout, _) = Run("scan", "--root", root, "--min-size", "1KB");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("体积 ≥", stdout);
        Assert.Contains("big", stdout);
        // 小目录不该出现在过滤结果里
        var filteredSection = stdout[stdout.IndexOf("体积 ≥", StringComparison.Ordinal)..];
        Assert.DoesNotContain("small", filteredSection);
    }

    [Fact]
    public void scan_非法min_size给出参数错误()
    {
        using var ws = new TempWorkspace("r6-scan-bad-size");
        var root = ws.CreateDirectory("root");

        var (code, _, stderr) = Run("scan", "--root", root, "--min-size", "很多");

        Assert.Equal(ExitCodes.InvalidArguments, code);
        Assert.Contains("无法解析 --min-size", stderr);
    }

    [Fact]
    public void scan_缺少路径时提示用法()
    {
        var (code, _, stderr) = Run("scan");
        Assert.Equal(ExitCodes.InvalidArguments, code);
        Assert.Contains("--root", stderr);
    }

    // ============================ selfcheck / verify / restore --id ============================

    [Fact]
    public void selfcheck_全绿()
    {
        var (code, stdout, stderr) = Run("selfcheck");

        Assert.True(code == ExitCodes.Success, $"selfcheck 失败：{stderr}");
        Assert.Contains("台账 JSONL 后端往返", stdout);
        Assert.Contains("SQLite", stdout);                 // 原生 SQLite 可用性
        Assert.Contains("能力矩阵", stdout);
        Assert.Contains("自检通过", stdout);
    }

    [Fact]
    public void verify_无记录时成功返回()
    {
        var (code, stdout, _) = Run("verify", "--json");
        Assert.Equal(ExitCodes.Success, code);
        Assert.True(stdout.Contains("没有迁移记录", StringComparison.Ordinal) || stdout.Contains("共校验", StringComparison.Ordinal));
    }

    [Fact]
    public void restore_未知id给出参数错误()
    {
        var (code, _, stderr) = Run("restore", "--id", "mig-不存在-batch", "--yes");
        Assert.Equal(ExitCodes.InvalidArguments, code);
        Assert.True(stderr.Contains("找不到记录", StringComparison.Ordinal) || stderr.Contains("需要", StringComparison.Ordinal));
    }

    // ============================ 辅助 ============================

    /// <summary>从测试输出目录向上找到仓库根（含 DirPorter.sln）。</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DirPorter.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（未发现 DirPorter.sln）。");
    }
}
