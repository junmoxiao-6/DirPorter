using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.FileSystem;

/// <summary>稀疏复制的结果。</summary>
public readonly record struct SparseReplicationResult(
    bool Applied,
    long ZeroedBytes,
    int SkippedForSafety,
    string? Note)
{
    public static SparseReplicationResult NotSparse { get; } = new(false, 0, 0, null);

    public static SparseReplicationResult Skipped(string note) => new(false, 0, 0, note);
}

/// <summary>
/// 稀疏文件复制（复制引擎 B 的元数据可选项）。
///
/// 为什么不能只调 <c>CopyFileEx</c> 了事：<c>CopyFileEx</c> 会把稀疏文件的"洞"也写成真实数据，
/// 目标文件因此丧失稀疏性 —— 对于 2GB 只占 10MB 的稀疏文件，这会把磁盘占用放大 200 倍。
///
/// 做法（**面向"绝不损坏数据"设计**）：
/// <list type="number">
/// <item>先让 <c>CopyFileEx</c> 完成一次完整字节复制 —— 此时目标已是源的正确副本；</item>
/// <item>按源的 <c>FSCTL_QUERY_ALLOCATED_RANGES</c> 求出"源上未分配（即洞）"的区间；</item>
/// <item>对每个洞区间，**先读目标对应内容确认为全零**，确认通过才 <c>FSCTL_SET_ZERO_DATA</c>。</item>
/// </list>
///
/// 第 3 步是安全阀：即使范围解析出任何偏差，也**不可能把真实数据抹成零**——
/// 凡是含非零字节的区域一律跳过并计入 <see cref="SparseReplicationResult.SkippedForSafety"/>。
/// 这是"宁可少省点空间，也绝不冒数据损坏风险"的取舍。
/// </summary>
public static class SparseFileOps
{
    private const int OutputBufferSize = 64 * 1024;
    private const int VerifyChunkSize = 64 * 1024;

    /// <summary>该路径是否带稀疏属性。</summary>
    public static bool IsSparse(string extendedPath)
    {
        var attributes = Kernel32.GetFileAttributes(extendedPath);
        return attributes != Kernel32.InvalidFileAttributes
               && (attributes & Kernel32.FileAttributeSparseFile) != 0;
    }

    /// <summary>
    /// 在目标文件已完整复制之后，把稀疏性补上。失败一律降级（返回 Note），不抛异常。
    /// </summary>
    public static SparseReplicationResult Replicate(string sourceExtended, string destExtended, long fileSize, CancellationToken cancellationToken)
    {
        if (fileSize <= 0) return SparseReplicationResult.NotSparse;

        var sourceHandle = Kernel32.InvalidHandleValue;
        var destHandle = Kernel32.InvalidHandleValue;

        try
        {
            sourceHandle = OpenForRead(sourceExtended);
            if (sourceHandle == Kernel32.InvalidHandleValue)
                return SparseReplicationResult.Skipped($"无法打开源文件读取分配范围：{Win32Error.Describe(Win32Error.Last)}");

            destHandle = OpenForWrite(destExtended);
            if (destHandle == Kernel32.InvalidHandleValue)
                return SparseReplicationResult.Skipped($"无法打开目标文件写入稀疏信息：{Win32Error.Describe(Win32Error.Last)}");

            var allocated = QueryAllocatedRanges(sourceHandle, fileSize, cancellationToken);
            if (allocated is null)
                return SparseReplicationResult.Skipped("查询源分配范围失败，跳过稀疏优化（目标保持完整副本）。");

            if (!MarkSparse(destHandle))
                return SparseReplicationResult.Skipped($"标记稀疏失败：{Win32Error.Describe(Win32Error.Last)}（目标保持完整副本）。");

            long zeroed = 0;
            var skipped = 0;

            foreach (var (start, end) in ComputeHoles(allocated, fileSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsRegionAllZero(destExtended, start, end - start, cancellationToken))
                {
                    skipped++;
                    continue;
                }

                if (ZeroRange(destHandle, start, end))
                {
                    zeroed += end - start;
                }
                else
                {
                    skipped++;
                }
            }

            var note = skipped > 0
                ? $"稀疏优化完成，但保守跳过了 {skipped} 个区间（内容非全零或写入失败），这些区间仍占实际空间。"
                : null;

            return new SparseReplicationResult(true, zeroed, skipped, note);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SparseReplicationResult.Skipped($"稀疏优化异常，已跳过（数据不受影响）：{ex.Message}");
        }
        finally
        {
            if (sourceHandle != Kernel32.InvalidHandleValue) Kernel32.CloseHandle(sourceHandle);
            if (destHandle != Kernel32.InvalidHandleValue) Kernel32.CloseHandle(destHandle);
        }
    }

