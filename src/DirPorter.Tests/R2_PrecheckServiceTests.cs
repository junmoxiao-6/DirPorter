using DirPorter.Core.Config;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.Precheck;
using Xunit;

namespace DirPorter.Tests;

/// <summary>测试用：不触发任何警告的警告名单。</summary>
internal sealed class NoWarningPolicy : IWarningListPolicy
{
    public WarningVerdict Evaluate(string plainAbsolutePath) => WarningVerdict.None;
}

/// <summary>测试用：固定命中一条警告的警告名单。</summary>
internal sealed class StubWarningPolicy : IWarningListPolicy
{
    public WarningVerdict Evaluate(string plainAbsolutePath)
        => new(true, new[] { new WarningHit("WN-TEST", "测试用警告：该目录被模拟程序按固定路径引用。", plainAbsolutePath) });
}

/// <summary>测试用：返回固定占用大小的树探测（用于空间用例，不必真的造出 GB 级文件）。</summary>
internal sealed class StubTreeProbe : ITreeProbe
{
    private readonly long _diskBytes;
    private readonly IReadOnlyList<SpecialObjectSummary> _specials;

    public StubTreeProbe(long diskBytes, IReadOnlyList<SpecialObjectSummary>? specials = null)
    {
        _diskBytes = diskBytes;
        _specials = specials ?? Array.Empty<SpecialObjectSummary>();
    }

    public Task<TreeProbeResult> ProbeAsync(string sourcePath, TreeProbeOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        => Task.FromResult(new TreeProbeResult(
            PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(sourcePath)),
            FileCount: 10, DirectoryCount: 2,
            LogicalBytes: _diskBytes, DiskBytes: _diskBytes, MaxFileBytes: _diskBytes,
            SpecialObjects: _specials, Errors: Array.Empty<ProbeError>(),
            DeepProbeApplied: true, DeepProbeSkippedForScale: false, Elapsed: TimeSpan.FromMilliseconds(1)));
}

