using System.Windows.Threading;

namespace DirPorter.App.Services;

/// <summary>
/// UI 输入防抖器（R12 性能修复）。
///
/// 背景：属性变化驱动的持久化/重算如果"每敲一键"同步执行（写盘、文件系统探测、
/// 遍历重算），输入会明显卡顿。本类把高频变化合并成"停止输入 延迟后执行一次"。
///
/// 测试说明：非 UI 环境（无消息泵）下 Tick 不会触发，用 <see cref="Flush"/> 手动结算；
/// 产品代码在关键节点（执行迁移前 / 应用退出前）也应 Flush 兜底，保证不丢最后一次。
/// </summary>
public sealed class UiDebouncer
{
    private readonly DispatcherTimer _timer;
    private Action? _pending;

    public UiDebouncer(TimeSpan delay)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        _timer.Tick += (_, _) => Flush();
    }

    /// <summary>登记一次待执行动作；延迟窗口内的重复调用只保留最后一次。</summary>
    public void Poke(Action action)
    {
        _pending = action;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>立即结算未决动作（没有待执行动作时是空操作）。</summary>
    public void Flush()
    {
        _timer.Stop();
        var action = _pending;
        _pending = null;
        if (action is null) return;

        // 防抖回调几乎都是"用户输入触发的重算"。一条非法输入（例如粘贴了带引号的路径）
        // 不该让整个应用退出 —— 实测曾直接崩到 DispatcherUnhandledException。
        // 这里兜住并写崩溃日志（便于定位），UX 仍由各页面自己的校验负责。
        try
        {
            action();
        }
        catch (Exception ex)
        {
            CrashReporter.WriteCrashLog("UiDebouncer", ex);
        }
    }
}
