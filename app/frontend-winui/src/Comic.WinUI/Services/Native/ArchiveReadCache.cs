using System.IO.Compression;

namespace Comic.WinUI.Services.Native;

/// <summary>缓存已解析的 ZIP 目录；图片读取独占流位置，空闲时不持有调用方的文件流。</summary>
internal sealed class ArchiveReadCache : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly ReattachableReadStream _stream = new();
    private readonly ZipArchive _archive;
    private readonly IReadOnlyList<ZipArchiveEntry> _entries;
    private ReadLease? _activeLease;
    private int _requests;
    private bool _closed;
    private bool _resourcesDisposed;

    public ArchiveReadCache(Stream file)
    {
        ZipArchive? archive = null;
        try
        {
            _stream.Attach(file);
            archive = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: true);
            // 目录在初次扫描时解析完毕；之后重绑文件仅用于读取所选条目。
            var entries = archive.Entries;
            _archive = archive;
            _entries = entries;
        }
        catch
        {
            try { archive?.Dispose(); }
            finally
            {
                _stream.Dispose();
                _readGate.Dispose();
            }
            throw;
        }
        finally { _stream.Detach(); }
    }

    /// <summary>只用于会话扫描元数据；读取条目内容须先取得 ReadLease。</summary>
    public IReadOnlyList<ZipArchiveEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                ThrowIfClosed();
                return _entries;
            }
        }
    }

    public async Task<ReadLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfClosed();
            _requests++;
        }
        var acquired = false;
        try
        {
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfClosed();
                var lease = new ReadLease(this);
                _activeLease = lease;
                return lease;
            }
        }
        catch
        {
            if (acquired) _readGate.Release();
            CompleteRequest();
            throw;
        }
    }

    public void Dispose()
    {
        var disposeResources = false;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            if (_requests == 0)
            {
                _resourcesDisposed = true;
                disposeResources = true;
            }
        }
        // 正在读取的租约继续拥有缓存；最后一个租约负责释放资源。
        if (disposeResources) DisposeResources();
    }

    private void Attach(ReadLease lease, Stream file)
    {
        lock (_gate)
        {
            EnsureActiveLease(lease);
            _stream.Attach(file);
        }
    }

    private IReadOnlyList<ZipArchiveEntry> GetEntries(ReadLease lease)
    {
        lock (_gate)
        {
            EnsureActiveLease(lease);
            return _entries;
        }
    }

    private void Release(ReadLease lease)
    {
        lock (_gate)
        {
            EnsureActiveLease(lease);
            _stream.Detach();
            _activeLease = null;
        }
        // 此请求仍计入 _requests；释放信号量期间 Dispose 不会销毁它。
        _readGate.Release();
        CompleteRequest();
    }

    private void CompleteRequest()
    {
        var disposeResources = false;
        lock (_gate)
        {
            _requests--;
            if (_closed && _requests == 0 && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                disposeResources = true;
            }
        }
        if (disposeResources) DisposeResources();
    }

    private void EnsureActiveLease(ReadLease lease)
    {
        if (!ReferenceEquals(_activeLease, lease)) throw new ObjectDisposedException(nameof(ReadLease));
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new UnauthorizedAccessException("压缩包已关闭，请重新打开文件。");
    }

    private void DisposeResources()
    {
        try { _archive.Dispose(); }
        finally
        {
            _stream.Dispose();
            _readGate.Dispose();
        }
    }

    internal sealed class ReadLease : IDisposable
    {
        private ArchiveReadCache? _owner;

        internal ReadLease(ArchiveReadCache owner) => _owner = owner;

        public IReadOnlyList<ZipArchiveEntry> Entries => GetOwner().GetEntries(this);

        /// <summary>调用方保留文件所有权，且须先释放条目流，再释放此租约。</summary>
        public void Attach(Stream file) => GetOwner().Attach(this, file);

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(this);
        }

        private ArchiveReadCache GetOwner() => Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(ReadLease));
    }

    private sealed class ReattachableReadStream : Stream
    {
        private Stream? _attached;
        private int _disposed;

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => Volatile.Read(ref _disposed) == 0;
        public override bool CanWrite => false;
        public override long Length => GetAttached().Length;
        public override long Position
        {
            get => GetAttached().Position;
            set => GetAttached().Position = value;
        }

        public void Attach(Stream file)
        {
            ArgumentNullException.ThrowIfNull(file);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!file.CanRead || !file.CanSeek) throw new ArgumentException("压缩包需要可读取、可定位的文件流。", nameof(file));
            if (Interlocked.CompareExchange(ref _attached, file, null) is not null) throw new InvalidOperationException("压缩包读取流已附加文件。");
        }

        public void Detach() => Interlocked.Exchange(ref _attached, null);

        public override int Read(byte[] buffer, int offset, int count) => GetAttached().Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => GetAttached().Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            GetAttached().ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            GetAttached().ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => GetAttached().Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
            Detach();
            base.Dispose(disposing);
        }

        private Stream GetAttached()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return Volatile.Read(ref _attached) ?? throw new InvalidOperationException("压缩包读取流未附加文件。");
        }
    }
}
