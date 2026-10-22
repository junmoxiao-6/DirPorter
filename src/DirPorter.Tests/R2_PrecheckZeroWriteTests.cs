using DirPorter.Core.Config;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.Precheck;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// 需求硬要求：PRECHECK 与 DRY_RUN 全程**零磁盘写入**（验收第 191 行）。
/// 本组用例用"目录树快照前后一致"来证明，而不是靠代码审阅。
/// </summary>
public class R2_PrecheckZeroWriteTests
{
    private static PrecheckService CreateService() => new(new Win32VolumeResolver(), new BlacklistPolicy(), new NoWarningPolicy(), new TreeProbe());

    private static List<string> Snapshot(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [Fact]
    public async Task 通过预检_不产生任何磁盘写入()
    {
        using var ws = new TempWorkspace("pc-nowrite-ok");
        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\a.txt", "hello");
        ws.CreateFile(@"Source\sub\b.txt", "world");

        var targetParent = ws.CreateDirectory("TargetParent");
        var target = Path.Combine(targetParent, "NewTarget");

        var before = Snapshot(ws.Root);

        var report = await CreateService().RunAsync(
            MigrationPlan.Single(source, target, LinkKind.Junction),
            null, CancellationToken.None);

        var after = Snapshot(ws.Root);

        Assert.False(report.IsBlocked);
        Assert.Equal(before, after);
        Assert.False(Directory.Exists(target), "预检不得创建目标目录");
    }

    [Fact]
    public async Task 被阻止的预检_同样零写入()
    {
        using var ws = new TempWorkspace("pc-nowrite-blocked");
        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\a.txt", "hello");
        var target = ws.CreateDirectory("Target");
        ws.CreateFile(@"Target\existing.txt", "data"); // 触发 TGT_EXISTS_NONEMPTY

        var before = Snapshot(ws.Root);

        var report = await CreateService().RunAsync(
            MigrationPlan.Single(source, target, LinkKind.Junction),
            null, CancellationToken.None);

        var after = Snapshot(ws.Root);

        Assert.True(report.IsBlocked);
        Assert.Equal(before, after);
        Assert.True(File.Exists(Path.Combine(target, "existing.txt")));
    }

    [Fact]
    public async Task 源命中黑名单_提前返回且目标未被触碰()
    {
        using var ws = new TempWorkspace("pc-nowrite-blacklist");
        var targetParent = ws.CreateDirectory("TargetParent");
        var before = Snapshot(ws.Root);

        var report = await CreateService().RunAsync(
            MigrationPlan.Single(@"C:\Windows", Path.Combine(targetParent, "T"), LinkKind.Junction),
            null, CancellationToken.None);

        Assert.True(report.IsBlocked);
        Assert.Equal(before, Snapshot(ws.Root));
    }

    [Fact]
    public async Task 预检不修改源文件内容与时间戳()
    {
        using var ws = new TempWorkspace("pc-nowrite-meta");
        var source = ws.CreateDirectory("Source");
        var file = ws.CreateFile(@"Source\a.txt", "immutable");
        var beforeWrite = File.GetLastWriteTimeUtc(file);
        var beforeLength = new FileInfo(file).Length;

        await CreateService().RunAsync(
            MigrationPlan.Single(source, ws.PathOf("Target"), LinkKind.Junction),
            null, CancellationToken.None);

        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(file));
        Assert.Equal(beforeLength, new FileInfo(file).Length);
        Assert.Equal("immutable", File.ReadAllText(file));
    }
}
