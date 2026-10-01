namespace Comic.WinUI.Services.Native;

public sealed partial class JmComicService
{
    private async Task<HttpResponseMessage> SendImageRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ImageReadTimeout);
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("图片服务器长时间未响应，正在重试。");
        }
    }

    internal async Task<byte[]> ReadImageDataAsync(Stream input, CancellationToken cancellationToken,
        Action<int>? bytesDownloaded = null, Func<int, CancellationToken, Task>? throttleBytes = null)
    {
        using var imageBuffer = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ImageReadTimeout);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, timeout.Token).AsTask().WaitAsync(_options.ImageReadTimeout, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("图片传输长时间无数据，正在重试。");
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                throw new TimeoutException("图片传输长时间无数据，正在重试。");
            }
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read == 0) break;
            if (throttleBytes is not null) await throttleBytes(read, cancellationToken);
            await imageBuffer.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesDownloaded?.Invoke(read);
        }
        return imageBuffer.ToArray();
    }
}
