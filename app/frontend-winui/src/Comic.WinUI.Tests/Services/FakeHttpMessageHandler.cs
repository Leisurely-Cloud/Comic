using System.Net;

namespace Comic.WinUI.Tests.Services;

/// <summary>
/// 测试用的 HTTP 传输层替身。所有请求都在本地应答,保证测试永远不会真的访问禁漫天堂。
/// 直接 new HttpClient() 的测试在结构上是允许联网的,这个替身把那条路堵死。
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly List<string> _requestedUris = [];
    private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = (request, _) => Task.FromResult(responder(request));
    }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    /// <summary>所有请求一律返回失败状态,用于驱动“站点不可用”这类失败路径。</summary>
    public static FakeHttpMessageHandler AlwaysFails(
        HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable) =>
        new(_ => new HttpResponseMessage(statusCode));

    /// <summary>请求开始后一直等待,直到被测代码传播取消信号。</summary>
    public static FakeHttpMessageHandler BlocksUntilCancelled() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    public IReadOnlyList<string> RequestedUris
    {
        get { lock (_requestedUris) return _requestedUris.ToList(); }
    }

    public Task WaitForRequestAsync(TimeSpan timeout) => _requestStarted.Task.WaitAsync(timeout);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_requestedUris) _requestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
        _requestStarted.TrySetResult();
        return await _responder(request, cancellationToken);
    }
}
