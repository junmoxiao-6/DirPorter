using System.Text;
using DirPorter.Core.Copy;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.Privilege;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R4：复制引擎 B（自研 CopyFileEx）的行为验证。
/// 关注点与引擎 A 相同（数据正确 + 元数据保留 + 安全红线），额外覆盖它的差异化能力（暂停 / 字节级进度）。
/// </summary>
public class R4_CopyEngineTests
{
    private static CopyFileExCopier CreateCopier()
    {
        // 刻意用"非提权"环境：既能真实走 DACL 搬运，又不会去动测试宿主进程的令牌。
        var privileges = new FakePrivilegeProbe();
        return new CopyFileExCopier(privileges, SecurityInfoOps.Prepare(privileges));
    }

    private static CopyOptions Options(long bytes, long files) => new()
    {
        Threads = 4,
        SourceTotalBytes = bytes,
        SourceFileCount = files,
    };

    private static string BuildSourceTree(TempWorkspace ws)
    {
        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\a.txt", "alpha");
        ws.CreateFile(@"Source\sub\b.txt", new string('b', 4096));
        ws.CreateFile(@"Source\sub\deep\c.bin", new string('c', 100_000));
        ws.CreateDirectory(@"Source\empty");
        return source;
    }

    // ============================ 基本正确性 ============================

    [Fact]
    public async Task 基本复制_内容与元数据与源一致()
    {
        using var ws = new TempWorkspace("r4-basic");
        var source = BuildSourceTree(ws);
        var target = ws.PathOf("Target");

        var result = await CreateCopier().CopyAsync(
            new CopyRequest(source, target, Options(200_000, 3)), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, result.Outcome);
        Assert.Equal(3, result.FilesCopied);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal("alpha", File.ReadAllText(ws.PathOf(@"Target\a.txt")));
        Assert.True(Directory.Exists(ws.PathOf(@"Target\empty")), "空目录必须被创建（与 /E 语义一致）");

        var verify = TreeComparer.Compare(source, target);
        Assert.True(verify.IsConsistent, verify.Describe());
    }

    [Fact]
    public async Task 空目录与深层目录结构完整复制()
    {
        using var ws = new TempWorkspace("r4-dirs");
        var source = ws.CreateDirectory("Source");
        ws.CreateDirectory(@"Source\A\B\C\D");
        ws.CreateFile(@"Source\A\B\C\D\leaf.txt", "leaf");

        var target = ws.PathOf("Target");
        var result = await CreateCopier().CopyAsync(
            new CopyRequest(source, target, Options(4, 1)), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, result.Outcome);
        Assert.True(File.Exists(ws.PathOf(@"Target\A\B\C\D\leaf.txt")));
    }

    /// <summary>
    /// 安全红线（与 /XJ 同语义）：源目录里的 Junction 必须被跳过，不得进入其目标。
    /// 否则会把被指向的真实数据整体复制一份（数据膨胀），甚至成环。
    /// </summary>
    [Fact]
    public async Task 源内的目录联接被跳过_不进入其目标()
    {
        using var ws = new TempWorkspace("r4-skip-reparse");

        var outside = ws.CreateDirectory("Outside");
        ws.CreateFile(@"Outside\big.bin", new string('x', 300_000));

        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\keep.txt", "keep");

        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(ws.PathOf(@"Source\LinkToOutside"), outside), LinkCreateOptions.Default);

        var target = ws.PathOf("Target");
        var result = await CreateCopier().CopyAsync(
            new CopyRequest(source, target, Options(300_000, 2)), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, result.Outcome);
        Assert.Equal(1, result.FilesCopied);                       // 只复制了 keep.txt
        Assert.False(Directory.Exists(ws.PathOf(@"Target\LinkToOutside")));   // 链接没有被复制过去
        Assert.False(File.Exists(ws.PathOf(@"Target\LinkToOutside\big.bin"))); // 更不可能穿透复制

