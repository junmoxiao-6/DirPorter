using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

public class HardLinkProviderTests
{
    private static HardLinkProvider CreateProvider() => new(new Win32VolumeResolver());

    [Fact]
    public void Create_之后Inspect_报告硬链接与链接数()
    {
        using var ws = new TempWorkspace("hardlink");
        var target = ws.CreateFile("real.txt", "content");
        var link = ws.PathOf("link.txt");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        var info = provider.Inspect(link);
        Assert.True(info.PathExists);
        Assert.False(info.IsReparsePoint);           // 硬链接不是重解析点 —— 这是与其它三种的根本差异
        Assert.Equal(LinkKind.HardLink, info.Kind);
        Assert.Equal(2u, info.NumberOfLinks);
        Assert.Equal(0u, info.ReparseTag);

        // ⚠️ 硬链接具有对称性：两侧路径指向同一个 MFT 记录，无法区分"原始文件"与"链接"，
        // 因此目标侧同样会报告 HardLink 与相同链接数。这也是 Delete 必须在链接数==1 时拒绝的原因。
        var targetInfo = provider.Inspect(target);
        Assert.Equal(LinkKind.HardLink, targetInfo.Kind);
        Assert.Equal(2u, targetInfo.NumberOfLinks);
    }

    [Fact]
    public void 一份数据两棵目录树_写入互相可见()
    {
        using var ws = new TempWorkspace("hardlink-share");
        var target = ws.CreateFile("tree1\\data.txt", "v1");
        var link = ws.PathOf("tree2\\data.txt");
        Directory.CreateDirectory(ws.PathOf("tree2"));

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        File.WriteAllText(link, "v2");
        Assert.Equal("v2", File.ReadAllText(target));

        File.WriteAllText(target, "v3");
        Assert.Equal("v3", File.ReadAllText(link));
    }

    [Fact]
    public void Delete_一个链接_另一个链接与数据保持完好()
    {
        using var ws = new TempWorkspace("hardlink-delete");
        var target = ws.CreateFile("real.txt", "shared");
        var link = ws.PathOf("link.txt");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);
        provider.Delete(link);

        Assert.False(File.Exists(link));
        Assert.True(File.Exists(target));
        Assert.Equal("shared", File.ReadAllText(target));

