using System.Net;

namespace Comic.WinUI.Tests.Services;

/// <summary>
/// 测试用的 HTTP 传输层替身。所有请求都在本地应答,保证测试永远不会真的访问禁漫天堂。
/// 直接 new HttpClient() 的测试在结构上是允许联网的,这个替身把那条路堵死。
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private readonly List<string> _requestedUris = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>所有请求一律返回失败状态,用于驱动“站点不可用”这类失败路径。</summary>
    public static FakeHttpMessageHandler AlwaysFails(
        HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable) =>
        new(_ => new HttpResponseMessage(statusCode));

    public IReadOnlyList<string> RequestedUris
    {
        get { lock (_requestedUris) return _requestedUris.ToList(); }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_requestedUris) _requestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
        return Task.FromResult(_responder(request));
    }
}
