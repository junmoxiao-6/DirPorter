using System.IO;

namespace DirPorter.Core.FileSystem;

/// <summary>一个 <c>.migbak</c> 停车区（迁移时源目录的备份区，位于源目录的父目录下）。</summary>
public sealed record ParkingInfo(string Path, int EntryCount, long TotalBytes, DateTime LastWriteUtc)
{
    public string SizeText
    {
        get
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = TotalBytes;
            var unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }

    public string Summary => $"{EntryCount} 个目录 · {SizeText}";
}

/// <summary>
/// <c>.migbak</c> 停车区的清点与清理。
/// 语义：迁移某目录时，源目录被改名到"其父目录下的 .migbak"里（可回滚兜底），
/// 默认按保留天数自动清理；<c>.ledger</c> 子目录是台账归档，长期保留。
/// </summary>
public static class ParkingInventory
{
    /// <summary>常见停车区根（用户实际出现过：用户主目录、Local、LocalLow、Roaming 各一个）。</summary>
    public static IReadOnlyList<string> DefaultRoots(string userProfile, string localAppData, string roamingAppData)
    {
        var roots = new List<string>
        {
            Path.Combine(userProfile, ".migbak"),
            Path.Combine(localAppData, ".migbak"),
            Path.Combine(roamingAppData, ".migbak"),
            Path.Combine(userProfile, "AppData", "LocalLow", ".migbak"),
        };

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>清点给定路径中真实存在的停车区（不递归搜索磁盘，避免误扫/耗时）。</summary>
    public static IReadOnlyList<ParkingInfo> Scan(IEnumerable<string> candidatePaths)
    {
        var result = new List<ParkingInfo>();

        foreach (var raw in candidatePaths.Where(static p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(raw)) continue;

                var dir = new DirectoryInfo(raw);
                if (!string.Equals(dir.Name, ".migbak", StringComparison.OrdinalIgnoreCase)) continue;

                long bytes = 0;
                var entries = 0;

                foreach (var child in dir.EnumerateDirectories())
                {
                    if (string.Equals(child.Name, ".ledger", StringComparison.OrdinalIgnoreCase)) continue;

                    entries++;
                    bytes += MeasureBytes(child);
                }

                result.Add(new ParkingInfo(dir.FullName, entries, bytes, dir.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个停车区不可读：跳过（不影响其它项展示）
            }
        }

        return result.OrderByDescending(static p => p.TotalBytes).ToList();
    }

    /// <summary>
    /// 删除停车区内容（保留 <c>.ledger</c> 台账归档）。
    /// 返回 (删除的目录数, 失败数)；失败不抛，交由 UI 汇总提示。
    /// </summary>
    public static (int Deleted, int Failed) Delete(string parkingPath)
    {
        var deleted = 0;
        var failed = 0;

        if (!Directory.Exists(parkingPath)) return (0, 0);

        foreach (var child in Directory.EnumerateDirectories(parkingPath))
        {
            if (string.Equals(Path.GetFileName(child), ".ledger", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                Directory.Delete(child, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return (deleted, failed);
    }

    private static long MeasureBytes(DirectoryInfo dir)
    {
        long total = 0;

        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += file.Length; } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }
}