        // 源侧目标数据毫发无损
        Assert.True(File.Exists(ws.PathOf(@"Outside\big.bin")));
    }

    [Fact]
    public async Task 时间戳与属性被保留()
    {
        using var ws = new TempWorkspace("r4-meta");
        var source = ws.CreateDirectory("Source");
        var file = ws.CreateFile(@"Source\stamped.txt", "stamped");

        var stamp = new DateTime(2024, 3, 5, 8, 30, 15, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var target = ws.PathOf("Target");
        var result = await CreateCopier().CopyAsync(
            new CopyRequest(source, target, Options(7, 1)), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, result.Outcome);

        var copied = ws.PathOf(@"Target\stamped.txt");
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(copied));
        Assert.True(File.GetAttributes(copied).HasFlag(FileAttributes.Hidden));

        // 只读属性会阻碍后面的清理，先摘掉
        File.SetAttributes(copied, FileAttributes.Normal);
        File.SetAttributes(file, FileAttributes.Normal);
    }

    /// <summary>
    /// 实测命题：CopyFileEx 的进度回调带 dwStreamNumber/StreamSize 参数，
    /// 说明它是**逐流复制**的 —— 因此 ADS 应当被保留。本用例把这个猜测变成证据。
    /// </summary>
    [Fact]
    public async Task 备用数据流_ADS_被保留()
    {
        using var ws = new TempWorkspace("r4-ads");
        var source = ws.CreateDirectory("Source");
        var file = ws.CreateFile(@"Source\with-ads.txt", "default-stream-content");

        var streamPath = file + ":secret";
        await File.WriteAllBytesAsync(streamPath, Encoding.UTF8.GetBytes("hidden-payload"));

        var streamsBefore = ListStreams(file);
        Assert.Contains(streamsBefore, s => s.Contains("secret", StringComparison.OrdinalIgnoreCase));

        var target = ws.PathOf("Target");
        var result = await CreateCopier().CopyAsync(
            new CopyRequest(source, target, Options(64, 1)), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, result.Outcome);

        var copied = ws.PathOf(@"Target\with-ads.txt");
        var streamsAfter = ListStreams(copied);

        Assert.Contains(streamsAfter, s => s.Contains("secret", StringComparison.OrdinalIgnoreCase));

        var payload = await File.ReadAllBytesAsync(copied + ":secret");
        Assert.Equal("hidden-payload", Encoding.UTF8.GetString(payload));
    }

    /// <summary>
    /// 稀疏文件：源占用磁盘远小于逻辑大小时，目标也必须保持稀疏，否则磁盘占用会被放大几十倍。
    /// </summary>
    [Fact]
    public async Task 稀疏文件_目标保持稀疏且内容一致()
    {
        using var ws = new TempWorkspace("r4-sparse");
        var source = ws.CreateDirectory("Source");

        const long logicalSize = 8L * 1024 * 1024;
        const int prefixBytes = 256 * 1024;
        var sparseFile = CreateSparseFile(ws.PathOf(@"Source\sparse.bin"), prefixBytes, logicalSize);

        var sourceAllocated = AllocatedSize(sparseFile);

        // 前置条件自检：源确实稀疏（否则本用例没有验证价值，直接失败而不是"假通过"）。
        Assert.True(sourceAllocated < logicalSize / 4,
            $"前置条件不成立：源文件未呈现稀疏（占用 {sourceAllocated} / 逻辑 {logicalSize}）。");

        var target = ws.PathOf("Target");
        var result = await CreateCopier().CopyAsync(
            new CopyRequest(source, target, Options(logicalSize, 1)), null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Success, result.Outcome);

        var copied = ws.PathOf(@"Target\sparse.bin");
        Assert.Equal(logicalSize, new FileInfo(copied).Length);

        var targetAllocated = AllocatedSize(copied);
        Assert.True(targetAllocated < logicalSize / 2,
            $"目标未保持稀疏：占用 {targetAllocated} / 逻辑 {logicalSize}。");
        Assert.True(targetAllocated <= sourceAllocated * 2 + 1024 * 1024,
            $"目标占用量异常偏大：源 {sourceAllocated}、目标 {targetAllocated}。");

        // 内容必须逐字节一致（洞读出来是全零）
        Assert.True(File.ReadAllBytes(sparseFile).AsSpan().SequenceEqual(File.ReadAllBytes(copied)),
            "稀疏复制后内容不一致 —— 这是不可接受的（可能把真实数据置零了）");
    }

    [Fact]
    public void 洞区间计算_覆盖首尾与中间()
    {
        // 分配于 [1MB, 2MB)，逻辑 4MB ⇒ 洞 = [0,1MB) + [2MB,4MB)
        var allocated = new List<(long Start, long Length)> { (1024 * 1024, 1024 * 1024) };
        var holes = SparseFileOps.ComputeHoles(allocated, 4 * 1024 * 1024).ToList();

        Assert.Equal(2, holes.Count);
        Assert.Equal((0L, 1024L * 1024), holes[0]);
        Assert.Equal((2L * 1024 * 1024, 4L * 1024 * 1024), holes[1]);

        // 完全没有分配 ⇒ 整文件都是洞
        Assert.Equal(new[] { (0L, 8L) }, SparseFileOps.ComputeHoles(Array.Empty<(long, long)>(), 8).ToArray());

        // 全部已分配 ⇒ 没有洞
        Assert.Empty(SparseFileOps.ComputeHoles(new[] { (0L, 8L) }, 8));
    }

    // ============================ 暂停 / 继续 / 取消 ============================

    [Fact]
    public async Task 暂停后不再推进_继续后完成()
    {
        using var ws = new TempWorkspace("r4-pause");
        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\payload.bin", new string('p', 2_000_000));

        var copier = CreateCopier();
        copier.Pause.Pause();          // 先挂起，保证行为确定（不受磁盘速度影响）

        var target = ws.PathOf("Target");
        var task = copier.CopyAsync(new CopyRequest(source, target, Options(2_000_000, 1)), null, CancellationToken.None);

        await Task.Delay(400);
        Assert.False(task.IsCompleted, "暂停期间复制不应完成");

        copier.Pause.Resume();
        var result = await task;

        Assert.Equal(CopyOutcome.Success, result.Outcome);
        Assert.Equal(2_000_000, new FileInfo(ws.PathOf(@"Target\payload.bin")).Length);
    }

    [Fact]
    public async Task 取消_返回Canceled而不是抛异常()
    {
        using var ws = new TempWorkspace("r4-cancel");
        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\payload.bin", new string('q', 2_000_000));

        var copier = CreateCopier();
        copier.Pause.Pause();

        using var cts = new CancellationTokenSource();
        var task = copier.CopyAsync(new CopyRequest(source, ws.PathOf("Target"), Options(2_000_000, 1)), null, cts.Token);

        await Task.Delay(200);
        cts.Cancel();

        // 约定：取消以 CopyOutcome.Canceled 返回，由引擎决定回滚；不允许以异常形式逃逸。
        var result = await task;
        Assert.Equal(CopyOutcome.Canceled, result.Outcome);

        copier.Pause.Resume();
    }

    [Fact]
    public void 暂停闸门语义()
    {
        var gate = new PauseGate();
        Assert.False(gate.IsPaused);

        gate.Pause();
        Assert.True(gate.IsPaused);

        // 取消优先于暂停：已取消时不应继续等待
        Assert.False(gate.WaitWhilePaused(() => true));

        gate.Resume();
        Assert.False(gate.IsPaused);
        Assert.True(gate.WaitWhilePaused(() => false));
    }

    [Fact]
    public void 能力声明_如实反映权限环境()
    {
        var standard = new CopyFileExCopier(new FakePrivilegeProbe(), SecurityInfoOps.Prepare(new FakePrivilegeProbe()));
        Assert.True(standard.Capabilities.PreservesAcl);
        Assert.False(standard.Capabilities.PreservesOwnerAndAuditing);   // 非提权：所有者/审计不可复制
        Assert.True(standard.Capabilities.SupportsPauseResume);
        Assert.Contains("字节级", standard.Capabilities.ProgressGranularity);

        var admin = new FakePrivilegeProbe { IsElevated = true, HasSeRestorePrivilege = true, HasSeSecurityPrivilege = true };
        // 注入"启用特权恒成功"，模拟已提权环境（不动测试宿主的进程令牌）
        var capability = SecurityInfoOps.Prepare(admin, _ => true);
        Assert.True(capability.CanCopyOwnerAndGroup);
        Assert.True(capability.CanCopyAuditing);
        Assert.False(capability.IsDegraded);
    }

    // ============================ 辅助 ============================

    private static string CreateSparseFile(string path, int prefixBytes, long logicalSize)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var prefix = new byte[prefixBytes];
        for (var i = 0; i < prefix.Length; i++) prefix[i] = (byte)(i % 251);

        stream.Write(prefix, 0, prefix.Length);
        stream.Flush();

        Assert.True(SparseFileOps.MarkSparse(stream.SafeFileHandle.DangerousGetHandle()),
            "无法把测试文件标记为稀疏，用例前置条件不成立");

        stream.SetLength(logicalSize);
        stream.Flush();

        return path;
    }

    private static long AllocatedSize(string path)
    {
        var low = Kernel32.GetCompressedFileSize(PathNormalizer.NormalizeAbsolute(path), out var high);
        if (low == 0xFFFFFFFF && high == 0xFFFFFFFF)
            throw new IOException($"GetCompressedFileSize 失败：{path}");

        return ((long)high << 32) | low;
    }

    internal static List<string> ListStreams(string path)
    {
        var result = new List<string>();
        var handle = Kernel32.FindFirstStream(
            PathNormalizer.NormalizeAbsolute(path), Kernel32.FindStreamInfoStandard, out var data, 0);

        if (handle == Kernel32.InvalidHandleValue) return result;

        try
        {
            result.Add(Kernel32.GetStreamName(data));
            while (Kernel32.FindNextStream(handle, out data)) result.Add(Kernel32.GetStreamName(data));
        }
        finally
        {
            Kernel32.FindClose(handle);
        }

        return result;
    }
}
