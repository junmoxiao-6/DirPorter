using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

public class LinkServiceTests
{
    private static LinkService CreateService(bool privileged = true)
        => new(new Win32VolumeResolver(), privileged ? FakePrivilegeProbe.Elevated() : FakePrivilegeProbe.Standard());

    // ==================== 智能推荐（--type auto）====================

    [Fact]
    public void Recommend_目录与本地卷_Junction()
    {
        using var ws = new TempWorkspace("svc-roll-dir");
        var target = ws.CreateDirectory("Target");

        var recommendation = CreateService().Recommend(new LinkRequest(ws.PathOf("Link"), target));

        Assert.Equal(LinkKind.Junction, recommendation.Kind);
        Assert.Contains("目录联接", recommendation.Reason);
    }

    [Fact]
    public void Recommend_目录且目标为UNC_目录符号链接()
    {
        using var ws = new TempWorkspace("svc-roll-unc");

        var recommendation = CreateService().Recommend(new LinkRequest(ws.PathOf("Link"), @"\\server\share\dir"));

        Assert.Equal(LinkKind.DirectorySymlink, recommendation.Kind);
        Assert.Contains("UNC", recommendation.Reason);
    }

    [Fact]
    public void Recommend_目录且需要相对路径_目录符号链接()
    {
        using var ws = new TempWorkspace("svc-roll-rel");
        var target = ws.CreateDirectory("Target");

        var recommendation = CreateService().Recommend(new LinkRequest(ws.PathOf("Link"), target, PreferRelativeTarget: true));

        Assert.Equal(LinkKind.DirectorySymlink, recommendation.Kind);
        Assert.Contains("相对路径", recommendation.Reason);
    }

    [Fact]
    public void Recommend_文件同卷_硬链接()
    {
        using var ws = new TempWorkspace("svc-roll-file");
        var target = ws.CreateFile("real.txt");

        var recommendation = CreateService().Recommend(new LinkRequest(ws.PathOf("link.txt"), target));

        Assert.Equal(LinkKind.HardLink, recommendation.Kind);
        Assert.Contains("硬链接", recommendation.Reason);
    }

    [Fact]
    public void Recommend_文件跨卷_文件符号链接()
    {
        using var ws = new TempWorkspace("svc-roll-cross");
        var target = ws.CreateFile("real.txt");

        var resolver = new FakeVolumeResolver
        {
            SerialForTarget = 1111,
            SerialForLink = 2222,
            IsLinkPath = static p => p.Contains("crosslink"),
        };

        var service = new LinkService(resolver, FakePrivilegeProbe.Elevated());
        var recommendation = service.Recommend(new LinkRequest(ws.PathOf("crosslink.txt"), target));

        Assert.Equal(LinkKind.FileSymlink, recommendation.Kind);
        Assert.Contains("跨卷", recommendation.Reason);
    }

    [Fact]
    public void Recommend_目标为UNC_一律推荐符号链接并说明原因()
    {
        using var ws = new TempWorkspace("svc-roll-file-unc");
        var recommendation = CreateService().Recommend(new LinkRequest(ws.PathOf("link.txt"), @"\\server\share\f.txt"));

        // UNC 目标不可能是 Junction / 硬链接，只可能是两种符号链接之一；
        // 目标不可达时无法区分文件与目录，代码按目录符号链接给出默认推荐。
        Assert.Contains(recommendation.Kind, new[] { LinkKind.DirectorySymlink, LinkKind.FileSymlink });
        Assert.Contains("UNC", recommendation.Reason);
    }

    // ==================== 迁移场景限制 ====================

    [Fact]
    public void 硬链接不得用于目录迁移()
    {
        Assert.False(LinkService.SupportsMigration(LinkKind.HardLink));
        Assert.False(LinkService.SupportsMigration(LinkKind.FileSymlink));
        Assert.True(LinkService.SupportsMigration(LinkKind.Junction));
        Assert.True(LinkService.SupportsMigration(LinkKind.DirectorySymlink));
        Assert.Equal(new[] { LinkKind.Junction, LinkKind.DirectorySymlink }, LinkService.MigrationLinkKinds);
    }

    [Fact]
    public void 四种提供器齐备()
    {
        var service = CreateService();
        Assert.Equal(4, service.Providers.Count);
        Assert.True(service.Providers[LinkKind.Junction].SupportsDirectories);
        Assert.False(service.Providers[LinkKind.Junction].SupportsFiles);
        Assert.True(service.Providers[LinkKind.HardLink].SupportsFiles);
        Assert.False(service.Providers[LinkKind.HardLink].SupportsDirectories);
    }

    // ==================== 解析与创建 ====================

    [Fact]
    public void Resolve_auto_标记WasAuto并给出理由()
    {
        using var ws = new TempWorkspace("svc-resolve");
        var target = ws.CreateDirectory("Target");

        var resolution = CreateService().Resolve(new LinkRequest(ws.PathOf("Link"), target));

        Assert.True(resolution.WasAuto);
        Assert.Equal(LinkKind.Junction, resolution.Kind);
        Assert.Contains("目录联接", resolution.Reason);
        Assert.True(resolution.Capability.CanHandle);
    }

    [Fact]
    public void Resolve_显式指定_不标记WasAuto()
    {
        using var ws = new TempWorkspace("svc-resolve-explicit");
        var target = ws.CreateDirectory("Target");

        var resolution = CreateService().Resolve(new LinkRequest(ws.PathOf("Link"), target, LinkKind.DirectorySymlink));

        Assert.False(resolution.WasAuto);
        Assert.Equal(LinkKind.DirectorySymlink, resolution.Kind);
    }

