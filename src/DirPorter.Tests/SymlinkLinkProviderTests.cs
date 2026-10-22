using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using DirPorter.Core.Privilege;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// 符号链接测试。符号链接需要管理员权限或开发者模式，
/// 本机（普通用户 + 未开开发者模式）默认走"权限拒绝"分支；
/// 两条分支都断言，保证在任何环境跑都有意义，而不是简单跳过。
/// </summary>
public class SymlinkLinkProviderTests
{
    private static bool RealSymlinkAvailable => NativeTestHelpers.RealSymlinkCreationAvailable;

    private static DirSymlinkLinkProvider DirProvider(FakePrivilegeProbe probe)
        => new(new Win32VolumeResolver(), probe);

    private static DirSymlinkLinkProvider RealDirProvider()
        => new(new Win32VolumeResolver(), Win32PrivilegeProbe.Current);

    private static FileSymlinkLinkProvider RealFileProvider()
        => new(new Win32VolumeResolver(), Win32PrivilegeProbe.Current);

    [Fact]
    public void 无权限_Evaluate_返回PrivilegeNeeded并写明原因与建议()
    {
        using var ws = new TempWorkspace("symlink-nopriv");
        var target = ws.CreateDirectory("Target");
        var provider = DirProvider(FakePrivilegeProbe.Standard());

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), target));

        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.PrivilegeNeeded, capability.Code);
        Assert.Contains("管理员", capability.Reason);
        Assert.Contains("开发者模式", capability.Reason);
        Assert.Contains("开发者模式", capability.Suggestion);
    }

    [Fact]
    public void 开发者模式_可创建符号链接()
    {
        using var ws = new TempWorkspace("symlink-devmode");
        var target = ws.CreateDirectory("Target");
        var provider = DirProvider(FakePrivilegeProbe.DeveloperMode());

        Assert.True(provider.Evaluate(new LinkRequest(ws.PathOf("Link"), target)).CanHandle);
    }

    [Fact]
    public void 管理员且持有特权_可创建符号链接()
    {
        using var ws = new TempWorkspace("symlink-admin");
        var target = ws.CreateDirectory("Target");
        var provider = DirProvider(FakePrivilegeProbe.Elevated());

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), target));
        Assert.True(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.Ok, capability.Code);
    }

    [Fact]
    public void 管理员已提升但令牌无特权_仍不可创建()
    {
        // 关键边界：UAC 已提升 != 令牌里真的有 SeCreateSymbolicLinkPrivilege。
        var probe = new FakePrivilegeProbe { IsElevated = true, HasSeCreateSymbolicLinkPrivilege = false };
        Assert.False(probe.CanCreateSymbolicLink);

        using var ws = new TempWorkspace("symlink-partial");
        var target = ws.CreateDirectory("Target");
        Assert.Equal(LinkCapabilityCode.PrivilegeNeeded, DirProvider(probe).Evaluate(new LinkRequest(ws.PathOf("Link"), target)).Code);
    }

    [Fact]
    public void 符号链接_不拒绝UNC目标_与Junction形成差异()
    {
        using var ws = new TempWorkspace("symlink-unc");
        var provider = DirProvider(FakePrivilegeProbe.Elevated());

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), @"\\server\share\dir"));

        // 符号链接支持 UNC，因此不会出现 UncNotSupported；
        // 这里断言的是"限制不存在"，而不是"创建能成功"。
        Assert.NotEqual(LinkCapabilityCode.UncNotSupported, capability.Code);
    }

    [Fact]
    public void 目录符号链接_目标是文件_TargetWrongKind()
    {
        using var ws = new TempWorkspace("symlink-wrong-dir");
        var file = ws.CreateFile("plain.txt");
        var provider = DirProvider(FakePrivilegeProbe.Elevated());

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), file));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.TargetWrongKind, capability.Code);
    }

    [Fact]
    public void 文件符号链接_目标是目录_TargetWrongKind()
    {
        using var ws = new TempWorkspace("symlink-wrong-file");
        var dir = ws.CreateDirectory("Dir");
        var provider = new FileSymlinkLinkProvider(new Win32VolumeResolver(), FakePrivilegeProbe.Elevated());

        var capability = provider.Evaluate(new LinkRequest(ws.PathOf("Link"), dir));
        Assert.False(capability.CanHandle);
        Assert.Equal(LinkCapabilityCode.TargetWrongKind, capability.Code);
    }

    [Fact]
    public void Delete_普通目录_被拒绝()
    {
        using var ws = new TempWorkspace("symlink-delete-refuse");
        var dir = ws.CreateDirectory("Real");
        ws.CreateFile(@"Real\f.txt");

        Assert.Throws<LinkDeleteRefusedException>(() => RealDirProvider().Delete(dir));
        Assert.True(File.Exists(Path.Combine(dir, "f.txt")));
    }

    [Fact]
    public void Create_真实环境_目录符号链接_按权限走对应分支()
    {
        using var ws = new TempWorkspace("symlink-real-dir");
        var target = ws.CreateDirectory("Target");
        var link = ws.PathOf("Link");
        var provider = RealDirProvider();

        if (!RealSymlinkAvailable)
        {
            // 本机环境：必须抛出明确的权限异常（CLI 据此返回退出码 5），且不留残留。
            var ex = Assert.Throws<LinkPrivilegeRequiredException>(
                () => provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default));
            Assert.Equal(LinkKind.DirectorySymlink, ex.Kind);
            Assert.False(provider.Exists(link));
            Assert.False(Directory.Exists(link));
            return;
        }

        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        var info = provider.Inspect(link);
        Assert.Equal(LinkKind.DirectorySymlink, info.Kind);
        Assert.Equal(ReparseData.TagSymlink, info.ReparseTag);
        Assert.False(info.IsRelative);
        Assert.False(info.IsDangling);

        File.WriteAllText(Path.Combine(link, "via-link.txt"), "ok");
        Assert.Equal("ok", File.ReadAllText(Path.Combine(target, "via-link.txt")));
    }

    /// <summary>★ Unlink 安全回归：删除目录符号链接不得触碰目标数据。</summary>
    [Fact]
    public void Delete_安全回归_目录符号链接_不触碰目标数据()
    {
        using var ws = new TempWorkspace("symlink-sentinel");
        var target = ws.CreateDirectory("Target");
        var sentinel = ws.CreateFile(@"Target\sentinel.txt", "DO-NOT-DELETE");
        var link = ws.PathOf("Link");
        var provider = RealDirProvider();

        if (!RealSymlinkAvailable)
        {
            Assert.Throws<LinkPrivilegeRequiredException>(
                () => provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default));
            Assert.True(File.Exists(sentinel));
            return;
        }

        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);
        provider.Delete(link);

        Assert.False(provider.Exists(link));
        Assert.True(Directory.Exists(target));
        Assert.Equal("DO-NOT-DELETE", File.ReadAllText(sentinel));
    }

    /// <summary>★ 相对路径符号链接：链接所在父目录整体改名后仍应正确解析。</summary>
    [Fact]
    public void 相对路径符号链接_父目录改名后仍能解析()
    {
        using var ws = new TempWorkspace("symlink-relative");
        var appDir = ws.CreateDirectory("App");
        var dataDir = ws.CreateDirectory("Data");
        ws.CreateFile(@"Data\payload.txt", "payload");
        var link = Path.Combine(appDir, "link");
        var provider = RealDirProvider();

        if (!RealSymlinkAvailable)
        {
            Assert.Throws<LinkPrivilegeRequiredException>(
                () => provider.Create(new LinkRequest(link, dataDir, PreferRelativeTarget: true), new LinkCreateOptions { Relative = true }));
            return;
        }

        provider.Create(new LinkRequest(link, dataDir, PreferRelativeTarget: true), new LinkCreateOptions { Relative = true });

        var before = provider.Inspect(link);
        Assert.Equal(LinkKind.DirectorySymlink, before.Kind);
        Assert.True(before.IsRelative, "相对路径符号链接应报告 IsRelative=true");
        Assert.False(before.IsDangling);

        // 整体改名父目录：链接随目录一起移动，相对目标仍应解析到同一位置。
        var renamedAppDir = ws.PathOf("App-Renamed");
        Directory.Move(appDir, renamedAppDir);
        var movedLink = Path.Combine(renamedAppDir, "link");

        var after = provider.Inspect(movedLink);
        Assert.Equal(LinkKind.DirectorySymlink, after.Kind);
        Assert.True(after.IsRelative);
        Assert.False(after.IsDangling);
        Assert.Equal(dataDir, after.TargetAbsolute!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(movedLink, "payload.txt")));
    }

    [Fact]
    public void Create_文件符号链接_按权限走对应分支()
    {
        using var ws = new TempWorkspace("symlink-real-file");
        var target = ws.CreateFile("real.txt", "content");
        var link = ws.PathOf("link.txt");
        var provider = RealFileProvider();

        if (!RealSymlinkAvailable)
        {
            Assert.Throws<LinkPrivilegeRequiredException>(
                () => provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default));
            return;
        }

        provider.Create(new LinkRequest(link, target), LinkCreateOptions.Default);

        var info = provider.Inspect(link);
        Assert.Equal(LinkKind.FileSymlink, info.Kind);
        Assert.Equal("content", File.ReadAllText(link));

        provider.Delete(link);
        Assert.False(provider.Exists(link));
        Assert.True(File.Exists(target));
        Assert.Equal("content", File.ReadAllText(target));
    }
}
