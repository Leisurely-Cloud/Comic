using System;
using Microsoft.UI.Dispatching;

namespace Comic.WinUI.Services;

/// <summary>
/// UI 线程调度抽象。
/// ViewModel 通过它把回调排回 UI 线程,而不是自己调用
/// <see cref="DispatcherQueue.GetForCurrentThread"/> —— 后者在非 UI 线程返回 null,
/// 会让 ViewModel 在单元测试里根本无法构造(一 TryEnqueue 就 NRE)。
/// </summary>
public interface IDispatcher
{
    /// <summary>把回调排入 UI 线程队列,返回是否入队成功。</summary>
    bool TryEnqueue(Action callback);
}

/// <summary>生产实现:包装 WinUI 的 <see cref="DispatcherQueue"/>。</summary>
public sealed class UiThreadDispatcher : IDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    public UiThreadDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue
            ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    /// <summary>
    /// 在 UI 线程上捕获当前 <see cref="DispatcherQueue"/> 并构造实例。
    /// 必须在 UI 线程调用,否则这里就会抛,而不是留到后面某次 TryEnqueue 才 NRE。
    /// </summary>
    public static UiThreadDispatcher CreateForCurrentThread()
    {
        var queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "当前线程没有 DispatcherQueue,UiThreadDispatcher 必须在 UI 线程上创建。");
        return new UiThreadDispatcher(queue);
    }

    public bool TryEnqueue(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _dispatcherQueue.TryEnqueue(() => callback());
    }
}