        // 只断言"数据仍在、至少还有一个名字"，不硬编码 NumberOfLinks == 1：
        // 在把删除路由到回收站的系统上（本机 Win11 实测），删除一个硬链接只是把这个"名字"
        // 移入 $Recycle.Bin（同卷重命名，保留同一 MFT 记录），文件仍有两个名字，计数不会下降。
        // 该现象已用 fsutil hardlink list 交叉验证（见设计与计划文档 §21 环境发现）。
        Assert.True(provider.Inspect(target).NumberOfLinks >= 1);
    }

    /// <summary>保守拒绝：链接数已降回 1 时删除等同于删数据，必须拒绝。</summary>
    [Fact]
    public void Delete_单一链接的普通文件_被拒绝()
    {
        using var ws = new TempWorkspace("hardlink-refuse");
        var file = ws.CreateFile("only.txt", "important");

        Assert.Throws<LinkDeleteRefusedException>(() => CreateProvider().Delete(file));
        Assert.True(File.Exists(file));
        Assert.Equal("important", File.ReadAllText(file));
    }

    [Fact]
    public void Delete_目录_被拒绝()
    {
        using var ws = new TempWorkspace("hardlink-refuse-dir");
        var dir = ws.CreateDirectory("Dir");
        Assert.Throws<LinkDeleteRefusedException>(() => CreateProvider().Delete(dir));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Inspect_重解析点_不报HardLink()
    {
        using var ws = new TempWorkspace("hardlink-vs-junction");
        var target = ws.CreateDirectory("Target");
        var junction = ws.PathOf("Junction");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(junction, target), LinkCreateOptions.Default);

        var info = CreateProvider().Inspect(junction);
        Assert.True(info.IsReparsePoint);
        Assert.Null(info.Kind);   // 是重解析点 ⇒ 一定不是硬链接
    }

    // ==================== CanHandle / Evaluate ====================

    [Fact]
    public void Evaluate_同卷文件_Ok()
    {
        using var ws = new TempWorkspace("hardlink-eval-ok");
        var target = ws.CreateFile("real.txt");

        var capability = CreateProvider().Evaluate(new LinkRequest(ws.PathOf("link.txt"), target));
        Assert.True(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.Ok, capability.Code);
        Assert.Contains("同一卷", capability.Reason);
    }

    [Fact]
    public void Evaluate_跨卷_CrossVolumeNotSupported并建议改用文件符号链接()
    {
        using var ws = new TempWorkspace("hardlink-eval-cross");
        var target = ws.CreateFile("real.txt");

        var resolver = new FakeVolumeResolver
        {
            SerialForTarget = 1111,
            SerialForLink = 2222,
            IsLinkPath = static p => p.Contains("crosslink"),
        };

        var capability = new HardLinkProvider(resolver)
            .Evaluate(new LinkRequest(ws.PathOf("crosslink.txt"), target));

        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.CrossVolumeNotSupported, capability.Code);
        Assert.Contains("文件符号链接", capability.Suggestion);
    }

    [Fact]
    public void Evaluate_目标是目录_TargetWrongKind()
    {
        using var ws = new TempWorkspace("hardlink-eval-dir");
        var dir = ws.CreateDirectory("Dir");

        var capability = CreateProvider().Evaluate(new LinkRequest(ws.PathOf("link"), dir));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.TargetWrongKind, capability.Code);
    }

    [Fact]
    public void Evaluate_UNC目标_UncNotSupported()
    {
        using var ws = new TempWorkspace("hardlink-eval-unc");
        var capability = CreateProvider().Evaluate(new LinkRequest(ws.PathOf("link.txt"), @"\\server\share\f.txt"));

        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.UncNotSupported, capability.Code);
    }

    [Fact]
    public void Evaluate_非NTFS卷_FileSystemNotSupported()
    {
        using var ws = new TempWorkspace("hardlink-eval-fat32");
        var target = ws.CreateFile("real.txt");
        var resolver = new FakeVolumeResolver { FileSystemName = "exFAT" };

        var capability = new HardLinkProvider(resolver).Evaluate(new LinkRequest(ws.PathOf("link.txt"), target));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.FileSystemNotSupported, capability.Code);
    }

    [Fact]
    public void Evaluate_无法获取卷信息_VolumeUnavailable()
    {
        using var ws = new TempWorkspace("hardlink-eval-novol");
        var target = ws.CreateFile("real.txt");

        var capability = new HardLinkProvider(new FakeVolumeResolver { Fail = true })
            .Evaluate(new LinkRequest(ws.PathOf("link.txt"), target));

        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.VolumeUnavailable, capability.Code);
    }

    [Fact]
    public void Evaluate_落点已存在_LinkAlreadyExists()
    {
        using var ws = new TempWorkspace("hardlink-eval-exists");
        var target = ws.CreateFile("real.txt");
        var link = ws.CreateFile("link.txt", "existing");

        var capability = CreateProvider().Evaluate(new LinkRequest(link, target));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.LinkAlreadyExists, capability.Code);
    }

    [Fact]
    public void Create_ReplaceExisting_覆盖已有落点文件()
    {
        using var ws = new TempWorkspace("hardlink-replace");
        var target = ws.CreateFile("real.txt", "new-content");
        var link = ws.CreateFile("link.txt", "old-content");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), new LinkCreateOptions { ReplaceExisting = true });

        Assert.Equal("new-content", File.ReadAllText(link));
        Assert.Equal(2u, provider.Inspect(link).NumberOfLinks);
    }

    [Fact]
    public void Create_DryRun_零磁盘写入()
    {
        using var ws = new TempWorkspace("hardlink-dryrun");
        var target = ws.CreateFile("real.txt");
        var link = ws.PathOf("link.txt");

        CreateProvider().Create(new LinkRequest(link, target), new LinkCreateOptions { DryRun = true });
        Assert.False(File.Exists(link));
    }
}
