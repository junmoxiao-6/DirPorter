using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.FileSystem;

/// <summary>特殊对象类别（PRECHECK 源侧第 5 条需逐项标注风险的对象）。</summary>
public enum SpecialObjectKind
{
    /// <summary>嵌套的 Junction / 目录符号链接（不会被递归复制，否则会成环）。</summary>
    NestedDirectoryReparsePoint,

    /// <summary>嵌套的文件符号链接。</summary>
    NestedFileSymlink,

    /// <summary>硬链接文件（多链接数），复制后会变成独立文件，丢失链接关系。</summary>
    HardLinkFile,

    /// <summary>稀疏文件。</summary>
    SparseFile,

    /// <summary>备用数据流（ADS）。</summary>
    AlternateDataStream,

    /// <summary>EFS 加密文件。</summary>
    EncryptedFile,

    /// <summary>OneDrive 等云占位文件（访问会触发下载）。</summary>
    CloudPlaceholder,

    /// <summary>超长路径（&gt; 260 字符），迁移后部分老程序可能无法访问。</summary>
    LongPath,
}

/// <summary>某一类特殊对象的汇总（含少量样本，避免大目录树把全部条目常驻内存）。</summary>
public sealed record SpecialObjectSummary(SpecialObjectKind Kind, int Count, IReadOnlyList<SpecialObject> Samples);

/// <summary>单个特殊对象。</summary>
public sealed record SpecialObject(SpecialObjectKind Kind, string Path, string Detail, uint FileAttributes);

/// <summary>遍历过程中的错误（无权限等），不中断整体预检。</summary>
public sealed record ProbeError(string Path, string Message, int Win32Code);

/// <summary>目录树探测选项。</summary>
public sealed record TreeProbeOptions
{
    /// <summary>
    /// 是否递归进入重解析点。**恒为 false**（硬约束 /XJ 语义）：
    /// 跟随嵌套 Junction 会形成环或数据膨胀，这是同类工具最经典的致命 bug 之一。
    /// </summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>
    /// 昂贵探测（硬链接数、ADS）的文件数上限。超过后自动降级为"仅逻辑属性探测"，
    /// 并在结果里标记 <see cref="TreeProbeResult.DeepProbeSkippedForScale"/>，绝不假装探测过。
    /// </summary>
    public int DeepProbeFileLimit { get; init; } = 20_000;

    public bool DetectHardLinks { get; init; } = true;

    public bool DetectAlternateDataStreams { get; init; } = true;

    /// <summary>每类特殊对象保留的样本数上限。</summary>
    public int MaxSamplesPerKind { get; init; } = 5;

    public int MaxDepth { get; init; } = 256;

    public static TreeProbeOptions Default { get; } = new();
}

/// <summary>目录树探测结果。</summary>
public sealed record TreeProbeResult(
    string RootPath,
    long FileCount,
    long DirectoryCount,
    long LogicalBytes,
    long DiskBytes,
    long MaxFileBytes,
    IReadOnlyList<SpecialObjectSummary> SpecialObjects,
    IReadOnlyList<ProbeError> Errors,
    bool DeepProbeApplied,
    bool DeepProbeSkippedForScale,
    TimeSpan Elapsed)
{
    public int CountOf(SpecialObjectKind kind)
        => SpecialObjects.FirstOrDefault(s => s.Kind == kind)?.Count ?? 0;

    public bool HasSpecialObject(SpecialObjectKind kind) => CountOf(kind) > 0;
}

