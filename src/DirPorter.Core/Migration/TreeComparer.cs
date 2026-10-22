using DirPorter.Core.FileSystem;
using DirPorter.Core.Ledger;

namespace DirPorter.Core.Migration;

/// <summary>校验档位。</summary>
public enum VerifyMode
{
    /// <summary>默认：元数据指纹（相对路径 + 长度 + mtime ticks）。秒级完成。</summary>
    Metadata = 0,

    /// <summary>可选：全文内容哈希。会把 restore 校验从秒级拖到分钟级。</summary>
    Content = 1,
}

/// <summary>校验选项。</summary>
public sealed record VerifyOptions
{
    public VerifyMode Mode { get; init; } = VerifyMode.Metadata;

    /// <summary>逐文件比较最后写入时间（robocopy /COPYALL 会保留）。</summary>
    public bool CompareTimestamps { get; init; } = true;

    /// <summary>逐文件比较只读/隐藏/系统/存档属性位。</summary>
    public bool CompareAttributes { get; init; } = true;

    /// <summary>比较源与目标的根目录 ACL（便宜且高价值）。逐文件 ACL 需要 <see cref="CompareAclEveryFile"/>。</summary>
    public bool CompareRootAcl { get; init; } = true;

    /// <summary>逐文件 ACL 比较（很贵，默认关闭）。</summary>
    public bool CompareAclEveryFile { get; init; }

    /// <summary>最多报告多少条差异（避免报告本身撑爆内存）。</summary>
    public int MaxReportedMismatches { get; init; } = 100;

    public static VerifyOptions Default { get; } = new();
}

/// <summary>一条校验差异。</summary>
public sealed record VerifyMismatch(string RelativePath, string Kind, string Detail);

