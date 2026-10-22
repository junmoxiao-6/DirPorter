using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DirPorter.Core.Config;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Scan;

/// <summary>缓存条目的新鲜度判定结果。</summary>
public enum ScanCacheFreshness
{
    /// <summary>命中且目录未变。</summary>
    Fresh = 0,

    /// <summary>没有缓存条目。</summary>
    Missing = 1,

    /// <summary>有缓存条目，但目录的修改时间已变化（增/删/改名）。</summary>
    Stale = 2,

    /// <summary>目录已不存在。</summary>
    Gone = 3,
}

/// <summary>
/// **单个目录**的扫描估算（不递归）。
///
/// ⚠️ 名称里带 <c>Estimate</c> 是刻意的：拿到它的人必须一眼看出"这是估算，不是实测"。
/// </summary>
public sealed record ScanEstimate(
    long DirectoryWriteTicks,
    DateTimeOffset CapturedAtUtc,
    long FileCount,
    long SubDirectoryCount,
    long LogicalBytes,
    long DiskBytes,
    string[] EntryNames);

/// <summary>
/// 整棵目录树的估算结果。
///
/// **它不是 <see cref="ScanSummary"/> 的替代品**，也不能用于 PRECHECK / 校验 / 空间复检。
/// </summary>
public sealed record ScanEstimateResult(
    string RootPath,
    long FileCount,
    long DirectoryCount,
    long LogicalBytes,
    long DiskBytes,
    int CacheHits,
    int CacheMisses,
    DateTimeOffset? OldestCaptureUtc,
    TimeSpan Elapsed)
{
    /// <summary>恒为 true —— 提醒调用方这是估算。</summary>
    public bool IsEstimate => true;

    /// <summary>必须随结果一起展示的有效性说明（UI 与 CLI 都要打印）。</summary>
    public const string ValidityCaveat =
        "估算值：按目录修改时间复用上次结果。目录内**文件内容或大小的变化不会改变父目录的修改时间**，"
        + "因此字节数可能已过期。仅用于预览；迁移预检、数据校验与空间复检一律使用实时扫描。";

    public string Describe()
        => $"（估算）{RootPath}：约 {FileCount} 个文件 / {DirectoryCount} 个子目录 / {SpaceRequirement.Format(LogicalBytes)}，"
           + $"耗时 {Elapsed.TotalMilliseconds:0} ms，命中缓存 {CacheHits} 个目录、实时读取 {CacheMisses} 个目录。";
}

/// <summary>
/// 扫描估算缓存（按目录修改时间键控）。
///
/// ## 有效性与局限（这是本类存在的全部理由，务必读完）
///
/// NTFS 语义：**在目录里增 / 删 / 改名条目会更新该目录的修改时间**；
/// 但**修改文件内容或大小不会**。因此：
///
/// <list type="table">
/// <item><term>可靠的</term><description>目录的"条目集合"（有哪些文件/子目录）。缓存条目里的 <c>EntryNames</c> 用它复用是成立的。</description></item>
/// <item><term><b>不可靠的</b></description>文件大小 / 字节数。文件被改写后父目录 mtime 不变，缓存会继续报旧值。</description></item>
/// </list>
///
/// 结论：本缓存**只能用于预览估算**，默认关闭（<see cref="Enabled"/>），
/// 且**严禁**接入 PRECHECK、复制后校验、目标空间复检等任何会影响决策的路径。
/// 用例 <c>R5_ScanCacheTests.文件内容变化不会让目录级缓存失效_因此只能用于估算</c>
/// 把这个局限写成了可执行断言 —— 局限本身也是被测试的行为。
///
/// 另外：缓存命中时仍然要 <c>stat</c> 一次目录本身（拿 mtime 做校验），
/// 但**不需要枚举目录内容**，也不需要为每个文件再做系统调用，这才是省下来的时间。
/// </summary>
public sealed class ScanEstimateCache
{
    private const int SchemaVersion = 1;
    private const long MaxCacheFileBytes = 8L * 1024 * 1024;

    private readonly Dictionary<string, ScanEstimate> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly string _filePath;

    private ScanEstimateCache(string filePath, bool enabled, Dictionary<string, ScanEstimate> entries)
    {
        _filePath = filePath;
        Enabled = enabled;

        foreach (var (key, value) in entries) _entries[key] = value;
    }

    /// <summary>是否启用（关闭时 <see cref="TryGet"/> 恒为 <see cref="ScanCacheFreshness.Missing"/>）。</summary>
    public bool Enabled { get; }