public interface ITreeProbe
{
    Task<TreeProbeResult> ProbeAsync(string sourcePath, TreeProbeOptions options, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// 流式目录树探测：一次遍历同时完成
/// ① 文件数/总字节（用于目标空间复检）② 特殊对象标注（PRECHECK 源侧第 5 条）。
///
/// 性能设计：
/// - <c>FindFirstFileExW</c> + <c>FindExInfoBasic</c> + <c>FIND_FIRST_EX_LARGE_FETCH</c>；
/// - 只在内存里保留聚合值与少量样本，绝不全量常驻（10 万文件也不会撑爆内存）；
/// - 磁盘占用按需调用 <c>GetCompressedFileSizeW</c>：仅对压缩/稀疏文件调用，
///   普通文件的"实际占用"与逻辑大小一致，避免每文件多一次系统调用。
/// </summary>
public sealed class TreeProbe : ITreeProbe
{
    public Task<TreeProbeResult> ProbeAsync(string sourcePath, TreeProbeOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Probe(sourcePath, options, progress, cancellationToken), cancellationToken);

    public static TreeProbeResult Probe(string sourcePath, TreeProbeOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var root = PathNormalizer.NormalizeAbsolute(sourcePath);
        var rootPlain = PathNormalizer.StripExtendedPrefix(root);

        var accumulator = new Accumulator(options);
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (directory, depth) = stack.Pop();
            if (depth > options.MaxDepth)
            {
                accumulator.AddError(directory, $"目录层级超过 {options.MaxDepth} 层上限，已停止深入。", 0);
                continue;
            }

            EnumerateDirectory(directory, depth, accumulator, stack, options, progress, cancellationToken);
        }

        started.Stop();

        return new TreeProbeResult(
            RootPath: rootPlain,
            FileCount: accumulator.FileCount,
            DirectoryCount: accumulator.DirectoryCount,
            LogicalBytes: accumulator.LogicalBytes,
            DiskBytes: accumulator.DiskBytes,
            MaxFileBytes: accumulator.MaxFileBytes,
            SpecialObjects: accumulator.BuildSummaries(),
            Errors: accumulator.Errors,
            DeepProbeApplied: accumulator.DeepProbeApplied,
            DeepProbeSkippedForScale: accumulator.DeepProbeSkipped,
            Elapsed: started.Elapsed);
    }

    private static void EnumerateDirectory(
        string directory, int depth, Accumulator accumulator, Stack<(string, int)> stack,
        TreeProbeOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var searchPath = PathNormalizer.EnsureTrailingSeparator(directory) + "*";
        var handle = Kernel32.FindFirstFileEx(
            searchPath, Kernel32.FindExInfoBasic, out var data,
            Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

        if (handle == Kernel32.InvalidHandleValue)
        {
            var code = Win32Error.Last;
            if (code != Kernel32.ErrorFileNotFound)
            {
                accumulator.AddError(PathNormalizer.StripExtendedPrefix(directory), "无法枚举目录内容。", code);
            }
            return;
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
                        accumulator.DirectoryCount++;

                        if (isReparse)
                        {
                            // ⚠️ 不递归进入：跟随嵌套 Junction 会成环 / 数据膨胀（/XJ 语义）。
                            accumulator.AddSpecial(
                                SpecialObjectKind.NestedDirectoryReparsePoint,
                                PathNormalizer.StripExtendedPrefix(childPath),
                                "嵌套的目录链接（Junction / 目录符号链接），迁移时不会被递归复制。",
                                data.dwFileAttributes);
                        }
                        else
                        {
                            stack.Push((childPath, depth + 1));
                        }
                    }
                    else
                    {
                        var size = Kernel32.ToFileSize(data);
                        accumulator.AddFile(childPath, size, data.dwFileAttributes, isReparse, options);
                    }

                    accumulator.CheckLongPath(childPath, data.dwFileAttributes);

                    if (accumulator.FileCount % 2000 == 0) progress?.Report(accumulator.FileCount);
                }

                if (!Kernel32.FindNextFile(handle, out data)) break;
            }
        }
        finally
        {
            Kernel32.FindClose(handle);
        }