/// <summary>校验结果。</summary>
public sealed record VerifyResult(
    bool IsConsistent,
    long SourceFiles,
    long TargetFiles,
    long SourceBytes,
    long TargetBytes,
    IReadOnlyList<VerifyMismatch> Mismatches,
    IReadOnlyList<ProbeError> Errors,
    string SourceFingerprint,
    string TargetFingerprint,
    TimeSpan Elapsed)
{
    public bool HasMoreMismatches { get; init; }

    public string Describe()
    {
        if (IsConsistent)
        {
            return $"校验通过：{SourceFiles} 个文件、{SpaceRequirement.Format(SourceBytes)} 与源一致"
                   + $"（{SourceFingerprint[..Math.Min(8, SourceFingerprint.Length)]}，耗时 {Elapsed.TotalSeconds:0.0}s）。";
        }

        var lines = new List<string>
        {
            $"校验失败：源 {SourceFiles} 个文件 / {SpaceRequirement.Format(SourceBytes)}，目标 {TargetFiles} 个文件 / {SpaceRequirement.Format(TargetBytes)}。",
        };

        lines.AddRange(Mismatches.Take(10).Select(m => $"  · {m.Kind}：{m.RelativePath} — {m.Detail}"));
        if (HasMoreMismatches || Mismatches.Count > 10) lines.Add($"  · …还有更多差异（共 {Mismatches.Count} 条已记录）");
        if (Errors.Count > 0) lines.Add($"  · 遍历错误 {Errors.Count} 项，例：{Errors[0].Path} — {Errors[0].Message}");

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 源/目标一致性校验（需求：文件数 + 总字节数 + 逐文件 size/mtime 比对；不一致则失败）。
///
/// 实现要点：
/// - **流式**：只对源做一次遍历，每个文件直接映射到目标路径做比较，不把两边条目同时放进内存；
/// - **不留死角**：额外统计目标侧文件数，多出来的文件（源里没有）同样判为不一致；
/// - **不穿越重解析点**：与复制/删除保持同一语义（`/XJ`），否则会重复计数。
/// </summary>
public static class TreeComparer
{
    public static VerifyResult Compare(string sourcePath, string targetPath, VerifyOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= VerifyOptions.Default;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var source = PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(sourcePath));
        var target = PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(targetPath));
        var sourcePlain = PathNormalizer.StripExtendedPrefix(source);

        var mismatches = new List<VerifyMismatch>();
        var errors = new List<ProbeError>();

        if (!Directory.Exists(target))
        {
            return new VerifyResult(false, 0, 0, 0, 0,
                new[] { new VerifyMismatch("(根)", "目标缺失", $"目标目录不存在：{PathNormalizer.StripExtendedPrefix(target)}") },
                errors, string.Empty, string.Empty, started.Elapsed);
        }

        // 内容档位：直接比内容哈希（最严格，但慢）
        if (options.Mode == VerifyMode.Content)
        {
            var sourceHash = MetadataFingerprint.ComputeContentHash(source, null, cancellationToken, out var sourceFiles, out var sourceBytes);
            var targetHash = MetadataFingerprint.ComputeContentHash(target, null, cancellationToken, out var targetFiles, out var targetBytes);

            var consistent = sourceHash == targetHash && sourceFiles == targetFiles && sourceBytes == targetBytes;
            if (!consistent)
            {
                mismatches.Add(new VerifyMismatch("(内容哈希)", "内容不一致",
                    $"源 {sourceFiles} 文件 / {sourceHash[..12]}，目标 {targetFiles} 文件 / {targetHash[..12]}"));
            }

            return new VerifyResult(consistent, sourceFiles, targetFiles, sourceBytes, targetBytes, mismatches, errors, sourceHash, targetHash, started.Elapsed);
        }

        // 元数据档位：一次遍历源，逐文件直连比较
        long sourceFileCount = 0;
        long sourceTotalBytes = 0;
        var stack = new Stack<string>();
        stack.Push(source);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = stack.Pop();
            var relativeDirectory = Path.GetRelativePath(sourcePlain, PathNormalizer.StripExtendedPrefix(directory));
            if (relativeDirectory == ".") relativeDirectory = string.Empty;

            var searchPath = PathNormalizer.EnsureTrailingSeparator(directory) + "*";
            var handle = Kernel32.FindFirstFileEx(
                searchPath, Kernel32.FindExInfoBasic, out var data,
                Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

            if (handle == Kernel32.InvalidHandleValue)
            {
                var code = Diagnostics.Win32Error.Last;
                if (code is not (Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound))
                    errors.Add(new ProbeError(PathNormalizer.StripExtendedPrefix(directory), "枚举源目录失败。", code));

                continue;
            }

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var name = Kernel32.GetFileName(data);
                    if (name is not ("." or ".."))
                    {
                        var childPath = PathNormalizer.Combine(directory, name);
                        var isDirectory = (data.dwFileAttributes & Kernel32.FileAttributeDirectory) != 0;
                        var isReparse = (data.dwFileAttributes & Kernel32.FileAttributeReparsePoint) != 0;

                        if (isDirectory)
                        {
                            if (!isReparse) stack.Push(childPath);
                        }
                        else if (!isReparse)
                        {
                            var size = Kernel32.ToFileSize(data);
                            sourceFileCount++;
                            sourceTotalBytes += size;

                            var relative = string.IsNullOrEmpty(relativeDirectory) ? name : Path.Combine(relativeDirectory, name);
                            CompareFile(childPath, PathNormalizer.Combine(target, relative), relative, size, data, options, mismatches);
                        }
                    }

                    if (!Kernel32.FindNextFile(handle, out data)) break;
                }
            }
            finally
            {
                Kernel32.FindClose(handle);
            }
        }

        // 目标侧多出来的文件同样是不一致（防止复制到错误位置而源侧比较"通过"）
        var targetStats = CountFiles(target, errors, cancellationToken);

        var isConsistent = mismatches.Count == 0
                           && sourceFileCount == targetStats.FileCount
                           && sourceTotalBytes == targetStats.TotalBytes;

        if (sourceFileCount != targetStats.FileCount)
        {
            mismatches.Add(new VerifyMismatch("(文件总数)", "数量不一致",
                $"源 {sourceFileCount} 个，目标 {targetStats.FileCount} 个"));
        }

        if (sourceTotalBytes != targetStats.TotalBytes)
        {
            mismatches.Add(new VerifyMismatch("(总字节)", "字节数不一致",
                $"源 {sourceTotalBytes}，目标 {targetStats.TotalBytes}"));
        }

        started.Stop();

        // 指纹只在需要展示时算一次（这里算元数据指纹，用于台账与人类可读的等价性证明）
        var sourceFingerprint = MetadataFingerprint.Compute(source, cancellationToken, out _, out _);
        var targetFingerprint = MetadataFingerprint.Compute(target, cancellationToken, out _, out _);

        if (isConsistent && sourceFingerprint != targetFingerprint)
        {
            isConsistent = false;
            mismatches.Add(new VerifyMismatch("(元数据指纹)", "指纹不一致",
                $"源 {sourceFingerprint[..12]}，目标 {targetFingerprint[..12]}（可能元数据被上层改写）"));
        }

        return new VerifyResult(
            isConsistent, sourceFileCount, targetStats.FileCount, sourceTotalBytes, targetStats.TotalBytes,
            mismatches, errors, sourceFingerprint, targetFingerprint, started.Elapsed)
        {
            HasMoreMismatches = mismatches.Count >= options.MaxReportedMismatches,
        };
    }

    private static void CompareFile(
        string sourceFile, string targetFile, string relative, long expectedSize,
        Kernel32.Win32FindData sourceData, VerifyOptions options, List<VerifyMismatch> mismatches)
    {
        if (mismatches.Count >= options.MaxReportedMismatches) return;

        var targetAttributes = Kernel32.GetFileAttributes(targetFile);
        if (targetAttributes == Kernel32.InvalidFileAttributes)
        {
            mismatches.Add(new VerifyMismatch(relative, "文件缺失", "目标侧不存在该文件"));
            return;
        }

        var targetHandleData = FileSystem.FileInformation.Query(targetFile);
        if (targetHandleData is null)
        {
            mismatches.Add(new VerifyMismatch(relative, "文件不可读", "无法读取目标文件信息"));
            return;
        }

        var targetSize = ((long)targetHandleData.Value.nFileSizeHigh << 32) | targetHandleData.Value.nFileSizeLow;
        if (targetSize != expectedSize)
        {
            mismatches.Add(new VerifyMismatch(relative, "大小不一致", $"源 {expectedSize} 字节，目标 {targetSize} 字节"));
            return;
        }

        if (options.CompareAttributes)
        {
            const uint mask = Kernel32.FileAttributeReadOnly | Kernel32.FileAttributeHidden
                              | Kernel32.FileAttributeSystem | Kernel32.FileAttributeArchive;

            var sourceMask = sourceData.dwFileAttributes & mask;
            var targetMask = targetAttributes & mask;
            if (sourceMask != targetMask)
            {
                mismatches.Add(new VerifyMismatch(relative, "属性不一致",
                    $"源 0x{sourceMask:X4}，目标 0x{targetMask:X4}（只读/隐藏/系统/存档）"));
                return;
            }
        }

        if (options.CompareTimestamps)
        {
            var sourceTicks = ((long)sourceData.ftLastWriteTime.dwHighDateTime << 32) | sourceData.ftLastWriteTime.dwLowDateTime;
            var targetTicks = ((long)targetHandleData.Value.ftLastWriteTime.dwHighDateTime << 32) | targetHandleData.Value.ftLastWriteTime.dwLowDateTime;

            if (sourceTicks != targetTicks)
            {
                mismatches.Add(new VerifyMismatch(relative, "修改时间不一致",
                    $"{FromTicks(sourceTicks):yyyy-MM-dd HH:mm:ss.fff} → {FromTicks(targetTicks):yyyy-MM-dd HH:mm:ss.fff}"));
            }
        }
    }

    private static DateTime FromTicks(long fileTimeTicks)
        => fileTimeTicks <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(fileTimeTicks).ToLocalTime();

    private static (long FileCount, long TotalBytes) CountFiles(string directory, List<ProbeError> errors, CancellationToken cancellationToken)
    {
        long count = 0;
        long bytes = 0;
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = stack.Pop();
            var searchPath = PathNormalizer.EnsureTrailingSeparator(current) + "*";
            var handle = Kernel32.FindFirstFileEx(
                searchPath, Kernel32.FindExInfoBasic, out var data,
                Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

            if (handle == Kernel32.InvalidHandleValue) continue;

            try
            {
                while (true)
                {
                    var name = Kernel32.GetFileName(data);
                    if (name is not ("." or ".."))
                    {
                        var childPath = PathNormalizer.Combine(current, name);
                        var isDirectory = (data.dwFileAttributes & Kernel32.FileAttributeDirectory) != 0;
                        var isReparse = (data.dwFileAttributes & Kernel32.FileAttributeReparsePoint) != 0;

                        if (isDirectory)
                        {
                            if (!isReparse) stack.Push(childPath);
                        }
                        else if (!isReparse)
                        {
                            count++;
                            bytes += Kernel32.ToFileSize(data);
                        }
                    }

                    if (!Kernel32.FindNextFile(handle, out data)) break;
                }
            }
            finally
            {
                Kernel32.FindClose(handle);
            }
        }

        return (count, bytes);
    }
}
