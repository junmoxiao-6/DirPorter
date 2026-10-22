using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.Config;

/// <summary>
/// DirPorter 的落盘位置。
///
/// 约定（需求「迁移记录页」+「非管理员降级路径」）：
/// - 首选 <c>%ProgramData%\DirPorter</c>；不可写时回落到 <c>%LOCALAPPDATA%\DirPorter</c>，
///   并在 UI/日志中明确告知"位置已降级"，绝不静默改变用户数据的存放处。
/// </summary>
public static class DirPorterPaths
{
    private static readonly Lazy<RootResolution> Root = new(ResolveRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>数据根目录。</summary>
    public static string DataRoot => Root.Value.Path;

    /// <summary>是否已降级到用户级目录（ProgramData 不可写）。</summary>
    public static bool IsFallback => Root.Value.IsFallback;

    /// <summary>降级原因（未降级为 null）。</summary>
    public static string? FallbackReason => Root.Value.Reason;

    /// <summary>运行中任务的状态文件目录（Journal）。</summary>
    public static string RunsDirectory => Path.Combine(DataRoot, "runs");

    /// <summary>日志目录。</summary>
    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>迁移记录索引（GUI 迁移记录页读取；**不作为回滚依据**，回滚只认台账）。</summary>
    public static string MigrationsFile => Path.Combine(DataRoot, "migrations.json");

    /// <summary>备份索引与保留策略的数据文件。</summary>
    public static string BackupIndexFile => Path.Combine(DataRoot, "backups", "index.json");

    /// <summary>复制引擎临时目录（robocopy 日志等）。</summary>
    public static string TempDirectory => Path.Combine(DataRoot, "tmp");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>数据根目录的环境变量覆盖：便携模式，以及演练/测试的完全隔离。</summary>
    public const string DataRootEnvironmentVariable = "DIRPORTER_DATA_ROOT";

    private static RootResolution ResolveRoot()
    {
        // 显式覆盖优先：让"数据跟着程序走"的便携模式与自动化演练都能隔离，不去碰 %ProgramData%。
        var overridden = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return TryPrepare(overridden, out var overrideError)
                ? new RootResolution(overridden, false, null)
                : new RootResolution(FallbackRoot(), true, $"无法使用 {DataRootEnvironmentVariable}={overridden}：{overrideError}");
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            var candidate = Path.Combine(programData, "DirPorter");
            if (TryPrepare(candidate, out var error)) return new RootResolution(candidate, false, null);
            return new RootResolution(FallbackRoot(), true, $"无法使用 {candidate}：{error}");
        }

        return new RootResolution(FallbackRoot(), true, "系统未提供 ProgramData 路径。");
    }

    private static string FallbackRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DirPorter");

    private static bool TryPrepare(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // 写入探针文件：CreateDirectory 成功不代表可写（可能是"只读继承"）。
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private readonly record struct RootResolution(string Path, bool IsFallback, string? Reason);
}
