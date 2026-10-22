using DirPorter.Core.RestartManager;
using Xunit;

namespace DirPorter.Tests;

/// <summary>可注入的进程信息桩，让"结束进程"的护栏逻辑可以被确定性地测试（不会真的结束任何进程）。</summary>
internal sealed class FakeProcessProbe : IProcessProbe
{
    public Dictionary<uint, (string Name, long Ticks)> Processes { get; } = new();

    public FakeProcessProbe With(uint pid, string name, long ticks = 1000)
    {
        Processes[pid] = (name, ticks);
        return this;
    }

    public bool Exists(uint processId) => Processes.ContainsKey(processId);

    public bool TryGetStartTimeTicks(uint processId, out long ticks)
    {
        if (Processes.TryGetValue(processId, out var value)) { ticks = value.Ticks; return true; }
        ticks = 0;
        return false;
    }

    public bool TryGetName(uint processId, out string name)
    {
        if (Processes.TryGetValue(processId, out var value)) { name = value.Name; return true; }
        name = string.Empty;
        return false;
    }
}

/// <summary>
/// R5：Restart Manager 深化。
///
/// 重点在**护栏**：结束进程是这个工具里最不可逆的动作之一，
/// 因此"什么情况下必须拒绝"要比"能不能杀"更值得被测试。
/// </summary>
public class R5_RestartManagerTests
{
    private static (RestartManagerLockService Service, FakeProcessProbe Probe) Create(
        KillPolicy? policy = null, FakeProcessProbe? probe = null)
    {
        var fake = probe ?? new FakeProcessProbe();
        return (new RestartManagerLockService { ProcessProbe = fake, KillPolicy = policy ?? KillPolicy.Default }, fake);
    }

    private static LockingProcess Process(
        uint pid, string app = "app.exe", string? service = null, long ticks = 1000)
        => new(app, pid, service, "主窗口", Restartable: true, StartTimeTicks: ticks);

    // ============================ 护栏 ============================

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public void 内核伪进程_一律拒绝(uint pid)
    {
        var (service, _) = Create();
        var result = service.CanKill(Process(pid, "System"));

        Assert.False(result.Allowed);
        Assert.Equal(KillRefusal.SystemCritical, result.Reason);
    }

    [Fact]
    public void 本工具自身_拒绝结束()
    {
        var (service, probe) = Create();
        probe.With((uint)Environment.ProcessId, "dirporter");

        var result = service.CanKill(Process((uint)Environment.ProcessId, "dirporter"));

        Assert.False(result.Allowed);
        Assert.Equal(KillRefusal.SelfOrAncestor, result.Reason);
    }

    [Theory]
    [InlineData("lsass")]
    [InlineData("csrss")]
    [InlineData("svchost")]
    [InlineData("wininit")]
    [InlineData("System")]
    public void 关键系统组件_拒绝结束(string name)
    {
        var (service, probe) = Create();
        probe.With(1234, name);

        var result = service.CanKill(Process(1234, name));

        Assert.False(result.Allowed);
        Assert.Equal(KillRefusal.SystemCritical, result.Reason);
    }

    [Fact]
    public void 资源管理器_默认拒绝_显式允许后放行()
    {
        var (strict, probeA) = Create();
        probeA.With(2000, "explorer");
        var refused = strict.CanKill(Process(2000, "explorer"));
        Assert.False(refused.Allowed);
        Assert.Equal(KillRefusal.Shell, refused.Reason);

        var (relaxed, probeB) = Create(new KillPolicy { AllowShell = true });
        probeB.With(2000, "explorer");
        Assert.True(relaxed.CanKill(Process(2000, "explorer")).Allowed);
    }

    [Fact]
    public void 系统服务_默认拒绝_显式允许后放行()
    {
        var (strict, probeA) = Create();
        probeA.With(3000, "some-svc");
        var refused = strict.CanKill(Process(3000, "some-svc", service: "SomeService"));
        Assert.False(refused.Allowed);
        Assert.Equal(KillRefusal.Service, refused.Reason);

        var (relaxed, probeB) = Create(new KillPolicy { AllowServices = true });
        probeB.With(3000, "some-svc");
        Assert.True(relaxed.CanKill(Process(3000, "some-svc", service: "SomeService")).Allowed);
    }

    [Fact]
    public void 进程已不存在_拒绝()
    {
        var (service, _) = Create();
        var result = service.CanKill(Process(9999, "ghost"));

        Assert.False(result.Allowed);
        Assert.Equal(KillRefusal.Gone, result.Reason);
    }

