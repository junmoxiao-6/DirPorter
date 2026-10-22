using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

/// <summary>安全删除器：单测覆盖"遇重解析点不穿越"这一铁律。</summary>
public class R3_SafeTreeDeleterTests
{
    [Fact]
    public void 删除整棵树_文件与目录都被删除()
    {
        using var ws = new TempWorkspace("del-tree");
        ws.CreateFile(@"A\a.txt", "1");
        ws.CreateFile(@"A\B\b.txt", "22");
        ws.CreateFile(@"C\c.txt", "333");

        var result = SafeTreeDeleter.DeleteTree(ws.PathOf("A"));

        Assert.True(result.Success, string.Join("；", result.Failures.Select(f => f.Message)));
        Assert.Equal(2, result.DeletedFiles);
        Assert.False(Directory.Exists(ws.PathOf("A")));
        Assert.True(Directory.Exists(ws.PathOf("C")));
        Assert.Equal(3, result.DeletedBytes);
    }

    /// <summary>
    /// ★ 铁律回归：`.migbak` 目录内部可能含嵌套 Junction，
    /// 递归删除一旦穿越就会把链接指向的真实数据全部删光。
    /// </summary>
    [Fact]
    public void 树内含Junction_删除不穿越_目标数据必须存活()
    {
        using var ws = new TempWorkspace("del-junction");
        var victim = ws.CreateDirectory("Victim");
        var sentinel = ws.CreateFile(@"Victim\sentinel.txt", "DO-NOT-DELETE");
        ws.CreateFile(@"Victim\sub\nested.txt", "nested");

        // 被删的树里放一个指向 Victim 的 Junction
        var doomed = ws.CreateDirectory("Doomed");
        ws.CreateFile(@"Doomed\own.txt", "own");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(ws.PathOf(@"Doomed\LinkToVictim"), victim), LinkCreateOptions.Default);

        var result = SafeTreeDeleter.DeleteTree(ws.PathOf("Doomed"));

        Assert.False(Directory.Exists(ws.PathOf("Doomed")));
        Assert.Equal(1, result.DeletedLinks);

        // 铁律：目标数据一个字节都不能少
        Assert.True(Directory.Exists(victim));
        Assert.Equal("DO-NOT-DELETE", File.ReadAllText(sentinel));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(victim, "sub", "nested.txt")));
    }

    [Fact]
    public void 清空目录内容_保留根目录()
    {
        using var ws = new TempWorkspace("del-clear");
        var root = ws.CreateDirectory("Target");
        ws.CreateFile(@"Target\a.txt", "1");
        ws.CreateFile(@"Target\sub\b.txt", "2");

        var result = SafeTreeDeleter.ClearDirectoryContents(root);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void 只读文件_也能删除()
    {
        using var ws = new TempWorkspace("del-readonly");
        var file = ws.CreateFile("ro.txt", "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var result = SafeTreeDeleter.DeleteTree(ws.Root);

        Assert.True(result.Success, string.Join("；", result.Failures.Select(f => f.Message)));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void 路径不存在_不抛异常()
    {
        using var ws = new TempWorkspace("del-missing");
        var result = SafeTreeDeleter.DeleteTree(ws.PathOf("nope"));

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(result.Success);
    }

    [Fact]
    public void 被占用文件_记录失败而不是抛异常()
    {
        using var ws = new TempWorkspace("del-locked");
        var file = ws.CreateFile("locked.txt", "x");

        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = SafeTreeDeleter.DeleteTree(ws.Root);

            Assert.True(result.AnyFailure);
            Assert.Contains(result.Failures, f => f.Path.Contains("locked.txt", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(file));   // 删不掉就保留，绝不硬删
        }
    }
}
