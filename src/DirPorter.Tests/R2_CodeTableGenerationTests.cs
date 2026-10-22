using System.Text;
using DirPorter.Core.Precheck;
using Xunit;
using Xunit.Abstractions;

namespace DirPorter.Tests;

/// <summary>
/// 文档即代码：从 <see cref="PrecheckCodes.All"/> 生成"拒绝码 ↔ 用户处理建议"对照表。
///
/// 为什么放在测试里：这张表是 GUI 引导按钮、CLI 退出码与用户文档的共同契约。
/// 手写文档一定会与实现漂移，因此让文档由代码生成、由测试保证存在。
/// 运行本测试会更新 <c>docs/precheck-codes.md</c>。
/// </summary>
public class R2_CodeTableGenerationTests
{
    private readonly ITestOutputHelper _output;

    public R2_CodeTableGenerationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void 生成预检拒绝码对照表()
    {
        var markdown = BuildMarkdown();
        var target = ResolveDocPath("precheck-codes.md");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        _output.WriteLine($"已生成：{target}");
        _output.WriteLine(markdown);

        Assert.True(File.Exists(target));
        Assert.Contains("SRC_BLACKLISTED", markdown);
        Assert.Contains("TGT_SPACE_INSUFFICIENT", markdown);
    }

    /// <summary>每个拒绝码都必须带建议动作与用户处理建议，否则生成的文档就是残缺的。</summary>
    [Fact]
    public void 拒绝码表的每条都完整()
    {
        Assert.All(PrecheckCodes.All, code =>
        {
            Assert.False(string.IsNullOrWhiteSpace(code.Code));
            Assert.False(string.IsNullOrWhiteSpace(code.Title));
            Assert.False(string.IsNullOrWhiteSpace(code.UserGuide), $"{code.Code} 缺少用户处理建议");
            Assert.Equal(code.Code, code.Code.Trim().ToUpperInvariant());
        });
    }

    private static string BuildMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DirPorter 预检拒绝码对照表");
        builder.AppendLine();
        builder.AppendLine("> ⚠️ 本文件由代码自动生成，请勿手工编辑。");
        builder.AppendLine("> 来源：`DirPorter.Core.Precheck.PrecheckCodes.All`（生成测试：`R2_CodeTableGenerationTests`）。");
        builder.AppendLine($"> 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();

        builder.AppendLine("## 严重级别语义");
        builder.AppendLine();
        builder.AppendLine("| 级别 | 含义 | 程序行为 |");
        builder.AppendLine("|---|---|---|");
        builder.AppendLine("| Info | 提示信息 | 不打断，仅展示 |");
        builder.AppendLine("| Warning | 警告 | 展示并计入风险摘要，可直接继续 |");
        builder.AppendLine("| RequireAcknowledge | 需显式确认 | 必须勾选「我理解风险」+ 二次确认（`--yes` 可代答） |");
        builder.AppendLine("| Blocking | 硬性阻止 | 拒绝执行，且保证磁盘零写入（`--force` 也不能绕过） |");
        builder.AppendLine();

        builder.AppendLine("## 建议动作枚举（GUI 引导按钮 / CLI 提示的契约）");
        builder.AppendLine();
        builder.AppendLine("| 动作 | 用户应做的事 |");
        builder.AppendLine("|---|---|");
        foreach (var action in Enum.GetValues<SuggestedAction>())
        {
            builder.AppendLine($"| `{action}` | {DescribeAction(action)} |");
        }
        builder.AppendLine();

        builder.AppendLine("## 拒绝码对照表");
        builder.AppendLine();
        builder.AppendLine("| 拒绝码 | 级别 | 标题 | 建议动作 | 用户应如何处理 |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var code in PrecheckCodes.All)
        {
            builder.AppendLine($"| `{code.Code}` | {code.Severity} | {code.Title} | `{code.Action}` | {code.UserGuide} |");
        }
        builder.AppendLine();

        builder.AppendLine("## 按严重级别分组");
        builder.AppendLine();
        foreach (var group in PrecheckCodes.All.GroupBy(c => c.Severity).OrderByDescending(g => g.Key))
        {
            builder.AppendLine($"### {group.Key}");
            builder.AppendLine();
            foreach (var code in group)
                builder.AppendLine($"- `{code.Code}` — {code.Title}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string DescribeAction(SuggestedAction action) => action switch
    {
        SuggestedAction.None => "无需动作",
        SuggestedAction.ChooseAnotherSource => "换一个源目录",
        SuggestedAction.ChooseAnotherTarget => "换一个目标位置或目标卷",
        SuggestedAction.FreeUpSpace => "释放目标卷空间或改迁到更大的卷",
        SuggestedAction.RunAsAdministrator => "以管理员身份重新运行（或开启开发者模式）",
        SuggestedAction.AcknowledgeRisk => "勾选「我理解风险」并二次确认",
        SuggestedAction.ReviewSpecialObjects => "在预览中逐项查看并确认特殊对象",
        SuggestedAction.ReuseOrRenameTarget => "复用空目录或给目标改名",
        SuggestedAction.RemoveNestedEntry => "无需动作（子条目已被自动跳过）",
        SuggestedAction.ResolveTargetConflict => "为冲突条目指定不同的目标目录",
        SuggestedAction.OpenExistingTarget => "打开既有目标位置或走还原入口",
        SuggestedAction.EnableLongPathSupport => "必要时开启系统长路径支持",
        _ => action.ToString(),
    };

    private static string ResolveDocPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DirPorter.sln")))
            directory = directory.Parent;

        var root = directory?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "docs", fileName);
    }
}
