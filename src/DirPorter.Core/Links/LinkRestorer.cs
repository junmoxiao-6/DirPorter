using System.IO;

namespace DirPorter.Core.Links;

/// <summary>
/// 「还原链接」：把数据从链接目标搬回链接所在位置，并移除链接本体。
/// 仅支持目录类链接（Junction / 目录符号链接）；文件符号链接与硬链接不在此列。
/// </summary>
public static class LinkRestorer
{
    public sealed record Result(bool Success, string Message);

    public static Result Restore(string linkPath, LinkService links, IProgress<string>? progress = null)
    {
        var info = links.Inspect(linkPath);

        if (!info.IsKnownLink || info.Kind is not (LinkKind.Junction or LinkKind.DirectorySymlink))
        {
            return new Result(false, "该条目不是可还原的目录链接（仅支持目录联接 / 目录符号链接）。");
        }

        if (info.IsDangling || string.IsNullOrWhiteSpace(info.TargetAbsolute))
        {
            return new Result(false, "链接目标不存在（悬空链接），无法还原数据。");
        }

        var target = info.TargetAbsolute;
        if (!Directory.Exists(target))
        {
            return new Result(false, $"目标目录不存在：{target}");
        }

        var parent = Path.GetDirectoryName(linkPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return new Result(false, "无法确定链接所在目录。");
        }

        var temp = Path.Combine(parent, $".restore-{Guid.NewGuid():N}");

        try
        {
            progress?.Report("正在复制数据…");
            CopyDirectory(target, temp);

            progress?.Report("正在移除链接…");
            links.Delete(linkPath);

            progress?.Report("正在回填…");
            Directory.Move(temp, linkPath);

            return new Result(true, $"已还原：数据已回到 {linkPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力清理半成品，不掩盖原始错误
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
            return new Result(false, $"还原失败：{ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
