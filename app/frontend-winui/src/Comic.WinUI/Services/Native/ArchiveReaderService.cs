using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Comic.WinUI.Models;

namespace Comic.WinUI.Services.Native;

/// <summary>只读、按页读取用户明确打开的 ZIP/CBZ；图片令牌只在当前授权会话内有效。</summary>
public sealed class ArchiveReaderService
{
    private const string ImageTokenPrefix = "comic-archive://";
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp",
    };
    private readonly object _gate = new();
    private readonly Dictionary<string, ArchiveSession> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArchiveSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenRequest> _opening = new(StringComparer.OrdinalIgnoreCase);
    private readonly ArchiveReaderLimits _limits;

    public ArchiveReaderService() : this(new ArchiveReaderLimits()) { }

    internal ArchiveReaderService(ArchiveReaderLimits limits) => _limits = limits;

    public static bool IsSupportedArchivePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (path.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public static bool IsArchiveImageToken(string? value) => value?.StartsWith(ImageTokenPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public bool IsAuthorizedArchive(string path)
    {
        lock (_gate) return _paths.ContainsKey(NormalizePath(path));
    }

    /// <summary>调用方须只传入用户通过文件选择器、拖放或最近记录明确选择的文件；其他读取方法不会隐式授权文件。</summary>
    public async Task<ReaderChaptersResponse> OpenAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = NormalizePath(archivePath);
        if (!IsSupportedArchivePath(path)) throw new InvalidDataException("请选择 .cbz 或 .zip 漫画文件。");
        var request = new OpenRequest(cancellationToken);
        OpenRequest? previous;
        lock (_gate)
        {
            _opening.Remove(path, out previous);
            RemoveSession(path);
            _opening[path] = request;
        }
        previous?.Cancel();
        ArchiveSession? pendingSession = null;
        try
        {
            var session = pendingSession = await Task.Run(() => Scan(path, request.Token), request.Token).ConfigureAwait(false);
            lock (_gate)
            {
                request.Token.ThrowIfCancellationRequested();
                if (!_opening.TryGetValue(path, out var current) || current != request) throw new OperationCanceledException(request.Token);
                _paths[path] = session;
                _sessions[session.Id] = session;
                pendingSession = null;
                return CopyChapters(session);
            }
        }
        finally
        {
            pendingSession?.Cache.Dispose();
            lock (_gate)
            {
                if (_opening.TryGetValue(path, out var current) && current == request) _opening.Remove(path);
            }
            request.Dispose();
        }
    }

    public void Close(string archivePath)
    {
        OpenRequest? request;
        lock (_gate)
        {
            var path = NormalizePath(archivePath);
            _opening.Remove(path, out request);
            RemoveSession(path);
        }
        request?.Cancel();
    }

    public Task<ReaderChaptersResponse> GetReaderChaptersAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var session = GetSession(archivePath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = OpenUnchanged(session);
            var response = CopyChapters(session);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAuthorized(session);
            return response;
        }, cancellationToken);
    }

    public Task<ReaderImagesResponse> GetChapterImagesAsync(string archivePath, string chapterDirectoryName, CancellationToken cancellationToken = default)
    {
        var session = GetSession(archivePath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = OpenUnchanged(session);
            if (!session.Chapters.TryGetValue(chapterDirectoryName, out var images)) throw new InvalidDataException("压缩包中没有这个章节，请重新打开文件。");
            var response = new ReaderImagesResponse
            {
                Images = images.Select(page => ImageTokenPrefix + session.Id + "/" + page.Index.ToString(CultureInfo.InvariantCulture)).ToList(),
            };
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAuthorized(session);
            return response;
        }, cancellationToken);
    }

    public Task<byte[]> GetImageBytesAsync(string imageToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = GetImageSession(imageToken, out var page);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] bytes;
                {
                    using var lease = await session.Cache.AcquireAsync(cancellationToken).ConfigureAwait(false);
                    using var file = OpenUnchanged(session);
                    ValidateFingerprint(file, page.DirectoryRecord);
                    // ZipArchiveEntry caches this offset after its first Open; keep validating the local header too.
                    if (page.LocalHeaderRecord is { } localHeader) ValidateFingerprint(file, localHeader);
                    else page.LocalHeaderRecord = InspectLocalHeader(file, page.LocalHeaderOffset);
                    cancellationToken.ThrowIfCancellationRequested();
                    lease.Attach(file);
                    var entry = lease.Entries[page.Index];
                    using var source = entry.Open();
                    bytes = new byte[checked((int)page.Length)];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = await source.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new InvalidDataException("压缩包中的图片数据不完整，请重新获取文件。");
                        offset += read;
                    }
                    if (await source.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                        throw new InvalidDataException("压缩包中的图片大小异常或数据校验失败，已停止读取。");
                }
                // CRC uses only this page's bytes; release the shared ZIP stream before this CPU work.
                var crc = uint.MaxValue;
                for (var index = 0; index < bytes.Length; index++)
                {
                    if ((index & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    crc = CrcTable[(crc ^ bytes[index]) & 0xff] ^ (crc >> 8);
                }
                if ((crc ^ uint.MaxValue) != page.Crc32)
                {
                    throw new InvalidDataException("压缩包中的图片大小异常或数据校验失败，已停止读取。");
                }
                cancellationToken.ThrowIfCancellationRequested();
                EnsureAuthorized(session);
                return bytes;
            }
            catch (InvalidDataException ex) when (!IsFriendly(ex)) { throw CorruptArchive(ex); }
            catch (NotSupportedException ex) { throw UnsupportedArchive(ex); }
            catch (IOException ex) { throw UnavailableArchive(ex); }
        }, cancellationToken);
    }

    private ArchiveSession Scan(string path, CancellationToken cancellationToken)
    {
        ArchiveReadCache? cache = null;
        try
        {
            using var file = OpenFile(path);
            if (file.Length > _limits.MaxArchiveBytes) throw new InvalidDataException("压缩包过大，当前最多支持 8 GiB 的文件。");
            var lastWrite = File.GetLastWriteTimeUtc(path);
            var directory = InspectDirectory(file, cancellationToken);
            file.Position = 0;
            cache = new ArchiveReadCache(file);
            var entries = cache.Entries;
            if (entries.Count != directory.Entries.Count) throw CorruptArchive();
            var chapters = new Dictionary<string, List<ArchivePage>>(StringComparer.Ordinal);
            var pages = new Dictionary<int, ArchivePage>();
            long totalBytes = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                var name = NormalizeEntryName(entry.FullName);
                if (name is null || !ImageExtensions.Contains(Path.GetExtension(name))) continue;
                if (directory.Entries[index].Encrypted) throw UnsupportedArchive();
                if (entry.Length <= 0) throw new InvalidDataException("压缩包中包含空图片，请检查或重新获取文件。");
                if (entry.Length > _limits.MaxImageBytes) throw new InvalidDataException("压缩包中的单张图片超过大小上限（64 MiB）。");
                if (entry.Length > _limits.MaxTotalImageBytes - totalBytes) throw new InvalidDataException("压缩包内图片总大小超过上限（16 GiB）。");
                totalBytes += entry.Length;
                var separator = name.LastIndexOf('/');
                var chapter = separator < 0 ? "/" : name[..separator];
                var metadata = directory.Entries[index];
                var page = new ArchivePage(index, name, entry.Length, metadata.Crc32, metadata.Record, metadata.LocalHeaderOffset);
                if (!chapters.TryGetValue(chapter, out var chapterPages)) chapters[chapter] = chapterPages = [];
                chapterPages.Add(page);
                pages[index] = page;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (pages.Count == 0) throw new InvalidDataException("压缩包中没有可阅读的图片，支持 JPG、PNG、WebP、GIF 和 BMP。");
            foreach (var chapter in chapters.Values) chapter.Sort((left, right) => NaturalNameComparer.Instance.Compare(left.Name, right.Name));
            var session = new ArchiveSession(Guid.NewGuid().ToString("N"), path, file.Length, lastWrite, chapters, pages, cache, directory.FooterRecords);
            cache = null;
            return session;
        }
        catch (InvalidDataException ex) when (!IsFriendly(ex)) { throw CorruptArchive(ex); }
        catch (NotSupportedException ex) { throw UnsupportedArchive(ex); }
        catch (IOException ex) { throw UnavailableArchive(ex); }
        catch (UnauthorizedAccessException ex) { throw new InvalidDataException("无法读取所选压缩包，请检查文件访问权限。", ex); }
        finally { cache?.Dispose(); }
    }

    // 在 ZipArchive 分配条目对象前验证中央目录大小和实际条目数，包括 ZIP64。
    private ArchiveDirectory InspectDirectory(FileStream file, CancellationToken cancellationToken)
    {
        if (file.Length < 22) throw CorruptArchive();
        var tail = new byte[(int)Math.Min(file.Length, 65_557)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        var footer = -1;
        for (var position = tail.Length - 22; position >= 0; position--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Read32(tail, position) == 0x06054b50 && position + 22 + Read16(tail, position + 20) == tail.Length)
            {
                footer = position;
                break;
            }
        }
        if (footer < 0) throw CorruptArchive();
        if (Read16(tail, footer + 4) != 0 || Read16(tail, footer + 6) != 0) throw UnsupportedArchive();
        ulong count = Read16(tail, footer + 10);
        ulong directorySize = Read32(tail, footer + 12);
        ulong directoryOffset = Read32(tail, footer + 16);
        var footerOffset = file.Length - tail.Length + footer;
        var footerRecords = new List<ArchiveFingerprint>
        {
            Fingerprint(footerOffset, tail.AsSpan(footer, 22)),
        };
        if (count == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            if (footerOffset < 20) throw CorruptArchive();
            file.Position = footerOffset - 20;
            var locator = new byte[20];
            file.ReadExactly(locator);
            footerRecords.Add(Fingerprint(footerOffset - 20, locator));
            if (Read32(locator, 0) != 0x07064b50 || Read32(locator, 4) != 0 || Read32(locator, 16) != 1) throw UnsupportedArchive();
            var zip64Offset = Read64(locator, 8);
            if (zip64Offset > (ulong)Math.Max(0, footerOffset - 56)) throw CorruptArchive();
            file.Position = (long)zip64Offset;
            var zip64 = new byte[56];
            file.ReadExactly(zip64);
            footerRecords.Add(Fingerprint((long)zip64Offset, zip64));
            if (Read32(zip64, 0) != 0x06064b50 || Read64(zip64, 4) < 44) throw CorruptArchive();
            if (Read32(zip64, 16) != 0 || Read32(zip64, 20) != 0 || Read64(zip64, 24) != Read64(zip64, 32)) throw UnsupportedArchive();
            count = Read64(zip64, 32);
            directorySize = Read64(zip64, 40);
            directoryOffset = Read64(zip64, 48);
        }
        else if (Read16(tail, footer + 8) != count) throw UnsupportedArchive();
        if (count > (ulong)_limits.MaxEntries) throw new InvalidDataException("压缩包内条目过多，当前最多支持 50000 个条目。");
        if (directorySize > (ulong)_limits.MaxDirectoryBytes) throw new InvalidDataException("压缩包的文件目录过大，无法安全打开。");
        if (directoryOffset > (ulong)footerOffset || directorySize > (ulong)footerOffset - directoryOffset) throw CorruptArchive();
        file.Position = (long)directoryOffset;
        var end = (long)(directoryOffset + directorySize);
        // A central record has three ushort-length fields. Reuse one bounded buffer for all records.
        var record = new byte[46 + 3 * ushort.MaxValue];
        var entries = new List<ArchiveEntryMetadata>((int)count);
        var actualCount = 0;
        while (file.Position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end - file.Position < 46) throw CorruptArchive();
            var recordOffset = file.Position;
            file.ReadExactly(record.AsSpan(0, 46));
            if (Read32(record, 0) != 0x02014b50) throw CorruptArchive();
            if (actualCount >= _limits.MaxEntries) throw new InvalidDataException("压缩包内条目过多，当前最多支持 50000 个条目。");
            var variableLength = Read16(record, 28) + Read16(record, 30) + Read16(record, 32);
            if (variableLength > end - file.Position) throw CorruptArchive();
            file.ReadExactly(record.AsSpan(46, variableLength));
            entries.Add(new ArchiveEntryMetadata((Read16(record, 8) & 1) != 0, Read32(record, 16),
                Fingerprint(recordOffset, record.AsSpan(0, 46 + variableLength)), GetLocalHeaderOffset(record)));
            actualCount++;
        }
        if ((ulong)actualCount != count) throw CorruptArchive();
        return new ArchiveDirectory(entries, footerRecords);
    }

    private ArchiveSession GetSession(string archivePath)
    {
        lock (_gate)
        {
            if (!_paths.TryGetValue(NormalizePath(archivePath), out var session)) throw new UnauthorizedAccessException("请先通过“打开压缩包”选择这个文件。");
            return session;
        }
    }

    private ArchiveSession GetImageSession(string token, out ArchivePage page)
    {
        lock (_gate)
        {
            var parts = token.StartsWith(ImageTokenPrefix, StringComparison.Ordinal) ? token[ImageTokenPrefix.Length..].Split('/') : [];
            if (parts.Length != 2 || !_sessions.TryGetValue(parts[0], out var session)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || !session.Pages.TryGetValue(index, out var result))
            {
                throw new UnauthorizedAccessException("压缩包图片访问已失效，请重新打开文件。");
            }
            page = result;
            return session;
        }
    }

    private FileStream OpenUnchanged(ArchiveSession session)
    {
        EnsureAuthorized(session);
        FileStream file;
        try { file = OpenFile(session.Path); }
        catch (IOException ex) { throw UnavailableArchive(ex); }
        catch (UnauthorizedAccessException ex) { throw new InvalidDataException("无法读取所选压缩包，请检查文件访问权限。", ex); }
        try
        {
            if (file.Length != session.Length || File.GetLastWriteTimeUtc(session.Path) != session.LastWriteUtc) throw ChangedArchive();
            foreach (var record in session.FooterRecords) ValidateFingerprint(file, record);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private void EnsureAuthorized(ArchiveSession session)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.Id, out var current) || current != session) throw new UnauthorizedAccessException("压缩包已关闭，请重新打开文件。");
        }
    }

    private void RemoveSession(string path)
    {
        if (_paths.Remove(path, out var previous))
        {
            _sessions.Remove(previous.Id);
            previous.Cache.Dispose();
        }
    }

    private static ArchiveFingerprint Fingerprint(long offset, ReadOnlySpan<byte> bytes) =>
        new(offset, bytes.Length, SHA256.HashData(bytes));

    private static long? GetLocalHeaderOffset(byte[] record)
    {
        var offset = Read32(record, 42);
        if (offset != uint.MaxValue) return offset;
        var extras = 46 + Read16(record, 28);
        var end = extras + Read16(record, 30);
        while (end - extras >= 4)
        {
            var id = Read16(record, extras);
            var length = Read16(record, extras + 2);
            extras += 4;
            if (length > end - extras) return null;
            if (id == 1)
            {
                var skip = (Read32(record, 24) == uint.MaxValue ? 8 : 0) + (Read32(record, 20) == uint.MaxValue ? 8 : 0);
                if (length < skip + 8) return null;
                var zip64Offset = Read64(record, extras + skip);
                return zip64Offset <= long.MaxValue ? (long)zip64Offset : null;
            }
            extras += length;
        }
        return null;
    }

    private static ArchiveFingerprint InspectLocalHeader(FileStream file, long? offset)
    {
        if (offset is null || offset < 0 || offset > file.Length - 30) throw CorruptArchive();
        file.Position = offset.Value;
        var header = new byte[30];
        file.ReadExactly(header);
        if (Read32(header, 0) != 0x04034b50) throw CorruptArchive();
        var length = 30 + Read16(header, 26) + Read16(header, 28);
        if (length > file.Length - offset.Value) throw CorruptArchive();
        var bytes = new byte[length];
        header.CopyTo(bytes, 0);
        file.ReadExactly(bytes.AsSpan(30));
        return Fingerprint(offset.Value, bytes);
    }

    private static void ValidateFingerprint(FileStream file, ArchiveFingerprint record)
    {
        file.Position = record.Offset;
        Span<byte> bytes = record.Length <= 1024 ? stackalloc byte[record.Length] : new byte[record.Length];
        file.ReadExactly(bytes);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        if (!hash.SequenceEqual(record.Hash)) throw ChangedArchive();
    }

    private static ReaderChaptersResponse CopyChapters(ArchiveSession session) => new()
    {
        MangaTitle = Path.GetFileNameWithoutExtension(session.Path),
        Chapters = session.Chapters.OrderBy(pair => pair.Key, NaturalNameComparer.Instance).Select((pair, index) => new ReaderChapterDto
        {
            DirName = pair.Key,
            Title = pair.Key == "/" ? "根目录" : pair.Key,
            Order = index + 1,
            ImageCount = pair.Value.Count,
        }).ToList(),
    };

    private static string? NormalizeEntryName(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.EndsWith('/') || name.Contains(':') || name.Contains('\0')) return null;
        var parts = name.Split('/');
        if (parts.Any(part => part.Length == 0 || part.StartsWith('.') || part.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))) return null;
        return name;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);
    private static FileStream OpenFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static ushort Read16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint Read32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ulong Read64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320u ^ (value >> 1);
            table[index] = value;
        }
        return table;
    }
    private static bool IsFriendly(InvalidDataException exception) => exception.Message.Any(character => character is >= '\u4e00' and <= '\u9fff');
    private static InvalidDataException CorruptArchive(Exception? inner = null) => new("压缩包已损坏或不是有效的 ZIP/CBZ 文件，请重新获取文件。", inner);
    private static InvalidDataException UnsupportedArchive(Exception? inner = null) => new("暂不支持加密、分卷或特殊压缩格式，请使用未加密的普通 ZIP/CBZ 文件。", inner);
    private static InvalidDataException UnavailableArchive(Exception? inner = null) => new("无法读取压缩包，文件可能已移动、删除或正被其他程序占用，请重新打开。", inner);
    private static InvalidDataException ChangedArchive() => new("压缩包已发生变化，请重新打开文件后继续阅读。");

    private readonly record struct ArchiveFingerprint(long Offset, int Length, byte[] Hash);
    private readonly record struct ArchiveEntryMetadata(bool Encrypted, uint Crc32, ArchiveFingerprint Record, long? LocalHeaderOffset);
    private sealed record ArchiveDirectory(List<ArchiveEntryMetadata> Entries, List<ArchiveFingerprint> FooterRecords);
    private sealed record ArchivePage(int Index, string Name, long Length, uint Crc32, ArchiveFingerprint DirectoryRecord, long? LocalHeaderOffset)
    {
        // Accessed only while holding the session's exclusive read lease.
        public ArchiveFingerprint? LocalHeaderRecord { get; set; }
    }
    private sealed record ArchiveSession(string Id, string Path, long Length, DateTime LastWriteUtc,
        Dictionary<string, List<ArchivePage>> Chapters, Dictionary<int, ArchivePage> Pages, ArchiveReadCache Cache,
        List<ArchiveFingerprint> FooterRecords);

    private sealed class OpenRequest : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;

        public OpenRequest(CancellationToken token)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _cancellation.Token;
        }

        public CancellationToken Token { get; }

        public void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _cancellation.Dispose();
    }
}

