namespace DirPorter.Core.Copy;

/// <summary>
/// 暂停/继续闸门。线程安全，可被 UI 或 CLI 任意时刻切换。
///
/// 语义与需求「暂停 = 停止推进、保留已复制的数据；继续 = 从断点接着做」一致：
/// 暂停只是阻塞工作线程，**已经复制到目标的文件不会被回滚**；
/// 继续后由复制引擎按"目标已存在且大小/时间一致即跳过"的规则自动增量续做。
/// </summary>
public sealed class PauseGate
{
    private readonly ManualResetEventSlim _resume = new(initialState: true);

    /// <summary>当前是否处于暂停状态。</summary>
    public bool IsPaused => !_resume.IsSet;

    /// <summary>暂停。之后的复制进展会被阻塞，直到 <see cref="Resume"/>。</summary>
    public void Pause() => _resume.Reset();

    /// <summary>继续。</summary>
    public void Resume() => _resume.Set();

    /// <summary>
    /// 在工作线程内部等待恢复。<paramref name="isCanceled"/> 为 true 时立即返回 false（不再等待）。
    /// 返回 false 表示"应当中止当前工作"。
    /// </summary>
    public bool WaitWhilePaused(Func<bool> isCanceled)
    {
        while (IsPaused)
        {
            if (isCanceled()) return false;

            // 50ms 一轮：既能及时响应取消，又不会忙等烧 CPU。
            _resume.Wait(50);
        }

        return true;
    }

    /// <summary>暂停下的可取消等待（异步版本，供 workers 在文件之间使用）。</summary>
    public async Task<bool> WaitWhilePausedAsync(Func<bool> isCanceled, CancellationToken cancellationToken)
    {
        while (IsPaused)
        {
            if (isCanceled() || cancellationToken.IsCancellationRequested) return false;
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }
}

/// <summary>
/// 支持暂停/继续的复制引擎。
/// 单独开接口（而不是塞进 <see cref="IFileCopier"/>）是刻意的：
/// robocopy 引擎天然做不到真正的"暂停"（只能杀掉进程再重启），
/// 因此能力必须按引擎声明，调用方通过 `copier is IPausableCopier` 判断，不得假定所有引擎都能暂停。
/// </summary>
public interface IPausableCopier
{
    /// <summary>暂停闸门。UI 可绑定"暂停/继续"按钮。</summary>
    PauseGate Pause { get; }
}
