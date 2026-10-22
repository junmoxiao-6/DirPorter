using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Config;

/// <summary>警告名单判定结果（不是拒绝，而是"必须勾选我理解风险 + 二次确认"）。</summary>
public sealed record WarningVerdict(bool RequiresAcknowledge, IReadOnlyList<WarningHit> Hits)
{
    public static readonly WarningVerdict None = new(false, Array.Empty<WarningHit>());
}

/// <summary>命中的一条警告。</summary>
public sealed record WarningHit(string RuleId, string Reason, string? Evidence);

/// <summary>
/// 警告名单（PRECHECK 源侧第 4 条）。命中不阻止操作，但必须显式确认。
/// </summary>
public interface IWarningListPolicy
{
    WarningVerdict Evaluate(string plainAbsolutePath);
}

/// <summary>
/// 内置警告名单：Program Files、ProgramData 非 Microsoft 子目录、杀毒/驱动/Office 相关目录、
/// 以及（若启用）被服务或开机启动项引用的目录。
/// </summary>
public sealed class WarningListPolicy : IWarningListPolicy
{
    private readonly List<(WarningRule Rule, PathPattern Pattern)> _patterns;
    private readonly List<(WarningRule Rule, PathPattern Pattern)> _exceptions;
    private readonly IReadOnlyList<string> _nameKeywords;
    private readonly StartupReferenceProbe? _startupProbe;

    public WarningListPolicy(StartupReferenceProbe? startupProbe = null)
    {
        _startupProbe = startupProbe;

        _patterns = DefaultRules.Select(r => (r, PathPattern.Parse(r.Pattern))).ToList();
        _exceptions = DefaultExceptions.Select(r => (r, PathPattern.Parse(r.Pattern))).ToList();
        _nameKeywords = DefaultNameKeywords;
    }

    /// <summary>需求「PRECHECK 源侧第 4 条」的默认规则集。</summary>
    public static IReadOnlyList<WarningRule> DefaultRules { get; } = new[]
    {
        new WarningRule("WN-PROGRAMFILES", @"%ProgramFiles%\**", "位于 Program Files：程序可能依赖固定安装路径，迁移后部分程序可能拒绝启动。"),
        new WarningRule("WN-PROGRAMFILES-X86", @"%ProgramFiles(x86)%\**", "位于 Program Files (x86)：同上，且 32 位程序对路径变化更敏感。"),
        new WarningRule("WN-PROGRAMDATA", @"%ProgramData%\**", "位于 ProgramData 下的非 Microsoft 子目录：可能有服务或共享组件引用该路径。"),
        new WarningRule("WN-SERVICE-REFERENCED", "(动态)", "该目录被系统服务或开机启动项引用，迁移后服务可能无法启动。"),
    };

    /// <summary>警告规则的例外（子模式命中时不再警告）。</summary>
    public static IReadOnlyList<WarningRule> DefaultExceptions { get; } = new[]
    {
        new WarningRule("WN-EX-PROGRAMDATA-MS", @"%ProgramData%\Microsoft\**", "ProgramData 下的 Microsoft 托管目录不属于「非 Microsoft 子目录」。"),
    };

    /// <summary>按目录名关键词判定高风险软件目录（杀毒 / 驱动 / Office 等）。</summary>
    public static IReadOnlyList<string> DefaultNameKeywords { get; } = new[]
    {
        "antivirus", "antimalware", "security", "defender", "kaspersky", "norton", "avast", "avg",
        "eset", "bitdefender", "mcafee", "360", "huorong", "火绒", "防火墙", "firewall",
        "driver", "drivers", "nvidia", "realtek", "intel", "amd", "razer", "logitech",
        "office", "wps", "onedrive", "windowsapps", "windows defender",
    };

    public WarningVerdict Evaluate(string plainAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(plainAbsolutePath)) return WarningVerdict.None;

        var normalized = PathNormalizer.TrimTrailingSeparator(PathNormalizer.StripExtendedPrefix(plainAbsolutePath));
        var hits = new List<WarningHit>();

        var excepted = _exceptions.Any(e => e.Pattern.Matches(normalized));

        foreach (var (rule, pattern) in _patterns)
        {
            if (rule.Id == "WN-SERVICE-REFERENCED") continue;
            if (pattern.Matches(normalized))
            {
                if (excepted) continue;
                hits.Add(new WarningHit(rule.Id, rule.Reason, normalized));
            }
        }

        // 名称关键词：逐段匹配，命中的段作为证据
        if (!excepted)
        {
            var segments = PathPattern.SplitPath(normalized);
            foreach (var segment in segments)
            {
                var keyword = _nameKeywords.FirstOrDefault(k => segment.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (keyword is not null)
                {
                    hits.Add(new WarningHit(
                        "WN-NAME-KEYWORD",
                        $"目录名包含高风险关键词「{keyword}」：可能是杀毒/驱动/Office 等对路径敏感的软件。",
                        segment));
                    break;
                }
            }
        }

        // 服务 / 开机启动项引用（只读注册表；失败静默降级为不命中，不阻塞预检）
        if (_startupProbe is not null)
        {
            var references = _startupProbe.FindReferences(normalized);
            foreach (var reference in references)
            {
                hits.Add(new WarningHit(
                    "WN-SERVICE-REFERENCED",
                    $"该目录被服务或开机启动项引用：{reference.Name}",
                    reference.Value));
            }
        }

        return hits.Count == 0 ? WarningVerdict.None : new WarningVerdict(true, hits);
    }
}

/// <summary>一条警告规则。</summary>
public sealed record WarningRule(string Id, string Pattern, string Reason);