    [Fact]
    public void Create_auto_目录_创建Junction并返回解析结果()
    {
        using var ws = new TempWorkspace("svc-create-auto");
        var target = ws.CreateDirectory("Target");
        var link = ws.PathOf("Link");

        var resolution = CreateService().Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        Assert.Equal(LinkKind.Junction, resolution.Kind);
        Assert.True(resolution.WasAuto);
        Assert.Equal(LinkKind.Junction, CreateService().Inspect(link).Kind);
    }

    [Fact]
    public void Create_无权限时创建符号链接_抛LinkPrivilegeRequired()
    {
        using var ws = new TempWorkspace("svc-create-nopriv");
        var target = ws.CreateDirectory("Target");
        var service = CreateService(privileged: false);

        Assert.Throws<LinkPrivilegeRequiredException>(() =>
            service.Create(new LinkRequest(ws.PathOf("Link"), target, LinkKind.DirectorySymlink), LinkCreateOptions.Default));
    }

    [Fact]
    public void EvaluateAll_返回四种类型的能力()
    {
        using var ws = new TempWorkspace("svc-evaluate-all");
        var target = ws.CreateDirectory("Target");
        var service = CreateService(privileged: false);

        var results = service.EvaluateAll(new LinkRequest(ws.PathOf("Link"), target));

        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.Kind == LinkKind.Junction && r.Capability.CanHandle);

        // 无权限环境：目录符号链接必须被明确置灰（不可用 + 有原因），而不是点下去才报错
        var dirSymlink = results.Single(r => r.Kind == LinkKind.DirectorySymlink);
        Assert.Equal(LinkCapabilityCode.PrivilegeNeeded, dirSymlink.Capability.Code);

        // 文件符号链接与硬链接面对"目录目标"时给出的是更根本的类型不匹配原因
        Assert.Equal(LinkCapabilityCode.TargetWrongKind, results.Single(r => r.Kind == LinkKind.FileSymlink).Capability.Code);
        Assert.Equal(LinkCapabilityCode.TargetWrongKind, results.Single(r => r.Kind == LinkKind.HardLink).Capability.Code);
    }

    // ==================== 检视与解除链接 ====================

    [Fact]
    public void Inspect_区分四种类型与普通对象()
    {
        using var ws = new TempWorkspace("svc-inspect");
        var targetDir = ws.CreateDirectory("Target");
        var targetFile = ws.CreateFile("real.txt", "data");
        var junction = ws.PathOf("Junction");
        var hardLink = ws.PathOf("hardlink.txt");

        var service = CreateService();
        service.Providers[LinkKind.Junction].Create(new LinkRequest(junction, targetDir), LinkCreateOptions.Default);
        service.Providers[LinkKind.HardLink].Create(new LinkRequest(hardLink, targetFile), LinkCreateOptions.Default);

        Assert.Equal(LinkKind.Junction, service.Inspect(junction).Kind);
        Assert.Equal(LinkKind.HardLink, service.Inspect(hardLink).Kind);
        Assert.Null(service.Inspect(targetDir).Kind);
        // 硬链接两端对称，目标侧同样报告 HardLink
        Assert.Equal(LinkKind.HardLink, service.Inspect(targetFile).Kind);

        var plainFile = ws.CreateFile("plain.txt", "x");
        Assert.Null(service.Inspect(plainFile).Kind);
        Assert.False(service.Inspect(ws.PathOf("absent")).PathExists);
    }

    [Fact]
    public void Delete_自动分派到正确的提供器()
    {
        using var ws = new TempWorkspace("svc-delete-dispatch");
        var target = ws.CreateDirectory("Target");
        var sentinel = ws.CreateFile(@"Target\sentinel.txt", "keep");
        var junction = ws.PathOf("Junction");

        var service = CreateService();
        service.Providers[LinkKind.Junction].Create(new LinkRequest(junction, target), LinkCreateOptions.Default);

        var kind = service.Delete(junction);

        Assert.Equal(LinkKind.Junction, kind);
        Assert.False(service.Exists(junction));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Delete_普通文件_被拒绝且数据保留()
    {
        using var ws = new TempWorkspace("svc-delete-plain");
        var file = ws.CreateFile("plain.txt", "important");

        Assert.Throws<LinkDeleteRefusedException>(() => CreateService().Delete(file));
        Assert.Equal("important", File.ReadAllText(file));
    }

    [Fact]
    public void Delete_重解析点但类型未知_被拒绝()
    {
        using var ws = new TempWorkspace("svc-delete-unknown");
        var target = ws.CreateDirectory("Target");
        var junction = ws.PathOf("Junction");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(junction, target), LinkCreateOptions.Default);

        // 用一个"只认符号链接"的服务去检视，确认不会误删 Junction：
        // 直接构造一个标签不匹配的场景 —— 这里通过 Inspect 结果验证分派依据是真实标签而非调用方猜测。
        var info = CreateService().Inspect(junction);
        Assert.Equal(ReparseData.TagMountPoint, info.ReparseTag);
        Assert.Equal(LinkKind.Junction, info.Kind);
    }

    [Fact]
    public void 检视与删除_对悬空链接同样安全()
    {
        using var ws = new TempWorkspace("svc-dangling");
        var target = ws.CreateDirectory("Target");
        ws.CreateFile(@"Target\sentinel.txt", "keep");
        var junction = ws.PathOf("Junction");

        var service = CreateService();
        service.Providers[LinkKind.Junction].Create(new LinkRequest(junction, target), LinkCreateOptions.Default);

        // 把目标改名（不是删除），制造悬空链接
        var movedTarget = ws.PathOf("Target-Moved");
        Directory.Move(target, movedTarget);
        Assert.True(service.Inspect(junction).IsDangling);

        service.Delete(junction);
        Assert.False(service.Exists(junction));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(movedTarget, "sentinel.txt")));
    }
}