/// <summary>
/// PRECHECK 19 条逐条验证（需求「前置校验」小节）。
/// 每条用例都断言"拒绝码"本身，因为拒绝码是 GUI 引导按钮与用户文档的契约。
/// </summary>
public class R2_PrecheckServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static PrecheckService CreateService(
        IVolumeResolver? volumes = null,
        IBlacklistPolicy? blacklist = null,
        IWarningListPolicy? warnings = null,
        ITreeProbe? probe = null)
        => new(volumes ?? new Win32VolumeResolver(),
               blacklist ?? new BlacklistPolicy(),
               warnings ?? new NoWarningPolicy(),
               probe ?? new TreeProbe());

    private static async Task<PrecheckReport> RunAsync(PrecheckService service, string source, string target, PrecheckOptions? options = null)
        => await service.RunAsync(MigrationPlan.Single(source, target, LinkKind.Junction, options), null, CancellationToken.None);

    private static bool Has(PrecheckReport report, string code) => report.Results.Any(r => r.Code == code);

    private static CheckResult Get(PrecheckReport report, string code)
        => report.Results.Single(r => r.Code == code);

    // ==================== 源侧 ====================

    [Fact]
    public async Task 第1条_源不存在_SRC_NOT_FOUND_且阻止()
    {
        using var ws = new TempWorkspace("pc-src-missing");
        var report = await RunAsync(CreateService(), ws.PathOf("NoSuchDir"), ws.PathOf("Target"));

        Assert.True(report.IsBlocked);
        Assert.Equal(CheckSeverity.Blocking, Get(report, "SRC_NOT_FOUND").Severity);
        Assert.Equal(SuggestedAction.ChooseAnotherSource, Get(report, "SRC_NOT_FOUND").Action);
    }

    [Fact]
    public async Task 第1条_源是文件_SRC_NOT_DIRECTORY()
    {
        using var ws = new TempWorkspace("pc-src-file");
        var file = ws.CreateFile("plain.txt");

        var report = await RunAsync(CreateService(), file, ws.PathOf("Target"));

        Assert.True(Has(report, "SRC_NOT_DIRECTORY"));
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public async Task 第2条_源已是链接_SRC_IS_LINK_并给出原目标()
    {
        using var ws = new TempWorkspace("pc-src-link");
        var data = ws.CreateDirectory("Data");
        var link = ws.PathOf("Migrated");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(link, data), LinkCreateOptions.Default);

        var report = await RunAsync(CreateService(), link, ws.PathOf("Target"));

        var result = Get(report, "SRC_IS_LINK");
        Assert.Equal(SuggestedAction.OpenExistingTarget, result.Action);
        Assert.Equal(data, result.RelatedPath!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("重复迁移", result.Detail);
    }

    [Fact]
    public async Task 第3条_源命中黑名单_SRC_BLACKLISTED_且硬拒绝()
    {
        using var ws = new TempWorkspace("pc-src-black");
        var report = await RunAsync(CreateService(), @"C:\Windows", ws.PathOf("Target"));

        var result = Get(report, "SRC_BLACKLISTED");
        Assert.Equal(CheckSeverity.Blocking, result.Severity);
        Assert.Contains("BL-WINDOWS", result.Detail);
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public async Task 第3条_用户Profile根被拦_但其子目录放行()
    {
        using var ws = new TempWorkspace("pc-src-profile");
        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var blocked = await RunAsync(CreateService(), profileRoot, ws.PathOf("Target"));
        Assert.True(Has(blocked, "SRC_BLACKLISTED"));

        // 普通用户目录必须放行（核心使用场景）
        var temp = await RunAsync(CreateService(), ws.Root, ws.PathOf("Target"));
        Assert.False(Has(temp, "SRC_BLACKLISTED"));
    }

    [Fact]
    public async Task 第4条_警告名单_需确认_勾选后不再提示()
    {
        using var ws = new TempWorkspace("pc-src-warn");
        var source = ws.CreateDirectory("Source");

        var pending = await RunAsync(CreateService(warnings: new StubWarningPolicy()), source, ws.PathOf("Target"));
        var warnResult = Get(pending, "SRC_WARNING_LIST");
        Assert.Equal(CheckSeverity.RequireAcknowledge, warnResult.Severity);
        Assert.Equal(SuggestedAction.AcknowledgeRisk, warnResult.Action);
        Assert.True(pending.RequiresAcknowledge);
        Assert.False(pending.IsBlocked);

        var acknowledged = await RunAsync(
            CreateService(warnings: new StubWarningPolicy()),
            source, ws.PathOf("Target"),
            new PrecheckOptions { AcknowledgeWarnings = true });

        Assert.False(Has(acknowledged, "SRC_WARNING_LIST"));
        Assert.False(acknowledged.RequiresAcknowledge);
    }

    [Fact]
    public async Task 第5条_树内嵌套链接_被标注为特殊对象()
    {
        using var ws = new TempWorkspace("pc-src-special");
        var source = ws.CreateDirectory("Source");
        var data = ws.CreateDirectory(@"Source\Data");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(ws.PathOf(@"Source\Linked"), data), LinkCreateOptions.Default);

        var report = await RunAsync(CreateService(), source, ws.PathOf("Target"));

        var result = Get(report, "SRC_SPECIAL_OBJECTS");
        Assert.Equal(CheckSeverity.Warning, result.Severity);
        Assert.Equal(SuggestedAction.ReviewSpecialObjects, result.Action);
        Assert.Contains("嵌套目录链接", result.Detail);
    }

    [Fact]
    public async Task 第5条_云占位类成本对象_升级为需确认()
    {
        using var ws = new TempWorkspace("pc-src-cost");
        var source = ws.CreateDirectory("Source");
        var specials = new[]
        {
            new SpecialObjectSummary(
                SpecialObjectKind.CloudPlaceholder, 12,
                new[] { new SpecialObject(SpecialObjectKind.CloudPlaceholder, @"C:\x\y", "云占位文件", 0) }),
        };

        var report = await RunAsync(
            CreateService(probe: new StubTreeProbe(1024, specials)),
            source, ws.PathOf("Target"));

        var result = Get(report, "SRC_SPECIAL_OBJECTS_ACK");
        Assert.Equal(CheckSeverity.RequireAcknowledge, result.Severity);
        Assert.True(report.RequiresAcknowledge);
    }

    [Fact]
    public async Task 第5条_评估摘要_给出文件数与占用()
    {
        using var ws = new TempWorkspace("pc-src-assess");
        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\a.txt", new string('x', 1234));

        var report = await RunAsync(CreateService(), source, ws.PathOf("Target"));

        var result = Get(report, "INFO_ASSESSMENT");
        Assert.Equal(CheckSeverity.Info, result.Severity);
        Assert.Contains("1 个文件", result.Detail);
        Assert.Contains("1.21 KB", result.Detail); // 1234 字节
    }

    [Fact]
    public async Task 第6条_权限探测通过_不产生权限结果()
    {
        using var ws = new TempWorkspace("pc-src-perm");
        var source = ws.CreateDirectory("Source");

        var report = await RunAsync(CreateService(), source, ws.PathOf("Target"));

        Assert.False(Has(report, "SRC_PERMISSION_DENIED"));
        Assert.False(Has(report, "SRC_ENUMERATION_FAILED"));
    }

    // ==================== 目标侧 ====================

    [Fact]
    public async Task 第7条_可移动盘目标_硬拒绝()
    {
        using var ws = new TempWorkspace("pc-tgt-removable");
        var source = ws.CreateDirectory("Source");
        var volumes = new FakeVolumeResolver { DriveType = DriveTypeKind.Removable };

        var report = await RunAsync(CreateService(volumes), source, ws.PathOf("Target"));

        var result = Get(report, "TGT_NOT_FIXED_VOLUME");
        Assert.Equal(SuggestedAction.ChooseAnotherTarget, result.Action);
        Assert.Contains("Removable", result.Detail);
    }

    [Fact]
    public async Task 第7条_无法获取卷信息_硬拒绝()
    {
        using var ws = new TempWorkspace("pc-tgt-novol");
        var source = ws.CreateDirectory("Source");

        var report = await RunAsync(CreateService(new FakeVolumeResolver { Fail = true }), source, ws.PathOf("Target"));

        Assert.True(Has(report, "TGT_VOLUME_UNAVAILABLE"));
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public async Task 第8条_exFAT目标_硬拒绝并说明原因()
    {
        using var ws = new TempWorkspace("pc-tgt-exfat");
        var source = ws.CreateDirectory("Source");
        var volumes = new FakeVolumeResolver { FileSystemName = "exFAT" };

        var report = await RunAsync(CreateService(volumes), source, ws.PathOf("Target"));

        var result = Get(report, "TGT_FS_UNSUPPORTED");
        Assert.Contains("exFAT", result.Detail);
        Assert.Contains("NTFS", result.UserGuide);
    }

    [Theory]
    [InlineData("same")]        // 目标 = 源
    [InlineData("inside")]      // 目标在源之内
    [InlineData("ancestor")]    // 源在目标之内
    public async Task 第9条_路径重叠_硬拒绝(string mode)
    {
        using var ws = new TempWorkspace("pc-tgt-overlap");
        var source = ws.CreateDirectory("Source");
        var target = mode switch
        {
            "same" => source,
            "inside" => Path.Combine(source, "Sub"),
            _ => ws.Root,
        };

        var report = await RunAsync(CreateService(), source, target);

        Assert.True(Has(report, "TGT_PATH_OVERLAP"), $"模式 {mode} 应被判为路径重叠");
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public async Task 第10条_目标空间不足_硬拒绝且给出缺口()
    {
        using var ws = new TempWorkspace("pc-tgt-space");
        var source = ws.CreateDirectory("Source");
        var volumes = new FakeVolumeResolver { FreeBytes = 1 * Gb };

        var report = await RunAsync(CreateService(volumes, probe: new StubTreeProbe(4 * Gb)), source, ws.PathOf("Target"));

        var result = Get(report, "TGT_SPACE_INSUFFICIENT");
        Assert.Equal(SuggestedAction.FreeUpSpace, result.Action);
        Assert.Contains("空间不足", result.Detail);
        Assert.Contains("缺少", result.Detail);          // 给出具体缺口
        Assert.Contains("1.05", result.Title);           // 系数写在标题里，逐条可见
    }

    [Fact]
    public async Task 第11条_目标同名非空目录_硬拒绝()
    {
        using var ws = new TempWorkspace("pc-tgt-nonempty");
        var source = ws.CreateDirectory("Source");
        var target = ws.CreateDirectory("Target");
        ws.CreateFile(@"Target\existing.txt", "data");

        var report = await RunAsync(CreateService(), source, target);

        var result = Get(report, "TGT_EXISTS_NONEMPTY");
        Assert.Equal(SuggestedAction.ReuseOrRenameTarget, result.Action);
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public async Task 第11条_目标同名空目录_询问是否复用()
    {
        using var ws = new TempWorkspace("pc-tgt-empty");
        var source = ws.CreateDirectory("Source");
        var target = ws.CreateDirectory("Target");

        var pending = await RunAsync(CreateService(), source, target);
        Assert.Equal(CheckSeverity.RequireAcknowledge, Get(pending, "TGT_EXISTS_EMPTY").Severity);
        Assert.False(pending.IsBlocked);

        var reused = await RunAsync(CreateService(), source, target, new PrecheckOptions { ReuseEmptyTargetDirectory = true });
        Assert.False(reused.IsBlocked);
        Assert.True(Has(reused, "TGT_EXISTS_EMPTY"));
    }

    [Fact]
    public async Task 第11条_目标已存在链接_硬拒绝()
    {
        using var ws = new TempWorkspace("pc-tgt-link");
        var source = ws.CreateDirectory("Source");
        var data = ws.CreateDirectory("Data");
        var target = ws.PathOf("AlreadyLinked");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(target, data), LinkCreateOptions.Default);

        var report = await RunAsync(CreateService(), source, target);

        Assert.True(Has(report, "TGT_EXISTS_NONEMPTY"));
        Assert.Contains("链接", Get(report, "TGT_EXISTS_NONEMPTY").Detail);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    public async Task 目标名是系统保留名_硬拒绝(string reserved)
    {
        using var ws = new TempWorkspace("pc-tgt-reserved");
        var source = ws.CreateDirectory("Source");

        var report = await RunAsync(CreateService(), source, Path.Combine(ws.Root, reserved));

        Assert.True(Has(report, "TGT_NAME_INVALID"), $"{reserved} 应被判为保留名");
    }

    [Fact]
    public async Task 第12条_目标父目录可写_不产生阻止()
    {
        using var ws = new TempWorkspace("pc-tgt-perm-ok");
        var source = ws.CreateDirectory("Source");

        var report = await RunAsync(CreateService(), source, ws.PathOf(@"New\Deep\Target"));

        Assert.False(Has(report, "TGT_PARENT_NOT_WRITABLE"));
        Assert.True(report.CanProceed);
    }

    // ==================== 批次侧 ====================

    [Fact]
    public async Task 第13条_批次内嵌套_子条目自动跳过()
    {
        using var ws = new TempWorkspace("pc-batch-nested");
        var outer = ws.CreateDirectory("Outer");
        ws.CreateDirectory(@"Outer\Inner");

        var plan = new MigrationPlan(new[]
        {
            new MigrationPlanEntry(outer, ws.PathOf("T-Outer"), LinkKind.Junction),
            new MigrationPlanEntry(Path.Combine(outer, "Inner"), ws.PathOf("T-Inner"), LinkKind.Junction),
        }, new PrecheckOptions());

        var report = await CreateService().RunAsync(plan, null, CancellationToken.None);

        Assert.Single(report.SkippedEntries);
        Assert.Equal(Path.Combine(outer, "Inner"), report.SkippedEntries[0].SourcePath, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("只迁移上级目录", report.SkippedEntries[0].Reason);

        var result = Get(report, "BATCH_NESTED_SOURCE");
        Assert.Equal(CheckSeverity.Info, result.Severity);
        Assert.Equal(SuggestedAction.RemoveNestedEntry, result.Action);
        Assert.Single(report.EffectiveEntries);
        Assert.False(report.IsBlocked);
    }

    [Fact]
    public async Task 第14条_批次目标冲突_硬拒绝()
    {
        using var ws = new TempWorkspace("pc-batch-conflict");
        var a = ws.CreateDirectory("A");
        var b = ws.CreateDirectory("B");
        var shared = ws.PathOf("Shared-Target");

        var plan = new MigrationPlan(new[]
        {
            new MigrationPlanEntry(a, shared, LinkKind.Junction),
            new MigrationPlanEntry(b, shared, LinkKind.Junction),
        }, new PrecheckOptions());

        var report = await CreateService().RunAsync(plan, null, CancellationToken.None);

        var result = Get(report, "BATCH_TARGET_CONFLICT");
        Assert.Equal(SuggestedAction.ResolveTargetConflict, result.Action);
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public async Task 第14条_源落在另一条目的目标内_也算冲突()
    {
        using var ws = new TempWorkspace("pc-batch-cross");
        var a = ws.CreateDirectory("A");
        var b = ws.CreateDirectory("B");
        var targetOfA = ws.PathOf("T-A");

        var plan = new MigrationPlan(new[]
        {
            new MigrationPlanEntry(a, targetOfA, LinkKind.Junction),
            new MigrationPlanEntry(ws.PathOf(@"T-A\Whatever"), ws.PathOf("T-B"), LinkKind.Junction),
        }, new PrecheckOptions());

        var report = await CreateService().RunAsync(plan, null, CancellationToken.None);

        // 第二个源既不存在、又落在第一个条目的目标之内 → 至少有冲突或源不存在之一被检出
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public async Task 第15条_单条都放得下但累计放不下_报批次空间不足()
    {
        using var ws = new TempWorkspace("pc-batch-space");
        var a = ws.CreateDirectory("A");
        var b = ws.CreateDirectory("B");

        // 单条 1.5GB × 1.05 = 1.575GB < 2.5GB（单条都能过）；两条合计 3.15GB > 2.5GB
        var volumes = new FakeVolumeResolver { FreeBytes = (long)(2.5 * Gb) };

        var plan = new MigrationPlan(new[]
        {
            new MigrationPlanEntry(a, ws.PathOf("T-A"), LinkKind.Junction),
            new MigrationPlanEntry(b, ws.PathOf("T-B"), LinkKind.Junction),
        }, new PrecheckOptions());

        var report = await CreateService(volumes, probe: new StubTreeProbe((long)(1.5 * Gb)))
            .RunAsync(plan, null, CancellationToken.None);

        Assert.False(Has(report, "TGT_SPACE_INSUFFICIENT")); // 单条都能过
        var result = Get(report, "BATCH_SPACE_TOTAL");
        Assert.Equal(SuggestedAction.FreeUpSpace, result.Action);
        Assert.Contains("累计", result.Detail);
    }

    // ==================== 成功路径与报告结构 ====================

    [Fact]
    public async Task 合法请求_可继续_并产出解析条目与探测缓存()
    {
        using var ws = new TempWorkspace("pc-ok");
        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\a.txt", "hello");
        var target = ws.PathOf("Target");

        var report = await RunAsync(CreateService(), source, target);

        Assert.False(report.IsBlocked);
        Assert.True(report.CanProceed);
        Assert.Single(report.EffectiveEntries);
        Assert.Equal("e-01", report.EffectiveEntries[0].EntryId);
        Assert.Single(report.Probes);
        Assert.Single(report.SpaceChecks);
        Assert.Equal(1, report.TotalSourceFileCount);
    }

    [Fact]
    public async Task 每个结果_都带有可操作的用户处理建议()
    {
        using var ws = new TempWorkspace("pc-guide");
        var report = await RunAsync(CreateService(), ws.PathOf("Missing"), ws.PathOf("Target"));

        Assert.All(report.Results, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.False(string.IsNullOrWhiteSpace(r.Detail));
            Assert.False(string.IsNullOrWhiteSpace(r.UserGuide), $"拒绝码 {r.Code} 缺少用户处理建议");
        });
    }

    [Fact]
    public void 拒绝码表_可枚举且与结果一致()
    {
        var codes = PrecheckCodes.All.Select(c => c.Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Contains("SRC_BLACKLISTED", codes);
        Assert.Contains("TGT_SPACE_INSUFFICIENT", codes);
        Assert.Contains("BATCH_SPACE_TOTAL", codes);

        // 每个码都必须在结果里出现过（保证没有"只在表里存在"的死码）
        Assert.NotNull(PrecheckCodes.Resolve("SRC_IS_LINK"));
        Assert.Null(PrecheckCodes.Resolve("NO_SUCH_CODE"));
    }
}
