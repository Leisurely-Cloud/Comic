using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class JmImageTransportTests
{
    [TestMethod]
    public async Task StalledImageRead_TimesOutRatherThanHoldingDownloadSlotForever()
    {
        using var client = new HttpClient();
        using var service = new JmComicService(client, new JmSiteOptions { ImageReadTimeout = TimeSpan.FromMilliseconds(30) });
        using var stream = new BlockingStream();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => service.ReadImageDataAsync(stream, CancellationToken.None));
    }

    [TestMethod]
    public async Task UserCancellation_IsNotReportedAsNetworkTimeout()
    {
        using var client = new HttpClient();
        using var service = new JmComicService(client);
        using var stream = new BlockingStream();
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReadImageDataAsync(stream, source.Token));
    }

    [TestMethod]
    public async Task SpeedLimitWait_IsExcludedFromNetworkTimeout()
    {
        using var client = new HttpClient();
        using var service = new JmComicService(client, new JmSiteOptions { ImageReadTimeout = TimeSpan.FromMilliseconds(20) });
        using var stream = new MemoryStream([1, 2, 3]);
        var result = await service.ReadImageDataAsync(stream, CancellationToken.None, throttleBytes: (_, token) => Task.Delay(70, token));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, result);
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
