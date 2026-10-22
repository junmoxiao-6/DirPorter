using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.Copy;

/// <summary>复制结果的终态。</summary>
public enum CopyOutcome
{
    Success = 0,
    Partial = 1,
    Failed = 2,
    Canceled = 3,
}

/// <summary>复制引擎对外的能力声明（必须如实，不许假装与另一引擎等价）。</summary>
public sealed record CopyCapabilities(
    bool PreservesAcl,
    bool PreservesOwnerAndAuditing,
    bool PreservesTimestamps,
    bool PreservesAlternateDataStreams,
    bool PreservesSparse,
    bool HandlesEncryptedFiles,
    bool SkipsReparsePoints,
    bool SupportsPauseResume,
    string ProgressGranularity);

/// <summary>复制请求。</summary>
public sealed record CopyRequest(string SourcePath, string TargetPath, CopyOptions Options);

/// <summary>复制选项。</summary>
public sealed record CopyOptions
{
    /// <summary>并发线程数（robocopy /MT）。</summary>
    public int Threads { get; init; } = 8;

    /// <summary>重试次数（robocopy /R）。</summary>
    public int RetryCount { get; init; } = 1;

    /// <summary>重试间隔秒（robocopy /W）。</summary>
    public int RetryWaitSeconds { get; init; } = 1;

    /// <summary>
    /// 跳过目录内的重解析点。**必须为 true**（对应 robocopy /XJ）：
    /// 否则遇到嵌套 Junction 会递归复制甚至形成环，导致数据膨胀或死循环。
    /// </summary>
    public bool SkipReparsePoints { get; init; } = true;

    /// <summary>用于计算百分比的源总字节数（来自 TreeProbe）。</summary>
    public long SourceTotalBytes { get; init; }

    /// <summary>源文件总数（来自 TreeProbe）。</summary>
    public long SourceFileCount { get; init; }

    /// <summary>失败时保留 robocopy 原始日志的目录（用于事后排障）。</summary>
    public string? LogArchiveDirectory { get; init; }

    /// <summary>是否搬运安全描述符（DACL/所有者/审计）。复制引擎 B 用；引擎 A 由 /COPY* 开关控制。</summary>
    public bool CopySecurity { get; init; } = true;

    /// <summary>是否补做稀疏优化（仅对带稀疏属性的文件生效）。</summary>
    public bool ReplicateSparse { get; init; } = true;

    /// <summary>
    /// 是否允许把 EFS 加密文件**解密后**复制到目标。
    /// 默认 false：静默解密是安全降级，必须由用户显式选择。
    /// </summary>
    public bool AllowDecryptEncryptedFiles { get; init; }

    /// <summary>无缓冲复制（大文件可减少缓存占用，但对小文件反而更慢）。</summary>
    public bool NoBuffering { get; init; }

    /// <summary>重试之间的退避是否按倍增长（默认固定间隔，与 robocopy /W 行为一致）。</summary>
    public bool ExponentialBackoff { get; init; }

    public static CopyOptions Default { get; } = new();
}

/// <summary>复制进度（字节级累计；不同引擎的精度如实反映在粒度描述里）。</summary>
public sealed record CopyProgress(
    long FilesTotal,
    long FilesDone,
    long BytesTotal,
    long BytesDone,
    double BytesPerSecond,
    TimeSpan? Eta,
    string? CurrentItem)
{
    public double Percent => BytesTotal <= 0 ? 0 : Math.Clamp(BytesDone * 100.0 / BytesTotal, 0, 100);
}

/// <summary>单个失败项。</summary>
public sealed record CopyFailure(string Path, string Reason, int? Win32Code);

/// <summary>复制结果。</summary>
public sealed record CopyResult(
    CopyOutcome Outcome,
    int ExitCode,
    long FilesCopied,
    long FilesSkipped,
    long BytesCopied,
    long FailedCount,
    IReadOnlyList<CopyFailure> Failures,
    string? RawLogPath,
    TimeSpan Elapsed)
{
    /// <summary>安全信息复制模式的调整说明（如从 /COPYALL 降级为 /COPY:DATSO）。</summary>
    public string? SecurityNote { get; init; }

    public string Describe()
        => Outcome switch
        {
            CopyOutcome.Success => $"复制完成：{FilesCopied} 个文件，{FileSystem.SpaceRequirement.Format(BytesCopied)}，耗时 {Elapsed.TotalSeconds:0.0}s。",
            CopyOutcome.Partial => $"复制部分成功：{FilesCopied} 个文件，失败 {FailedCount} 项，耗时 {Elapsed.TotalSeconds:0.0}s。",
            CopyOutcome.Canceled => "复制已取消。",
            _ => $"复制失败：退出码 {ExitCode}，失败 {FailedCount} 项。",
        };
}

/// <summary>
/// 复制引擎抽象。v1 提供 robocopy 实现（<see cref="RobocopyCopier"/>）；
/// 自研多线程实现保留同一契约，可在不改调用方的前提下替换。
/// </summary>
public interface IFileCopier
{
    string Name { get; }

    CopyCapabilities Capabilities { get; }

    Task<CopyResult> CopyAsync(CopyRequest request, IProgress<CopyProgress>? progress, CancellationToken cancellationToken);
}
