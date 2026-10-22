using System.Diagnostics;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using Xunit;
using Xunit.Abstractions;

namespace DirPorter.Tests;

/// <summary>
/// 与系统工具对账（验收标准：inspect 输出必须与 dir /AL、fsutil reparsepoint query 的结果一致）。
///
/// 本组测试的关键价值：验证我们能正确解析**由 Windows 自己创建**的 Junction
/// （此前所有用例都只读自己写出来的重解析缓冲区，那是自证循环）。
/// 这同时解决设计文档 §18 的 TODO(verify)-6：PrintName/SubstituteName 的真实布局。
/// </summary>
public class SystemToolParityTests
{
    private readonly ITestOutputHelper _output;

    public SystemToolParityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void 解析mklink创建的Junction_与系统工具一致()
    {
        using var ws = new TempWorkspace("parity-mklink");
        var target = ws.CreateDirectory("RealTarget");
        var link = ws.PathOf("SysLink");

        var exitCode = RunCmd($"mklink /J \"{link}\" \"{target}\"", out var mklinkOutput, out var mklinkError);
        _output.WriteLine($"mklink 退出码={exitCode}");
        _output.WriteLine($"mklink 输出：{mklinkOutput.Trim()}");
        if (!string.IsNullOrWhiteSpace(mklinkError)) _output.WriteLine($"mklink 错误：{mklinkError.Trim()}");

        Assert.True(Directory.Exists(link), $"系统 mklink 未能创建 Junction：{mklinkOutput} {mklinkError}");

        // 1) 我们的解析结果
        var provider = new JunctionLinkProvider(new Win32VolumeResolver());
        var info = provider.Inspect(link);

        _output.WriteLine($"我们的 Inspect：{info.Describe()}");
        _output.WriteLine($"原始重解析缓冲区（前 64 字节）：{info.RawHexPreview}");

        Assert.True(info.IsReparsePoint);
        Assert.Equal(LinkKind.Junction, info.Kind);
        Assert.Equal(0xA0000003u, info.ReparseTag);
        Assert.Equal(target, info.TargetAbsolute!, StringComparer.OrdinalIgnoreCase);
        Assert.False(info.IsRelative);
        Assert.False(info.IsDangling);

        // 2) fsutil reparsepoint query 的标签必须与我们的解析一致
        var fsutilExit = RunCmd($"fsutil reparsepoint query \"{link}\"", out var fsutilOutput, out var fsutilError);
        _output.WriteLine($"fsutil 退出码={fsutilExit}");
        _output.WriteLine($"fsutil 输出：{Environment.NewLine}{fsutilOutput}{fsutilError}");

        Assert.Contains("a0000003", fsutilOutput.ToLowerInvariant());

        // 3) dir /AL 必须把它标为 JUNCTION 类条目
        var parent = Path.GetDirectoryName(link)!;
        RunCmd($"dir /AL \"{parent}\"", out var dirOutput, out _);
        _output.WriteLine($"dir /AL 输出：{Environment.NewLine}{dirOutput}");

        Assert.Contains("JUNCTION", dirOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFileName(link), dirOutput, StringComparison.OrdinalIgnoreCase);

        // 4) 通过链接读写，确认系统层面的重定向确实生效
        File.WriteAllText(Path.Combine(link, "parity.txt"), "parity");
        Assert.Equal("parity", File.ReadAllText(Path.Combine(target, "parity.txt")));
    }

    [Fact]
    public void 我们创建的Junction_能被系统工具识别()
    {
        using var ws = new TempWorkspace("parity-ours");
        var target = ws.CreateDirectory("Target");
        var link = ws.PathOf("OurLink");

        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        var exit = RunCmd($"fsutil reparsepoint query \"{link}\"", out var output, out var error);
        _output.WriteLine($"fsutil 退出码={exit}{Environment.NewLine}{output}{error}");

        Assert.Contains("a0000003", output.ToLowerInvariant());

        var parent = Path.GetDirectoryName(link)!;
        RunCmd($"dir /AL \"{parent}\"", out var dirOutput, out _);
        _output.WriteLine($"dir /AL 输出：{Environment.NewLine}{dirOutput}");
        Assert.Contains("JUNCTION", dirOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 外部工具创建的Junction_删除后目标数据无损()
    {
        using var ws = new TempWorkspace("parity-delete");
        var target = ws.CreateDirectory("Target");
        var sentinel = ws.CreateFile(@"Target\sentinel.txt", "KEEP");
        var link = ws.PathOf("SysLink");

        RunCmd($"mklink /J \"{link}\" \"{target}\"", out var out1, out var err1);
        Assert.True(Directory.Exists(link), $"{out1} {err1}");

        new JunctionLinkProvider(new Win32VolumeResolver()).Delete(link);

        Assert.False(Directory.Exists(link));
        Assert.Equal("KEEP", File.ReadAllText(sentinel));
    }

    [Fact]
    public void FSCTL常量_由Windows行为间接验证()
    {
        // 若 FSCTL 常量写错，Create/Delete 必然失败；此处再做一次显式断言作为回归锚点。
        Assert.Equal(0x000900A4u, Kernel32.FsctlSetReparsePoint);
        Assert.Equal(0x000900A8u, Kernel32.FsctlGetReparsePoint);
        Assert.Equal(0x000900ACu, Kernel32.FsctlDeleteReparsePoint);
    }

    private static int RunCmd(string arguments, out string stdout, out string stderr)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var process = Process.Start(psi)!;
        stdout = process.StandardOutput.ReadToEnd();
        stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return process.ExitCode;
    }
}
