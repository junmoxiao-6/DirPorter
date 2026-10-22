using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Scan;

/// <summary>扫描条目类别。</summary>
public enum ScanEntryKind
{
    /// <summary>普通文件。</summary>
    File = 0,

    /// <summary>普通目录（会被深入）。</summary>
    Directory = 1,

    /// <summary>重解析点（Junction / 符号链接 / 其它）。**不会被深入**（/XJ 语义）。</summary>
    ReparsePoint = 2,
}

/// <summary>
/// 一个扫描条目。
///
/// 刻意做成 <c>readonly record struct</c>：10 万条目时结构体数组远小于对象数组，
/// 且内含的字符串是唯一的大头 —— 因此**默认不收集条目**，只在调用方明确需要时才收集。
/// </summary>
public readonly record struct ScanEntry(
    ScanEntryKind Kind,
    string RelativePath,
    long Size,
    long DiskBytes,
    uint Attributes,
    long CreationTicks,
    long LastWriteTicks)
{
    public bool IsDirectory => (Attributes & Kernel32.FileAttributeDirectory) != 0;
    public bool IsReparsePoint => Kind == ScanEntryKind.ReparsePoint;
}

/// <summary>扫描选项。</summary>
public sealed record ScanOptions
{
    /// <summary>
    /// 是否跟随重解析点。**恒为 false**（硬约束，与 robocopy <c>/XJ</c> 同语义）。
    /// 字段保留是为了在代码里显式表达"这里不做那件事"，而不是遗漏。
    /// </summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>最大递归深度。</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>
    /// 条目预算。&gt;0 时超过预算即停止深入，并在结果里置 <see cref="ScanSummary.Truncated"/>。
    /// 用于"只想快速看看规模"的场景，**绝不用于迁移决策**。
    /// </summary>
    public long MaxEntries { get; init; }

    /// <summary>并行度。0 = 自动（处理器数，上限 16）；1 = 单线程（顺序确定，便于对照测试）。</summary>
    public int Threads { get; init; }

    /// <summary>是否做昂贵探测（硬链接数、ADS）。</summary>
    public bool DetectSpecialObjects { get; init; } = true;

    /// <summary>
    /// 昂贵探测的文件数上限。超过后自动降级并置
    /// <see cref="ScanSummary.DeepProbeSkippedForScale"/>，如实告知"没探测全"。
    /// </summary>
    public int DeepProbeFileLimit { get; init; } = 20_000;

    public bool DetectHardLinks { get; init; } = true;

    public bool DetectAlternateDataStreams { get; init; } = true;

    /// <summary>每类特殊对象保留的样本数上限（避免大目录树把样本堆满内存）。</summary>
    public int MaxSamplesPerKind { get; init; } = 5;

    /// <summary>扩展名统计的桶上限（超出的归入 "(其它)"，防止恶意/异常文件名撑爆内存）。</summary>
    public int MaxExtensionBuckets { get; init; } = 512;

    /// <summary>是否收集全部条目（内存 O(n)）。默认 false。</summary>
    public bool CollectEntries { get; init; }

    public static ScanOptions Default { get; } = new();
}

/// <summary>扫描进度快照。</summary>
public sealed record ScanProgress(
    int DirectoriesScanned,
    long FilesFound,
    long BytesFound,
    string CurrentDirectory);

/// <summary>
/// 扫描过程中的可变统计器（多线程安全）。
///
/// ⚠️ 这是唯一在扫描期间持续存在的可变状态，且它的内存占用是 **O(1)**：
/// 只存聚合值与有界的样本/扩展名桶，不存条目本身。
/// 这正是"10 万文件峰值内存 &lt;300MB"的关键 —— 内存不随文件数增长。
/// </summary>
public sealed class ScanStats
{
    private long _files;
    private long _directories;
    private long _reparsePoints;
    private long _logicalBytes;
    private long _diskBytes;
    private long _maxFileBytes;
    private long _deepProbeSkipped;

    private readonly object _lock = new();
    private string? _maxFilePath;
    private readonly Dictionary<string, long> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private long _extensionOverflow;

    public long FileCount => Interlocked.Read(ref _files);
    public long DirectoryCount => Interlocked.Read(ref _directories);
    public long ReparsePointCount => Interlocked.Read(ref _reparsePoints);
    public long LogicalBytes => Interlocked.Read(ref _logicalBytes);
    public long DiskBytes => Interlocked.Read(ref _diskBytes);
    public long MaxFileBytes => Interlocked.Read(ref _maxFileBytes);

    public string? MaxFilePath
    {
        get { lock (_lock) return _maxFilePath; }
    }

    /// <summary>是否因文件数超过上限而跳过了部分昂贵探测。</summary>
    public bool DeepProbeSkipped => Interlocked.Read(ref _deepProbeSkipped) != 0;

    internal void MarkDeepProbeSkipped() => Interlocked.Exchange(ref _deepProbeSkipped, 1);

