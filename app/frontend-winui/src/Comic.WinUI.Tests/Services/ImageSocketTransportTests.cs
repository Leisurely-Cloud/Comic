using System.Net;
using System.Net.Sockets;
using System.Text;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

/// <summary>Use real loopback HTTP sockets, not a fake handler, to exercise broken image transfers.</summary>
[TestClass]
public sealed class ImageSocketTransportTests
{
    [TestMethod]
    public async Task SocketBodyStall_TimesOutAfterHeadersWithoutHoldingReaderForever()
    {
        await using var server = new ImageServer(3, async (_, token) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var client = CreateClient();
        using var service = new JmComicService(client, new JmSiteOptions { ImageReadTimeout = TimeSpan.FromMilliseconds(200) });
        using var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();

        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => service.ReadImageDataAsync(stream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task SocketBodyDisconnect_DoesNotAcceptTruncatedImageAsSuccess()
    {
        await using var server = new ImageServer(5, async (stream, token) =>
            await stream.WriteAsync(new byte[] { 1, 2 }, token));
        using var client = CreateClient();
        using var service = new JmComicService(client);
        using var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();

        await Assert.ThrowsAsync<IOException>(
            () => service.ReadImageDataAsync(stream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task SocketUserCancellation_RemainsCancellationRatherThanTimeout()
    {
        await using var server = new ImageServer(3, async (_, token) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var client = CreateClient();
        using var service = new JmComicService(client);
        using var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ReadImageDataAsync(stream, stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task SlowSocketWithProgress_ResetsIdleTimeoutAndReportsEveryByte()
    {
        await using var server = new ImageServer(4, async (stream, token) =>
        {
            for (byte value = 1; value <= 4; value++)
            {
                if (value > 1) await Task.Delay(200, token);
                await stream.WriteAsync(new byte[] { value }, token);
            }
        });
        using var client = CreateClient();
        using var service = new JmComicService(client, new JmSiteOptions { ImageReadTimeout = TimeSpan.FromMilliseconds(500) });
        using var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        var bytes = 0;

        var result = await service.ReadImageDataAsync(stream, CancellationToken.None,
            bytesDownloaded: count => bytes += count).WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, result);
        Assert.AreEqual(4, bytes);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OnlineImageStall_ExhaustsRetriesInsteadOfHanging(bool sendHeaders)
    {
        await using var server = new ImageServer(3, async (_, token) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, token), connections: 3, sendHeaders: sendHeaders);
        using var client = CreateClient();
        using var service = new JmComicService(client, new JmSiteOptions { ImageReadTimeout = TimeSpan.FromMilliseconds(100) });

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => service.FetchChapterImageAsync(new JmImageSource(server.Url.AbsoluteUri, 0))
                .WaitAsync(TimeSpan.FromSeconds(4)));

        Assert.AreEqual(3, server.RequestCount);
    }

    [TestMethod]
    public async Task OnlineImageCancellation_StopsCurrentSocketWithoutRetrying()
    {
        await using var server = new ImageServer(3, async (_, token) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var client = CreateClient();
        using var service = new JmComicService(client);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.FetchChapterImageAsync(new JmImageSource(server.Url.AbsoluteUri, 0), stop.Token));

        Assert.AreEqual(1, server.RequestCount);
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler { UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(5) };

    private sealed class ImageServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private int _requestCount;

        public ImageServer(int contentLength, Func<NetworkStream, CancellationToken, Task> writeBody,
            int connections = 1, bool sendHeaders = true)
        {
            _listener.Start();
            Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/neutral-test-image");
            _worker = ServeAsync(contentLength, writeBody, connections, sendHeaders);
        }

        public Uri Url { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync(int contentLength, Func<NetworkStream, CancellationToken, Task> writeBody,
            int connections, bool sendHeaders)
        {
            var handlers = new List<Task>();
            try
            {
                for (var index = 0; index < connections; index++)
                {
                    var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _requestCount);
                    handlers.Add(HandleAsync(connection, contentLength, writeBody, sendHeaders));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(handlers); }
        }

        private async Task HandleAsync(TcpClient connection, int contentLength,
            Func<NetworkStream, CancellationToken, Task> writeBody, bool sendHeaders)
        {
            using var ownedConnection = connection;
            try
            {
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {contentLength}\r\nConnection: close\r\n\r\n");
                if (sendHeaders) await stream.WriteAsync(headers, _stop.Token);
                await writeBody(stream, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _worker.WaitAsync(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }
}
