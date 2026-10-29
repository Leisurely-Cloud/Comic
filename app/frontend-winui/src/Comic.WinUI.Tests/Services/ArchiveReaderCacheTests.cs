using System.Buffers.Binary;
using System.IO.Compression;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ArchiveReaderCacheTests
{
    private string _root = null!;
    private ArchiveReaderService _service = null!;
    private readonly List<string> _archivePaths = [];

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "Comic.WinUI.ArchiveCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new ArchiveReaderService();
        _archivePaths.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var path in _archivePaths) _service.Close(path);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CachedArchive_ConcurrentAndRepeatedPagesReturnExactBytesAndReleaseFile(bool stored)
    {
        var pages = CreatePages(12, 7);
        var path = CreateArchive(stored ? "stored.cbz" : "deflated.zip", pages,
            stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        var original = File.ReadAllBytes(path);
        await _service.OpenAsync(path);
        AssertExclusiveAccess(path);
        var tokens = (await _service.GetChapterImagesAsync(path, "/")).Images;
        Assert.AreEqual(pages.Length, tokens.Count);

        for (var round = 0; round < 3; round++)
        {
            await Task.WhenAll(tokens.Select((token, index) => ReadExpectedAsync(token, pages[index])));
            AssertExclusiveAccess(path);
        }
        for (var index = tokens.Count - 1; index >= 0; index--)
        {
            await ReadExpectedAsync(tokens[index], pages[index]);
            AssertExclusiveAccess(path);
        }

        CollectionAssert.AreEqual(original, File.ReadAllBytes(path), "缓存和并发读取不能修改源压缩包。");
        CollectionAssert.AreEquivalent(new[] { path }, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task CachedArchives_ConcurrentSessionsRemainIndependentWhenOneCloses()
    {
        var firstPages = CreatePages(6, 13);
        var secondPages = CreatePages(6, 97);
        var first = CreateArchive("first.cbz", firstPages, CompressionLevel.Optimal);
        var second = CreateArchive("second.zip", secondPages, CompressionLevel.NoCompression);
        await Task.WhenAll(_service.OpenAsync(first), _service.OpenAsync(second));
        var firstTokens = (await _service.GetChapterImagesAsync(first, "/")).Images;
        var secondTokens = (await _service.GetChapterImagesAsync(second, "/")).Images;

        await Task.WhenAll(firstTokens.Select((token, index) => ReadExpectedAsync(token, firstPages[index]))
            .Concat(secondTokens.Select((token, index) => ReadExpectedAsync(token, secondPages[index]))));
        AssertExclusiveAccess(first);
        AssertExclusiveAccess(second);

        _service.Close(first);
        Assert.IsFalse(_service.IsAuthorizedArchive(first));
        Assert.IsTrue(_service.IsAuthorizedArchive(second));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _service.GetImageBytesAsync(firstTokens[0]));
        await Task.WhenAll(secondTokens.Select((token, index) => ReadExpectedAsync(token, secondPages[index])));
        AssertExclusiveAccess(first);
        AssertExclusiveAccess(second);
    }

    [TestMethod]
    public async Task CachedPage_ChangedStoredPayloadWithOriginalSizeAndTimestampStillFailsCrcAndReleasesFile()
    {
        var pages = CreatePages(1, 23);
        var path = CreateArchive("changed-payload.cbz", pages, CompressionLevel.NoCompression);
        var original = File.ReadAllBytes(path);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await _service.OpenAsync(path);
        var token = (await _service.GetChapterImagesAsync(path, "/")).Images.Single();
        await ReadExpectedAsync(token, pages[0]);
        AssertExclusiveAccess(path);

        PatchPreservingMetadata(path, bytes =>
        {
            Assert.AreEqual(0x04034b50u, Read32(bytes, 0));
            Assert.AreEqual((ushort)0, Read16(bytes, 8), "此回归需要 stored 条目，直接修改图片负载。");
            var payloadOffset = 30 + Read16(bytes, 26) + Read16(bytes, 28);
            bytes[payloadOffset + pages[0].Length / 2] ^= 0xff;
        });

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.GetImageBytesAsync(token));
        StringAssert.Contains(error.Message, "校验失败");
        AssertExclusiveAccess(path);

        File.WriteAllBytes(path, original);
        File.SetLastWriteTimeUtc(path, timestamp);
        await ReadExpectedAsync(token, pages[0]);
        AssertExclusiveAccess(path);
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("length")]
    [DataRow("compressedLength")]
    [DataRow("count")]
    [DataRow("localName")]
    [DataRow("localLength")]
    [DataRow("localCompressedLength")]
    public async Task CachedArchive_ChangedDirectoryWithOriginalSizeAndTimestampIsRejectedAndReleasesFile(string field)
    {
        var pages = CreatePages(2, 37);
        var path = CreateArchive("changed-directory.cbz", pages, CompressionLevel.NoCompression);
        await _service.OpenAsync(path);
        var token = (await _service.GetChapterImagesAsync(path, "/")).Images[0];
        await ReadExpectedAsync(token, pages[0]);
        AssertExclusiveAccess(path);

        PatchPreservingMetadata(path, bytes =>
        {
            var footer = bytes.Length - 22;
            Assert.AreEqual(0x06054b50u, Read32(bytes, footer));
            var central = checked((int)Read32(bytes, footer + 16));
            Assert.AreEqual(0x02014b50u, Read32(bytes, central));
            switch (field)
            {
                case "name":
                    bytes[central + 46] ^= 1;
                    break;
                case "length":
                    Write32(bytes, central + 24, Read32(bytes, central + 24) + 1);
                    break;
                case "compressedLength":
                    Write32(bytes, central + 20, Read32(bytes, central + 20) + 1);
                    break;
                case "count":
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(footer + 8, 2), 1);
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(footer + 10, 2), 1);
                    break;
                case "localName":
                    bytes[30] ^= 1;
                    break;
                case "localLength":
                    Write32(bytes, 22, Read32(bytes, 22) + 1);
                    break;
                case "localCompressedLength":
                    Write32(bytes, 18, Read32(bytes, 18) + 1);
                    break;
                default:
                    Assert.Fail("Unknown archive metadata fixture field.");
                    break;
            }
        });

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.GetImageBytesAsync(token));
        StringAssert.Contains(error.Message, "发生变化");
        AssertExclusiveAccess(path);
    }

    [TestMethod]
    public async Task CachedArchive_CloseAndReopenRevokesCachedTokensAndNewSessionReadsCorrectly()
    {
        var pages = CreatePages(3, 59);
        var path = CreateArchive("reopen.zip", pages, CompressionLevel.Optimal);
        await _service.OpenAsync(path);
        var oldTokens = (await _service.GetChapterImagesAsync(path, "/")).Images;
        await ReadExpectedAsync(oldTokens[1], pages[1]);
        AssertExclusiveAccess(path);

        _service.Close(path);
        Assert.IsFalse(_service.IsAuthorizedArchive(path));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _service.GetImageBytesAsync(oldTokens[1]));
        AssertExclusiveAccess(path);

        await _service.OpenAsync(path);
        var newTokens = (await _service.GetChapterImagesAsync(path, "/")).Images;
        Assert.AreNotEqual(oldTokens[1], newTokens[1]);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _service.GetImageBytesAsync(oldTokens[1]));
        await Task.WhenAll(newTokens.Select((token, index) => ReadExpectedAsync(token, pages[index])));
        AssertExclusiveAccess(path);
    }

    private async Task ReadExpectedAsync(string token, byte[] expected) =>
        CollectionAssert.AreEqual(expected, await _service.GetImageBytesAsync(token));

    private string CreateArchive(string name, byte[][] pages, CompressionLevel compression)
    {
        var path = Path.Combine(_root, name);
        _archivePaths.Add(path);
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        for (var index = 0; index < pages.Length; index++)
        {
            using var image = archive.CreateEntry($"page{index:D4}.png", compression).Open();
            image.Write(pages[index]);
        }
        return path;
    }

    private static byte[][] CreatePages(int count, int seed) => Enumerable.Range(0, count)
        .Select(page => Enumerable.Range(0, 65_537 + page * 997)
            .Select(index => (byte)((index * 17 + page * 23 + seed) % 251)).ToArray()).ToArray();

    private static void AssertExclusiveAccess(string path)
    {
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.CanRead && exclusive.CanWrite, "压缩包读取结束后必须释放文件句柄。");
    }

    private static void PatchPreservingMetadata(string path, Action<byte[]> patch)
    {
        var timestamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        var length = bytes.Length;
        patch(bytes);
        Assert.AreEqual(length, bytes.Length);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.AreEqual((long)length, new FileInfo(path).Length);
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path));
    }

    private static ushort Read16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static uint Read32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static void Write32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