    private static IntPtr OpenForRead(string extendedPath)
        => Kernel32.CreateFile(
            extendedPath,
            Kernel32.GenericRead,
            Kernel32.FileShareRead | Kernel32.FileShareWrite | Kernel32.FileShareDelete,
            IntPtr.Zero,
            Kernel32.OpenExisting,
            0,
            IntPtr.Zero);

    private static IntPtr OpenForWrite(string extendedPath)
        => Kernel32.CreateFile(
            extendedPath,
            Kernel32.GenericRead | Kernel32.GenericWrite,
            Kernel32.FileShareRead | Kernel32.FileShareWrite | Kernel32.FileShareDelete,
            IntPtr.Zero,
            Kernel32.OpenExisting,
            0,
            IntPtr.Zero);

    /// <summary>查询 [0, fileSize) 内**实际占用磁盘**的区间（迭代处理 ERROR_MORE_DATA）。</summary>
    private static unsafe List<(long Start, long Length)>? QueryAllocatedRanges(IntPtr handle, long fileSize, CancellationToken cancellationToken)
    {
        var ranges = new List<(long, long)>();
        var input = new Kernel32.FileAllocatedRangeBuffer { FileOffset = 0, Length = fileSize };

        var inputBuffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.FileAllocatedRangeBuffer>());
        var outputBuffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(OutputBufferSize);

        try
        {
            while (input.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                System.Runtime.InteropServices.Marshal.StructureToPtr(input, inputBuffer, fDeleteOld: false);

                var ok = Kernel32.DeviceIoControl(
                    handle, Kernel32.FsctlQueryAllocatedRanges,
                    inputBuffer, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.FileAllocatedRangeBuffer>(),
                    outputBuffer, OutputBufferSize, out var returned, IntPtr.Zero);

                var error = Win32Error.Last;
                if (!ok && error != Kernel32.ErrorMoreData) return null;

                var count = (int)(returned / System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.FileAllocatedRangeBuffer>());
                if (count == 0) break;

                for (var i = 0; i < count; i++)
                {
                    var item = System.Runtime.InteropServices.Marshal.PtrToStructure<Kernel32.FileAllocatedRangeBuffer>(
                        outputBuffer + i * System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.FileAllocatedRangeBuffer>());
                    ranges.Add((item.FileOffset, item.Length));
                }

                var last = ranges[^1];
                var nextOffset = last.Item1 + last.Item2;
                if (nextOffset <= input.FileOffset) break;   // 防御：不前进就退出，避免死循环

                input = new Kernel32.FileAllocatedRangeBuffer { FileOffset = nextOffset, Length = fileSize - nextOffset };
            }

            return ranges;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(inputBuffer);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(outputBuffer);
        }
    }

    /// <summary>求 [0, fileSize) 中未被分配（即"洞"）的区间。</summary>
    internal static IEnumerable<(long Start, long End)> ComputeHoles(IReadOnlyList<(long Start, long Length)> allocated, long fileSize)
    {
        var cursor = 0L;

        foreach (var (start, length) in allocated.OrderBy(static r => r.Start))
        {
            var clampedStart = Math.Clamp(start, 0, fileSize);
            if (clampedStart > cursor) yield return (cursor, clampedStart);

            cursor = Math.Max(cursor, Math.Clamp(start + length, 0, fileSize));
        }

        if (cursor < fileSize) yield return (cursor, fileSize);
    }

    /// <summary>把句柄对应的文件标记为稀疏（测试与复制流程共用）。</summary>
    internal static bool MarkSparse(IntPtr handle)
    {
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(byte));
        try
        {
            System.Runtime.InteropServices.Marshal.WriteByte(buffer, 1);
            return Kernel32.DeviceIoControl(
                handle, Kernel32.FsctlSetSparse, buffer, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ZeroRange(IntPtr handle, long start, long end)
    {
        var size = System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.FileZeroDataInformation>();
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            var info = new Kernel32.FileZeroDataInformation { FileOffset = start, BeyondFinalZero = end };
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            return Kernel32.DeviceIoControl(
                handle, Kernel32.FsctlSetZeroData, buffer, (uint)size, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>安全阀：读取目标区间，确认全部为 0 才允许置零。</summary>
    private static bool IsRegionAllZero(string extendedPath, long start, long length, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(extendedPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, VerifyChunkSize, FileOptions.None);

        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[VerifyChunkSize];
        var remaining = length;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var want = (int)Math.Min(VerifyChunkSize, remaining);
            var read = stream.Read(buffer, 0, want);
            if (read <= 0) return false;

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != 0) return false;
            }

            remaining -= read;
        }

        return true;
    }
}
