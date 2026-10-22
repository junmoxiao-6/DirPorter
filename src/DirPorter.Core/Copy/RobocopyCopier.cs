using System.Diagnostics;
using System.Globalization;
using System.Text;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Copy;

/// <summary>
/// 复制引擎 A：驱动系统自带 robocopy。
///
/// 取舍理由（见 R0 设计 §10）：本产品的差异化是"校验 + 回滚 + 可观测"，不是复制速度。
/// robocopy 的 `/COPYALL` 对 ACL / 所有者 / 审计 / 时间戳 / ADS / 稀疏 / EFS 的处理经过十余年验证，
/// 元数据正确性的权重远高于进度平滑度。
///
/// 硬性要求：
/// 1. **`/XJ` 必须带** —— 否则遇到目录内已有的 junction 会递归复制甚至形成环（数据膨胀 / 死循环）；
/// 2. 退出码 ≥ 8 才判失败（0–7 都可能"有内容"）；1–7 需再核对日志汇总行；
/// 3. 日志以 UTF-16 读取（`/UNICODE`），且只解析字段不解析本地化文本。
///
/// 进度：解析 `/BYTES /FP /NC /NDL /NP` 输出的"每文件一行"，逐行累计字节数。
/// 粒度是**文件级**（不是文件内），对 5 万文件场景足够平滑。
/// 暂停 = 终止进程（已复制的文件保留）；继续 = 重新发起，robocopy 会按 size+mtime 跳过相同文件。
/// </summary>
public sealed class RobocopyCopier : IFileCopier
{
    private readonly string _robocopyPath;
    private readonly Privilege.IPrivilegeProbe _privileges;

    public RobocopyCopier(string? robocopyPath = null, Privilege.IPrivilegeProbe? privileges = null)
    {
        _robocopyPath = robocopyPath ?? Path.Combine(PathNormalizer.StripExtendedPrefix(Environment.SystemDirectory), "Robocopy.exe");
        _privileges = privileges ?? Privilege.Win32PrivilegeProbe.Current;
    }

    public string Name => "robocopy";

    public CopyCapabilities Capabilities { get; } = new(
        PreservesAcl: true,
        PreservesOwnerAndAuditing: false,   // 审计 SACL 需要 SeSecurityPrivilege：非提权时自动降级为 /COPY:DATSO
        PreservesTimestamps: true,
        PreservesAlternateDataStreams: true,
        PreservesSparse: true,
        HandlesEncryptedFiles: false,          // TODO(verify)-2：EFS 行为未实测，能力声明保守
        SkipsReparsePoints: true,
        SupportsPauseResume: false,
        ProgressGranularity: "文件级（每文件一行累计字节）");

    public static bool IsAvailable(string? robocopyPath = null)
        => File.Exists(robocopyPath ?? Path.Combine(PathNormalizer.StripExtendedPrefix(Environment.SystemDirectory), "Robocopy.exe"));

    public async Task<CopyResult> CopyAsync(CopyRequest request, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(_robocopyPath))
            throw new FileNotFoundException($"未找到系统复制工具：{_robocopyPath}。robocopy 是 Windows 内置组件，请确认系统完整性。");

