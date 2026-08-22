using Comic.WinUI.Services;

namespace Comic.WinUI.Tests.Services;

/// <summary>
/// 测试用调度器:把回调排队但不执行,由测试显式 Flush。
/// 真实的 DispatcherQueue 就是排队后异步执行的,这个替身让测试能精确控制
/// 「已入队、尚未执行」这个时间窗口 —— 过期回调类的 bug 只在这个窗口里发生。
/// </summary>
internal sealed class DeferredDispatcher : IDispatcher
{
    private readonly List<Action> _pending = [];

    public int PendingCount
    {
        get { lock (_pending) return _pending.Count; }
    }

    public bool TryEnqueue(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_pending) _pending.Add(callback);
        return true;
    }

    /// <summary>只执行最早入队的那个回调。</summary>
    public void FlushFirst()
    {
        Action callback;
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                throw new InvalidOperationException("没有待执行的回调。");
            }

            callback = _pending[0];
            _pending.RemoveAt(0);
        }

        callback();
    }

    /// <summary>等到至少有 <paramref name="count"/> 个回调入队。超时即抛,避免测试挂死。</summary>
    public async Task WaitForPendingAsync(int count, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (PendingCount < count)
        {
            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException(
                    $"等待 {count} 个回调入队超时,当前只有 {PendingCount} 个。");
            }

            await Task.Delay(5);
        }
    }
}
