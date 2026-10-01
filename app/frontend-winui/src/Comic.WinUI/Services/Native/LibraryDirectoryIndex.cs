namespace Comic.WinUI.Services.Native;

/// <summary>按漫画目录缓存条目。文件系统变更只使对应漫画失效，监听溢出时全量重建。</summary>
internal sealed class LibraryDirectoryIndex : IDisposable
{
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, IndexedManga> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher _watcher;
    internal int RebuiltEntryCount { get; private set; }

    public LibraryDirectoryIndex(string root)
    {
        _root = root;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 65536,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += (_, args) => { Invalidate(args.OldFullPath); Invalidate(args.FullPath); };
        _watcher.Error += (_, _) => Invalidate();
        _watcher.EnableRaisingEvents = true;
    }

    public IReadOnlyList<LibraryEntry> GetEntries(Func<DirectoryInfo, long, LibraryEntry?> build)
    {
        lock (_gate)
        {
            var directories = new DirectoryInfo(_root).EnumerateDirectories()
                .Where(directory => !directory.Name.StartsWith('.') &&
                    !directory.Name.EndsWith("_CBZ", StringComparison.OrdinalIgnoreCase) &&
                    !directory.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList();
            var existing = directories.Select(directory => directory.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var removed in _entries.Keys.Where(path => !existing.Contains(path)).ToList()) _entries.Remove(removed);
            foreach (var directory in directories)
            {
                directory.Refresh();
                // 轻量核对章节目录与元数据，捕获尚未送达的创建/删除通知。
                var metadata = new FileInfo(Path.Combine(directory.FullName, "元数据.json"));
                var stamp = $"{(metadata.Exists ? metadata.LastWriteTimeUtc.Ticks : 0)}:{(metadata.Exists ? metadata.Length : 0)}:" +
                    string.Join(';', directory.EnumerateDirectories().OrderBy(item => item.Name).Select(item => item.Name));
                if (!_entries.TryGetValue(directory.FullName, out var cached) || cached.Stamp != stamp || _dirty.Remove(directory.FullName))
                {
                    _dirty.Remove(directory.FullName);
                    var files = directory.EnumerateFiles("*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        IgnoreInaccessible = true,
                    }).ToDictionary(file => Path.GetRelativePath(directory.FullName, file.FullName), file => new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks), StringComparer.OrdinalIgnoreCase);
                    _entries[directory.FullName] = new IndexedManga(stamp, build(directory, files.Values.Sum(file => file.Length)), files);
                    RebuiltEntryCount++;
                }
            }
            return _entries.Values.Where(item => item.Entry is not null).Select(item => item.Entry!).ToList();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        // Windows 读取目录也可能发出 Changed 通知。目录结构由轻量签名核对，
        // 只让实际文件写入、创建、删除触发深度重建，避免扫描本身使索引失效。
        if (args.ChangeType == WatcherChangeTypes.Changed)
        {
            if (Directory.Exists(args.FullPath)) return;
            lock (_gate)
            {
                var relative = Path.GetRelativePath(_root, args.FullPath);
                var manga = Path.Combine(_root, relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
                if (_entries.TryGetValue(manga, out var cached) && cached.Files.TryGetValue(Path.GetRelativePath(manga, args.FullPath), out var previous))
                {
                    var file = new FileInfo(args.FullPath);
                    if (file.Exists && previous == new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks)) return;
                }
            }
        }
        Invalidate(args.FullPath);
    }

    public void Invalidate(string? path = null)
    {
        lock (_gate)
        {
            if (path is null) { _entries.Clear(); _dirty.Clear(); return; }
            var relative = Path.GetRelativePath(_root, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return;
            var manga = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (manga.Length > 0 && !manga.StartsWith('.')) _dirty.Add(Path.Combine(_root, manga));
        }
    }

    public void Dispose() => _watcher.Dispose();

    private readonly record struct FileStamp(long Length, long WrittenAt);
    private sealed record IndexedManga(string Stamp, LibraryEntry? Entry, Dictionary<string, FileStamp> Files);
}
