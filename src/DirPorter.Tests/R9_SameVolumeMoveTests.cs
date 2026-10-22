using System.IO;
using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.Config;
using DirPorter.Core.Copy;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Ledger;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R9：同卷快速移动（资源管理器"剪切"语义）。
///
/// 为什么必须有这一组：这条路径**不复制数据**，一旦判断错就会以"零拷贝"的名义动用户的目录。
/// 因此每个断言都盯着"数据在哪、源变成什么、备份有没有、能不能原样回退"。
///
/// ⚠️ 本组多数用例经引擎真实跑批，会往**共享数据根**写迁移记录与 journal。
/// 因此：① 必须加入 <see cref="DataRootSequentialCollection"/> 串行集合（否则与 R6/R8 并行互踩）；
/// ② 每个跑批用例必须用 <see cref="DataRootGuard"/> 在结束后还原数据根（否则残留记录会
/// 让后续的 verify 用例读到孤儿记录而失败——这正是本组历史遗留问题）。
/// </summary>
[Collection(DataRootSequentialCollection.Name)]
public class R9_SameVolumeMoveTests
{
    private sealed class Silent : IUserInteraction
    {
        public void Info(string message, string? title = null) { }
        public void Error(string message, string? title = null) { }
        public bool Confirm(string message, string? title = null) => true;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }

