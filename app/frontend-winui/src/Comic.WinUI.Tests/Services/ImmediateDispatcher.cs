using Comic.WinUI.Services;

namespace Comic.WinUI.Tests.Services;

/// <summary>
/// 测试用调度器:直接同步执行回调,不涉及任何 UI 线程。
/// 生产代码里 TryEnqueue 是排队异步执行的,这里同步跑,所以测试断言可以写在
/// 调用之后,不必轮询等待。
/// </summary>
internal sealed class ImmediateDispatcher : IDispatcher
{
    /// <summary>入队次数,用于断言某条路径确实走了 UI 线程调度。</summary>
    public int EnqueueCount { get; private set; }

    public bool TryEnqueue(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnqueueCount++;
        callback();
        return true;
    }
}
