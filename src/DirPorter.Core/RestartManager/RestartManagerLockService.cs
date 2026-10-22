using System.Diagnostics;
using System.Runtime.InteropServices;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.RestartManager;

/// <summary>
/// 占用某个路径的进程。
///
/// <paramref name="StartTimeTicks"/> 是**进程创建时间**，与 PID 一起构成进程身份。
/// 只凭 PID 去结束进程是危险的：Windows 会复用 PID，一个过期的 PID 可能指向完全无关的新进程
/// （经典事故：本想结束卡住的编辑器，结果打死了刚启动的构建服务）。
/// </summary>
public sealed record LockingProcess(
    string AppName,
    uint ProcessId,
    string? ServiceShortName,
    string ApplicationType,
    bool Restartable,
    long StartTimeTicks = 0)
{
    public string Describe()
    {
        var name = string.IsNullOrWhiteSpace(AppName) ? "(未知程序)" : AppName;
        var service = string.IsNullOrWhiteSpace(ServiceShortName) ? string.Empty : $"（服务 {ServiceShortName}）";
        return $"{name}{service} PID={ProcessId}";
    }

    /// <summary>进程身份（PID + 创建时间）。用于结束前的复核。</summary>
    public string Identity => BuildIdentity(ProcessId, StartTimeTicks);

    public static string BuildIdentity(uint processId, long startTimeTicks)
        => $"{processId}@{startTimeTicks}";
}

/// <summary>占用检测结果。</summary>
public sealed record OccupancyReport(
    IReadOnlyList<LockingProcess> Processes,
    int RegisteredResourceCount,
    string? Error)
{
    public bool HasOccupancy => Processes.Count > 0;

    public static OccupancyReport Empty { get; } = new(Array.Empty<LockingProcess>(), 0, null);

    public string Describe()
        => Error is not null
            ? $"占用检测未完成：{Error}"
            : Processes.Count == 0
                ? "未检测到占用进程。"
                : $"检测到 {Processes.Count} 个占用进程：{string.Join("、", Processes.Select(static p => p.Describe()))}";
}

/// <summary>被占用的一批资源（用于把占用定位到具体文件）。</summary>
public sealed record LockedBatch(IReadOnlyList<string> Paths, IReadOnlyList<LockingProcess> Processes);

