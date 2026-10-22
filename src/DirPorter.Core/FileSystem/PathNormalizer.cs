using System.Text;

namespace DirPorter.Core.FileSystem;

/// <summary>
/// 路径规范化。需求硬约束：全程使用 <c>\\?\</c> 扩展长度路径前缀。
///
/// 设计要点：
/// - 已经是扩展路径的输入原样保留（幂等），不做 GetFullPath，避免 &gt;260 字符时受非 longPathAware 进程限制。
/// - UNC 路径转换为 <c>\\?\UNC\server\share\...</c>。
/// - 相对路径经 <c>Path.GetFullPath</c> 解析（CLI 场景必需），解析结果再前缀化。
/// - 去尾分隔符（卷根除外），保证字符串比较可靠。
/// </summary>
public static class PathNormalizer
{
    public const string ExtendedPrefix = @"\\?\";
    public const string ExtendedUncPrefix = @"\\?\UNC\";
    public const string DevicePrefix = @"\\.\";

    /// <summary>是否已带扩展长度前缀。</summary>
    public static bool IsExtendedPath(string path)
        => path.StartsWith(ExtendedPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为 UNC 路径（含已加前缀的形式）。</summary>
    public static bool IsUnc(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(@"\\", StringComparison.Ordinal) && !IsExtendedPath(path);
    }

    /// <summary>
    /// 规范化为带 <c>\\?\</c> 前缀的绝对路径。
    /// 传入 null / 空白 / 非法字符会抛 <see cref="ArgumentException"/>。
    /// </summary>
    /// <summary>
    /// 清洗"粘贴进来的路径"。
    /// Windows 资源管理器「复制为路径」会产出带双引号的 <c>"C://path"</c>；
    /// 用户从命令行/文档里复制也常带引号或首尾空白。这些引号对路径本身无意义，
    /// 但会在校验时被判为非法字符（实测：链接页粘贴后直接崩到全局异常）。
    /// </summary>
    public static string CleanInput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var text = path.Trim();

        // 成对或落单的首尾引号都去掉（用户常只删掉一个）。
        while (text.Length > 0 && (text[0] == '"' || text[0] == '\''))
        {
            text = text[1..].TrimStart();
        }

        while (text.Length > 0 && (text[^1] == '"' || text[^1] == '\''))
        {
            text = text[..^1].TrimEnd();
        }

        return text;
    }

    public static string NormalizeAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空。", nameof(path));
        if (path.IndexOfAny(InvalidChars) >= 0) throw new ArgumentException($"路径包含非法字符：{path}", nameof(path));

        string result;
        if (path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            // \\.\ 设备路径：无路径语义（如 \\.\C:），原样返回，调用方自行负责。
            return path;
        }

        if (IsExtendedPath(path))
        {
            result = path;
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share\...  →  \\?\UNC\server\share\...
            result = ExtendedUncPrefix + path[2..];
        }
        else
        {
            // 已经是完全限定的盘符路径（C:\...）时不做 GetFullPath：
            // 避免 >260 字符路径在经过非 longPathAware 的 API 时被拒绝（TODO(verify)-7 的实测点）。
            if (!IsDriveQualified(path))
            {
                path = Path.GetFullPath(path);
            }

            result = ExtendedPrefix + path.Replace('/', '\\');
        }

        return TrimTrailingSeparator(result);
    }

    /// <summary>去掉扩展长度前缀，返回普通 Win32 路径（供 robocopy / 日志 / UI 展示使用）。</summary>
    public static string StripExtendedPrefix(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[ExtendedUncPrefix.Length..];
        if (path.StartsWith(ExtendedPrefix, StringComparison.OrdinalIgnoreCase))
            return path[ExtendedPrefix.Length..];
        return path;
    }

    /// <summary>去掉尾部分隔符；卷根（<c>C:\</c>）保留一个分隔符。</summary>
    public static string TrimTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return path;

        // \\?\C: 形式（去掉了根的斜杠）需要补回
        if (trimmed.EndsWith(":", StringComparison.Ordinal)) return trimmed + "\\";

        // \\?\UNC\server\share 是合法根，不需要补
        return trimmed;
    }

    /// <summary>确保以单个分隔符结尾（用于拼接子项）。</summary>
    public static string EnsureTrailingSeparator(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return directory;
        return directory.EndsWith("\\", StringComparison.Ordinal) ? directory : directory + "\\";
    }

    /// <summary>拼接子路径（不做存在性检查）。</summary>
    public static string Combine(string directory, string child)
        => EnsureTrailingSeparator(directory) + child.TrimStart('\\', '/');

    /// <summary>
    /// <paramref name="candidate"/> 是否等于 <paramref name="ancestor"/> 或位于其下。
    /// 两侧自动规范化；大小写不敏感（NTFS 语义）。
    /// </summary>
    public static bool IsSameOrUnder(string candidate, string ancestor)
    {
        var c = NormalizeForCompare(candidate);
        var a = NormalizeForCompare(ancestor);

        if (string.Equals(c, a, StringComparison.OrdinalIgnoreCase)) return true;

        var aWithSep = a.EndsWith("\\", StringComparison.Ordinal) ? a : a + "\\";
        return c.StartsWith(aWithSep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取卷根（如 <c>C:\</c>）；UNC 返回共享根。取不到返回 null。</summary>
    public static string? TryGetVolumeRoot(string path)
    {
        try
        {
            var plain = TrimTrailingSeparator(StripExtendedPrefix(NormalizeAbsolute(path)));
            if (plain.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // \\server\share\a\b → \\server\share
                var parts = plain.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return null;
                return $@"\\{parts[0]}\{parts[1]}";
            }

            if (plain.Length >= 2 && plain[1] == ':')
            {
                return plain.Length == 2 ? plain + "\\" : plain[..2] + "\\";
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>返回用于比较的规范化字符串（扩展前缀 + 无尾分隔符 + 统一分隔符）。</summary>
    public static string NormalizeForCompare(string path)
    {
        var normalized = NormalizeAbsolute(path).Replace('/', '\\');
        return TrimTrailingSeparator(normalized);
    }

    /// <summary>去除路径首尾空白并统一分隔符为反斜杠（仅做文本层面处理，不做前缀化）。</summary>
    public static string CleanupSeparators(string path) => path.Trim().Replace('/', '\\');

    /// <summary>目录名是否合法（不校验保留名，保留名为上层策略）。</summary>
    public static bool HasValidDirectoryName(string path)
    {
        var trimmed = TrimTrailingSeparator(path.TrimEnd('\\', '/'));
        var name = Path.GetFileName(trimmed);
        return !string.IsNullOrEmpty(name) && name.IndexOfAny(InvalidChars) < 0;
    }

    /// <summary>是否为完全限定的盘符路径（<c>C:\...</c>）。</summary>
    public static bool IsDriveQualified(string path)
    {
        if (path.Length < 3) return false;
        var isLetter = path[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
        return isLetter && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    }

    private static readonly char[] InvalidChars = { '"', '<', '>', '|' };
}
