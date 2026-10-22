using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

public class JunctionLinkProviderTests
{
    private static JunctionLinkProvider CreateProvider() => new(new Win32VolumeResolver());

    [Fact]
    public void Create_之后Inspect_报告Junction与目标绝对路径()
    {
        using var ws = new TempWorkspace("junction");
        var target = ws.CreateDirectory("Target");
        var link = ws.PathOf("Link");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        var info = provider.Inspect(link);
        Assert.True(info.PathExists);
        Assert.True(info.IsReparsePoint);
        Assert.Equal(LinkKind.Junction, info.Kind);
        Assert.Equal(ReparseData.TagMountPoint, info.ReparseTag);
        Assert.Equal(target, info.TargetAbsolute!, StringComparer.OrdinalIgnoreCase);
        Assert.False(info.IsRelative);
        Assert.False(info.IsDangling);
    }

    [Fact]
    public void Create_之后通过链接读写_落到目标目录()
    {
        using var ws = new TempWorkspace("junction-rw");
        var target = ws.CreateDirectory("Target");
        var link = ws.PathOf("Link");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        File.WriteAllText(Path.Combine(link, "through-link.txt"), "hello");

        Assert.True(File.Exists(Path.Combine(target, "through-link.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "through-link.txt")));
        Assert.True(Directory.Exists(link));
    }

    /// <summary>
    /// ★ 最高优先级安全回归（需求第 200 行，不允许跳过）：
    /// 删除 Junction 后，目标目录内的数据必须完整无损。
    /// </summary>
    [Fact]
    public void Delete_安全回归_不触碰目标数据_哨兵文件必须存活()
    {
        using var ws = new TempWorkspace("junction-sentinel");
        var target = ws.CreateDirectory("Target");
        var sentinelPath = ws.CreateFile(@"Target\sentinel.txt", "DO-NOT-DELETE");
        ws.CreateFile(@"Target\sub\nested.txt", "nested");
        var link = ws.PathOf("Link");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);
        Assert.True(provider.Exists(link));

        provider.Delete(link);

        // 1) 链接本体已消失
        Assert.False(provider.Exists(link));
        Assert.False(Directory.Exists(link));

        // 2) 目标数据完好无损
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal("DO-NOT-DELETE", File.ReadAllText(sentinelPath));
        Assert.True(File.Exists(Path.Combine(target, "sub", "nested.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(target, "sub", "nested.txt")));
    }

    [Fact]
    public void Delete_普通目录_被拒绝且目录保持存在()
    {
        using var ws = new TempWorkspace("junction-refuse");
        var realDir = ws.CreateDirectory("RealDir");
        ws.CreateFile(@"RealDir\keep.txt");

        var provider = CreateProvider();
        Assert.Throws<LinkDeleteRefusedException>(() => provider.Delete(realDir));

        Assert.True(Directory.Exists(realDir));
        Assert.True(File.Exists(Path.Combine(realDir, "keep.txt")));
    }

    [Fact]
    public void Delete_普通文件_被拒绝()
    {
        using var ws = new TempWorkspace("junction-refuse-file");
        var file = ws.CreateFile("plain.txt");

        var provider = CreateProvider();
        Assert.Throws<LinkDeleteRefusedException>(() => provider.Delete(file));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Delete_路径不存在_抛InvalidOperation()
    {
        using var ws = new TempWorkspace("junction-missing");
        var provider = CreateProvider();
        Assert.Throws<InvalidOperationException>(() => provider.Delete(ws.PathOf("nope")));
    }

    [Fact]
    public void Inspect_普通目录_Kind为null()
    {
        using var ws = new TempWorkspace("junction-plain");
        var dir = ws.CreateDirectory("Plain");

        var info = CreateProvider().Inspect(dir);
        Assert.True(info.PathExists);
        Assert.False(info.IsReparsePoint);
        Assert.Null(info.Kind);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public void Inspect_路径不存在_PathExists为false()
    {
        using var ws = new TempWorkspace("junction-absent");
        var info = CreateProvider().Inspect(ws.PathOf("absent"));
        Assert.False(info.PathExists);
        Assert.Null(info.Kind);
    }

    [Fact]
    public void Inspect_悬空Junction_IsDangling为true()
    {
        using var ws = new TempWorkspace("junction-dangling");
        var target = ws.CreateDirectory("Target");
        var link = ws.PathOf("Link");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        // 目标被移走 → 链接悬空
        Directory.Delete(target, recursive: false);

        var info = provider.Inspect(link);
        Assert.Equal(LinkKind.Junction, info.Kind);
        Assert.True(info.IsDangling);

        // 悬空链接也必须能被安全清除
        provider.Delete(link);
        Assert.False(provider.Exists(link));
    }

    // ==================== CanHandle / Evaluate 拒绝理由逐条覆盖 ====================

    [Fact]
    public void Evaluate_目标不存在_TargetMissing()
    {
        using var ws = new TempWorkspace("junction-eval-missing");
        var provider = CreateProvider();
        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), ws.PathOf("NoSuchTarget")));

        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.TargetMissing, capability.Code);
    }

    [Fact]
    public void Evaluate_目标是文件_TargetWrongKind()
    {
        using var ws = new TempWorkspace("junction-eval-file");
        var file = ws.CreateFile("file.txt");
        var provider = CreateProvider();

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), file));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.TargetWrongKind, capability.Code);
    }

    [Fact]
    public void Evaluate_UNC目标_UncNotSupported并建议改用符号链接()
    {
        using var ws = new TempWorkspace("junction-eval-unc");
        var provider = CreateProvider();

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), @"\\server\share\dir"));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.UncNotSupported, capability.Code);
        Assert.Contains("符号链接", capability.Suggestion);
    }

    [Fact]
    public void Evaluate_链接父目录不存在_LinkParentMissing()
    {
        using var ws = new TempWorkspace("junction-eval-parent");
        var target = ws.CreateDirectory("Target");
        var provider = CreateProvider();

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf(@"NoParent\Link"), target));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.LinkParentMissing, capability.Code);
    }

    [Fact]
    public void Evaluate_目标在链接路径内部_PathSelfOrAncestor()
    {
        using var ws = new TempWorkspace("junction-eval-self");
        var provider = CreateProvider();

        var link = ws.CreateDirectory("Outer");
        var target = ws.CreateDirectory(@"Outer\Inner");

        var capability = provider.Evaluate(new LinkRequest(link, target));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.PathSelfOrAncestor, capability.Code);
    }

    [Fact]
    public void Evaluate_落点是已存在的非空目录_LinkPathNotEmpty()
    {
        using var ws = new TempWorkspace("junction-eval-occupied");
        var target = ws.CreateDirectory("Target");
        var occupied = ws.CreateDirectory("Occupied");
        ws.CreateFile(@"Occupied\data.txt");

        var capability = CreateProvider().Evaluate(new LinkRequest(occupied, target));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.LinkPathNotEmpty, capability.Code);
    }

    [Fact]
    public void Evaluate_落点已是链接_LinkAlreadyExists()
    {
        using var ws = new TempWorkspace("junction-eval-exists");
        var target = ws.CreateDirectory("Target");
        var anotherTarget = ws.CreateDirectory("Target2");
        var link = ws.PathOf("Link");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        var capability = provider.Evaluate(new LinkRequest(link, anotherTarget));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.LinkAlreadyExists, capability.Code);
    }

    [Fact]
    public void Evaluate_目标是非固定卷_VolumeNotFixed()
    {
        using var ws = new TempWorkspace("junction-eval-removable");
        var target = ws.CreateDirectory("Target");
        var resolver = new FakeVolumeResolver { DriveType = DriveTypeKind.Removable };
        var provider = new JunctionLinkProvider(resolver);

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), target));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.VolumeNotFixed, capability.Code);
    }

    [Fact]
    public void Evaluate_目标是FAT32_FileSystemNotSupported()
    {
        using var ws = new TempWorkspace("junction-eval-fat32");
        var target = ws.CreateDirectory("Target");
        var resolver = new FakeVolumeResolver { FileSystemName = "FAT32" };
        var provider = new JunctionLinkProvider(resolver);

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), target));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.FileSystemNotSupported, capability.Code);
    }

    [Fact]
    public void Evaluate_无法获取卷信息_VolumeUnavailable()
    {
        using var ws = new TempWorkspace("junction-eval-novol");
        var target = ws.CreateDirectory("Target");
        var provider = new JunctionLinkProvider(new FakeVolumeResolver { Fail = true });

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), target));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.VolumeUnavailable, capability.Code);
    }

    [Fact]
    public void Evaluate_合法请求_Ok且说明跨卷与权限()
    {
        using var ws = new TempWorkspace("junction-eval-ok");
        var target = ws.CreateDirectory("Target");
        var capability = CreateProvider().Evaluate(new LinkRequest(ws.PathOf("Link"), target));

        Assert.True(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.Ok, capability.Code);
        Assert.Contains("跨卷", capability.Reason);
    }

    [Fact]
    public void CanHandle_out参数_在失败时给出原因与建议()
    {
        using var ws = new TempWorkspace("junction-canhandle");
        var provider = CreateProvider();

        var canHandle = provider.CanHandle(new LinkRequest(ws.PathOf("Link"), ws.PathOf("Missing")), out var reason);
        Assert.False(canHandle);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("不存在", reason);
    }

    [Fact]
    public void Create_结构校验失败_不产生任何磁盘残留()
    {
        using var ws = new TempWorkspace("junction-noleak");
        var link = ws.PathOf("Link");
        var provider = CreateProvider();

        Assert.Throws<InvalidOperationException>(() =>
            provider.Create(new LinkRequest(link, ws.PathOf("MissingTarget")), LinkCreateOptions.Default));

        Assert.False(provider.Exists(link));
        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void Create_DryRun_零磁盘写入()
    {
        using var ws = new TempWorkspace("junction-dryrun");
        var target = ws.CreateDirectory("Target");
        var link = ws.PathOf("Link");
        var provider = CreateProvider();

        provider.Create(new LinkRequest(link, target), new LinkCreateOptions { DryRun = true });

        Assert.False(provider.Exists(link));
        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void Create_ReplaceExisting_先删旧链接再建新链接()
    {
        using var ws = new TempWorkspace("junction-replace");
        var first = ws.CreateDirectory("First");
        var second = ws.CreateDirectory("Second");
        var link = ws.PathOf("Link");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, first), LinkCreateOptions.Default);
        provider.Create(new LinkRequest(link, second), new LinkCreateOptions { ReplaceExisting = true });

        var info = provider.Inspect(link);
        Assert.Equal(LinkKind.Junction, info.Kind);
        Assert.Equal(second, info.TargetAbsolute!, StringComparer.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(first)); // 旧目标未被误删
    }

    [Fact]
    public void Create_带emoji与空格与中文的路径_成功()
    {
        using var ws = new TempWorkspace("junction-unicode");
        var target = ws.CreateDirectory("目标 目录 🚀");
        var link = ws.PathOf("链接 位置 🚀");

        var provider = CreateProvider();
        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        File.WriteAllText(Path.Combine(link, "a b.txt"), "ok");
        Assert.Equal("ok", File.ReadAllText(Path.Combine(target, "a b.txt")));
        Assert.Equal(target, provider.Inspect(link).TargetAbsolute!, StringComparer.OrdinalIgnoreCase);
    }
}