    public string FilePath => _filePath;

    public int Count { get { lock (_lock) return _entries.Count; } }

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    /// <summary>默认缓存位置。</summary>
    public static string DefaultFilePath
        => Path.Combine(DirPorterPaths.DataRoot, "cache", "scan-estimates.json");

    /// <summary>打开（不存在则视为空缓存）。</summary>
    public static ScanEstimateCache Open(string? filePath = null, bool enabled = true)
    {
        var path = filePath ?? DefaultFilePath;

        Dictionary<string, ScanEstimate> loaded = new(StringComparer.OrdinalIgnoreCase);

        if (enabled && File.Exists(path))
        {
            try
            {
                var model = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
                if (model is not null && model.SchemaVersion == SchemaVersion && model.Directories is not null)
                {
                    foreach (var (key, value) in model.Directories)
                    {
                        if (value?.EntryNames is null) continue;
                        loaded[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                // 缓存损坏绝不能影响主流程：丢弃即可。
                Debug.WriteLine($"[DirPorter] 扫描估算缓存读取失败（已忽略）：{ex.Message}");
            }
        }

        return new ScanEstimateCache(path, enabled, loaded);
    }

    /// <summary>查询某目录的估算。</summary>
    public ScanCacheFreshness TryGet(string directoryPath, out ScanEstimate? estimate)
    {
        estimate = null;

        if (!Enabled)
        {
            Misses++;
            return ScanCacheFreshness.Missing;
        }

        var key = Key(directoryPath);

        ScanEstimate? cached;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out cached)) cached = null;
        }

        if (cached is null)
        {
            Misses++;
            return ScanCacheFreshness.Missing;
        }

        var extended = PathNormalizer.NormalizeAbsolute(directoryPath);
        var attributes = Kernel32.GetFileAttributes(extended);
        if (attributes == Kernel32.InvalidFileAttributes)
        {
            lock (_lock) _entries.Remove(key);
            Misses++;
            return ScanCacheFreshness.Gone;
        }

        var info = FileInformation.Query(extended);
        var currentTicks = info is null ? -1 : ToTicks(info.Value.ftLastWriteTime);

        if (currentTicks != cached.DirectoryWriteTicks)
        {
            Misses++;
            return ScanCacheFreshness.Stale;
        }

        Hits++;
        estimate = cached;
        return ScanCacheFreshness.Fresh;
    }

    public void Put(string directoryPath, ScanEstimate estimate)
    {
        if (!Enabled) return;

        lock (_lock) _entries[Key(directoryPath)] = estimate;
    }

    /// <summary>原子写盘（先写 .tmp 再替换），并做体积上限控制。</summary>
    public void Save()
    {
        if (!Enabled) return;

        try
        {
            Dictionary<string, ScanEstimate> snapshot;
            lock (_lock) snapshot = new Dictionary<string, ScanEstimate>(_entries, StringComparer.OrdinalIgnoreCase);

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var model = new CacheFile { SchemaVersion = SchemaVersion, Directories = snapshot };
            var json = JsonSerializer.Serialize(model, JsonOptions);

            if (json.Length > MaxCacheFileBytes)
            {
                // 超上限：只保留最近的一半（按捕获时间倒序），避免缓存文件无限膨胀。
                var trimmed = snapshot
                    .OrderByDescending(static kv => kv.Value.CapturedAtUtc)
                    .Take(Math.Max(1, snapshot.Count / 2))
                    .ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                model = new CacheFile { SchemaVersion = SchemaVersion, Directories = trimmed };
                json = JsonSerializer.Serialize(model, JsonOptions);

                lock (_lock)
                {
                    _entries.Clear();
                    foreach (var (key, value) in trimmed) _entries[key] = value;
                }
            }

            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, json);

            if (!Kernel32.MoveFileEx(
                    PathNormalizer.NormalizeAbsolute(temporary),
                    PathNormalizer.NormalizeAbsolute(_filePath),
                    Kernel32.MoveFileReplaceExisting | Kernel32.MoveFileWriteThrough))
            {
                var code = Win32Error.Last;
                Debug.WriteLine($"[DirPorter] 扫描估算缓存原子替换失败：{Win32Error.Describe(code)}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DirPorter] 扫描估算缓存写入失败（不影响主流程）：{ex.Message}");
        }
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Hits = 0;
        Misses = 0;
    }