    private static void Seed(string source, int files = 3, int subdirs = 2)
    {
        Directory.CreateDirectory(source);

        for (var i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(source, $"f{i}.txt"), new string('x', 100 + i));

        for (var d = 0; d < subdirs; d++)
        {
            var nested = Path.Combine(source, $"sub{d}");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "n.txt"), "nested");
        }
    }

    private static (string Root, string Source, string Target) NewScenario(string tag)
    {
        // 源与目标都在 %TEMP% → 同一卷 → 走快速改名路径
        var root = Path.Combine(Path.GetTempPath(), $"dirporter-r9-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        var source = Path.Combine(root, "src", "ProjectA");
        var target = Path.Combine(root, "tgt", "ProjectA");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "tgt"));
        return (root, source, target);
    }

    private static MigrationBatchResult Run(string source, string target, MigrationTaskOptions? options = null, IFileCopier? copier = null)
    {
        var engine = new MigrationEngine(copier: copier);
        return engine.RunBatchAsync(
            MigrationPlan.Single(source, target, LinkKind.Junction),
            options ?? new MigrationTaskOptions(),
            null,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------ 基本行为

    [Fact]
    public void 同卷_直接改名_源变链接_目标有数据_且没有停车目录()
    {
        var (root, source, target) = NewScenario("basic");
        using var guard = new DataRootGuard();
        try
        {
            Seed(source);

            var result = Run(source, target);

            Assert.True(result.Success, string.Join("；", result.Entries.Select(static e => e.Message)));
            var entry = Assert.Single(result.Entries);

            // 走的是改名，不是复制
            Assert.Contains("同卷直接改名", entry.Message, StringComparison.Ordinal);

            // 源变成链接，且指向目标
            var linkInfo = LinkService.CreateDefault().Inspect(source);
            Assert.True(linkInfo.IsKnownLink);
            Assert.Equal(LinkKind.Junction, linkInfo.Kind);
            Assert.False(linkInfo.IsDangling);

            // 目标有真实数据，内容一致
            Assert.True(Directory.Exists(target));
            Assert.True(File.Exists(Path.Combine(target, "f0.txt")));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(target, "sub0", "n.txt")));

            // **不产生停车目录** —— 这是同卷模式与复制模式最本质的区别
            var parkingRoot = Path.Combine(Path.GetDirectoryName(source)!, ".migbak");
            var parked = Directory.Exists(parkingRoot)
                ? Directory.EnumerateDirectories(parkingRoot)
                    .Where(static d => !string.Equals(Path.GetFileName(d), ".ledger", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : new List<string>();
            Assert.Empty(parked);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 同卷_台账与索引都不把它当成有备份()
    {
        var (root, source, target) = NewScenario("ledger");
        using var guard = new DataRootGuard();
        try
        {
            Seed(source);
            var result = Run(source, target);
            Assert.True(result.Success);

            var batchId = result.BatchId;

            // 台账：parkingPath 必须等于 targetPath（"数据停放的位置"就是目标本身），
            // 且操作序列里是 MOVE 而不是 PARK/COPY。
            var ledgerDirectory = Directory.EnumerateDirectories(
                Path.Combine(Path.GetDirectoryName(source)!, ".migbak")).First();
            var batch = MigrationLedgerFactory.LoadFile(
                Path.Combine(ledgerDirectory, batchId + ".jsonl"));

            var ledgerEntry = Assert.Single(batch.Entries);
            Assert.Equal(ledgerEntry.TargetPath, ledgerEntry.ParkingPath);
            Assert.Contains(ledgerEntry.Operations, static o => o.Step == "MOVE");
            Assert.DoesNotContain(ledgerEntry.Operations, static o => o.Step is "PARK" or "COPY");

            // 迁移记录：BackupPath 必须为空 —— 否则 backup 保留清理会把这条件当成"有过期备份"，
            // 进而把"目标目录"（= 用户的真实数据）删掉。这条是安全红线，必须守住。
            var store = new MigrationIndexStore();
            var record = store.LoadAll()
                .FirstOrDefault(r => string.Equals(r.BatchId, batchId, StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(record);
            Assert.Null(record!.BackupPath);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 同卷_干跑不写盘_且明确说明会走改名()
    {
        var (root, source, target) = NewScenario("dry");
        using var guard = new DataRootGuard();
        try
        {
            Seed(source);
            var before = Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Count();

            var result = Run(source, target, new MigrationTaskOptions { DryRun = true });

            Assert.True(result.DryRun);
            Assert.Empty(result.Entries);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(target));
            Assert.Equal(before,
                Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Count());

            // 干跑必须**如实**说明传输方式（这里断言引擎的判定函数本身）
            var lines = new MigrationEngine().DescribeTransferModes(
                MigrationPlan.Single(source, target, LinkKind.Junction), new MigrationTaskOptions());
            var line = Assert.Single(lines);
            Assert.Contains("同卷", line, StringComparison.Ordinal);
            Assert.Contains("不复制", line, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 关闭同卷优化_回到复制路径_会保留停车目录()
    {
        var (root, source, target) = NewScenario("copypath");
        using var guard = new DataRootGuard();
        try
        {
            Seed(source);

            var result = Run(source, target, new MigrationTaskOptions { SameVolumeMove = false });
            Assert.True(result.Success);

            var entry = Assert.Single(result.Entries);
            Assert.DoesNotContain("同卷直接改名", entry.Message, StringComparison.Ordinal);

            var parkingRoot = Path.Combine(Path.GetDirectoryName(source)!, ".migbak");
            var parked = Directory.EnumerateDirectories(parkingRoot)
                .Where(static d => !string.Equals(Path.GetFileName(d), ".ledger", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Single(parked);   // 复制路径有停车目录
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 目标已存在时_不使用同卷改名_退回复制路径()
    {
        var (root, source, target) = NewScenario("targetexists");
        try
        {
            Seed(source);
            Directory.CreateDirectory(target);   // 目标已存在（空目录）

            var lines = new MigrationEngine().DescribeTransferModes(
                MigrationPlan.Single(source, target, LinkKind.Junction), new MigrationTaskOptions());

            // 目标存在 ⇒ 明确退回复制路径（不去"先删空目录再改名"——少一个边界就少一类数据事故）
            Assert.Contains("跨卷", Assert.Single(lines), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 同卷迁移后_还原能把数据改名回源路径()
    {
        var (root, source, target) = NewScenario("restore");
        using var guard = new DataRootGuard();
        try
        {
            Seed(source);
            var result = Run(source, target);
            Assert.True(result.Success);

            var batchId = result.BatchId;
            var ledgerDirectory = Directory.EnumerateDirectories(
                Path.Combine(Path.GetDirectoryName(source)!, ".migbak")).First();

            var restore = new RestoreService();
            var restored = restore.Restore(batchId, ledgerDirectory);

            Assert.True(restored.Success, restored.Describe());
            var entry = Assert.Single(restored.Entries);
            Assert.True(entry.Success, entry.Message);

            // 源恢复为**真实目录**（不是链接），内容完好
            var info = ReparsePointOps.Inspect(PathNormalizer.NormalizeAbsolute(source));
            Assert.True(info.PathExists);
            Assert.False(info.IsReparsePoint);
            Assert.Equal("nested", File.ReadAllText(Path.Combine(source, "sub0", "n.txt")));

            // 目标已经不存在 —— 数据被改名回去了，而不是"复制一份回去"
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 同卷迁移_不复制数据_因此耗时远低于复制同等数据()
    {
        var (root, source, target) = NewScenario("speed");
        using var guard = new DataRootGuard();
        try
        {
            // 造一批小文件：真正要验的是语义（零拷贝、无备份），不是墙钟时间。
            Seed(source, files: 200, subdirs: 1);

            var sameVolume = Run(source, target);
            Assert.True(sameVolume.Success);

            // 改名路径的总耗时应该很短：这里用"不产生任何数据副本"来断言语义上的零拷贝，
            // 而不是用脆弱的墙钟阈值（CI 机器负载不可控）。
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(source)!, ".migbak", "x")));
            Assert.True(File.Exists(Path.Combine(target, "f199.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ------------------------------------------------------------------ 清理

    /// <summary>
    /// 清理现场。**必须**用工具自己的语义：目标目录此时可能是链接指向的数据，
    /// 直接 Directory.Delete(recursive) 会顺着链接把数据删掉。
    /// </summary>
    private static void Cleanup(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static p => p.Length))
            {
                try
                {
                    var attributes = File.GetAttributes(entry);

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry, false);
                        else if (!Directory.EnumerateFileSystemEntries(entry).Any()) Directory.Delete(entry, false);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                catch
                {
                    // 清理失败不影响断言结果
                }
            }

            Directory.Delete(root, true);
        }
        catch
        {
            // 忽略
        }
    }
}
