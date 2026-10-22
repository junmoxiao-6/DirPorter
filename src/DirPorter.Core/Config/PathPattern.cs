using System.Text;

namespace DirPorter.Core.Config;

/// <summary>
/// 路径模式匹配器，用于黑名单 / 警告名单的数据驱动规则。
///
/// 语法：
/// <list type="bullet">
/// <item><c>{VOLUME_ROOT}</c>：匹配任意卷根（<c>C:\</c>），且要求"只有卷根"。</item>
/// <item><c>%VAR%</c>：环境变量展开（<c>%SystemRoot%</c> / <c>%ProgramFiles(x86)%</c> 等）。</item>
/// <item><c>*</c>：匹配**恰好一个**路径段，如 <c>C:\Users\*</c> 只匹配各用户的 Profile 根目录本身，不匹配其子目录。</item>
/// <item>结尾 <c>\**</c>：匹配**零个或多个**后续段（含自身），即"自身 + 全部子树"。</item>
/// <item>开头 <c>**\</c>：任意深度的前导段，如 <c>**\WinSxS\**</c> 可匹配任意位置下的 WinSxS。</item>
/// </list>
/// ⚠️ <c>*</c> 与 <c>**</c> 是两种不同语义，**不可混用**：
/// 早期版本把"结尾 *"同时当作单段通配与子树，导致 <c>C:\Users\*</c> 把整个用户目录树全部拦下（含临时目录）。
/// 该 bug 由 R2 单元测试捕获，此处已改为显式双星号。
/// 匹配大小写不敏感（NTFS 语义），不做重解析点跟随。
/// </summary>
public sealed class PathPattern
{
    private const string VolumeRootToken = "{VOLUME_ROOT}";

    private readonly string[] _segments;
    private readonly bool _volumeRootOnly;
    private readonly bool _leadingAnyDepth;
    private readonly bool _trailingSubtree;

    private PathPattern(string raw, string[] segments, bool volumeRootOnly, bool leadingAnyDepth, bool trailingSubtree)
    {
        Raw = raw;
        _segments = segments;
        _volumeRootOnly = volumeRootOnly;
        _leadingAnyDepth = leadingAnyDepth;
        _trailingSubtree = trailingSubtree;
    }

    /// <summary>原始模式文本（用于日志与规则展示）。</summary>
    public string Raw { get; }

    /// <summary>解析结果是否为空（例如环境变量缺失导致的空模式）。</summary>
    public bool IsEmpty => !_volumeRootOnly && _segments.Length == 0;

    public static PathPattern Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        var text = raw.Trim();

        if (text.Equals(VolumeRootToken, StringComparison.OrdinalIgnoreCase))
            return new PathPattern(raw, Array.Empty<string>(), volumeRootOnly: true, leadingAnyDepth: false, trailingSubtree: false);

        text = ExpandEnvironment(text);
        text = text.TrimEnd('\\');

        var parts = text.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // ** 是子树/任意深度标记；* 只代表"恰好一个段"。
        var leading = parts.Length > 0 && parts[0] == "**";
        var trailing = parts.Length > 1 && parts[^1] == "**";

        var core = parts.AsSpan();
        if (leading) core = core[1..];
        if (trailing && core.Length > 0) core = core[..^1];

        return new PathPattern(raw, core.ToArray(), volumeRootOnly: false, leadingAnyDepth: leading, trailingSubtree: trailing);
    }

    /// <summary>
    /// 匹配一个"无 <c>\\?\</c> 前缀"的绝对路径。
    /// </summary>
    public bool Matches(string plainAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(plainAbsolutePath)) return false;

        var input = SplitPath(plainAbsolutePath);

        if (_volumeRootOnly) return input.Length == 1;

        // 开头 * 时允许在任意深度开始匹配；否则必须从卷根开始。
        var lastStart = _leadingAnyDepth ? input.Length - 1 : 0;

        for (var start = 0; start <= lastStart; start++)
        {
            if (start < 0 || start > input.Length) continue;

            var available = input.Length - start;
            if (available < _segments.Length) continue;
            if (!_trailingSubtree && available != _segments.Length) continue;

            var matched = true;
            for (var i = 0; i < _segments.Length; i++)
            {
                if (!SegmentMatches(_segments[i], input[start + i]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched) return true;
        }

        return false;
    }

    private static bool SegmentMatches(string patternSegment, string inputSegment)
        => patternSegment == "*" || string.Equals(patternSegment, inputSegment, StringComparison.OrdinalIgnoreCase);

    /// <summary>把普通绝对路径拆成段。<c>C:\Users\A</c> → [C:, Users, A]；<c>C:\</c> → [C:]；UNC → [\\srv, share, ...]。</summary>
    public static string[] SplitPath(string plainAbsolutePath)
    {
        var text = plainAbsolutePath.Replace('/', '\\');

        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var uncParts = text.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (uncParts.Length == 0) return Array.Empty<string>();
            // UNC 根合并为一段：\\server\share
            if (uncParts.Length == 1) return new[] { @"\\" + uncParts[0] };
            var result = new string[uncParts.Length - 1];
            result[0] = @"\\" + uncParts[0] + "\\" + uncParts[1];
            Array.Copy(uncParts, 2, result, 1, uncParts.Length - 2);
            return result;
        }

        return text.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ExpandEnvironment(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);
        var index = 0;

        while (index < pattern.Length)
        {
            var start = pattern.IndexOf('%', index);
            if (start < 0)
            {
                builder.Append(pattern, index, pattern.Length - index);
                break;
            }

            var end = pattern.IndexOf('%', start + 1);
            if (end < 0)
            {
                builder.Append(pattern, index, pattern.Length - index);
                break;
            }

            builder.Append(pattern, index, start - index);

            var name = pattern[(start + 1)..end];
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                // 环境变量缺失时保留原样，让规则自然失配（而不是误匹配成空串）。
                builder.Append(pattern, start, end - start + 1);
            }
            else
            {
                builder.Append(value);
            }

            index = end + 1;
        }

        return builder.ToString();
    }
}
