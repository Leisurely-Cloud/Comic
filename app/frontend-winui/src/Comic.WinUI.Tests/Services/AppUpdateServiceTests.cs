using System.Net;
using System.Security.Cryptography;
using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class AppUpdateServiceTests
{
    private readonly string _downloads = Path.Combine(Path.GetTempPath(), "ComicUpdateTests", Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_downloads);

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_downloads, true);

    [TestMethod]
    public async Task Check_ParsesReleaseNotesAndSelectsSetupExecutable()
    {
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v999.0.0","name":"未来版本","body":"更新说明","html_url":"https://example.test/release","assets":[{"name":"ComicDownloader-999.0.0-Setup.exe","browser_download_url":"https://example.test/setup.exe","size":42}]}""")
        });
        using var client = new HttpClient(handler);
        var service = new AppUpdateService(client);

        var result = await service.CheckAsync();

        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("v999.0.0", result.LatestVersion);
        Assert.AreEqual("更新说明", result.ReleaseNotes);
        Assert.AreEqual("ComicDownloader-999.0.0-Setup.exe", result.AssetName);
    }

    [TestMethod]
    public async Task Check_PreservesAssetDigestForDownloadVerification()
    {
        var digest = "sha256:" + new string('a', 64);
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"tag_name":"v999.0.0","assets":[{"name":"ComicDownloader-999.0.0-Setup.exe","digest":"{{digest}}"}]}""")
        }));

        var result = await new AppUpdateService(client).CheckAsync();

        Assert.AreEqual(digest, result.AssetDigest);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(6)]
    public async Task Download_WrongBodyLengthPreservesPreviousInstaller(int actualLength)
    {
        var previous = WritePreviousInstaller();
        using var client = CreateClient(new StreamContent(new DownloadStream(new byte[actualLength])));
        var progress = new RecordedProgress();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateService(client).DownloadInstallerAsync(Update(4), progress));

        AssertPreviousInstaller(previous);
        Assert.IsFalse(progress.Values.Contains(100));
    }

    [TestMethod]
    public async Task Download_ConflictingContentLengthRejectsResponseWithoutReplacingInstaller()
    {
        var previous = WritePreviousInstaller();
        using var client = CreateClient(new ByteArrayContent(new byte[3]));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateService(client).DownloadInstallerAsync(Update(4)));

        AssertPreviousInstaller(previous);
    }

    [TestMethod]
    public async Task Download_WrongDigestPreservesPreviousInstaller()
    {
        var previous = WritePreviousInstaller();
        using var client = CreateClient(new ByteArrayContent(new byte[] { 1, 2, 3, 4 }));
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[] { 4, 3, 2, 1 }));
        var progress = new RecordedProgress();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateService(client).DownloadInstallerAsync(Update(4, digest), progress));

        AssertPreviousInstaller(previous);
        Assert.IsFalse(progress.Values.Contains(100));
    }

    [TestMethod]
    [DataRow("sha256:not-a-digest")]
    [DataRow("md5:abcd")]
    public async Task Download_InvalidDigestDoesNotRequestOrOverwriteInstaller(string digest)
    {
        var previous = WritePreviousInstaller();
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateService(client).DownloadInstallerAsync(Update(4, digest)));

        Assert.AreEqual(0, handler.RequestedUris.Count);
        AssertPreviousInstaller(previous);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Download_VerifiedBodyPublishesOnlyAfterValidation(bool includeDigest)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        using var client = CreateClient(new ByteArrayContent(bytes));
        var digest = includeDigest ? "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)) : "";
        var progress = new RecordedProgress();

        var path = await CreateService(client).DownloadInstallerAsync(Update(bytes.Length, digest), progress);

        Assert.AreEqual(Destination, path);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(100d, progress.Values[^1]);
        Assert.IsTrue(progress.Values.Take(progress.Values.Count - 1).All(value => value < 100));
        Assert.AreEqual(1, Directory.GetFiles(_downloads).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Download_StalledBodyTimesOutAndCleansOnlyItsTemporaryFile(bool ignoreCancellation)
    {
        var previous = WritePreviousInstaller();
        using var stream = new DownloadStream([], stall: true, ignoreCancellation: ignoreCancellation);
        using var client = CreateClient(new StreamContent(stream));
        using var stop = new CancellationTokenSource();
        var operation = CreateService(client, TimeSpan.FromMilliseconds(100))
            .DownloadInstallerAsync(Update(4), cancellationToken: stop.Token);
        try
        {
            Assert.AreSame(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(2))),
                "A stalled response must not leave the updater waiting indefinitely.");
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => operation);
            AssertPreviousInstaller(previous);
        }
        finally
        {
            stop.Cancel();
            stream.Dispose();
            try { await operation; } catch { }
        }
    }

    [TestMethod]
    public async Task Download_StalledResponseHeadersAlsoTimeOut()
    {
        var previous = WritePreviousInstaller();
        using var handler = FakeHttpMessageHandler.BlocksUntilCancelled();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var stop = new CancellationTokenSource();
        var operation = CreateService(client, TimeSpan.FromMilliseconds(100))
            .DownloadInstallerAsync(Update(4), cancellationToken: stop.Token);
        try
        {
            Assert.AreSame(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(2))));
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => operation);
            AssertPreviousInstaller(previous);
        }
        finally
        {
            stop.Cancel();
            try { await operation; } catch { }
        }
    }

    [TestMethod]
    public async Task Download_CallerCancellationIsNotReportedAsTimeout()
    {
        var previous = WritePreviousInstaller();
        using var stream = new DownloadStream([], stall: true);
        using var client = CreateClient(new StreamContent(stream));
        using var stop = new CancellationTokenSource();
        var operation = CreateService(client).DownloadInstallerAsync(Update(4), cancellationToken: stop.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        stop.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        AssertPreviousInstaller(previous);
    }

    [TestMethod]
    public async Task Download_SlowContinuousProgressResetsIdleTimeout()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
        using var client = CreateClient(new StreamContent(new DownloadStream(bytes, TimeSpan.FromMilliseconds(80))));

        var path = await CreateService(client, TimeSpan.FromMilliseconds(300)).DownloadInstallerAsync(Update(bytes.Length));

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task Download_ConcurrentCancellationDoesNotDeleteOtherSuccessfulDownload()
    {
        using var blocked = new DownloadStream([], stall: true);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var requests = 0;
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Interlocked.Increment(ref requests) == 1
                ? new StreamContent(blocked)
                : new ByteArrayContent(bytes)
        });
        using var client = new HttpClient(handler);
        using var stop = new CancellationTokenSource();
        var service = CreateService(client);
        var canceled = service.DownloadInstallerAsync(Update(4), cancellationToken: stop.Token);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var path = await service.DownloadInstallerAsync(Update(bytes.Length));
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            Assert.AreEqual(1, Directory.GetFiles(_downloads).Length);
        }
        finally
        {
            stop.Cancel();
            blocked.Dispose();
            try { await canceled; } catch { }
        }
    }

    private string Destination => Path.Combine(_downloads, "ComicDownloader-999.0.0-Setup.exe");

    private byte[] WritePreviousInstaller()
    {
        var bytes = new byte[] { 9, 8, 7 };
        File.WriteAllBytes(Destination, bytes);
        return bytes;
    }

    private void AssertPreviousInstaller(byte[] bytes)
    {
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Destination));
        Assert.AreEqual(1, Directory.GetFiles(_downloads).Length, "Incomplete files must be removed.");
    }

    private AppUpdateService CreateService(HttpClient client, TimeSpan? timeout = null) =>
        new(client, _downloads, timeout);

    private static AppUpdateInfo Update(long size, string digest = "") =>
        new("2.9.0", "v999.0.0", true, "test", "", "https://example.test/release",
            "ComicDownloader-999.0.0-Setup.exe", "https://example.test/setup.exe", size, digest);

    private static HttpClient CreateClient(HttpContent content) =>
        new(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

    private sealed class RecordedProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }

    private sealed class DownloadStream(byte[] bytes, TimeSpan delay = default, bool stall = false,
        bool ignoreCancellation = false) : Stream
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (stall)
            {
                await _released.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
                return 0;
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            var count = Math.Min(bytes.Length - _position, delay > TimeSpan.Zero ? 1 : buffer.Length);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _released.TrySetCanceled();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
