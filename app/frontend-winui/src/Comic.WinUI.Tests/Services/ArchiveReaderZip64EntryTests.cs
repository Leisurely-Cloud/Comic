using System.Buffers.Binary;
using System.IO.Compression;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ArchiveReaderZip64EntryTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task CachedZip64Entry_ResolvesOffsetAfterOptionalSizeFields(bool zip64Length, bool zip64CompressedLength)
    {
        var root = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Zip64EntryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "zip64-entry.cbz");
        var service = new ArchiveReaderService();
        try
        {
            var expected = Enumerable.Range(0, 4097).Select(index => (byte)(index % 251)).ToArray();
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            using (var image = archive.CreateEntry("1.png", CompressionLevel.Optimal).Open()) image.Write(expected);
            AddZip64EntryOffset(path, zip64Length, zip64CompressedLength);
            var original = File.ReadAllBytes(path);

            await service.OpenAsync(path);
            var token = (await service.GetChapterImagesAsync(path, "/")).Images.Single();
            CollectionAssert.AreEqual(expected, await service.GetImageBytesAsync(token));
            CollectionAssert.AreEqual(expected, await service.GetImageBytesAsync(token));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsTrue(exclusive.CanWrite);
        }
        finally
        {
            service.Close(path);
            Directory.Delete(root, true);
        }
    }

    private static void AddZip64EntryOffset(string path, bool includeLength, bool includeCompressedLength)
    {
        var bytes = File.ReadAllBytes(path);
        var footerOffset = bytes.Length - 22;
        var directoryOffset = checked((int)Read32(bytes, footerOffset + 16));
        var header = bytes.AsSpan(directoryOffset, 46).ToArray();
        var nameLength = Read16(header, 28);
        var extraLength = Read16(header, 30);
        var commentLength = Read16(header, 32);
        var localOffset = Read32(header, 42);
        var length = Read32(header, 24);
        var compressedLength = Read32(header, 20);
        var payloadLength = 8 + (includeLength ? 8 : 0) + (includeCompressedLength ? 8 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30, 2), checked((ushort)(extraLength + 4 + payloadLength)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42, 4), uint.MaxValue);
        if (includeLength) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), uint.MaxValue);
        if (includeCompressedLength) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), uint.MaxValue);

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(bytes, 0, directoryOffset);
            writer.Write(header);
            writer.Write(bytes, directoryOffset + 46, nameLength + extraLength);
            writer.Write((ushort)1);
            writer.Write((ushort)payloadLength);
            if (includeLength) writer.Write((ulong)length);
            if (includeCompressedLength) writer.Write((ulong)compressedLength);
            writer.Write((ulong)localOffset);
            writer.Write(bytes, directoryOffset + 46 + nameLength + extraLength, commentLength);
            var newDirectoryLength = checked((uint)(output.Position - directoryOffset));
            var footer = bytes.AsSpan(footerOffset, 22).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(12, 4), newDirectoryLength);
            writer.Write(footer);
        }
        File.WriteAllBytes(path, output.ToArray());
    }

    private static ushort Read16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint Read32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
