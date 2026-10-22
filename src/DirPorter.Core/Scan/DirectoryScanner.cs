using System.Collections.Concurrent;
using System.Diagnostics;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Scan;

/// <summary>
/// 低内存并行目录树扫描器。
///
/// 与 <see cref="TreeProbe"/> 的关系（**刻意保留两份独立实现**）：
/// TreeProbe 是 PRECHECK / 迁移引擎用的探测（深度优先、单线程，语义已被 R2/R3 大量用例锁定）；
/// 本扫描器面向"先看清规模"的场景（层序并行、可截断、可收集条目）。
/// 两者实现路径不同，因此可以**互为对照**——R5 的对账用例正是拿它们比同源结果，
/// 而不是把其中一个改成另一个的薄封装（那样就失去了交叉验证的价值）。
///
/// 性能设计：
/// <list type="number">
/// <item><b>层序并行 BFS</b>：一次只把"当前层的目录"摊在内存里并行处理，
/// 内存是 O(最宽一层的目录数)，与总条目数无关；</item>
/// <item><b>聚合而非收集</b>：默认只累加计数/字节，不保留条目（<see cref="ScanOptions.CollectEntries"/> 才收集）；</item>
/// <item><b>单次系统调用拿完整条目</b>：<c>FindFirstFileExW</c> + <c>FindExInfoBasic</c> + <c>FIND_FIRST_EX_LARGE_FETCH</c>；
/// 只有压缩/稀疏文件才多调一次 <c>GetCompressedFileSizeW</c>；相对路径每目录只算一次，不逐文件 <c>GetRelativePath</c>；</item>
/// <item><b>昂贵探测有预算</b>：硬链接数/ADS 代价高，超过上限即降级并如实上报。</item>
/// </list>
///
/// 铁律：**绝不深入重解析点**（与 <c>robocopy /XJ</c> 同语义）。
/// </summary>
public static class DirectoryScanner
{
    private const int MaxRecordedErrors = 200;

    public static ScanSummary Scan(
        string rootPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Scan(rootPath, options, progress, cancellationToken, collector: null);

    /// <summary>
    /// 扫描目录树。
    /// <paramref name="collector"/> 由调用方提供时用于接收每个条目（**必须是线程安全的**）。
    /// </summary>
    public static ScanSummary Scan(
        string rootPath,
        ScanOptions? options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        Action<ScanEntry>? collector)
    {
        options ??= ScanOptions.Default;

        var started = Stopwatch.StartNew();
        var root = PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(rootPath));
        var rootPlain = PathNormalizer.StripExtendedPrefix(root);

        if (!ReparsePointOps.Inspect(root).PathExists)
        {
            started.Stop();
            return new ScanSummary(
                rootPlain, 0, 0, 0, 0, 0, 0, null,
                new Dictionary<string, long>(), Array.Empty<SpecialObjectSummary>(),
                new[] { new ProbeError(rootPlain, "路径不存在。", Kernel32.ErrorFileNotFound) },
                null, false, false, false, started.Elapsed);
        }

        var stats = new ScanStats();
        var errors = new ConcurrentQueue<ProbeError>();
        var droppedErrors = 0;
        var specials = new SpecialTracker(options);
        var collectEntries = options.CollectEntries || collector is not null;
        var entries = collectEntries ? new ConcurrentQueue<ScanEntry>() : null;

        var budget = new EntryBudget(options.MaxEntries);
        var gate = new DeepProbeGate(options.DeepProbeFileLimit);
        var sink = new EntrySink(stats, specials, entries, options, budget, collector);

        var threads = ResolveThreads(options.Threads);
        var directoriesScanned = 0;
        var frontier = new List<(string Path, int Depth)> { (root, 0) };

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.IsExhausted) break;

            directoriesScanned += frontier.Count;
            progress?.Report(new ScanProgress(directoriesScanned, stats.FileCount, stats.LogicalBytes, rootPlain));

            var next = new ConcurrentQueue<List<(string Path, int Depth)>>();

