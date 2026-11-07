using System.Security.Cryptography;

namespace DbDataKit.Runner.Tests;

/// <summary>Runs alone (no other test in this process at the same time), so the allocation count is the cache's own.</summary>
[CollectionDefinition(nameof(ArtifactCacheMemoryTests), DisableParallelization = true)]
public sealed class ArtifactCacheMemoryCollection;

[Collection(nameof(ArtifactCacheMemoryTests))]
public sealed class ArtifactCacheMemoryTests : IDisposable
{
    private const long FileSize = 50L * 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbdatakit-r05m-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => ArtifactTestSupport.DeleteTree(_root);

    [Fact]
    public async Task A_50_MiB_file_streams_without_a_50_MiB_allocation()
    {
        var source = new GeneratedSource(FileSize);
        var file = new ArtifactFile("data/big.bin", await source.HashAsync(), FileSize);
        var cache = new ArtifactCache(Path.Combine(_root, "cache"), source);

        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        using var view = await cache.MaterializeAsync("v1", [file]);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(FileSize, ArtifactTestSupport.LengthOf(view.PathOf("data/big.bin")));
        Assert.Equal(FileSize, new FileInfo(cache.ObjectPath(file.Sha256)).Length);
        // one copy buffer and some bookkeeping; a whole-file buffer would be 50 MiB, so this has a wide margin on both sides
        Assert.True(allocated < 10L * 1024 * 1024, $"materializing a {FileSize} byte file allocated {allocated} bytes");
        Assert.True(source.LargestRead <= 1024 * 1024, $"the cache asked for {source.LargestRead} bytes in one read");
    }

    /// <summary>Deterministic content produced on the fly, never held.</summary>
    private sealed class GeneratedSource(long size) : IArtifactSource
    {
        public int LargestRead { get; private set; }

        public async Task<string> HashAsync()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            await using var stream = new PatternStream(size, this, track: false);
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0) hash.AppendData(buffer, 0, read);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        public Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new PatternStream(size, this, track: true));

        private sealed class PatternStream(long length, GeneratedSource owner, bool track) : Stream
        {
            private long _position;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (track && buffer.Length > owner.LargestRead) owner.LargestRead = buffer.Length;
                var n = (int)Math.Min(buffer.Length, length - _position);
                for (var i = 0; i < n; i++) buffer[i] = (byte)((_position + i) * 31 >> 3);
                _position += n;
                return n;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
