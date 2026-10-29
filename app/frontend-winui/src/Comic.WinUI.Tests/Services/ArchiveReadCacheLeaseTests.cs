using System.IO.Compression;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ArchiveReadCacheLeaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task CancelledQueuedLeaseDoesNotInterruptActiveLeaseOrReleaseItsGate()
    {
        var fixture = CreateFixture();
        using var owner = new MemoryStream(fixture.Archive);
        using var cache = new ArchiveReadCache(owner);
        using var first = await cache.AcquireAsync(CancellationToken.None).WaitAsync(Timeout);
        using var firstStream = new MemoryStream(fixture.Archive);
        first.Attach(firstStream);
        using var cancellation = new CancellationTokenSource();
        var second = cache.AcquireAsync(cancellation.Token);
        Assert.IsFalse(second.IsCompleted, "首个租约仍拥有读取权，第二个请求必须等待。");

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second.WaitAsync(Timeout));
        CollectionAssert.AreEqual(fixture.Pages[0], await ReadEntryAsync(first, 0));
        var third = cache.AcquireAsync(CancellationToken.None);
        Assert.IsFalse(third.IsCompleted, "取消排队请求不能释放仍被首个租约持有的读取权。");

        first.Dispose();
        using var resumed = await third.WaitAsync(Timeout);
        using var thirdStream = new MemoryStream(fixture.Archive);
        resumed.Attach(thirdStream);
        CollectionAssert.AreEqual(fixture.Pages[1], await ReadEntryAsync(resumed, 1));
        Assert.IsTrue(firstStream.CanRead, "释放租约不能接管调用方流的所有权。");
    }

    [TestMethod]
    public async Task DisposeWhileActiveAndQueuedDefersCleanupAndQueuedRequestFailsAsUnauthorized()
    {
        var fixture = CreateFixture();
        using var owner = new MemoryStream(fixture.Archive);
        using var cache = new ArchiveReadCache(owner);
        using var first = await cache.AcquireAsync(CancellationToken.None).WaitAsync(Timeout);
        using var firstStream = new MemoryStream(fixture.Archive);
        first.Attach(firstStream);
        var queued = cache.AcquireAsync(CancellationToken.None);
        Assert.IsFalse(queued.IsCompleted);

        cache.Dispose();
        cache.Dispose();
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => { _ = cache.Entries; });
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => cache.AcquireAsync(CancellationToken.None).WaitAsync(Timeout));
        Assert.IsFalse(queued.IsCompleted, "关闭缓存后，排队请求仍须等活动租约安全释放。");
        CollectionAssert.AreEqual(fixture.Pages[2], await ReadEntryAsync(first, 2));

        first.Dispose();
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => queued.WaitAsync(Timeout));
        Assert.IsTrue(firstStream.CanRead);
        Assert.IsTrue(owner.CanRead);
    }

    [TestMethod]
    public async Task PreCancelledAcquireDoesNotOccupyGate()
    {
        var fixture = CreateFixture();
        using var owner = new MemoryStream(fixture.Archive);
        using var cache = new ArchiveReadCache(owner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.AcquireAsync(cancellation.Token).WaitAsync(Timeout));

        using var lease = await cache.AcquireAsync(CancellationToken.None).WaitAsync(Timeout);
        using var stream = new MemoryStream(fixture.Archive);
        lease.Attach(stream);
        CollectionAssert.AreEqual(fixture.Pages[0], await ReadEntryAsync(lease, 0));
    }

    [TestMethod]
    public void InvalidArchiveConstructionDoesNotDisposeOwnerStream()
    {
        byte[] invalid = [1, 2, 3, 4, 5];
        using var owner = new MemoryStream(invalid);

        Assert.ThrowsExactly<InvalidDataException>(() => new ArchiveReadCache(owner));

        Assert.IsTrue(owner.CanRead);
        owner.Position = 0;
        CollectionAssert.AreEqual(invalid, owner.ToArray());
        Assert.AreEqual(1, owner.ReadByte());
    }

    [TestMethod]
    public void ConstructionAndDisposeDetachWithoutDisposingOwnerStream()
    {
        var fixture = CreateFixture();
        using var owner = new MemoryStream(fixture.Archive);
        using var cache = new ArchiveReadCache(owner);
        Assert.IsTrue(owner.CanRead);
        Assert.AreEqual(fixture.Pages.Length, cache.Entries.Count);
        owner.Position = 0;
        Assert.AreEqual((int)fixture.Archive[0], owner.ReadByte());

        cache.Dispose();

        Assert.IsTrue(owner.CanRead);
        owner.Position = 0;
        Assert.AreEqual((int)fixture.Archive[0], owner.ReadByte());
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => { _ = cache.Entries; });
    }

    [TestMethod]
    public async Task ReattachedStreamsWithDifferentStartingPositionsReadCorrectEntries()
    {
        var fixture = CreateFixture();
        using var owner = new MemoryStream(fixture.Archive);
        using var cache = new ArchiveReadCache(owner);
        owner.Dispose();
        int[] entryIndexes = [1, 0, 2, 1, 0];
        long[] positions = [fixture.Archive.Length - 7, 1, 0, fixture.Archive.Length / 2, fixture.Archive.Length];

        for (var index = 0; index < entryIndexes.Length; index++)
        {
            using var lease = await cache.AcquireAsync(CancellationToken.None).WaitAsync(Timeout);
            using var stream = new MemoryStream(fixture.Archive) { Position = positions[index] };
            lease.Attach(stream);
            CollectionAssert.AreEqual(fixture.Pages[entryIndexes[index]], await ReadEntryAsync(lease, entryIndexes[index]));
            lease.Dispose();
            Assert.IsTrue(stream.CanRead);
        }
    }

    private static async Task<byte[]> ReadEntryAsync(ArchiveReadCache.ReadLease lease, int index)
    {
        using var entry = lease.Entries[index].Open();
        using var output = new MemoryStream();
        await entry.CopyToAsync(output).WaitAsync(Timeout);
        return output.ToArray();
    }

    private static (byte[] Archive, byte[][] Pages) CreateFixture()
    {
        byte[][] pages = [[1, 2, 3, 4], Enumerable.Range(0, 16_387).Select(index => (byte)(index % 251)).ToArray(), [9, 8, 7, 6, 5]];
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < pages.Length; index++)
            {
                using var entry = archive.CreateEntry($"{index}.png", index == 1 ? CompressionLevel.Optimal : CompressionLevel.NoCompression).Open();
                entry.Write(pages[index]);
            }
        }
        return (output.ToArray(), pages);
    }
}
