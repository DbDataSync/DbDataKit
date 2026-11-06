using System.Security.Cryptography;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q04: the chunked object store, on every engine.</summary>
public sealed class PayloadStoreTests : IDisposable
{
    private const int Mebi = 1024 * 1024;

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private (StateDatabase Database, PayloadStore Store) Open(string engine, int chunkSize = PayloadStore.DefaultChunkSize)
    {
        var database = _engines.Provision(engine).Open([PayloadStore.Module]);
        return (database, new PayloadStore(database, chunkSize));
    }

    /// <summary>Deterministic bytes, so a payload can be written and checked without holding it.</summary>
    private static byte[] Block(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void The_module_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(PayloadStore.Module));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(4096)]
    [InlineData(10_000)]
    public void A_payload_round_trips_whatever_its_size_against_the_chunk_size(int length)
    {
        var (_, store) = Open(StateEngineIds.Sqlite, chunkSize: 1024);
        var data = Block(length, length);

        Guid id;
        using (var writer = store.OpenWrite(new PayloadOptions(MediaType: "application/octet-stream")))
        {
            id = writer.Id;
            // written in pieces that do not line up with the chunks
            for (var offset = 0; offset < data.Length; offset += 333)
                writer.Write(data, offset, Math.Min(333, data.Length - offset));
            writer.Complete();
        }

        using var reader = store.OpenRead(id);
        using var copy = new MemoryStream();
        reader.CopyTo(copy);
        Assert.Equal(data, copy.ToArray());
        var info = store.Get(id)!;
        Assert.Equal(length, info.Size);
        Assert.Equal("application/octet-stream", info.MediaType);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), info.Sha256);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_payload_just_over_and_exactly_at_a_multiple_of_the_chunk_round_trips_on_every_engine(string engine)
    {
        var (_, store) = Open(engine, chunkSize: 4096);
        foreach (var length in new[] { 4096 * 3, 4096 * 3 + 1, 5 })
        {
            var data = Block(length, length);
            using var writer = store.OpenWrite();
            writer.Write(data);
            writer.Complete();

            using var reader = store.OpenRead(writer.Id);
            var read = new byte[length];
            reader.ReadExactly(read);
            Assert.Equal(data, read);
            Assert.Equal(0, reader.Read(new byte[1]));
        }
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_twenty_mebibyte_payload_streams_in_and_out_holding_a_few_chunks(string engine)
    {
        var (_, store) = Open(engine);
        const int total = 20 * Mebi;
        var block = Block(7, Mebi / 4);
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;

        Guid id;
        await using (var writer = store.OpenWrite())
        {
            id = writer.Id;
            for (var written = 0; written < total; written += block.Length)
            {
                await writer.WriteAsync(block);
                if (written % Mebi == 0) peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
            }

            writer.Complete();
        }

        long read = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var reader = store.OpenRead(id))
        {
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await reader.ReadAsync(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                read += n;
                if (read % Mebi == 0) peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
            }
        }

        Assert.Equal(total, read);
        var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < total; i += block.Length) expected.AppendData(block);
        Assert.Equal(expected.GetHashAndReset(), hash.GetHashAndReset());
        // a chunk being written or read, the one being sent, and slack: far below the payload's 20 MiB
        Assert.True(peak < 6 * Mebi, $"live memory grew by {peak / (double)Mebi:F1} MiB while streaming 20 MiB");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_changed_byte_is_found_when_the_payload_is_read_to_its_end(string engine)
    {
        var (database, store) = Open(engine, chunkSize: 1024);
        var data = Block(3, 3000);
        using (var writer = store.OpenWrite())
        {
            writer.Write(data);
            writer.Complete();
            Tamper(database, writer.Id, sequence: 1);

            using var reader = store.OpenRead(writer.Id);
            var ex = Assert.Throws<InvalidDataException>(() => reader.CopyTo(Stream.Null));
            Assert.Contains("SHA-256", ex.Message);
            Assert.Contains(writer.Id.ToString(), ex.Message);
        }
    }

    private static void Tamper(StateDatabase database, Guid id, long sequence)
    {
        using var connection = database.OpenConnection();
        using var read = database.Command("payloads", connection, "SELECT data FROM {table:chunk} WHERE payload_id = $id AND sequence = $sequence;");
        read.Bind(database, "id", id).Bind(database, "sequence", sequence);
        var bytes = (byte[])read.ExecuteScalar()!;
        bytes[0] ^= 0xFF;
        using var write = database.Command("payloads", connection, "UPDATE {table:chunk} SET data = $data WHERE payload_id = $id AND sequence = $sequence;");
        write.Bind(database, "data", bytes).Bind(database, "id", id).Bind(database, "sequence", sequence);
        Assert.Equal(1, write.ExecuteNonQuery());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_payload_its_writer_did_not_complete_cannot_be_read(string engine)
    {
        var (_, store) = Open(engine, chunkSize: 1024);
        Guid id;
        using (var writer = store.OpenWrite())
        {
            id = writer.Id;
            writer.Write(Block(1, 5000));
        } // disposed without Complete: the writer died

        var ex = Assert.Throws<InvalidOperationException>(() => store.OpenRead(id));
        Assert.Contains("not completed", ex.Message);
        Assert.False(store.Get(id)!.Complete);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_missing_chunk_is_reported_by_payload_and_number(string engine)
    {
        var (database, store) = Open(engine, chunkSize: 1024);
        using var writer = store.OpenWrite();
        writer.Write(Block(1, 3000));
        writer.Complete();
        using (var connection = database.OpenConnection())
        {
            using var delete = database.Command("payloads", connection, "DELETE FROM {table:chunk} WHERE payload_id = $id AND sequence = 1;");
            delete.Bind(database, "id", writer.Id);
            delete.ExecuteNonQuery();
        }

        using var reader = store.OpenRead(writer.Id);
        var ex = Assert.Throws<InvalidDataException>(() => reader.CopyTo(Stream.Null));
        Assert.Contains("chunk 1", ex.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Delete_removes_the_payload_and_its_chunks(string engine)
    {
        var (database, store) = Open(engine, chunkSize: 1024);
        using var writer = store.OpenWrite(new PayloadOptions(OwnerModule: "things", OwnerRow: "r1"));
        writer.Write(Block(1, 3000));
        writer.Complete();

        Assert.True(store.Delete(writer.Id));
        Assert.False(store.Delete(writer.Id));
        Assert.Null(store.Get(writer.Id));
        using var connection = database.OpenConnection();
        using var count = database.Command("payloads", connection, "SELECT COUNT(*) FROM {table:chunk};");
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public void A_store_needs_the_module()
    {
        using var engines = new StateEngines();
        var other = new StateModule("other", StateScopeKind.Install, ["CREATE TABLE {table:t} (id {{key}} PRIMARY KEY);"]);
        var database = engines.Provision(StateEngineIds.Sqlite).Open([other]);

        Assert.Throws<InvalidOperationException>(() => new PayloadStore(database));
    }
}