            if (threads <= 1)
            {
                foreach (var item in frontier)
                {
                    var children = new List<(string, int)>();
                    EnumerateDirectory(item.Path, item.Depth, children);
                    next.Enqueue(children);
                }
            }
            else
            {
                Parallel.ForEach(
                    frontier,
                    new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken },
                    item =>
                    {
                        var children = new List<(string, int)>();
                        EnumerateDirectory(item.Path, item.Depth, children);
                        next.Enqueue(children);
                    });
            }

            var following = new List<(string Path, int Depth)>();
            foreach (var bag in next) following.AddRange(bag);
            frontier = following;
        }

        started.Stop();

        progress?.Report(new ScanProgress(directoriesScanned, stats.FileCount, stats.LogicalBytes, rootPlain));

        return new ScanSummary(
            RootPath: rootPlain,
            FileCount: stats.FileCount,
            DirectoryCount: stats.DirectoryCount,
            ReparsePointCount: stats.ReparsePointCount,
            LogicalBytes: stats.LogicalBytes,
            DiskBytes: stats.DiskBytes,
            MaxFileBytes: stats.MaxFileBytes,
            MaxFilePath: stats.MaxFilePath,
            Extensions: stats.ExtensionsSnapshot(),
            SpecialObjects: specials.Build(),
            Errors: errors.ToArray(),
            Entries: entries?.ToArray(),
            DeepProbeApplied: stats.FileCount > 0 && !stats.DeepProbeSkipped,
            DeepProbeSkippedForScale: stats.DeepProbeSkipped,
            Truncated: budget.IsExhausted,
            Elapsed: started.Elapsed);

        // ---------------- 局部函数 ----------------

        void EnumerateDirectory(string directory, int depth, List<(string, int)> children)
        {
            if (depth >= options.MaxDepth)
            {
                Record(new ProbeError(PathNormalizer.StripExtendedPrefix(directory),
                    $"目录层级达到 {options.MaxDepth} 层上限，已停止深入。", 0));
                return;
            }

            // 相对目录只算一次：逐文件调用 Path.GetRelativePath 在 10 万文件时是明显的性能黑洞。
            var relativeDirectory = Path.GetRelativePath(rootPlain, PathNormalizer.StripExtendedPrefix(directory));
            if (relativeDirectory == ".") relativeDirectory = string.Empty;

            var searchPath = PathNormalizer.EnsureTrailingSeparator(directory) + "*";
            var handle = Kernel32.FindFirstFileEx(
                searchPath, Kernel32.FindExInfoBasic, out var data,
                Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

            if (handle == Kernel32.InvalidHandleValue)
            {
                var code = Win32Error.Last;
                if (code is not (Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound))
                {
                    Record(new ProbeError(PathNormalizer.StripExtendedPrefix(directory), "无法枚举目录内容。", code));
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
                        var relative = relativeDirectory.Length == 0 ? name : Path.Combine(relativeDirectory, name);
                        var attributes = data.dwFileAttributes;
                        var isDirectory = (attributes & Kernel32.FileAttributeDirectory) != 0;
                        var isReparse = (attributes & Kernel32.FileAttributeReparsePoint) != 0;

                        if (isReparse)
                        {
                            // ⚠️ 铁律：不深入重解析点。
                            sink.AddReparsePoint(relative, childPath, attributes, isDirectory);
                        }
                        else if (isDirectory)
                        {
                            sink.AddDirectory(relative, childPath, attributes);
                            children.Add((childPath, depth + 1));
                        }
                        else
                        {
                            sink.AddFile(relative, childPath, Kernel32.ToFileSize(data), attributes,
                                ToTicks(data.ftCreationTime), ToTicks(data.ftLastWriteTime), gate);
                        }
                    }

                    if (!Kernel32.FindNextFile(handle, out data)) break;
                    if (budget.IsExhausted) break;
                }
            }
            finally
            {
                Kernel32.FindClose(handle);
            }
        }

        void Record(ProbeError error)
        {
            if (errors.Count >= MaxRecordedErrors)
            {
                if (Interlocked.Increment(ref droppedErrors) == 1)
                {
                    errors.Enqueue(new ProbeError(error.Path,
                        $"后续遍历错误已省略（单次扫描最多记录 {MaxRecordedErrors} 条）。", error.Win32Code));
                }

                return;
            }

            errors.Enqueue(error);
        }
    }

    private static int ResolveThreads(int requested)
    {
        if (requested > 0) return Math.Clamp(requested, 1, 128);

        // 目录枚举是 I/O 密集：线程数超过逻辑核数收益迅速递减；
        // 上限 16 是为了避免在机械盘 / 网络路径上把 I/O 队列打爆反而变慢。
        return Math.Clamp(Environment.ProcessorCount, 1, 16);
    }

    private static long ToTicks(Kernel32.FileTime time) => ((long)time.dwHighDateTime << 32) | time.dwLowDateTime;

    // ==================================================================
    //  内部协作类型
    // ==================================================================

    /// <summary>条目预算：达到上限后停止深入并标记截断。</summary>
    private sealed class EntryBudget
    {
        private readonly long _max;
        private long _seen;

        public EntryBudget(long max) => _max = max;

        public bool IsExhausted => _max > 0 && Interlocked.Read(ref _seen) >= _max;

        public void Count() => Interlocked.Increment(ref _seen);
    }

    /// <summary>昂贵探测的共享预算（跨线程只放行前 N 个文件）。</summary>
    private sealed class DeepProbeGate
    {
        private readonly int _limit;
        private long _seen;

        public DeepProbeGate(int limit) => _limit = limit;

        public bool TryEnter() => _limit > 0 && Interlocked.Increment(ref _seen) <= _limit;
    }

    /// <summary>把单个条目落到统计器 / 样本 / 条目集合上。</summary>
    private sealed class EntrySink
    {
        private readonly ScanStats _stats;
        private readonly SpecialTracker _specials;
        private readonly ConcurrentQueue<ScanEntry>? _entries;
        private readonly ScanOptions _options;
        private readonly EntryBudget _budget;
        private readonly Action<ScanEntry>? _collector;

        public EntrySink(
            ScanStats stats, SpecialTracker specials, ConcurrentQueue<ScanEntry>? entries,
            ScanOptions options, EntryBudget budget, Action<ScanEntry>? collector)
        {
            _stats = stats;
            _specials = specials;
            _entries = entries;
            _options = options;
            _budget = budget;
            _collector = collector;
        }

        public void AddDirectory(string relative, string fullPath, uint attributes)
        {
            _stats.AddDirectory();
            _budget.Count();
            CheckLongPath(fullPath, attributes);
            Emit(new ScanEntry(ScanEntryKind.Directory, relative, 0, 0, attributes, 0, 0));
        }

        public void AddReparsePoint(string relative, string fullPath, uint attributes, bool isDirectory)
        {
            _stats.AddReparsePoint();
            _budget.Count();

            var detail = isDirectory
                ? "嵌套的目录链接（Junction / 目录符号链接），迁移时不会被递归复制。"
                : "嵌套的文件符号链接（或其它重解析点），复制时会按链接对象处理。";

            _specials.Add(
                isDirectory ? SpecialObjectKind.NestedDirectoryReparsePoint : SpecialObjectKind.NestedFileSymlink,
                PathNormalizer.StripExtendedPrefix(fullPath), detail, attributes);

            CheckLongPath(fullPath, attributes);
            Emit(new ScanEntry(ScanEntryKind.ReparsePoint, relative, 0, 0, attributes, 0, 0));
        }

        public void AddFile(
            string relative, string fullPath, long size, uint attributes,
            long creationTicks, long lastWriteTicks, DeepProbeGate gate)
        {
            var isCompressed = (attributes & Kernel32.FileAttributeCompressed) != 0;
            var isSparse = (attributes & Kernel32.FileAttributeSparseFile) != 0;
            var diskBytes = isCompressed || isSparse ? DiskSize(fullPath, size) : size;

            _stats.AddFile(relative, size, diskBytes);
            _stats.AddExtension(Path.GetExtension(relative), _options.MaxExtensionBuckets);
            _budget.Count();

            if (_options.DetectSpecialObjects)
            {
                DetectDeclaredSpecials(fullPath, attributes, isSparse);

                if (gate.TryEnter())
                {
                    ProbeExpensiveObjects(fullPath, attributes);
                }
                else
                {
                    // 超出预算：如实标记"没探测全"，不假装探测过。
                    _stats.MarkDeepProbeSkipped();
                }
            }

            CheckLongPath(fullPath, attributes);
            Emit(new ScanEntry(ScanEntryKind.File, relative, size, diskBytes, attributes, creationTicks, lastWriteTicks));
        }

        private void DetectDeclaredSpecials(string fullPath, uint attributes, bool isSparse)
        {
            if (isSparse)
            {
                _specials.Add(SpecialObjectKind.SparseFile, PathNormalizer.StripExtendedPrefix(fullPath),
                    "稀疏文件：逻辑大小远大于实际占用。", attributes);
            }

            if ((attributes & Kernel32.FileAttributeEncrypted) != 0)
            {
                _specials.Add(SpecialObjectKind.EncryptedFile, PathNormalizer.StripExtendedPrefix(fullPath),
                    "EFS 加密文件：跨卷复制行为未实测（TODO-2），建议先干跑验证。", attributes);
            }

            if ((attributes & (Kernel32.FileAttributeRecallOnDataAccess | Kernel32.FileAttributeRecallOnOpen)) != 0)
            {
                _specials.Add(SpecialObjectKind.CloudPlaceholder, PathNormalizer.StripExtendedPrefix(fullPath),
                    "云占位文件：迁移会触发从云端下载，可能产生大量网络流量与等待。", attributes);
            }
        }

        private void ProbeExpensiveObjects(string fullPath, uint attributes)
        {
            if (_options.DetectHardLinks && TryCountLinks(fullPath, out var links) && links > 1)
            {
                _specials.Add(SpecialObjectKind.HardLinkFile, PathNormalizer.StripExtendedPrefix(fullPath),
                    $"硬链接文件（链接数 {links}）：复制后会变成独立文件，链接关系与服务端去重会丢失。", attributes);
            }

            if (_options.DetectAlternateDataStreams && TryDetectAds(fullPath, out var streamCount))
            {
                _specials.Add(SpecialObjectKind.AlternateDataStream, PathNormalizer.StripExtendedPrefix(fullPath),
                    $"存在 {streamCount - 1} 个备用数据流（ADS）：仅 robocopy /COPYALL 能完整保留。", attributes);
            }
        }

        private void Emit(ScanEntry entry)
        {
            if (_entries is null && _collector is null) return;

            _entries?.Enqueue(entry);
            _collector?.Invoke(entry);
        }

        private void CheckLongPath(string fullPath, uint attributes)
        {
            if (!_options.DetectSpecialObjects) return;

            var plain = PathNormalizer.StripExtendedPrefix(fullPath);
            if (plain.Length > 260)
            {
                _specials.Add(SpecialObjectKind.LongPath, plain,
                    $"路径长度 {plain.Length} 字符，超过 260：本工具用 \\\\?\\ 前缀正常处理，但部分老程序可能访问失败。",
                    attributes);
            }
        }

        private static long DiskSize(string extendedPath, long logicalSize)
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
            if (handle == Kernel32.InvalidHandleValue) return false;

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

    /// <summary>特殊对象汇总（计数 + 有界样本）。</summary>
    private sealed class SpecialTracker
    {
        private readonly int _maxSamples;
        private readonly object _lock = new();
        private readonly Dictionary<SpecialObjectKind, int> _counts = new();
        private readonly Dictionary<SpecialObjectKind, List<SpecialObject>> _samples = new();

        public SpecialTracker(ScanOptions options) => _maxSamples = options.MaxSamplesPerKind;

        public void Add(SpecialObjectKind kind, string path, string detail, uint attributes)
        {
            lock (_lock)
            {
                _counts[kind] = _counts.GetValueOrDefault(kind) + 1;

                if (!_samples.TryGetValue(kind, out var list))
                {
                    list = new List<SpecialObject>();
                    _samples[kind] = list;
                }

                if (list.Count < _maxSamples)
                {
                    list.Add(new SpecialObject(kind, path, detail, attributes));
                }
            }
        }

        public IReadOnlyList<SpecialObjectSummary> Build()
        {
            lock (_lock)
            {
                return _counts
                    .OrderByDescending(static kv => kv.Value)
                    .Select(kv => new SpecialObjectSummary(
                        kv.Key, kv.Value,
                        _samples.TryGetValue(kv.Key, out var list) ? list : new List<SpecialObject>()))
                    .ToList();
            }
        }
    }
}