    internal void AddDirectory() => Interlocked.Increment(ref _directories);

    internal void AddReparsePoint() => Interlocked.Increment(ref _reparsePoints);

    internal void AddFile(string relativePath, long size, long diskBytes)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _logicalBytes, size);
        Interlocked.Add(ref _diskBytes, diskBytes);

        var isBigger = false;
        var current = Interlocked.Read(ref _maxFileBytes);
        while (size > current)
        {
            var previous = Interlocked.CompareExchange(ref _maxFileBytes, size, current);
            if (previous == current) { isBigger = true; break; }
            current = previous;
        }

        if (!isBigger) return;

        lock (_lock) _maxFilePath = relativePath;
    }

    internal void AddExtension(string? extension, int maxBuckets)
    {
        var key = string.IsNullOrEmpty(extension) ? "(无扩展名)" : extension.ToLowerInvariant();

        lock (_lock)
        {
            if (_extensions.TryGetValue(key, out var count))
            {
                _extensions[key] = count + 1;
                return;
            }

            if (_extensions.Count >= maxBuckets)
            {
                _extensionOverflow++;
                return;
            }

            _extensions[key] = 1;
        }
    }

    public IReadOnlyDictionary<string, long> ExtensionsSnapshot()
    {
        lock (_lock)
        {
            var copy = new Dictionary<string, long>(_extensions, StringComparer.OrdinalIgnoreCase);
            if (_extensionOverflow > 0) copy["(其它)"] = _extensionOverflow;
            return copy;
        }
    }
}

/// <summary>扫描结果（不可变快照）。</summary>
public sealed record ScanSummary(
    string RootPath,
    long FileCount,
    long DirectoryCount,
    long ReparsePointCount,
    long LogicalBytes,
    long DiskBytes,
    long MaxFileBytes,
    string? MaxFilePath,
    IReadOnlyDictionary<string, long> Extensions,
    IReadOnlyList<SpecialObjectSummary> SpecialObjects,
    IReadOnlyList<ProbeError> Errors,
    IReadOnlyList<ScanEntry>? Entries,
    bool DeepProbeApplied,
    bool DeepProbeSkippedForScale,
    bool Truncated,
    TimeSpan Elapsed)
{
    /// <summary>目录数是否包含根目录本身：**不包含**（与 <c>dir /s /b /ad</c> 口径一致）。</summary>
    public bool DirectoryCountExcludesRoot => true;

    public int CountOf(SpecialObjectKind kind)
        => SpecialObjects.FirstOrDefault(s => s.Kind == kind)?.Count ?? 0;

    public bool HasSpecialObject(SpecialObjectKind kind) => CountOf(kind) > 0;

    public bool IsClean => Errors.Count == 0 && !Truncated;

    public string Describe()
    {
        var lines = new List<string>
        {
            $"{RootPath}：{FileCount} 个文件 / {DirectoryCount} 个子目录 / {SpaceRequirement.Format(LogicalBytes)}"
            + $"（实际占用 {SpaceRequirement.Format(DiskBytes)}），耗时 {Elapsed.TotalSeconds:0.00}s。",
        };

        if (MaxFilePath is not null)
            lines.Add($"  最大文件：{MaxFilePath}（{SpaceRequirement.Format(MaxFileBytes)}）");

        if (ReparsePointCount > 0)
            lines.Add($"  重解析点 {ReparsePointCount} 个（已跳过，不深入）");

        if (SpecialObjects.Count > 0)
        {
            lines.Add("  特殊对象：" + string.Join("、",
                SpecialObjects.Select(s => $"{Describe(s.Kind)}×{s.Count}")));
        }

        if (DeepProbeSkippedForScale)
            lines.Add($"  注意：文件数超过深入探测上限（{FileCount}），硬链接/ADS 未逐文件检查。");

        if (Truncated)
            lines.Add("  注意：达到条目预算被截断，结果**不完整**，不能作为迁移依据。");

        if (Errors.Count > 0)
            lines.Add($"  遍历错误 {Errors.Count} 项，例：{Errors[0].Path} — {Errors[0].Message}");

        return string.Join(Environment.NewLine, lines);
    }

    public static string Describe(SpecialObjectKind kind) => kind switch
    {
        SpecialObjectKind.NestedDirectoryReparsePoint => "嵌套目录链接",
        SpecialObjectKind.NestedFileSymlink => "嵌套文件链接",
        SpecialObjectKind.HardLinkFile => "硬链接文件",
        SpecialObjectKind.SparseFile => "稀疏文件",
        SpecialObjectKind.AlternateDataStream => "备用数据流",
        SpecialObjectKind.EncryptedFile => "EFS 加密文件",
        SpecialObjectKind.CloudPlaceholder => "云占位文件",
        SpecialObjectKind.LongPath => "超长路径",
        _ => kind.ToString(),
    };
}