internal sealed record ArchiveReaderLimits(
    long MaxArchiveBytes = 8L * 1024 * 1024 * 1024,
    int MaxEntries = 50_000,
    long MaxImageBytes = 64L * 1024 * 1024,
    long MaxTotalImageBytes = 16L * 1024 * 1024 * 1024,
    long MaxDirectoryBytes = 64L * 1024 * 1024);

/// <summary>数字片段按数值比较，不转换为整数，因此支持任意位数的页码。</summary>
internal sealed class NaturalNameComparer : IComparer<string>
{
    public static NaturalNameComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var first = 0;
        var second = 0;
        while (first < left.Length && second < right.Length)
        {
            if (char.IsAsciiDigit(left[first]) && char.IsAsciiDigit(right[second]))
            {
                var firstEnd = first;
                var secondEnd = second;
                while (firstEnd < left.Length && char.IsAsciiDigit(left[firstEnd])) firstEnd++;
                while (secondEnd < right.Length && char.IsAsciiDigit(right[secondEnd])) secondEnd++;
                var firstValue = first;
                var secondValue = second;
                while (firstValue < firstEnd && left[firstValue] == '0') firstValue++;
                while (secondValue < secondEnd && right[secondValue] == '0') secondValue++;
                var result = (firstEnd - firstValue).CompareTo(secondEnd - secondValue);
                if (result == 0) result = left.AsSpan(firstValue, firstEnd - firstValue).SequenceCompareTo(right.AsSpan(secondValue, secondEnd - secondValue));
                if (result != 0) return result;
                first = firstEnd;
                second = secondEnd;
                continue;
            }
            var characterResult = char.ToUpperInvariant(left[first]).CompareTo(char.ToUpperInvariant(right[second]));
            if (characterResult != 0) return characterResult;
            first++;
            second++;
        }
        var lengthResult = (left.Length - first).CompareTo(right.Length - second);
        return lengthResult != 0 ? lengthResult : StringComparer.Ordinal.Compare(left, right);
    }
}
