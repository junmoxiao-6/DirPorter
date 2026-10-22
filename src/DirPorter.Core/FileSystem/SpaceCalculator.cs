namespace DirPorter.Core.FileSystem;

/// <summary>空间复检结果（PRECHECK 目标侧第 10 条 / 批次侧第 15 条）。</summary>
public sealed record SpaceRequirement(
    string VolumeRoot,
    long SourceDiskBytes,
    double Factor,
    long RequiredBytes,
    long TargetFreeBytes,
    long ShortfallBytes)
{
    public bool IsSatisfied => ShortfallBytes <= 0;

    /// <summary>人类可读的结论，直接用于 UI 与 CLI 输出。</summary>
    public string Describe() => IsSatisfied
        ? $"空间充足：需要 {Format(RequiredBytes)}，目标卷 {VolumeRoot} 可用 {Format(TargetFreeBytes)}。"
        : $"空间不足：需要 {Format(RequiredBytes)}，目标卷 {VolumeRoot} 仅可用 {Format(TargetFreeBytes)}，缺少 {Format(ShortfallBytes)}。";

    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}

/// <summary>
/// 空间计算（需求：占用按 <c>GetCompressedFileSize</c> 统计，不是名义大小；目标卷剩余 ≥ 源实际占用 × 1.05）。
/// </summary>
public static class SpaceCalculator
{
    /// <summary>默认余量系数。</summary>
    public const double DefaultHeadroomFactor = 1.05;

    /// <summary>对单个源目录做空间复检。</summary>
    public static SpaceRequirement Evaluate(string targetPath, long sourceDiskBytes, IVolumeResolver volumes, double factor = DefaultHeadroomFactor)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        if (factor < 1.0) throw new ArgumentOutOfRangeException(nameof(factor), factor, "余量系数不得小于 1.0。");

        var free = GetFreeBytes(targetPath, volumes, out var volumeRoot);
        var required = (long)Math.Ceiling(sourceDiskBytes * factor);
        var shortfall = required - free;

        return new SpaceRequirement(volumeRoot, sourceDiskBytes, factor, required, free, Math.Max(0, shortfall));
    }

    /// <summary>批次级：把多个源按目标卷分组，各自校验该卷的剩余空间。</summary>
    public static IReadOnlyList<SpaceRequirement> EvaluateBatch(
        IEnumerable<(string TargetPath, long SourceDiskBytes)> items,
        IVolumeResolver volumes,
        double factor = DefaultHeadroomFactor)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(volumes);

        var byVolume = new Dictionary<string, (long Required, long Free)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (targetPath, sourceBytes) in items)
        {
            var free = GetFreeBytes(targetPath, volumes, out var volumeRoot);
            var required = (long)Math.Ceiling(sourceBytes * factor);

            if (byVolume.TryGetValue(volumeRoot, out var existing))
                byVolume[volumeRoot] = (existing.Required + required, Math.Min(existing.Free, free));
            else
                byVolume[volumeRoot] = (required, free);
        }

        return byVolume.Select(kv => new SpaceRequirement(
            kv.Key,
            SourceDiskBytes: 0,
            Factor: factor,
            RequiredBytes: kv.Value.Required,
            TargetFreeBytes: kv.Value.Free,
            ShortfallBytes: Math.Max(0, kv.Value.Required - kv.Value.Free))).ToList();
    }

    /// <summary>取某个路径所在卷的可用字节数（失败返回 0，绝不抛）。</summary>
    public static long GetFreeBytes(string path, IVolumeResolver volumes, out string volumeRoot)
    {
        volumeRoot = PathNormalizer.TryGetVolumeRoot(path) ?? path;

        try
        {
            if (volumes.TryGetVolumeInfo(path, out var info))
            {
                volumeRoot = info.RootPath;
                return info.FreeBytesAvailable;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 查询卷可用空间失败：{ex.Message}");
        }

        return 0;
    }
}
