using DirPorter.Core.RestartManager;

namespace DirPorter.Core.Migration;

/// <summary>
/// 源目录被其他进程占用 —— 在**迁移开始前**就失败，而不是等到复制/改名阶段甩一个裸 Win32 码。
///
/// 背景（0.1.5 实测）：迁移 <c>Roaming\Coze</c> / <c>Roaming\@deepseek-ai</c> 时，
/// 客户端仍在运行 → COPYING 报"另一个程序正在使用(32)"或 PARKING 改名报"拒绝访问(5)"，
/// 走完回滚后用户只看到错误码，得不到"关掉程序再试"的行动指引。
/// 现在改为：占用扫描检出即拦截，消息列出进程并给两条可执行出路。
/// </summary>
public sealed class SourceOccupiedException : IOException
{
    public SourceOccupiedException(string sourcePath, OccupancyReport occupancy, string? prefix = null)
        : base(BuildMessage(sourcePath, occupancy, prefix))
    {
        SourcePath = sourcePath;
        Occupancy = occupancy;
    }

    public string SourcePath { get; }

    public OccupancyReport Occupancy { get; }

    private static string BuildMessage(string sourcePath, OccupancyReport occupancy, string? prefix)
    {
        var head = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix + "：";
        var processes = occupancy.Processes.Count == 0
            ? "（占用方未能识别）"
            : string.Join("、", occupancy.Processes.Select(static p => p.Describe()));

        return head
               + "源目录正被其他程序使用，无法安全迁移。"
               + $"\n占用程序：{processes}"
               + "\n请关闭这些程序后重试；或勾选「结束占用进程（慎用）」由本工具结束它们。"
               + $"\n源目录：{sourcePath}";
    }
}
