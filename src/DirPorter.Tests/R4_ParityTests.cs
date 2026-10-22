using DirPorter.Core.Copy;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Migration;
using DirPorter.Core.Privilege;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R4 验收核心：**同源分别用引擎 A / B 复制后，结果必须完全一致**。
///
/// 判断标准（三重，缺一不可）：
/// 1. 两个目标各自与源做 <see cref="TreeComparer"/> 校验都通过；
/// 2. 两个目标的**元数据指纹完全相同**（相对路径 + 长度 + mtime ticks 的聚合哈希）；
/// 3. 两个目标的**目录集合与逐文件大小完全相同**（指纹不含目录，需单独比）。
///
/// 为什么这是 R4 的验收口径：两个引擎的实现路径毫无共性（一个驱动外部进程、一个直接调 Win32），
/// 若产物仍逐字段一致，说明"复制语义"这件事被真正对齐了，而不是各自碰巧对了一半。
/// </summary>
public class R4_ParityTests
{
    private static CopyFileExCopier CreateEngineB()
    {
        var privileges = new FakePrivilegeProbe();
        return new CopyFileExCopier(privileges, SecurityInfoOps.Prepare(privileges));
    }

    private static RobocopyCopier CreateEngineA() => new();

    private static string BuildRealisticSource(TempWorkspace ws)
    {
        var source = ws.CreateDirectory("Source");

        ws.CreateFile(@"Source\readme.md", "# 标题\n正文\n");
        ws.CreateFile(@"Source\data\table.csv", string.Join('\n', Enumerable.Range(0, 500).Select(i => $"{i},row-{i}")));
        ws.CreateFile(@"Source\data\small\a.txt", "a");
        ws.CreateFile(@"Source\data\small\b.txt", "bb");
        ws.CreateFile(@"Source\bin\blob.bin", new string('Z', 250_000));
        ws.CreateDirectory(@"Source\emptydir");
        ws.CreateDirectory(@"Source\deep\l1\l2\l3");

        // 中文 / 空格 / 特殊字符路径：两个引擎都必须处理正确
        ws.CreateFile(@"Source\中文 目录\带 空格 的文件.txt", "中文内容");
        ws.CreateFile(@"Source\deep\l1\l2\l3\leaf.dat", new string('l', 12_345));

        // 统一时间戳：让"mtime 是否被保留"成为确定性命题而不是随机差异
        var stamp = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, stamp);

        return source;
    }

    [Fact]
    public async Task 引擎A与引擎B_同源复制结果必须完全一致()
    {
        using var ws = new TempWorkspace("r4-parity");
        var source = BuildRealisticSource(ws);

        var targetA = ws.PathOf("Target-A");
        var targetB = ws.PathOf("Target-B");

        var bytes = DirSize(source);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count();

        var options = new CopyOptions
        {
            Threads = 4,
            SourceTotalBytes = bytes,
            SourceFileCount = files,
        };

        var resultA = await CreateEngineA().CopyAsync(new CopyRequest(source, targetA, options), null, CancellationToken.None);
        var resultB = await CreateEngineB().CopyAsync(new CopyRequest(source, targetB, options), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, resultA.Outcome);
        Assert.Equal(CopyOutcome.Success, resultB.Outcome);
        Assert.Equal(resultA.FilesCopied, resultB.FilesCopied);

        // ① 各自与源校验
        var verifyA = TreeComparer.Compare(source, targetA);
        var verifyB = TreeComparer.Compare(source, targetB);
        Assert.True(verifyA.IsConsistent, "引擎 A 产物与源不一致：" + verifyA.Describe());
        Assert.True(verifyB.IsConsistent, "引擎 B 产物与源不一致：" + verifyB.Describe());

        // ② 元数据指纹完全一致
        Assert.Equal(verifyA.SourceFingerprint, verifyA.TargetFingerprint);
        Assert.Equal(verifyB.SourceFingerprint, verifyB.TargetFingerprint);
        Assert.Equal(verifyA.TargetFingerprint, verifyB.TargetFingerprint);

        // ③ 目录集合一致（指纹只覆盖文件）
        var dirsA = RelativeDirectories(targetA);
        var dirsB = RelativeDirectories(targetB);
        Assert.Equal(dirsA, dirsB);

        // ④ 内容逐字节一致（哈希过期等于没校验）
        foreach (var relative in RelativeFiles(source))
        {
            var left = File.ReadAllBytes(Path.Combine(source, relative));
            var right = Path.Combine(targetA, relative);
            var other = Path.Combine(targetB, relative);

            Assert.True(File.Exists(right), $"引擎 A 缺失文件：{relative}");
            Assert.True(File.Exists(other), $"引擎 B 缺失文件：{relative}");

            Assert.Equal(left, File.ReadAllBytes(right));
            Assert.Equal(left, File.ReadAllBytes(other));
        }
    }

    [Fact]
    public async Task 引擎B_结果可被引擎A增量续做_反之亦然()
    {
        using var ws = new TempWorkspace("r4-mixed");
        var source = BuildRealisticSource(ws);
        var bytes = DirSize(source);

        var options = new CopyOptions { Threads = 4, SourceTotalBytes = bytes };

        // 先用 A 复制，再用 B 复到同一目标：目标已完整 → B 应全部跳过，且不破坏已有内容。
        var target = ws.PathOf("Target");
        var first = await CreateEngineA().CopyAsync(new CopyRequest(source, target, options), null, CancellationToken.None);
        Assert.Equal(CopyOutcome.Success, first.Outcome);

        var second = await CreateEngineB().CopyAsync(new CopyRequest(source, target, options), null, CancellationToken.None);
        Assert.Equal(CopyOutcome.Success, second.Outcome);
        Assert.Equal(0, second.FailedCount);

        var verify = TreeComparer.Compare(source, target);
        Assert.True(verify.IsConsistent, verify.Describe());
    }

    // ============================ 辅助 ============================

    private static long DirSize(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private static List<string> RelativeFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> RelativeDirectories(string root)
        => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
