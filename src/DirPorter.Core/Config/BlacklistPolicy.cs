using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Config;

/// <summary>黑名单判定结果。</summary>
public sealed record BlacklistVerdict(bool IsBlocked, string? RuleId, string? Reason)
{
    public static readonly BlacklistVerdict NotBlocked = new(false, null, null);
}

/// <summary>
/// 系统关键目录黑名单（PRECHECK 源侧第 3 条，命中即**硬拒绝**）。
/// 规则数据驱动，便于审计与后续扩表。
/// </summary>
public interface IBlacklistPolicy
{
    /// <summary>按"无 <c>\\?\</c> 前缀"的绝对路径判定。</summary>
    BlacklistVerdict Evaluate(string plainAbsolutePath);

    IReadOnlyList<BlacklistRule> Rules { get; }
}

/// <summary>一条黑名单规则。</summary>
public sealed record BlacklistRule(string Id, string Pattern, string Reason);

/// <summary>
/// 内置黑名单。规则与需求「PRECHECK 源侧第 3 条」一一对应，逐条都有正反单元测试。
/// </summary>
public sealed class BlacklistPolicy : IBlacklistPolicy
{
    private readonly List<(BlacklistRule Rule, PathPattern Pattern)> _rules;

    public BlacklistPolicy(IEnumerable<BlacklistRule>? additionalRules = null)
    {
        _rules = DefaultRules
            .Concat(additionalRules ?? Enumerable.Empty<BlacklistRule>())
            .Select(rule => (rule, PathPattern.Parse(rule.Pattern)))
            .Where(entry => !entry.Item2.IsEmpty)
            .ToList();
    }

    public IReadOnlyList<BlacklistRule> Rules => _rules.Select(static r => r.Rule).ToList();

    /// <summary>需求「PRECHECK 源侧第 3 条」的默认规则集。</summary>
    public static IReadOnlyList<BlacklistRule> DefaultRules { get; } = new[]
    {
        new BlacklistRule("BL-VOLUME-ROOT", "{VOLUME_ROOT}", "任意卷的根目录。迁移卷根会破坏整个卷的结构。"),
        new BlacklistRule("BL-WINDOWS", @"%SystemRoot%\**", "Windows 系统目录及其子目录。"),
        new BlacklistRule("BL-SVI", @"%SystemDrive%\System Volume Information\**", "系统卷信息目录（卷影副本、索引数据库）。"),
        new BlacklistRule("BL-RECYCLE", @"%SystemDrive%\$Recycle.Bin\**", "回收站目录。"),
        new BlacklistRule("BL-USERS", @"%SystemDrive%\Users", "用户目录总根。"),
        // ⚠️ 顺序要求：更具体的规则必须排在前面。
        // BL-PROFILE（C:\Users\*，恰好一个段）同样会匹配 C:\Users\Public，
        // 因此 BL-PUBLIC 必须排在它之前，否则错误码会指向错误的规则。
        new BlacklistRule("BL-PUBLIC", @"%SystemDrive%\Users\Public\**", "公用用户目录。"),
        new BlacklistRule("BL-PROFILE", @"%SystemDrive%\Users\*", "用户 Profile 根目录本身（可迁移其下的具体子目录，如 Documents）。"),
        new BlacklistRule("BL-PROGRAMDATA", @"%ProgramData%", "ProgramData 根目录本身。"),
        new BlacklistRule("BL-WINSXS", @"**\WinSxS\**", "组件存储（WinSxS）目录，位于任意卷。"),
        new BlacklistRule("BL-APPDATA-MS-LOCAL", @"%SystemDrive%\Users\*\AppData\Local\Microsoft\**", "AppData\\Local\\Microsoft 下的系统托管目录。"),
        new BlacklistRule("BL-APPDATA-PACKAGES", @"%SystemDrive%\Users\*\AppData\Local\Packages\**", "Windows 应用包目录（Packages）。"),
        new BlacklistRule("BL-APPDATA-MS-ROAMING", @"%SystemDrive%\Users\*\AppData\Roaming\Microsoft\**", "AppData\\Roaming\\Microsoft 下的系统托管目录。"),
    };

    public BlacklistVerdict Evaluate(string plainAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(plainAbsolutePath)) return BlacklistVerdict.NotBlocked;

        var normalized = PathNormalizer.TrimTrailingSeparator(PathNormalizer.StripExtendedPrefix(plainAbsolutePath));

        foreach (var (rule, pattern) in _rules)
        {
            if (pattern.Matches(normalized))
                return new BlacklistVerdict(true, rule.Id, rule.Reason);
        }

        return BlacklistVerdict.NotBlocked;
    }
}
