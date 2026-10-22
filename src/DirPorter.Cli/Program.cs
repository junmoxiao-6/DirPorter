using DirPorter.Core.Cli;

namespace DirPorter.Cli;

/// <summary>
/// R3 版命令行入口（控制台子系统）。
///
/// R7 会把它换成 WPF 单 exe（WinExe + AttachConsole(ATTACH_PARENT_PROCESS)）：
/// 那时本文件退化为 App 的 Program.Main，而 <see cref="CliHost"/> 逻辑原地复用、零改动。
/// 单 exe 下需要的 AttachConsole 处理会在 R7 随 WPF 宿主一起落地（此处刻意不提前引入）。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var exitCode = CliHost.Run(args, Console.Out, Console.Error);

        // 保证管道/重定向场景下输出被完整刷新
        Console.Out.Flush();
        Console.Error.Flush();
        return exitCode;
    }
}