        progress?.Report(accumulator.FileCount);
    }

    private sealed class Accumulator
    {
        private readonly TreeProbeOptions _options;
        private readonly Dictionary<SpecialObjectKind, int> _counts = new();
        private readonly Dictionary<SpecialObjectKind, List<SpecialObject>> _samples = new();

        public Accumulator(TreeProbeOptions options) => _options = options;

        public long FileCount { get; private set; }
        public long DirectoryCount { get; set; }
        public long LogicalBytes { get; private set; }
        public long DiskBytes { get; private set; }
        public long MaxFileBytes { get; private set; }
        public bool DeepProbeSkipped { get; private set; }

        /// <summary>是否对**全部**文件都完成了昂贵探测（只要有文件被跳过即为 false）。</summary>
        public bool DeepProbeApplied => FileCount > 0 && !DeepProbeSkipped;
        public List<ProbeError> Errors { get; } = new();

        public void AddError(string path, string message, int win32Code)
        {
            if (Errors.Count >= 200) return;
            Errors.Add(new ProbeError(path, message, win32Code));
        }

        public void AddSpecial(SpecialObjectKind kind, string path, string detail, uint attributes)
        {
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;

            if (!_samples.TryGetValue(kind, out var list))
            {
                list = new List<SpecialObject>();
                _samples[kind] = list;
            }

            if (list.Count < _options.MaxSamplesPerKind)
                list.Add(new SpecialObject(kind, path, detail, attributes));
        }

        public void AddFile(string path, long size, uint attributes, bool isReparsePoint, TreeProbeOptions options)
        {
            FileCount++;
            LogicalBytes += size;
            if (size > MaxFileBytes) MaxFileBytes = size;

            var isCompressed = (attributes & Kernel32.FileAttributeCompressed) != 0;
            var isSparse = (attributes & Kernel32.FileAttributeSparseFile) != 0;

            if (isCompressed || isSparse)
            {
                // 只有压缩/稀疏文件的"实际占用"与逻辑大小不同，才值得多一次系统调用。
                DiskBytes += GetDiskSize(path, size);
            }
            else
            {
                DiskBytes += size;
            }

            if (isReparsePoint)
            {
                AddSpecial(SpecialObjectKind.NestedFileSymlink, PathNormalizer.StripExtendedPrefix(path),
                    "嵌套的文件符号链接（或其它重解析点），复制时会按链接对象处理。", attributes);
            }

            if ((attributes & Kernel32.FileAttributeSparseFile) != 0)
            {
                AddSpecial(SpecialObjectKind.SparseFile, PathNormalizer.StripExtendedPrefix(path),
                    "稀疏文件：逻辑大小远大于实际占用。", attributes);
            }

            if ((attributes & Kernel32.FileAttributeEncrypted) != 0)
            {
                AddSpecial(SpecialObjectKind.EncryptedFile, PathNormalizer.StripExtendedPrefix(path),
                    "EFS 加密文件：跨卷复制行为未实测（TODO-2），建议先干跑验证。", attributes);
            }

            if ((attributes & (Kernel32.FileAttributeRecallOnDataAccess | Kernel32.FileAttributeRecallOnOpen)) != 0)
            {
                AddSpecial(SpecialObjectKind.CloudPlaceholder, PathNormalizer.StripExtendedPrefix(path),
                    "云占位文件：迁移会触发从云端下载，可能产生大量网络流量与等待。", attributes);
            }

            var deepProbeAllowed = FileCount <= options.DeepProbeFileLimit;
            if (!deepProbeAllowed)
            {
                DeepProbeSkipped = true;
                return;
            }

            if (options.DetectHardLinks && TryCountLinks(path, out var links) && links > 1)
            {
                AddSpecial(SpecialObjectKind.HardLinkFile, PathNormalizer.StripExtendedPrefix(path),
                    $"硬链接文件（链接数 {links}）：复制后会变成独立文件，链接关系与服务端去重会丢失。", attributes);
            }

            if (options.DetectAlternateDataStreams && TryDetectAds(path, out var streamCount))
            {
                AddSpecial(SpecialObjectKind.AlternateDataStream, PathNormalizer.StripExtendedPrefix(path),
                    $"存在 {streamCount - 1} 个备用数据流（ADS）：仅 robocopy /COPYALL 能完整保留。", attributes);
            }
        }

        public void CheckLongPath(string path, uint attributes)
        {
            var plain = PathNormalizer.StripExtendedPrefix(path);
            if (plain.Length > 260)
            {
                AddSpecial(SpecialObjectKind.LongPath, plain,
                    $"路径长度 {plain.Length} 字符，超过 260：本工具用 \\\\?\\ 前缀正常处理，但部分老程序可能访问失败。", attributes);
            }
        }

        public IReadOnlyList<SpecialObjectSummary> BuildSummaries()
            => _counts
                .OrderByDescending(static kv => kv.Value)
                .Select(kv => new SpecialObjectSummary(kv.Key, kv.Value, _samples.TryGetValue(kv.Key, out var list) ? list : new List<SpecialObject>()))
                .ToList();

        private static long GetDiskSize(string extendedPath, long logicalSize)
        {
            var low = Kernel32.GetCompressedFileSize(extendedPath, out var high);
            if (low == 0xFFFFFFFF && Win32Error.Last != Kernel32.ErrorSuccess) return logicalSize;
            var value = ((long)high << 32) | low;
            return value < 0 ? logicalSize : value;
        }

        private static bool TryCountLinks(string extendedPath, out uint links)
        {
            links = 0;
            var info = FileInformation.Query(extendedPath);
            if (info is null) return false;
            links = info.Value.nNumberOfLinks;
            return true;
        }

        private static bool TryDetectAds(string extendedPath, out int streamCount)
        {
            streamCount = 0;
            var handle = Kernel32.FindFirstStream(extendedPath, Kernel32.FindStreamInfoStandard, out var data, 0);
            if (handle == Kernel32.InvalidHandleValue)
            {
                // 没有额外数据流时，FindFirstStreamW 可能直接以 ERROR_HANDLE_EOF 失败，也可能是
                // 成功返回默认流 "::$DATA"。两种行为都在此处被正确处理。
                return false;
            }

            try
            {
                streamCount = 1;
                while (Kernel32.FindNextStream(handle, out data))
                {
                    streamCount++;
                    if (streamCount > 64) break;
                }

                return streamCount > 1;
            }
            finally
            {
                Kernel32.FindClose(handle);
            }
        }
    }
}
