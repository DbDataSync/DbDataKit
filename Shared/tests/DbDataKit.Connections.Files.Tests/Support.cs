using System.Text;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Files.Tests;

/// <summary>What the tests of the file stores share: a connection from text, the secrets of a test as a dictionary, the engines' variables (<c>testenv/testenv.sh up sftp ftps</c>) and the one contract every store keeps.</summary>
internal static class Support
{
    public static ConnectionDefinition Load(string yaml)
    {
        var result = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, "connections/files.yml");
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        return result.Definition!;
    }

    public static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public static ISecretResolver Secrets(IReadOnlyDictionary<string, string> values) =>
        new SecretResolverChain([new EnvSecretProvider(name => values.GetValueOrDefault(name))]);

    /// <summary>The variable, or the test is skipped saying what to start (it fails instead when <c>DBDATAKIT_TEST_REQUIRE_FILE_SERVERS</c> is true, as CI sets it).</summary>
    public static string Var(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            Assert.False(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_FILE_SERVERS") is "true", $"{name} is required but not set");
            Assert.Skip($"Start the file servers (testenv/testenv.sh up sftp ftps) and set {name}");
        }

        return value!;
    }

    public static async Task<string> TextAsync(IFileStore store, string path)
    {
        await using var stream = await store.OpenReadAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>A stream that gives its first bytes, then waits: a writer that is half way through a file.</summary>
    public sealed class Stalled(string first, string rest) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first);
        private readonly byte[] _rest = Encoding.UTF8.GetBytes(rest);
        private int _position;
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the writer has taken the first bytes and asks for more.</summary>
        public Task HalfWritten => _reading.Task;

        public void Finish() => _go.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position < _first.Length)
            {
                var n = Math.Min(count, _first.Length - _position);
                Array.Copy(_first, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            _reading.TrySetResult();
            _go.Task.GetAwaiter().GetResult();
            var at = _position - _first.Length;
            if (at >= _rest.Length) return 0;
            var m = Math.Min(count, _rest.Length - at);
            Array.Copy(_rest, at, buffer, offset, m);
            _position += m;
            return m;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _first.Length + _rest.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>What every file store keeps (plan 27a IX04): write, list, read, replace, delete; a missing file is a permanent not-found; a path that leaves the root is refused; and a file being written is not seen under its name, only when it is whole.</summary>
    public static async Task ContractAsync(IFileStore store, IFileStore observer, string folder, bool canList = true, bool partFile = true)
    {
        var name = $"{folder}/hello-{Guid.NewGuid():N}.txt";
        await store.WriteAsync(name, Bytes("hello"));
        Assert.Equal("hello", await TextAsync(store, name));
        await store.WriteAsync(name, Bytes("hello, again"));   // replaced
        Assert.Equal("hello, again", await TextAsync(store, name));

        if (canList)
        {
            var entry = Assert.Single(await store.ListAsync(folder), e => e.Path == name);
            Assert.Equal((Path.GetFileName(name), 12L, false), (entry.Name, entry.Size, entry.IsDirectory));
            Assert.True(entry.Modified > DateTimeOffset.UtcNow.AddHours(-26));

            // a producer's own partial file is left out of the listing, and shown when asked for
            var partial = $"{folder}/feed-{Guid.NewGuid():N}.csv.part";
            await store.WriteAsync(partial, Bytes("1,2"));
            Assert.DoesNotContain(await store.ListAsync(folder), e => e.Path == partial);
            Assert.Contains(await store.ListAsync(folder, includeTemporary: true), e => e.Path == partial);
            await store.DeleteAsync(partial);

            // a file being written is not under its name until it is whole (a second connection looks while the first is half way)
            var whole = $"{folder}/whole-{Guid.NewGuid():N}.csv";
            var stalled = new Stalled("a,b\n1,2\n", "3,4\n");
            var writing = store.WriteAsync(whole, stalled);
            await stalled.HalfWritten.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain(await observer.ListAsync(folder), e => e.Path == whole);
            // a store that writes through a .part file shows it to a reader that asks for temporary names; an object store puts the object whole, so there is nothing to show
            if (partFile) Assert.Contains(await observer.ListAsync(folder, includeTemporary: true), e => e.Path == whole + FileNames.PartSuffix);
            await Assert.ThrowsAsync<ConnectionFailureException>(() => observer.OpenReadAsync(whole));
            stalled.Finish();
            await writing.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("a,b\n1,2\n3,4\n", await TextAsync(store, whole));
            Assert.DoesNotContain(await store.ListAsync(folder, includeTemporary: true), e => e.Path.EndsWith(".part", StringComparison.Ordinal) && e.Path.StartsWith(whole, StringComparison.Ordinal));
            await store.DeleteAsync(whole);
        }

        await store.DeleteAsync(name);
        var missing = await Assert.ThrowsAsync<ConnectionFailureException>(() => store.OpenReadAsync(name));
        Assert.Equal(ConnectionFailureKind.NotFound, missing.Failure.Kind);
        Assert.False(missing.Failure.IsTemporary);

        var outside = await Assert.ThrowsAsync<ConnectionFailureException>(() => store.OpenReadAsync("/../../etc/passwd"));
        Assert.Equal(ConnectionFailureKind.Permission, outside.Failure.Kind);
    }
}
