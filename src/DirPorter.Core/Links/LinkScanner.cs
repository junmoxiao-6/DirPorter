using System.IO;

namespace DirPorter.Core.Links;

/// <summary>扫描到的一个链接（含类型与来源/目标）。</summary>
public sealed record ScannedLink(string LinkPath, LinkKind Kind, string? TargetPath, bool IsDangling)
{
    public string KindText => Kind switch
    {
        LinkKind.Junction => "目录联接 (Junction)",
        LinkKind.DirectorySymlink => "目录符号链接",
        LinkKind.FileSymlink => "文件符号链接",
        _ => "硬链接",
    };

    public string HealthText => IsDangling ? "悬空（目标丢失）" : "有效";
}

/// <summary>
/// 扫描指定根目录下的链接（junction / 目录符号链接 / 文件符号链接 / 可选硬链接）。
/// 语义：遇到重解析点**不递归**（避免进入链接目标导致重复/环）。
/// </summary>
public static class LinkScanner
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "WinSxS", "$Recycle.Bin", "System Volume Information", "node_modules", ".git", ".migbak",
    };

    public sealed record Options(bool IncludeHardLinks = false, int MaxEntries = 40_000);

    public static IReadOnlyList<ScannedLink> Scan(IEnumerable<string> roots, LinkService links, Options? options = null)
    {
        options ??= new Options();
        var results = new List<ScannedLink>();
        var visited = 0;

        foreach (var root in roots.Where(static r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;

            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0 && visited < options.MaxEntries)
            {
                var current = stack.Pop();

                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                foreach (var entry in entries)
                {
                    visited++;
                    if (visited >= options.MaxEntries) break;

                    var name = Path.GetFileName(entry);
                    if (SkipNames.Contains(name)) continue;

                    try
                    {
                        var info = links.Inspect(entry);

                        if (info.IsReparsePoint && info.Kind is LinkKind.Junction or LinkKind.DirectorySymlink or LinkKind.FileSymlink)
                        {
                            results.Add(new ScannedLink(info.Path, info.Kind!.Value, info.TargetAbsolute, info.IsDangling));
                            continue;   // 不递归进链接
                        }

                        if (options.IncludeHardLinks && info.Kind == LinkKind.HardLink && info.NumberOfLinks > 1)
                        {
                            results.Add(new ScannedLink(info.Path, LinkKind.HardLink, info.TargetAbsolute, false));
                            continue;
                        }

                        if (info.IsDirectory && !info.IsReparsePoint) stack.Push(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        return results.OrderBy(static l => l.LinkPath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
