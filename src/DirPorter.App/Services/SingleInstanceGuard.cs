using System.Threading;

namespace DirPorter.App.Services;

/// <summary>
/// 单实例守卫（需求「通用」：单实例运行）。
///
/// 两步机制：
/// <list type="number">
/// <item>命名 Mutex 判定所有者 —— 第二个实例拿不到所有权即退出。</item>
/// <item>命名事件把「又来一个实例」的意图转达给已有实例，让它把自己带到前台，
///       而不是简单地把新实例丢掉、让用户以为程序没反应。</item>
/// </list>
///
/// 命名用 <c>Local\</c>（每登录会话一个实例）而非 <c>Global\</c>：
/// 多用户 / 远程桌面场景下不应该互相顶掉对方的窗口。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\DirPorter.SingleInstance.Mutex";
    private const string ActivateEventName = @"Local\DirPorter.SingleInstance.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registration;
    private readonly bool _ownsMutex;

    private SingleInstanceGuard(bool ownsMutex) => _ownsMutex = ownsMutex;

    /// <summary>本进程是否是唯一实例（首个实例）。</summary>
    public bool IsOwner => _ownsMutex;

    public static SingleInstanceGuard Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return new SingleInstanceGuard(createdNew) { _mutex = mutex };
    }

    /// <summary>非所有者实例调用：唤醒已有实例把自己带到前台，然后本实例退出。</summary>
    public void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch
        {
            // 拿不到事件也无所谓：单实例语义（只有一个进程在跑）已经达成。
        }
    }

    /// <summary>所有者实例调用：注册等待，收到信号时在 UI 线程执行回调。</summary>
    public void ListenForActivation(Action onActivate)
    {
        if (!_ownsMutex) return;

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => onActivate(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activateEvent?.Dispose();

        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try { _mutex.ReleaseMutex(); } catch { /* 已被释放或未持有 */ }
            }

            _mutex.Dispose();
        }
    }
}
