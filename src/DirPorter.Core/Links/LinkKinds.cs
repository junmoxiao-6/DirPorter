namespace DirPorter.Core.Links;

/// <summary>
/// 链接类型的解析与序列化（单一事实来源）。
///
/// 为什么抽出来：CLI 的 <c>--type</c>、UI 的默认建链类型（UiSettings）、
/// 历史记录的 LinkType 回读都要把「字符串 ↔ <see cref="LinkKind"/>」互转。
/// 三处各写一份 switch 必然漂移（CLI 接受 <c>dirsymlink</c> 而 UI 设置只认
/// <c>symlink-dir</c> 这类事，编译器一声不响），因此收敛到此处。
/// </summary>
public static class LinkKinds
{
    /// <summary>迁移场景支持的链接类型（目录级）。硬链接与文件符号链接不支持目录，不得出现在迁移候选里。</summary>
    public static IReadOnlyList<LinkKind> MigrationKinds { get; } = new[] { LinkKind.Junction, LinkKind.DirectorySymlink };

    /// <summary>持久化与命令行使用的规范名（与 <c>mklink</c> 参数对应，便于人工核对）。</summary>
    public static string ToStorageString(LinkKind kind) => kind switch
    {
        LinkKind.Junction => "junction",
        LinkKind.DirectorySymlink => "symlink-dir",
        LinkKind.FileSymlink => "symlink-file",
        LinkKind.HardLink => "hardlink",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 解析链接类型。空值/auto → null（表示自动推荐）。
    /// 别名与 CLI 历史行为保持一致：<c>j</c>/<c>junction</c>、<c>d</c>/<c>symlink-dir</c>/<c>dirsymlink</c> …
    /// </summary>
    public static bool TryParse(string? value, out LinkKind? kind)
    {
        kind = null;

        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "" or "auto": return true;
            case "junction" or "j": kind = LinkKind.Junction; return true;
            case "symlink-dir" or "dir-symlink" or "dirsymlink" or "d": kind = LinkKind.DirectorySymlink; return true;
            case "symlink-file" or "file-symlink" or "filesymlink": kind = LinkKind.FileSymlink; return true;
            case "hardlink" or "hard-link" or "h": kind = LinkKind.HardLink; return true;
            default: return false;
        }
    }

    /// <summary>解析，失败时回退到 <paramref name="fallback"/>（设置文件被手改坏也不至于起不来）。</summary>
    public static LinkKind ParseOr(string? value, LinkKind fallback)
        => TryParse(value, out var kind) ? kind ?? fallback : fallback;
}