    /// <summary>
    /// ★ PID 复用防护：报告里的 PID 与实际进程的创建时间不符时，必须拒绝。
    /// 没有这一条，"结束占用进程"就可能在系统繁忙时打死一个完全无关的新进程。
    /// </summary>
    [Fact]
    public void PID被复用_创建时间不匹配_拒绝结束()
    {
        var (service, probe) = Create();
        probe.With(4000, "editor", ticks: 7777);                 // 当前实际创建时间

        var stale = Process(4000, "editor", ticks: 1111);         // 报告里记录的旧创建时间
        var result = service.CanKill(stale);

        Assert.False(result.Allowed);
        Assert.Equal(KillRefusal.IdentityMismatch, result.Reason);
        Assert.Contains("已被复用", result.Message);
    }

    [Fact]
    public void 普通进程_允许结束()
    {
        var (service, probe) = Create();
        probe.With(5000, "notepad", ticks: 4242);

        var result = service.CanKill(Process(5000, "notepad", ticks: 4242));

        Assert.True(result.Allowed);
        Assert.Equal(KillRefusal.None, result.Reason);
    }

    [Fact]
    public void TryKill_被护栏拒绝时不执行任何结束动作()
    {
        var (service, _) = Create();

        // PID 4 是内核伪进程，护栏必然拒绝；这里验证"拒绝即不动作"
        var killed = service.TryKill(Process(4, "System"), out var error);

        Assert.False(killed);
        Assert.Contains("内核伪进程", error);
    }

    [Fact]
    public void 进程身份字符串_包含PID与创建时间()
    {
        Assert.Equal("1234@5678", LockingProcess.BuildIdentity(1234, 5678));
        Assert.Equal("1234@5678", Process(1234, ticks: 5678).Identity);
    }

    // ============================ 真实 RM 调用 ============================

    [Fact]
    public void 真实占用检测_能报出持有文件的进程()
    {
        using var ws = new TempWorkspace("r5-rm-locked");
        var file = ws.CreateFile("locked.txt", "content");

        // 独占持有该文件：Restart Manager 应能报出持有者（即本进程）
        using var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var report = new RestartManagerLockService().FindLockingProcesses(new[] { file });

        Assert.Null(report.Error);
        Assert.True(report.HasOccupancy, "独占持有文件时 Restart Manager 应报告占用；" + report.Describe());
        Assert.Contains(report.Processes, p => p.ProcessId == (uint)Environment.ProcessId);
    }

    [Fact]
    public void 真实占用检测_未被占用时返回空()
    {
        using var ws = new TempWorkspace("r5-rm-free");
        var file = ws.CreateFile("free.txt", "content");

        var report = new RestartManagerLockService().FindLockingProcesses(new[] { file });

        Assert.Null(report.Error);
        Assert.False(report.HasOccupancy, report.Describe());
    }

    [Fact]
    public void 按批定位_只报告被占用的批次()
    {
        using var ws = new TempWorkspace("r5-rm-batch");
        var free1 = ws.CreateFile("free1.txt", "a");
        var locked = ws.CreateFile("locked.txt", "b");
        var free2 = ws.CreateFile("free2.txt", "c");

        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var attribution = new RestartManagerLockService()
            .FindLockedResources(new[] { free1, locked, free2 }, batchSize: 2);

        Assert.Null(attribution.Error);
        Assert.True(attribution.HasOccupancy);
        Assert.Equal(3, attribution.ScannedResources);
        Assert.Equal(2, attribution.BatchSize);

        // 被占用的那个文件必须落在被报告的批次里
        Assert.Contains(attribution.Batches, b => b.Paths.Contains(locked, StringComparer.OrdinalIgnoreCase));
        Assert.All(attribution.Batches, b => Assert.Contains(b.Processes, p => p.ProcessId == (uint)Environment.ProcessId));
    }

    [Fact]
    public void 路径不存在时不注册_直接返回空()
    {
        using var ws = new TempWorkspace("r5-rm-missing");

        var report = new RestartManagerLockService().FindLockingProcesses(new[] { ws.PathOf("nope.txt") });

        Assert.False(report.HasOccupancy);
        Assert.Null(report.Error);
        Assert.Equal(0, report.RegisteredResourceCount);
    }

    [Fact]
    public void 占用报告描述_含进程名与PID()
    {
        var report = new OccupancyReport(
            new[] { new LockingProcess("notepad.exe", 4242, null, "主窗口", true, 1) }, 1, null);

        var text = report.Describe();
        Assert.Contains("notepad.exe", text);
        Assert.Contains("4242", text);
    }
}