    /// <summary>
    /// 用缓存估算整棵树：命中的目录直接用缓存，未命中的目录**只读一层**并写入缓存。
    /// 不递归进入未命中目录的子目录 —— 那些子目录会在后续轮次（或下一次调用）命中。
    /// </summary>
    public ScanEstimateResult EstimateTree(string rootPath, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var root = PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(rootPath));
        var rootPlain = PathNormalizer.StripExtendedPrefix(root);

        long files = 0, directories = 0, logical = 0, disk = 0;
        DateTimeOffset? oldest = null;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = stack.Pop();
            var freshness = TryGet(directory, out var estimate);

            if (freshness != ScanCacheFreshness.Fresh || estimate is null)
            {
                // 未命中：实时读一层（不递归），并把结果写进缓存。
                var measured = MeasureSingleDirectory(directory);
                if (measured is null) continue;

                estimate = measured;
                Put(directory, measured);
            }

            files += estimate.FileCount;
            logical += estimate.LogicalBytes;
            disk += estimate.DiskBytes;

            if (oldest is null || estimate.CapturedAtUtc < oldest) oldest = estimate.CapturedAtUtc;

            foreach (var name in estimate.EntryNames)
            {
                if (name.EndsWith('\\'))
                {
                    directories++;
                    stack.Push(PathNormalizer.Combine(directory, name[..^1]));
                }
            }
        }

        started.Stop();

        return new ScanEstimateResult(
            rootPlain, files, directories, logical, disk,
            Hits, Misses, oldest, started.Elapsed);
    }

    /// <summary>只读一层目录，产出可直接缓存的估算（子目录名以 <c>\</c> 结尾区分）。</summary>
    private static ScanEstimate? MeasureSingleDirectory(string directory)
    {
        var extended = PathNormalizer.NormalizeAbsolute(directory);
        var info = FileInformation.Query(extended);
        if (info is null) return null;

        var names = new List<string>();
        long fileCount = 0, dirCount = 0, logical = 0, disk = 0;

        var searchPath = PathNormalizer.EnsureTrailingSeparator(extended) + "*";
        var handle = Kernel32.FindFirstFileEx(
            searchPath, Kernel32.FindExInfoBasic, out var data,
            Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

        if (handle != Kernel32.InvalidHandleValue)
        {
            try
            {
                while (true)
                {
                    var name = Kernel32.GetFileName(data);
                    if (name is not ("." or ".."))
                    {
                        var attributes = data.dwFileAttributes;
                        var isDirectory = (attributes & Kernel32.FileAttributeDirectory) != 0;
                        var isReparse = (attributes & Kernel32.FileAttributeReparsePoint) != 0;

                        if (isDirectory)
                        {
                            // 重解析点记名字但不深入（/XJ 语义），因此也不入栈。
                            names.Add(name + "\\");
                            if (!isReparse) dirCount++;
                        }
                        else
                        {
                            names.Add(name);

                            var size = Kernel32.ToFileSize(data);
                            fileCount++;
                            logical += size;

                            var isCompressed = (attributes & Kernel32.FileAttributeCompressed) != 0;
                            var isSparse = (attributes & Kernel32.FileAttributeSparseFile) != 0;
                            disk += isCompressed || isSparse ? DiskSize(extended, name, size) : size;
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

        // MeasureSingleDirectory 只返回**非重解析点**的子目录计数，但 EntryNames 里保留了
        // 目录名（含重解析点）—— 后者用于"结构复用"，前者用于"统计"。
        var value = info.Value;
        return new ScanEstimate(
            DirectoryWriteTicks: ToTicks(value.ftLastWriteTime),
            CapturedAtUtc: DateTimeOffset.UtcNow,
            FileCount: fileCount,
            SubDirectoryCount: dirCount,
            LogicalBytes: logical,
            DiskBytes: disk,
            EntryNames: names.ToArray());
    }

    private static long DiskSize(string directoryExtended, string name, long logicalSize)
    {
        var low = Kernel32.GetCompressedFileSize(PathNormalizer.Combine(directoryExtended, name), out var high);
        if (low == 0xFFFFFFFF && Win32Error.Last != Kernel32.ErrorSuccess) return logicalSize;
        var value = ((long)high << 32) | low;
        return value < 0 ? logicalSize : value;
    }

    private static long ToTicks(Kernel32.FileTime time) => ((long)time.dwHighDateTime << 32) | time.dwLowDateTime;

    private static string Key(string directoryPath)
        => PathNormalizer.TrimTrailingSeparator(
            PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(directoryPath)));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class CacheFile
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("directories")] public Dictionary<string, ScanEstimate>? Directories { get; set; }
    }
}
