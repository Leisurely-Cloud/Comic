using System.Buffers.Binary;
using System.IO.Compression;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ArchiveReaderServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "Comic.WinUI.ArchiveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task Open_GroupsRootAndNestedDirectoriesAndNaturallySortsPages()
    {
        var huge = new string('9', 60);
        var larger = "1" + new string('0', 60);
        var path = CreateArchive("漫画.cbz", ("10.jpg", [10]), ("2.jpg", [2]), ("1.jpg", [1]),
            ("第10话/1.png", [10]), ("第2话/10.png", [10]), ("第2话/2.png", [2]),
            ($"第2话/{larger}.png", [8]), ($"第2话/{huge}.png", [9]),
            ("卷1/第1话/1.webp", [11]), ("卷2/第1话/1.webp", [21]));
        var service = new ArchiveReaderService();

        var result = await service.OpenAsync(path);

        Assert.AreEqual("漫画", result.MangaTitle);
        CollectionAssert.AreEqual(new[] { "/", "卷1/第1话", "卷2/第1话", "第2话", "第10话" }, result.Chapters.Select(chapter => chapter.DirName).ToArray());
        Assert.AreEqual("根目录", result.Chapters[0].Title);
        var root = await service.GetChapterImagesAsync(path, "/");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 10 }, await ReadFirstBytes(service, root.Images));
        var nested = await service.GetChapterImagesAsync(path, "第2话");
        CollectionAssert.AreEqual(new byte[] { 2, 10, 9, 8 }, await ReadFirstBytes(service, nested.Images));
    }

    [TestMethod]
    public async Task Open_RecognizesAllReaderFormatsAndIgnoresHiddenUnsafeAndNonImageEntries()
    {
        var path = CreateArchive("formats.ZIP", ("1.JPG", [1]), ("2.jpeg", [2]), ("3.png", [3]),
            ("4.webp", [4]), ("5.gif", [5]), ("6.bmp", [6]), ("ComicInfo.xml", [0]),
            ("__MACOSX/1.jpg", [0]), ("chapter/.resources/1.jpg", [0]), ("._1.jpg", [0]),
            ("../outside.jpg", [0]), ("C:/outside.jpg", [0]), ("/outside.jpg", [0]),
            ("deep\\chapter\\7.JPG", [7]));
        var service = new ArchiveReaderService();

        var result = await service.OpenAsync(path);

        Assert.AreEqual(7, result.Chapters.Sum(chapter => chapter.ImageCount));
        CollectionAssert.AreEqual(new[] { "/", "deep/chapter" }, result.Chapters.Select(chapter => chapter.DirName).ToArray());
    }

    [TestMethod]
    public async Task ReadsOnlyRequestedPageWithoutExtractionOrChangingOriginalArchive()
    {
        var bytes = Enumerable.Range(0, 10_000).Select(index => (byte)(index % 251)).ToArray();
        var path = CreateArchive("readonly.cbz", ("chapter/1.png", bytes), ("chapter/2.png", [2]));
        var original = File.ReadAllBytes(path);
        var service = new ArchiveReaderService();
        await service.OpenAsync(path);

        var images = await service.GetChapterImagesAsync(path, "chapter");
        Assert.IsTrue(images.Images.All(ArchiveReaderService.IsArchiveImageToken));
        Assert.IsFalse(images.Images.Any(token => token.Contains(_root, StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEqual(bytes, await service.GetImageBytesAsync(images.Images[0]));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.CanWrite, "操作结束后不能留下压缩包文件句柄。");
    }

    [TestMethod]
    public async Task DuplicateNamesHaveDistinctOpaqueTokensAndReadCorrectEntries()
    {
        var path = CreateArchive("duplicates.zip", ("1.jpg", [1]), ("1.jpg", [2]));
        var service = new ArchiveReaderService();
        await service.OpenAsync(path);
        var images = await service.GetChapterImagesAsync(path, "/");

        Assert.AreEqual(2, images.Images.Distinct().Count());
        CollectionAssert.AreEquivalent(new byte[] { 1, 2 }, await ReadFirstBytes(service, images.Images));
    }

    [TestMethod]
    public async Task ReadingRequiresExplicitAuthorizationAndForgedTokensCannotReadExternalFiles()
    {
        var path = CreateArchive("not-opened.zip", ("1.jpg", [1]));
        var outside = Path.Combine(_root, "private.jpg");
        File.WriteAllBytes(outside, [42]);
        var service = new ArchiveReaderService();
        Assert.IsFalse(service.IsAuthorizedArchive(path));

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.GetReaderChaptersAsync(path));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.GetChapterImagesAsync(path, "/"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.GetImageBytesAsync(outside));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.GetImageBytesAsync("comic-archive://" + Uri.EscapeDataString(path) + "/0"));
        await service.OpenAsync(path);
        var token = (await service.GetChapterImagesAsync(path, "/")).Images[0];
        var session = token[..token.LastIndexOf('/')];
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.GetImageBytesAsync(session + "/1"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.GetImageBytesAsync(session + "/../private.jpg"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => new ArchiveReaderService().GetImageBytesAsync(token));
    }

    [TestMethod]
    public async Task CloseAndReopenRevokeOldTokensWithoutRevokingOtherArchives()
    {
        var first = CreateArchive("first.zip", ("1.jpg", [1]));
        var second = CreateArchive("second.zip", ("1.jpg", [2]));
        var service = new ArchiveReaderService();
        await service.OpenAsync(first);
        await service.OpenAsync(second);
        var oldToken = (await service.GetChapterImagesAsync(first, "/")).Images[0];
        var secondToken = (await service.GetChapterImagesAsync(second, "/")).Images[0];

        await service.OpenAsync(first);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.GetImageBytesAsync(oldToken));
        service.Close(first);
        Assert.IsFalse(service.IsAuthorizedArchive(first));
        Assert.IsTrue(service.IsAuthorizedArchive(second));
        CollectionAssert.AreEqual(new byte[] { 2 }, await service.GetImageBytesAsync(secondToken));
    }

    [TestMethod]
    public async Task ChangedAndMissingArchivesProduceFriendlyErrors()
    {
        var path = CreateArchive("changed.zip", ("1.jpg", [1]));
        var service = new ArchiveReaderService();
        await service.OpenAsync(path);
        var token = (await service.GetChapterImagesAsync(path, "/")).Images[0];
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        var changed = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.GetImageBytesAsync(token));
        StringAssert.Contains(changed.Message, "发生变化");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.GetChapterImagesAsync(path, "/"));
        await service.OpenAsync(path);
        File.Delete(path);
        var missing = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.GetReaderChaptersAsync(path));
        StringAssert.Contains(missing.Message, "重新打开");
    }

    [TestMethod]
    public async Task CancelledOpeningAndClosingWhileScanningNeverRegistersArchive()
    {
        var path = CreateArchive("cancel.zip", Enumerable.Range(0, 5_000).Select(index => ($"{index}.jpg", new byte[] { 1 })).ToArray());
        var service = new ArchiveReaderService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.OpenAsync(path, cancellation.Token));
        Assert.IsFalse(service.IsAuthorizedArchive(path));

        var pending = service.OpenAsync(path);
        service.Close(path);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsFalse(service.IsAuthorizedArchive(path));
        await service.OpenAsync(path);
        var token = (await service.GetChapterImagesAsync(path, "/")).Images[0];
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.GetImageBytesAsync(token, cancellation.Token));
    }

    [TestMethod]
    public async Task EmptyInvalidUnsupportedAndEncryptedArchivesHaveChineseMessages()
    {
        var empty = CreateArchive("empty.zip", ("info.txt", [1]));
        var invalid = Path.Combine(_root, "bad.cbz");
        File.WriteAllText(invalid, "not a ZIP file");
        var unsupported = Path.Combine(_root, "book.rar");
        var encrypted = CreateArchive("encrypted.zip", ("1.jpg", [1]));
        PatchCentralDirectory(encrypted, (bytes, offset) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8, 2), 1));
        var service = new ArchiveReaderService();

        StringAssert.Contains((await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.OpenAsync(empty))).Message, "没有可阅读");
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.OpenAsync(invalid))).Message, "损坏");
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.OpenAsync(unsupported))).Message, ".cbz");
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.OpenAsync(encrypted))).Message, "加密");
        Assert.IsFalse(service.IsAuthorizedArchive(encrypted));
    }

    [TestMethod]
    public async Task AllResourceLimitsAreEnforcedBeforeAllocation()
    {
        var path = CreateArchive("limits.zip", ("1.jpg", [1, 2, 3]), ("2.jpg", [4, 5, 6]), ("metadata.txt", [7]));
        await ExpectLimit(new ArchiveReaderLimits(MaxArchiveBytes: 1), path, "压缩包过大");
        await ExpectLimit(new ArchiveReaderLimits(MaxEntries: 2), path, "条目过多");
        await ExpectLimit(new ArchiveReaderLimits(MaxImageBytes: 2), path, "单张图片");
        await ExpectLimit(new ArchiveReaderLimits(MaxTotalImageBytes: 5), path, "图片总大小");
        await ExpectLimit(new ArchiveReaderLimits(MaxDirectoryBytes: 1), path, "文件目录过大");
    }

    [TestMethod]
    public async Task MisdeclaredDirectoryCountsAndImageLengthsAreRejected()
    {
        var countPath = CreateArchive("wrong-count.zip", ("1.jpg", [1]), ("2.jpg", [2]));
        var bytes = File.ReadAllBytes(countPath);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10, 2), 1);
        File.WriteAllBytes(countPath, bytes);
        var service = new ArchiveReaderService();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.OpenAsync(countPath));

        var lengthPath = CreateArchive("wrong-length.zip", ("1.jpg", [1, 2, 3, 4]));
        PatchCentralDirectory(lengthPath, (data, offset) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 24, 4), 1));
        await service.OpenAsync(lengthPath);
        var token = (await service.GetChapterImagesAsync(lengthPath, "/")).Images[0];
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.GetImageBytesAsync(token))).Message, "图片大小异常");
    }

    [TestMethod]
    public async Task ReturnedChapterListsCannotMutateSessionIndex()
    {
        var path = CreateArchive("copies.zip", ("1.jpg", [1]));
        var service = new ArchiveReaderService();
        var response = await service.OpenAsync(path);
        response.Chapters[0].DirName = "tampered";
        response.Chapters.Clear();

        var reloaded = await service.GetReaderChaptersAsync(path);
        Assert.AreEqual("/", reloaded.Chapters.Single().DirName);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.GetChapterImagesAsync(path, "tampered"));
    }

    [TestMethod]
    public async Task ChangedImagePayloadWithOriginalLengthAndCrcIsRejected()
    {
        var path = Path.Combine(_root, "corrupt-payload.zip");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var image = archive.CreateEntry("1.jpg", CompressionLevel.NoCompression).Open()) image.Write([1, 2, 3, 4]);
        var bytes = File.ReadAllBytes(path);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
        bytes[30 + nameLength + extraLength] ^= 0xff;
        File.WriteAllBytes(path, bytes);
        var service = new ArchiveReaderService();
        await service.OpenAsync(path);
        var token = (await service.GetChapterImagesAsync(path, "/")).Images[0];

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.GetImageBytesAsync(token));
        StringAssert.Contains(exception.Message, "校验失败");
    }

    [TestMethod]
    public async Task Zip64WithMaximumLengthCommentCanBeRead()
    {
        var path = CreateArchive("zip64.zip", ("1.jpg", [1, 2, 3]));
        var original = File.ReadAllBytes(path);
        var originalFooter = original.Length - 22;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(originalFooter + 10, 2));
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(originalFooter + 12, 4));
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(originalFooter + 16, 4));
        using (var output = File.Create(path))
        using (var writer = new BinaryWriter(output))
        {
            writer.Write(original, 0, originalFooter);
            writer.Write(0x06064b50u);
            writer.Write(44UL);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ulong)count);
            writer.Write((ulong)count);
            writer.Write((ulong)directorySize);
            writer.Write((ulong)directoryOffset);
            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write((ulong)originalFooter);
            writer.Write(1u);
            writer.Write(0x06054b50u);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(ushort.MaxValue);
            writer.Write(ushort.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write(ushort.MaxValue);
            writer.Write(new byte[ushort.MaxValue]);
        }
        var service = new ArchiveReaderService();
        await service.OpenAsync(path);
        var token = (await service.GetChapterImagesAsync(path, "/")).Images[0];
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await service.GetImageBytesAsync(token));
    }

    private string CreateArchive(string name, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            using var output = archive.CreateEntry(item.Name, CompressionLevel.Optimal).Open();
            output.Write(item.Bytes);
        }
        return path;
    }

    private static async Task<byte[]> ReadFirstBytes(ArchiveReaderService service, IEnumerable<string> tokens)
    {
        var result = new List<byte>();
        foreach (var token in tokens) result.Add((await service.GetImageBytesAsync(token))[0]);
        return result.ToArray();
    }

    private static async Task ExpectLimit(ArchiveReaderLimits limits, string path, string message)
    {
        var service = new ArchiveReaderService(limits);
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.OpenAsync(path));
        StringAssert.Contains(exception.Message, message);
        Assert.IsFalse(service.IsAuthorizedArchive(path));
    }

    private static void PatchCentralDirectory(string path, Action<byte[], int> patch)
    {
        var bytes = File.ReadAllBytes(path);
        for (var position = 0; position <= bytes.Length - 46; position++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4)) != 0x02014b50) continue;
            patch(bytes, position);
            File.WriteAllBytes(path, bytes);
            return;
        }
        Assert.Fail("Fixture central directory was not found.");
    }
}