        var source = PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(request.SourcePath));
        var target = PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(request.TargetPath));

        // /COPYALL 含 U（SACL 审计信息），需要 SeSecurityPrivilege。
        // 实测（本机非管理员）：使用 /COPYALL 时 robocopy 直接返回 16（严重错误，一个文件都不复制）。
        // 因此非提权时默认降级为 /COPY:DATSO（仍保留 ACL / 所有者 / 属性 / 时间戳 / ADS，仅少了审计 SACL）。
        var useCopyAll = _privileges.IsElevated;
        var result = await RunOnceAsync(source, target, request.Options, useCopyAll, progress, cancellationToken).ConfigureAwait(false);

        // 兜底：即使已提权，个别令牌也可能没有 SeSecurityPrivilege → 检测到 16 就自动降级重试一次。
        if (result.ExitCode == 16 && useCopyAll && result.Outcome != CopyOutcome.Canceled)
        {
            var retry = await RunOnceAsync(source, target, request.Options, useCopyAll: false, progress, cancellationToken).ConfigureAwait(false);
            return retry with
            {
                SecurityNote = $"首次使用 /COPYALL 失败（退出码 16，通常是缺少 SeSecurityPrivilege），"
                               + "已自动降级为 /COPY:DATSO 重试（ACL/所有者/属性/时间戳/ADS 仍保留，仅审计 SACL 未复制）。",
            };
        }

        return useCopyAll
            ? result
            : result with { SecurityNote = "以普通权限运行：使用 /COPY:DATSO（审计 SACL 需要管理员，未复制）。" };
    }

    /// <summary>执行一次 robocopy。</summary>
    private async Task<CopyResult> RunOnceAsync(
        string source, string target, CopyOptions options, bool useCopyAll,
        IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"dirporter-robocopy-{Guid.NewGuid():N}.log");
        var arguments = BuildArguments(source, target, options, logPath, useCopyAll);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_robocopyPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            },
            EnableRaisingEvents = true,
        };

        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        process.Start();

        // 进度：边跑边 tail 日志文件（robocopy 会持续写日志）。
        using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tailTask = TailLogAsync(logPath, options, progress, progressCancellation.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            progressCancellation.Cancel();
            await SafeAwait(tailTask).ConfigureAwait(false);
            TryDelete(logPath);
            return new CopyResult(CopyOutcome.Canceled, -1, 0, 0, 0, 0, Array.Empty<CopyFailure>(), null, stopwatch.Elapsed);
        }

        progressCancellation.Cancel();
        await SafeAwait(tailTask).ConfigureAwait(false);

        stopwatch.Stop();

        var exitCode = process.ExitCode;
        var summary = ParseLog(logPath, options, exitCode);

        // 失败时把原始日志归档，作为排障证据；成功则删除临时日志。
        string? archivedLog = null;
        if (summary.Outcome != CopyOutcome.Success && !string.IsNullOrWhiteSpace(options.LogArchiveDirectory))
        {
            archivedLog = ArchiveLog(logPath, options.LogArchiveDirectory!);
        }

        TryDelete(logPath);

        return summary with { RawLogPath = archivedLog, Elapsed = stopwatch.Elapsed };
    }

    /// <summary>当前权限下将使用的安全信息复制开关（供 Dry-run 与日志复现）。</summary>
    public static string DescribeCopySwitch(bool isElevated)
        => isElevated
            ? "/COPYALL（数据+属性+时间戳+SACL+所有者+审计）"
            : "/COPY:DATSO（数据+属性+时间戳+SACL+所有者；审计 SACL 需管理员，非提权时会自动降级）";

    /// <summary>
    /// 构造固定参数集。参数顺序与内容都刻意固定，便于日志复盘与回归对比。
    /// </summary>
    public static List<string> BuildArguments(string source, string target, CopyOptions options, string logPath, bool useCopyAll = true)
    {
        var arguments = new List<string>
        {
            source,
            target,
            "/E",                 // 复制子目录（含空目录）
            useCopyAll ? "/COPYALL" : "/COPY:DATSO",
            "/DCOPY:DAT",         // 目录：数据 + 属性 + 时间戳
            "/R:" + options.RetryCount,
            "/W:" + options.RetryWaitSeconds,
            "/MT:" + Math.Clamp(options.Threads, 1, 128),
            "/BYTES",             // 以字节显示大小，便于机器解析
            "/FP",                // 显示完整路径
            "/NC",                // 不显示文件类别
            "/NDL",               // 不显示目录列表
            "/NP",                // 不显示进度百分比（我们自己算）
            "/UNICODE",           // 日志 UTF-16，避免中文路径乱码
            "/LOG:" + logPath,
        };

        if (options.SkipReparsePoints)
            arguments.Add("/XJ"); // ⚠️ 必须：跳过目录内的 junction/符号链接，防成环与数据膨胀

        return arguments;
    }

    /// <summary>
    /// 解析日志：进度行（`&lt;字节&gt;\t&lt;完整路径&gt;`）+ 汇总行 + 退出码。
    /// 只解析字段，不依赖本地化文案（中英文日志都能过）。
    /// </summary>
    private static CopyResult ParseLog(string logPath, CopyOptions options, int exitCode)
    {
        long filesCopied = 0;
        long filesSkipped = 0;
        long bytesCopied = 0;
        var failures = new List<CopyFailure>();
        var summaryFound = false;

        // 记录每个文件是否已在进度中被计入，避免汇总行重复累加
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(logPath))
        {
            var lines = ReadLogLines(logPath).ToList();

            // 优先使用日志末尾的**汇总块**作为权威计数：它由 robocopy 自己统计，
            // 且只依赖数字（与本地化文案无关），不受逐行解析的边界情况影响。
            if (TryParseSummary(lines, out var summary))
            {
                summaryFound = true;
                filesCopied = summary.FilesCopied;
                filesSkipped = summary.FilesSkipped;
                bytesCopied = summary.BytesCopied;
            }

            foreach (var line in lines)
            {
                if (TryParseFileLine(line, out var size, out var path))
                {
                    // 汇总块已给出权威计数时不再累加（否则重复计数）；仅在无汇总时兜底。
                    if (summaryFound)
                    {
                        counted.Add(path);
                    }
                    else if (counted.Add(path))
                    {
                        filesCopied++;
                        bytesCopied += size;
                    }

                    continue;
                }

                if (IsFailureLine(line, out var reason, out var failedPath) && failedPath is not null)
                {
                    failures.Add(new CopyFailure(failedPath, reason, null));
                }
                else if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) && !IsSummaryLine(line))
                {
                    failures.Add(new CopyFailure("(未定位)", line.Trim(), null));
                }

                // 汇总块可用时不用这条启发式（它取不到正确列，历史上一直算错）
                if (!summaryFound
                    && (line.Contains("Skipped", StringComparison.OrdinalIgnoreCase) || line.Contains("跳过", StringComparison.Ordinal)))
                {
                    var number = ExtractFirstNumber(line);
                    if (number is not null && line.Contains(':')) filesSkipped = number.Value;
                }
            }
        }

        var outcome = ComputeOutcome(exitCode, failures.Count);

        return new CopyResult(
            Outcome: outcome,
            ExitCode: exitCode,
            FilesCopied: filesCopied,
            FilesSkipped: filesSkipped,
            BytesCopied: bytesCopied,
            FailedCount: failures.Count,
            Failures: failures,
            RawLogPath: null,
            Elapsed: TimeSpan.Zero);
    }

    /// <summary>
    /// 解析日志末尾的汇总块。
    ///
    /// robocopy 会输出三行同构统计（目录 / 文件 / 字节），每行格式为
    /// <c>&lt;本地化标签&gt; 总数 复制 跳过 不匹配 失败 其他</c>。
    /// 这里**只判断行末 6 个 token 是否都是整数**，完全不依赖标签文案，中英文日志都能解析。
    /// </summary>
    internal static bool TryParseSummary(IReadOnlyList<string> lines, out SummaryStats summary)
    {
        summary = default;
        var rows = new List<long[]>();

        foreach (var line in lines)
        {
            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6) continue;

            var tail = new long[6];
            var ok = true;

            for (var i = 0; i < 6; i++)
            {
                if (!long.TryParse(tokens[^(6 - i)], NumberStyles.Integer, CultureInfo.InvariantCulture, out tail[i]))
                {
                    ok = false;
                    break;
                }
            }

            if (ok) rows.Add(tail);
        }

        // 目录 / 文件 / 字节三行同构。取最后三行（多段汇总时以最后一段为准）。
        // 之所以敢用"末 6 个 token 全为整数"作为唯一判据：日志里其余数字行
        //（时间行只剩 4 个时间值、速度行带千分位逗号）都不满足这个条件。
        if (rows.Count < 3) return false;

        var bytes = rows[^1];
        var files = rows[^2];
        var dirs = rows[^3];

        summary = new SummaryStats(
            DirectoriesTotal: dirs[0], DirectoriesCopied: dirs[1],
            FilesTotal: files[0], FilesCopied: files[1], FilesSkipped: files[2], FilesFailed: files[4],
            BytesTotal: bytes[0], BytesCopied: bytes[1]);

        return true;
    }

    /// <summary>日志汇总块（列顺序固定：总数/复制/跳过/不匹配/失败/其他）。</summary>
    internal readonly record struct SummaryStats(
        long DirectoriesTotal,
        long DirectoriesCopied,
        long FilesTotal,
        long FilesCopied,
        long FilesSkipped,
        long FilesFailed,
        long BytesTotal,
        long BytesCopied);

    /// <summary>robocopy 退出码语义：0–7 视为成功域（0 表示无变化），≥8 为失败。</summary>
    private static CopyOutcome ComputeOutcome(int exitCode, int failureLines)
    {
        if (exitCode >= 8) return CopyOutcome.Failed;
        if (failureLines > 0) return CopyOutcome.Partial;
        return CopyOutcome.Success;
    }

    /// <summary>
    /// 读取日志行。
    /// 实测修正：robocopy 的 /LOG 文件是**系统 ANSI 单字节编码**，不是 UTF-16。
    /// /UNICODE 只影响控制台，不影响日志文件。此前用 Encoding.Unicode 读取，
    /// 导致中文与路径全部乱码、逐文件行与错误行都解析不出来（已复制文件数恒为 0）。
    /// </summary>
    private static IEnumerable<string> ReadLogLines(string logPath) => AnsiText.ReadLines(logPath);

    /// <summary>解析形如 `␣␣␣␣␣1234␣C:\path\file.txt` 的行。</summary>
    internal static bool TryParseFileLine(string line, out long size, out string path)
    {
        size = 0;
        path = string.Empty;

        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;

        var separator = trimmed.IndexOfAny(new[] { '\t', ' ' });
        if (separator <= 0) return false;

        var numberPart = trimmed[..separator].Trim();
        if (!long.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) return false;

        var pathPart = trimmed[separator..].Trim();
        if (pathPart.Length < 3) return false;

        // 必须是绝对路径（robocopy /FP 输出全路径）
        if (!(pathPart.Length >= 2 && pathPart[1] == ':')) return false;

        path = pathPart;
        return true;
    }

    private static bool IsFailureLine(string line, out string reason, out string? path)
    {
        reason = string.Empty;
        path = null;

        var trimmed = line.Trim();
        var markers = new[] { "ERROR ", "ERROR:", "错误", "拒绝访问", "Access is denied" };
        if (!markers.Any(m => trimmed.Contains(m, StringComparison.OrdinalIgnoreCase))) return false;

        var separator = trimmed.IndexOf(' ');
        var remainder = separator > 0 ? trimmed[separator..].Trim() : trimmed;

        var tab = remainder.LastIndexOfAny(new[] { '\t', ' ' });
        if (tab > 3)
        {
            var candidate = remainder[(tab + 1)..].Trim();
            if (candidate.Length >= 3 && candidate[1] == ':') path = candidate;
        }

        reason = trimmed;
        return true;
    }

    private static bool IsSummaryLine(string line)
        => line.Contains("Bytes :", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Files :", StringComparison.OrdinalIgnoreCase)
           || line.Contains("字节", StringComparison.Ordinal);

    private static long? ExtractFirstNumber(string line)
    {
        var digits = new StringBuilder();
        foreach (var c in line)
        {
            if (char.IsDigit(c)) digits.Append(c);
            else if (digits.Length > 0) break;
        }

        return digits.Length > 0 && long.TryParse(digits.ToString(), out var value) ? value : null;
    }

    /// <summary>边跑边读日志尾部，按已见文件行累计进度。文件级粒度，节流在调用方。</summary>
    private static async Task TailLogAsync(string logPath, CopyOptions options, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        if (progress is null) return;

        var stopwatch = Stopwatch.StartNew();
        long bytesDone = 0;
        long filesDone = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportedAt = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(logPath))
                {
                    // 日志为系统 ANSI 编码（见 AnsiText），不能用 UTF-16 读。
                    foreach (var line in ReadLogLines(logPath))
                    {
                        if (!TryParseFileLine(line, out var size, out var path)) continue;
                        if (!seen.Add(path)) continue;

                        filesDone++;
                        bytesDone += size;

                        // 节流：≤10 次/秒（需求非功能要求）
                        if (stopwatch.Elapsed - reportedAt < TimeSpan.FromMilliseconds(100)) continue;
                        reportedAt = stopwatch.Elapsed;

                        var elapsed = stopwatch.Elapsed.TotalSeconds;
                        var speed = elapsed > 0 ? bytesDone / elapsed : 0;
                        var remaining = options.SourceTotalBytes - bytesDone;
                        var eta = speed > 0 && remaining > 0 ? TimeSpan.FromSeconds(remaining / speed) : (TimeSpan?)null;

                        progress.Report(new CopyProgress(
                            options.SourceFileCount, filesDone, options.SourceTotalBytes, bytesDone,
                            speed, eta, Path.GetFileName(path)));
                    }
                }
            }
            catch (IOException)
            {
                // 日志正在被 robocopy 写入，下一轮再读
            }
            catch (UnauthorizedAccessException)
            {
            }

            try
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        progress.Report(new CopyProgress(options.SourceFileCount, filesDone, options.SourceTotalBytes, bytesDone, 0, null, null));
    }

    private static string ArchiveLog(string logPath, string archiveDirectory)
    {
        try
        {
            Directory.CreateDirectory(archiveDirectory);
            var target = Path.Combine(archiveDirectory, $"robocopy-{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(logPath)}");
            File.Copy(logPath, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 归档 robocopy 日志失败：{ex.Message}");
            return logPath;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 终止 robocopy 失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 删除临时日志失败：{ex.Message}");
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 进度采集任务异常：{ex.Message}");
        }
    }
}
