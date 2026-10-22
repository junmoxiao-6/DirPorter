using System.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using DirPorter.Core.Scan;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R5：低内存并行扫描器。
///
/// 除了"数得对"，本组用例刻意包含两类**对账**：
/// ① 与另一份独立实现（<see cref="TreeProbe"/>）比同源结果；
/// ② 与系统自带 <c>dir /s</c> 比条目数 —— 不靠自证，靠外部口径印证。
/// </summary>
public class R5_ScanTests
{
    private static string BuildTree(TempWorkspace ws)
    {
        var root = ws.CreateDirectory("Tree");
        ws.CreateFile(@"Tree\a.txt", "alpha");
        ws.CreateFile(@"Tree\b.log", new string('b', 1000));
        ws.CreateFile(@"Tree\sub\c.txt", new string('c', 2000));
        ws.CreateFile(@"Tree\sub\deep\d.txt", new string('d', 4000));
        ws.CreateFile(@"Tree\sub\deep\e.txt", "e");
        ws.CreateDirectory(@"Tree\empty");
        return root;
    }

    [Fact]
    public void 计数与字节_与文件系统实际一致()
    {
        using var ws = new TempWorkspace("r5-scan-basic");
        var root = BuildTree(ws);

        var summary = DirectoryScanner.Scan(root, new ScanOptions { Threads = 1 });

        Assert.Equal(5, summary.FileCount);
        Assert.Equal(0, summary.ReparsePointCount);
        Assert.False(summary.Truncated);
        Assert.True(summary.IsClean, summary.Describe());

        // 目录数不含根：Tree\sub、Tree\sub\deep、Tree\empty 共 3 个
        Assert.Equal(3, summary.DirectoryCount);

        // 字节数逐项核对：5 + 1000 + 2000 + 4000 + 1
        Assert.Equal(7006, summary.LogicalBytes);
        Assert.Equal(7006, summary.DiskBytes);                    // 无压缩/稀疏时两者一致
        Assert.Equal(4000, summary.MaxFileBytes);
        Assert.EndsWith(@"sub\deep\d.txt", summary.MaxFilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 与TreeProbe独立实现_同源结果必须一致()
    {
        using var ws = new TempWorkspace("r5-scan-parity");
        var root = BuildTree(ws);

        var scanned = DirectoryScanner.Scan(root, new ScanOptions { Threads = 4 });
        var probed = TreeProbe.Probe(root, TreeProbeOptions.Default, null, CancellationToken.None);

        Assert.Equal(probed.FileCount, scanned.FileCount);
        Assert.Equal(probed.DirectoryCount, scanned.DirectoryCount);
        Assert.Equal(probed.LogicalBytes, scanned.LogicalBytes);
        Assert.Equal(probed.DiskBytes, scanned.DiskBytes);
        Assert.Equal(probed.MaxFileBytes, scanned.MaxFileBytes);
    }

    [Fact]
    public void 与系统dir命令对账_条目数一致()
    {
        using var ws = new TempWorkspace("r5-scan-dir");
        var root = BuildTree(ws);

        var summary = DirectoryScanner.Scan(root, new ScanOptions { Threads = 1 });

        var filesFromDir = CountDirLines(root, "/s /b /a-d");
        var dirsFromDir = CountDirLines(root, "/s /b /ad");

        Assert.Equal(filesFromDir, summary.FileCount);
        Assert.Equal(dirsFromDir, summary.DirectoryCount);
    }

    [Fact]
    public void 不深入重解析点_但计入重解析点数量()
    {
        using var ws = new TempWorkspace("r5-scan-reparse");
        var outside = ws.CreateDirectory("Outside");
        ws.CreateFile(@"Outside\big.bin", new string('x', 500_000));

        var root = ws.CreateDirectory("Tree");
        ws.CreateFile(@"Tree\keep.txt", "keep");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(ws.PathOf(@"Tree\Link"), outside), LinkCreateOptions.Default);

        var summary = DirectoryScanner.Scan(root, new ScanOptions { Threads = 1 });

        Assert.Equal(1, summary.FileCount);            // 只看到 keep.txt
        Assert.Equal(0, summary.DirectoryCount);       // 联接不计入子目录数
        Assert.Equal(1, summary.ReparsePointCount);
        Assert.Equal(4, summary.LogicalBytes);         // keep.txt 的 4 字节；联接下那 500KB 完全没进来

        Assert.True(summary.HasSpecialObject(SpecialObjectKind.NestedDirectoryReparsePoint));
        Assert.Equal(500_000, new FileInfo(ws.PathOf(@"Outside\big.bin")).Length);   // 目标数据未被触碰
    }

    [Fact]
    public void 空目录树_计数全零且无错误()
    {
        using var ws = new TempWorkspace("r5-scan-empty");
        var root = ws.CreateDirectory("Empty");

        var summary = DirectoryScanner.Scan(root);

        Assert.Equal(0, summary.FileCount);
        Assert.Equal(0, summary.DirectoryCount);
        Assert.Equal(0, summary.LogicalBytes);
        Assert.Empty(summary.Errors);
        Assert.False(summary.Truncated);
    }

    [Fact]
    public void 路径不存在_报错且不抛异常()
    {
        using var ws = new TempWorkspace("r5-scan-missing");

        var summary = DirectoryScanner.Scan(ws.PathOf("不存在"));

        Assert.Equal(0, summary.FileCount);
        Assert.NotEmpty(summary.Errors);
        Assert.False(summary.IsClean);
    }

    [Fact]
    public void 条目预算_触发截断并如实标记()
    {
        using var ws = new TempWorkspace("r5-scan-budget");
        var root = ws.CreateDirectory("Big");
        for (var i = 0; i < 20; i++) ws.CreateFile($@"Big\f{i}.txt", "x");

        var summary = DirectoryScanner.Scan(root, new ScanOptions { MaxEntries = 10, Threads = 1 });

        Assert.True(summary.Truncated);                       // 必须明确告知"不完整"
        Assert.True(summary.FileCount + summary.DirectoryCount >= 10);
        Assert.False(summary.IsClean);
    }

    [Fact]
    public void 收集条目_数量与类别对得上()
    {
        using var ws = new TempWorkspace("r5-scan-entries");
        var root = BuildTree(ws);

        var summary = DirectoryScanner.Scan(root, new ScanOptions { CollectEntries = true, Threads = 2 });

        Assert.NotNull(summary.Entries);
        Assert.Equal(summary.FileCount + summary.DirectoryCount + summary.ReparsePointCount, summary.Entries!.Count);
        Assert.Equal(summary.FileCount, summary.Entries.Count(static e => e.Kind == ScanEntryKind.File));
        Assert.Contains(summary.Entries, e => e.RelativePath.EndsWith(@"sub\deep\d.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 并行与单线程_结果一致()
    {
        using var ws = new TempWorkspace("r5-scan-threads");
        var root = BuildTree(ws);

        var single = DirectoryScanner.Scan(root, new ScanOptions { Threads = 1 });
        var multi = DirectoryScanner.Scan(root, new ScanOptions { Threads = 8 });

        Assert.Equal(single.FileCount, multi.FileCount);
        Assert.Equal(single.DirectoryCount, multi.DirectoryCount);
        Assert.Equal(single.LogicalBytes, multi.LogicalBytes);
        Assert.Equal(single.DiskBytes, multi.DiskBytes);
    }

    [Fact]
    public void 稀疏与ADS被标注为特殊对象()
    {
        using var ws = new TempWorkspace("r5-scan-special");
        var root = ws.CreateDirectory("Tree");

        var sparse = ws.CreateFile(@"Tree\sparse.bin", "seed");
        using (var stream = new FileStream(sparse, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Flush();
            Assert.True(SparseFileOps.MarkSparse(stream.SafeFileHandle.DangerousGetHandle()));
            stream.SetLength(4L * 1024 * 1024);
        }

        var ads = ws.CreateFile(@"Tree\ads.txt", "main");
        File.WriteAllText(ads + ":secret", "hidden");

        var summary = DirectoryScanner.Scan(root, new ScanOptions { Threads = 1 });

        Assert.True(summary.HasSpecialObject(SpecialObjectKind.SparseFile));
        Assert.True(summary.HasSpecialObject(SpecialObjectKind.AlternateDataStream));

        // 稀疏文件的实际占用必须小于逻辑大小
        Assert.True(summary.DiskBytes < summary.LogicalBytes,
            $"DiskBytes={summary.DiskBytes} LogicalBytes={summary.LogicalBytes}");
    }

    [Fact]
    public void 扩展名统计_覆盖所有文件()
    {
        using var ws = new TempWorkspace("r5-scan-ext");
        var root = ws.CreateDirectory("Tree");
        ws.CreateFile(@"Tree\a.txt", "1");
        ws.CreateFile(@"Tree\b.txt", "2");
        ws.CreateFile(@"Tree\c.log", "3");
        ws.CreateFile(@"Tree\noext", "4");

        var summary = DirectoryScanner.Scan(root, new ScanOptions { Threads = 1 });

        Assert.Equal(2, summary.Extensions[".txt"]);
        Assert.Equal(1, summary.Extensions[".log"]);
        Assert.Equal(1, summary.Extensions["(无扩展名)"]);
    }

    [Fact]
    public void 深目录结构_不受默认深度上限影响()
    {
        using var ws = new TempWorkspace("r5-scan-deep");
        var root = ws.CreateDirectory("Tree");
        var path = root;
        for (var i = 0; i < 12; i++) path = Directory.CreateDirectory(Path.Combine(path, $"d{i}")).FullName;
        File.WriteAllText(Path.Combine(path, "leaf.txt"), "leaf");

        var summary = DirectoryScanner.Scan(root, new ScanOptions { Threads = 4 });

        Assert.Equal(1, summary.FileCount);
        Assert.Equal(12, summary.DirectoryCount);
        Assert.Empty(summary.Errors);
    }

    [Fact]
    public void 取消_及时抛出()
    {
        using var ws = new TempWorkspace("r5-scan-cancel");
        var root = ws.CreateDirectory("Tree");
        // 只造少量文件：本用例验证的是"取消信号被及时响应"，不依赖文件数量
        for (var i = 0; i < 25; i++) ws.CreateFile($@"Tree\f{i}.txt", "x");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => DirectoryScanner.Scan(root, new ScanOptions { Threads = 1 }, null, cts.Token));
    }

    // ============================ 辅助 ============================

    private static long CountDirLines(string directory, string switches)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("dir");
        foreach (var part in switches.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            startInfo.ArgumentList.Add(part);
        startInfo.ArgumentList.Add(directory);

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(static line => line.Trim().Length > 0);
    }
}
