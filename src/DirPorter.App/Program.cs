using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DirPorter.App.Services;
using DirPorter.Core.Cli;
using Microsoft.Win32.SafeHandles;

namespace DirPorter.App;

/// <summary>
/// 单 exe 入口（需求「命令行模式」：同一 exe，无参时启动 GUI）。
///
/// 分流规则：
/// <list type="bullet">
/// <item>带任何参数 → CLI 模式：<c>AttachConsole(ATTACH_PARENT_PROCESS)</c> 接回父控制台后交给 <see cref="CliHost"/>。</item>
/// <item>无参数 → GUI 模式：启动 WPF 应用（含单实例、崩溃兜底）。</item>
/// <item>带 <c>--elevate</c> 且未提权 → 以管理员身份重启自身，转发子进程输出与退出码。</item>
/// </list>
///
/// 已知限制（WinExe 宿主的固有代价，README 已写明）：
/// GUI 子系统的进程默认不被 cmd 等待，交互式使用时提示符会先返回、输出可能交错；
/// 重定向 / 管道场景（脚本集成）不受影响，输出完整。
/// </summary>
internal static class Program
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0) return RunCliEntry(args);
            return RunGui();
        }
        catch (Exception ex)
        {
            // CLI 模式下任何异常都不该让用户看到 .NET 堆栈；GUI 模式下由全局兜底接管。
            try
            {
                Console.Error.WriteLine($"[DirPorter] 未处理的内部错误：{ex}");
            }
            catch
            {
                // 连 stderr 都没有（无控制台）时不再抛。
            }

            return ExitCodes.InternalError;
        }
    }

    // ---------------------------------------------------------------- CLI

    /// <summary>
    /// CLI 入口：先处理 <c>--elevate</c>，再进正常执行。
    /// 这样"命令行提权"与"图形界面提权"共用同一套令牌探测与启动逻辑。
    /// </summary>
    private static int RunCliEntry(string[] args)
    {
        if (!ElevationArguments.TryTakeElevate(args, out var inner)) return RunCli(args);

        if (ElevationService.Current.IsElevated)
        {
            // 已经提权（例如在管理员控制台里直接跑）：去掉开关照常执行，不再弹 UAC。
            return RunCli(inner);
        }

        return RelaunchElevated(inner);
    }

    /// <summary>
    /// 以管理员身份重启自身并转发输出/退出码。
    ///
    /// 输出为什么要走文件：<c>runas</c> 通过 ShellExecute 启动，子进程会拿到**自己的新控制台**，
    /// 父进程既拿不到它的 stdout，也没法直接打印。所以子进程用 <c>--elevate-relay</c> 把输出落盘，
    /// 父进程等它退出后读出来打印 —— 保证脚本里 <c>--elevate</c> 的输出与退出码都是完整的。
    /// </summary>
    private static int RelaunchElevated(string[] args)
    {
        var relayPath = Path.Combine(Path.GetTempPath(), $"dirporter-elevate-{Guid.NewGuid():N}.log");

        var childArguments = new List<string>(args) { ElevationArguments.RelaySwitch, relayPath };
        var launch = ElevationService.Current.LaunchElevated(
            ElevationArguments.ToCommandLine(childArguments), waitForExit: true);

        if (!launch.Started)
        {
            var console = OpenStandardWriter(StdErrorHandle) ?? TextWriter.Null;
            console.WriteLine(launch.Canceled
                ? "[DirPorter] 已取消管理员提权：命令未执行，磁盘未做任何改动。"
                : $"[DirPorter] 无法以管理员身份启动：{launch.Error}");
            console.WriteLine("[DirPorter] 提示：也可以用「开发者模式」免提权创建符号链接（设置 → 系统 → 开发者选项）。");
            console.Flush();
            return ExitCodes.NeedsAdministrator;
        }

        try
        {
            if (File.Exists(relayPath))
            {
                var console = OpenStandardWriter(StdOutputHandle) ?? TextWriter.Null;
                foreach (var line in File.ReadAllLines(relayPath)) console.WriteLine(line);
                console.Flush();
            }
        }
        catch
        {
            // 转发失败不影响退出码
        }
        finally
        {
            try { File.Delete(relayPath); } catch { /* 临时文件删不掉不算错 */ }
        }

        return launch.ExitCode;
    }

    private static int RunCli(string[] args)
    {
        var attached = AttachConsole(AttachParentProcess);

        var stdout = OpenStandardWriter(StdOutputHandle) ?? TextWriter.Null;
        var stderr = OpenStandardWriter(StdErrorHandle) ?? TextWriter.Null;

        // 提权子进程：把输出再写一份到回传文件（父进程据此转发）
        StreamWriter? relay = null;
        if (ElevationArguments.TryTakeRelay(args, out var relayPath, out var withoutRelay)
            && !string.IsNullOrWhiteSpace(relayPath))
        {
            args = withoutRelay;

            try
            {
                relay = new StreamWriter(relayPath!, append: false, new UTF8Encoding(false)) { AutoFlush = true };
                stdout = new TeeTextWriter(stdout, relay);
                stderr = new TeeTextWriter(stderr, relay);
            }
            catch
            {
                relay = null;   // 回传失败就只写控制台，不影响命令本身
            }
        }

        try
        {
            var exitCode = CliHost.Run(args, stdout, stderr);
            stdout.Flush();
            stderr.Flush();
            return exitCode;
        }
        catch (Exception ex)
        {
            try
            {
                stderr.WriteLine($"[DirPorter] 内部错误：{ex.Message}");
                stderr.Flush();
            }
            catch
            {
                // 连 stderr 都写不出去（无控制台）时不再抛。
            }

            return ExitCodes.InternalError;
        }
        finally
        {
            relay?.Dispose();

            // 只在自己附加的场合才分离，避免影响已有控制台。
            if (attached) FreeConsole();
        }
    }

    /// <summary>
    /// 打开标准输出/错误流。**任何情况下都不得抛异常**。
    ///
    /// 踩过的坑：直接用 <c>new FileStream(new SafeFileHandle(GetStdHandle(...)))</c> 包装句柄，
    /// 在管道句柄（MSYS/Git Bash、部分 CI 的重定向）上会抛 ArgumentException/IOException，
    /// 而且当时这段代码在 try 之外 → 整个进程崩溃并打印 .NET 堆栈。
    /// 正确顺序：先用 <see cref="Console.OpenStandardOutput"/>（它能正确处理控制台/管道/文件三种情形），
    /// 失败再退回句柄包装，最后退回丢弃输出（此时也绝不能崩）。
    /// </summary>
    private static TextWriter? OpenStandardWriter(int handleId)
    {
        // 1) 首选 Console 自己的流
        try
        {
            var consoleStream = handleId == StdOutputHandle
                ? Console.OpenStandardOutput()
                : Console.OpenStandardError();

            if (consoleStream is not null && consoleStream != Stream.Null)
                return new StreamWriter(consoleStream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            // 落到下面
        }

        // 2) 退回直接包装标准句柄
        try
        {
            var handle = GetStdHandle(handleId);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;

            var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write);
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- GUI

    /// <summary>
    /// 启动 GUI 前的提权通知（键名 + 可选参数）。UAC 被取消或启动失败时降级继续运行，
    /// 由 <see cref="App.OnStartup"/> 读出并在界面底部提示——"自动请求"不等于"提不到权就用不了"。
    /// </summary>
    internal static (string Key, string? Arg)? ElevationNotice { get; private set; }

    /// <summary>
    /// GUI 入口：**启动即请求管理员权限**（需求：每次启动应用时自动请求并获取）。
    ///
    /// 实现路径说明（为什么不用 requireAdministrator 清单）：
    /// 单 exe 同时承载 GUI 与 CLI。写死 requireAdministrator 后，连只读的
    /// <c>scan</c> / <c>inspect</c> 和脚本管道调用都会弹 UAC 甚至挂起，
    /// 且同一 exe 内无法切回普通权限。因此清单保持 asInvoker，
    /// 由这里在 GUI 启动前主动以 runas 重启自身：
    ///  · UAC 同意  → 提权实例接管，本进程直接退出（用户视角就是"启动时提权"）；
    ///  · UAC 取消  → 降级继续跑 GUI，权限横幅常驻说明，不变成"程序起不来"；
    ///  · 启动失败  → 同上，底部提示具体原因。
    /// CLI 模式**不**自动提权（只读命令必须能在脚本里安静运行），保留 <c>--elevate</c> 手动开关。
    /// </summary>
    private static int RunGui()
    {
        var elevation = ElevationService.Current;

        if (!elevation.IsElevated)
        {
            var launch = elevation.LaunchElevated(string.Empty);

            if (launch.Started)
            {
                // 提权实例已经跑起来（同一个 exe、无参 = GUI 模式），本进程让位。
                return 0;
            }

            // 取消/失败都不阻断启动：记下通知，降级运行。
            ElevationNotice = launch.Canceled
                ? ("priv.elevationCancelled", null)
                : ("priv.elevationFailed", launch.Error ?? "未知原因");
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
}