/// <summary>按批注册的占用归属结果。</summary>
public sealed record LockAttribution(
    IReadOnlyList<LockedBatch> Batches,
    int ScannedResources,
    int BatchSize,
    string? Error)
{
    public bool HasOccupancy => Batches.Count > 0;

    public int LockedResourceHint => Batches.Sum(static b => b.Paths.Count);

    public string Describe()
    {
        if (Error is not null) return $"占用定位未完成：{Error}";
        if (Batches.Count == 0) return $"已检查 {ScannedResources} 个文件，未发现占用。";

        var lines = new List<string>
        {
            $"已检查 {ScannedResources} 个文件（每批 {BatchSize} 个），{Batches.Count} 批被占用：",
        };

        foreach (var batch in Batches.Take(10))
        {
            lines.Add($"  · 批次含 {batch.Paths.Count} 个文件（例：{batch.Paths[0]}），持有者："
                      + string.Join("、", batch.Processes.Select(static p => p.Describe())));
        }

        if (Batches.Count > 10) lines.Add($"  · …还有 {Batches.Count - 10} 批");

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>结束进程的拒绝原因。</summary>
public enum KillRefusal
{
    /// <summary>允许结束。</summary>
    None = 0,

    /// <summary>关键系统进程（PID 0/4 或系统组件）。</summary>
    SystemCritical = 1,

    /// <summary>就是本工具自身或它的父进程。</summary>
    SelfOrAncestor = 2,

    /// <summary>资源管理器（结束会让桌面闪断，默认拒绝）。</summary>
    Shell = 3,

    /// <summary>系统服务（默认拒绝）。</summary>
    Service = 4,

    /// <summary>进程已不存在。</summary>
    Gone = 5,

    /// <summary>进程身份不匹配（PID 复用），拒绝结束。</summary>
    IdentityMismatch = 6,
}

/// <summary>结束进程前的护栏判定结果。</summary>
public sealed record KillGuardResult(bool Allowed, KillRefusal Reason, string Message)
{
    public static KillGuardResult Allow { get; } = new(true, KillRefusal.None, "可以通过护栏检查。");

    public static KillGuardResult Refuse(KillRefusal reason, string message) => new(false, reason, message);
}

/// <summary>进程信息探测（抽出来是为了让护栏逻辑能被确定性地测试）。</summary>
public interface IProcessProbe
{
    bool TryGetStartTimeTicks(uint processId, out long ticks);

    bool TryGetName(uint processId, out string name);

    bool Exists(uint processId);
}

/// <summary>基于 <see cref="Process"/> 的实现。</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public static SystemProcessProbe Instance { get; } = new();

    public bool TryGetStartTimeTicks(uint processId, out long ticks)
    {
        ticks = 0;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            ticks = process.StartTime.ToUniversalTime().Ticks;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryGetName(uint processId, out string name)
    {
        name = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Exists(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>结束进程的策略开关（默认最保守）。</summary>
public sealed record KillPolicy
{
    /// <summary>是否允许结束资源管理器（默认否：会让桌面闪断）。</summary>
    public bool AllowShell { get; init; }

    /// <summary>是否允许结束系统服务（默认否）。</summary>
    public bool AllowServices { get; init; }

    public static KillPolicy Default { get; } = new();
}

/// <summary>文件占用服务（Restart Manager）。</summary>
public interface IFileLockService
{
    /// <summary>检测这些路径（目录或文件）当前被哪些进程占用。</summary>
    OccupancyReport FindLockingProcesses(IEnumerable<string> paths);

    /// <summary>
    /// 把占用**定位到具体文件**：按批注册，返回被占用的批次（每批附带持有进程）。
    /// 代价是每批一个 RM 会话，因此只适合"从几十个文件里找出罪魁祸首"，不适合全树扫描。
    /// </summary>
    LockAttribution FindLockedResources(IEnumerable<string> paths, int batchSize = 64, int maxResources = 5000);

    /// <summary>结束进程前的护栏判定（关键进程 / 自身 / PID 复用 / 服务）。</summary>
    KillGuardResult CanKill(LockingProcess process);

    /// <summary>
    /// 结束进程。**必须传 <see cref="LockingProcess"/> 而不是裸 PID**：
    /// 实现会复核进程创建时间，防止 PID 复用导致误杀无关进程。
    /// </summary>
    bool TryKill(LockingProcess process, out string error);
}

/// <summary>
/// Restart Manager 封装（需求「文件占用处理」）。
///
/// 设计取舍：
/// <list type="number">
/// <item><b>默认不逐文件注册</b>：RM 的资源注册是逐路径的，10 万文件逐个注册既慢又容易撞上批量上限。
/// 因此默认按"源目录 + 前 N 个顶层文件"注册，并在报告里如实说明注册规模。</item>
/// <item><b>需要"到底哪个文件被占用"时用 <see cref="FindLockedResources"/></b>：
/// 分批注册 + 逐批查询，把占用缩小到某一批文件。</item>
/// <item><b>结束进程必须过护栏</b>：关键系统进程、本工具自身/父进程、资源管理器、系统服务默认拒绝；
/// 且复核进程创建时间，避免 PID 复用误杀。</item>
/// </list>
/// </summary>
public sealed class RestartManagerLockService : IFileLockService
{
    public const int CchRmSessionKey = 32;
    public const int CchRmMaxAppName = 255;
    public const int CchRmMaxServiceName = 63;
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int RmInvalidSession = -1;
    private const int MaxGetListRetries = 4;

    /// <summary>默认注册的顶层文件数上限（目录本身始终注册）。</summary>
    public int MaxTopLevelFiles { get; init; } = 256;

    /// <summary>是否逐文件注册（慢，但覆盖完整）。</summary>
    public bool RegisterEveryFile { get; init; }

    public KillPolicy KillPolicy { get; init; } = KillPolicy.Default;

    public IProcessProbe ProcessProbe { get; init; } = SystemProcessProbe.Instance;

    public OccupancyReport FindLockingProcesses(IEnumerable<string> paths)
    {
        var resources = CollectResources(paths).ToList();
        if (resources.Count == 0) return OccupancyReport.Empty;

        return QueryOnce(resources);
    }

    public LockAttribution FindLockedResources(IEnumerable<string> paths, int batchSize = 64, int maxResources = 5000)
    {
        var size = Math.Clamp(batchSize, 1, 2048);

        var resources = paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Take(Math.Max(1, maxResources))
            .Select(static p => PathNormalizer.NormalizeAbsolute(p))
            .Where(static p => ReparsePointOps.Inspect(p).PathExists)
            .Select(PathNormalizer.StripExtendedPrefix)
            .ToList();

        if (resources.Count == 0) return new LockAttribution(Array.Empty<LockedBatch>(), 0, size, null);

        var batches = new List<LockedBatch>();
        string? firstError = null;

        for (var offset = 0; offset < resources.Count; offset += size)
        {
            var slice = resources.Skip(offset).Take(size).ToList();
            var report = QueryOnce(slice);

            if (report.Error is not null && firstError is null) firstError = report.Error;
            if (report.HasOccupancy) batches.Add(new LockedBatch(slice, report.Processes));
        }

        return new LockAttribution(batches, resources.Count, size, firstError);
    }

    public KillGuardResult CanKill(LockingProcess process)
    {
        // 1) PID 0 / 4 是内核伪进程，任何情况下都不能碰。
        if (process.ProcessId <= 4)
        {
            return KillGuardResult.Refuse(KillRefusal.SystemCritical,
                $"PID {process.ProcessId} 是内核伪进程，拒绝结束。");
        }

        // 2) 不能结束自己（否则会把自己一起带走）。
        var current = (uint)Environment.ProcessId;
        if (process.ProcessId == current)
        {
            return KillGuardResult.Refuse(KillRefusal.SelfOrAncestor, "这是本工具自身，拒绝结束。");
        }

        // 3) 关键系统组件黑名单。
        if (ProcessProbe.TryGetName(process.ProcessId, out var name) && IsCriticalName(name))
        {
            return KillGuardResult.Refuse(KillRefusal.SystemCritical,
                $"{name} 属于关键系统组件，结束它可能导致系统不稳定，拒绝。");
        }

        // 4) 资源管理器：默认拒绝（结束会让桌面与任务栏闪断）。
        if (!KillPolicy.AllowShell && !string.IsNullOrEmpty(process.AppName)
            && process.AppName.Contains("explorer", StringComparison.OrdinalIgnoreCase))
        {
            return KillGuardResult.Refuse(KillRefusal.Shell,
                "这是资源管理器（explorer.exe）。结束它会让桌面闪断；如确需，请在选项中显式允许。");
        }

        // 5) 服务：默认拒绝。
        if (!KillPolicy.AllowServices && !string.IsNullOrWhiteSpace(process.ServiceShortName))
        {
            return KillGuardResult.Refuse(KillRefusal.Service,
                $"这是系统服务（{process.ServiceShortName}）。结束服务请用服务管理器；如确需，请在选项中显式允许。");
        }

        // 6) 进程存在性 + 身份复核（防 PID 复用）。
        if (!ProcessProbe.Exists(process.ProcessId))
        {
            return KillGuardResult.Refuse(KillRefusal.Gone, "进程已不存在。");
        }

        if (process.StartTimeTicks != 0
            && ProcessProbe.TryGetStartTimeTicks(process.ProcessId, out var actual)
            && actual != process.StartTimeTicks)
        {
            return KillGuardResult.Refuse(KillRefusal.IdentityMismatch,
                $"PID {process.ProcessId} 已被复用（创建时间不匹配：报告 {process.StartTimeTicks}，当前 {actual}），拒绝结束。");
        }

        return KillGuardResult.Allow;
    }

    public bool TryKill(LockingProcess process, out string error)
    {
        var guard = CanKill(process);
        if (!guard.Allowed)
        {
            error = guard.Message;
            return false;
        }

        try
        {
            using var target = Process.GetProcessById((int)process.ProcessId);

            // 复核一次：CanKill 与真正的 Kill 之间仍有极小的竞态窗口。
            if (process.StartTimeTicks != 0)
            {
                try
                {
                    var actual = target.StartTime.ToUniversalTime().Ticks;
                    if (actual != process.StartTimeTicks)
                    {
                        error = "进程身份在结束前发生变化（PID 复用），已放弃。";
                        return false;
                    }
                }
                catch (Exception)
                {
                    // 个别受保护进程不允许读 StartTime：护栏已放行，这里不再阻止。
                }
            }

            target.Kill();
            target.WaitForExit(5000);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            error = "进程已不存在。";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsCriticalName(string name)
    {
        // 列表刻意保守：漏列只是少一个保护，误列会让用户失去一个正常功能；
        // 而**误杀**关键组件会让系统蓝屏或会话崩溃，代价不对称。
        return name.ToLowerInvariant() switch
        {
            "system" or "registry" or "smss" or "csrss" or "wininit" or "services"
                or "lsass" or "winlogon" or "svchost" or "dwm" or "fontdrvhost"
                or "memory compression" or "msmpeng" or "securityhealthservice"
                or "audiodg" or "logonui" or "sihost" or "ctfmon" => true,
            _ => false,
        };
    }

    /// <summary>一次 RM 会话：注册 + 查询（含 ERROR_MORE_DATA 重试）。</summary>
    private OccupancyReport QueryOnce(IReadOnlyList<string> resources)
    {
        var sessionKey = new char[CchRmSessionKey + 1];
        var status = RmStartSession(out var session, 0, sessionKey);
        if (status != ErrorSuccess)
            return new OccupancyReport(Array.Empty<LockingProcess>(), 0, $"RmStartSession 失败：{Win32Error.Describe(status)}");

        var stringHandles = new List<IntPtr>();
        var pointerArray = IntPtr.Zero;

        try
        {
            foreach (var resource in resources) stringHandles.Add(Marshal.StringToHGlobalUni(resource));

            pointerArray = Marshal.AllocHGlobal(IntPtr.Size * stringHandles.Count);
            for (var i = 0; i < stringHandles.Count; i++)
                Marshal.WriteIntPtr(pointerArray, i * IntPtr.Size, stringHandles[i]);

            status = RmRegisterResources(session, (uint)resources.Count, pointerArray, 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (status != ErrorSuccess)
            {
                return new OccupancyReport(Array.Empty<LockingProcess>(), resources.Count,
                    $"RmRegisterResources 失败：{Win32Error.Describe(status)}（已注册资源 {resources.Count} 项）");
            }

            var elementSize = Marshal.SizeOf<RmProcessInfo>();

            for (var attempt = 0; attempt < MaxGetListRetries; attempt++)
            {
                uint needed = 0;
                uint count = 0;

                status = RmGetList(session, out needed, ref count, IntPtr.Zero, out _);

                if (status == ErrorSuccess && needed == 0)
                    return new OccupancyReport(Array.Empty<LockingProcess>(), resources.Count, null);

                if (status != ErrorMoreData && status != ErrorSuccess)
                {
                    return new OccupancyReport(Array.Empty<LockingProcess>(), resources.Count,
                        $"RmGetList 失败：{Win32Error.Describe(status)}");
                }

                count = needed;
                var buffer = Marshal.AllocHGlobal((int)(elementSize * Math.Max(1, count)));

                try
                {
                    status = RmGetList(session, out needed, ref count, buffer, out _);

                    // 期间又有新进程加入：重试，拿更大的缓冲。
                    if (status == ErrorMoreData) continue;

                    if (status != ErrorSuccess)
                    {
                        return new OccupancyReport(Array.Empty<LockingProcess>(), resources.Count,
                            $"RmGetList(数据) 失败：{Win32Error.Describe(status)}");
                    }

                    var processes = new List<LockingProcess>((int)count);
                    for (var i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<RmProcessInfo>(buffer + i * elementSize);
                        processes.Add(new LockingProcess(
                            AppName: ToString(info.strAppName, CchRmMaxAppName + 1),
                            ProcessId: info.Process.dwProcessId,
                            ServiceShortName: NullIfEmpty(ToString(info.strServiceShortName, CchRmMaxServiceName + 1)),
                            ApplicationType: DescribeAppType(info.ApplicationType),
                            Restartable: info.bRestartable != 0,
                            StartTimeTicks: FileTimeToTicks(info.Process.ProcessStartTime)));
                    }

                    return new OccupancyReport(processes, resources.Count, null);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return new OccupancyReport(Array.Empty<LockingProcess>(), resources.Count,
                "RmGetList 连续返回 ERROR_MORE_DATA（进程列表持续变化），已放弃本次检测。");
        }
        catch (Exception ex)
        {
            return new OccupancyReport(Array.Empty<LockingProcess>(), resources.Count, ex.Message);
        }
        finally
        {
            if (pointerArray != IntPtr.Zero) Marshal.FreeHGlobal(pointerArray);
            foreach (var handle in stringHandles) Marshal.FreeHGlobal(handle);
            if (session != RmInvalidSession) RmEndSession(session);
        }
    }

    private IEnumerable<string> CollectResources(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            string extended;
            try
            {
                extended = PathNormalizer.NormalizeAbsolute(path);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!ReparsePointOps.Inspect(extended).PathExists) continue;

            yield return PathNormalizer.StripExtendedPrefix(extended);

            if (!RegisterEveryFile) continue;

            foreach (var file in EnumerateFilesBounded(extended, MaxTopLevelFiles))
                yield return file;
        }
    }

    /// <summary>注册模式下的补充资源（有界，避免大目录树把 API 打爆）。</summary>
    private IEnumerable<string> EnumerateFilesBounded(string extendedDirectory, int limit)
    {
        var produced = 0;
        var searchPath = PathNormalizer.EnsureTrailingSeparator(extendedDirectory) + "*";
        var handle = Kernel32.FindFirstFileEx(
            searchPath, Kernel32.FindExInfoBasic, out var data,
            Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

        if (handle == Kernel32.InvalidHandleValue) yield break;

        try
        {
            while (produced < limit)
            {
                var name = Kernel32.GetFileName(data);
                var isDirectory = (data.dwFileAttributes & Kernel32.FileAttributeDirectory) != 0;
                var isReparse = (data.dwFileAttributes & Kernel32.FileAttributeReparsePoint) != 0;

                if (name is not ("." or "..") && !isDirectory && !isReparse)
                {
                    produced++;
                    yield return PathNormalizer.StripExtendedPrefix(PathNormalizer.Combine(extendedDirectory, name));
                }

                if (!Kernel32.FindNextFile(handle, out data)) break;
            }
        }
        finally
        {
            Kernel32.FindClose(handle);
        }
    }

    private static long FileTimeToTicks(Kernel32.FileTime time)
        => ((long)time.dwHighDateTime << 32) | time.dwLowDateTime;

    private static string ToString(char[]? buffer, int length)
    {
        if (buffer is null) return string.Empty;

        var span = buffer.AsSpan(0, Math.Min(length, buffer.Length));
        var terminator = span.IndexOf('\0');
        if (terminator >= 0) span = span[..terminator];
        return new string(span);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DescribeAppType(int type) => type switch
    {
        0 => "未知",
        1 => "主窗口",
        2 => "其它窗口",
        3 => "服务",
        4 => "资源管理器",
        5 => "控制台",
        1000 => "关键系统进程",
        _ => type.ToString(),
    };

    // ---------------- Restart Manager P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public uint dwProcessId;
        public Kernel32.FileTime ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = CchRmMaxAppName + 1)]
        public char[] strAppName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = CchRmMaxServiceName + 1)]
        public char[] strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        public int bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RmStartSession(out int pSessionHandle, int dwSessionFlags, char[] strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RmRegisterResources(
        int dwSessionHandle, uint nFiles, IntPtr rgsFilenames,
        uint nApplications, IntPtr rgApplications, uint nServices, IntPtr rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RmGetList(
        int dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, IntPtr rgAffectedApps, out uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RmEndSession(int dwSessionHandle);
}
