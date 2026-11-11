using System.Security.Cryptography;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>Runs alone: it measures the live memory of the process, which other tests running at the same time would disturb.</summary>
[CollectionDefinition("snapshot-memory", DisableParallelization = true)]
public sealed class SnapshotMemoryCollection;

/// <summary>Plan 29b X01: a payload far larger than the memory an export may use.</summary>
[Collection("snapshot-memory")]
public sealed class StateSnapshotPayloadTests : IDisposable
{
    private const int Mebi = 1024 * 1024;
    private const int PayloadMebi = 200;

    private readonly StateEngines _engines = new();
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        _engines.Dispose();
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp folder left behind is untidy and nothing else */ }
        }
    }

    private sealed class SyncProgress(Action<SnapshotProgress> report) : IProgress<SnapshotProgress>
    {
        public void Report(SnapshotProgress value) => report(value);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_two_hundred_mebibyte_payload_is_exported_holding_a_few_chunks_in_memory(string engine)
    {
        var database = _engines.Provision(engine).Open([PayloadStore.Module], StateScope.Standalone);
        var store = new PayloadStore(database);   // 1 MiB chunks

        // 200 MiB of bytes that are cheap to produce and not repetitive at the chunk size: a block, with the block number written into its start
        var block = new byte[Mebi];
        new Random(11).NextBytes(block);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Guid id;
        using (var writer = store.OpenWrite())
        {
            id = writer.Id;
            for (var i = 0; i < PayloadMebi; i++)
            {
                BitConverter.TryWriteBytes(block, i);
                expected.AppendData(block);
                writer.Write(block);
            }

            writer.Complete();
        }

        block = [];
        var directory = Path.Combine(Path.GetTempPath(), "dbdatakit-snapshot-" + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);

        // live memory (after a full collection) at every progress report, against what the process held before the export
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;
        var reports = 0;
        var progress = new SyncProgress(p =>
        {
            if (p.Phase != "payload") return;
            reports++;
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
        });

        var result = await StateSnapshotWriter.WriteAsync(database, [PayloadStore.Module], StateScope.Standalone, directory, SnapshotOptions.Default, progress, CancellationToken.None);

        var entry = Assert.Single(result.Manifest.Payloads);
        Assert.Equal(id, entry.Id);
        Assert.Equal((long)PayloadMebi * Mebi, entry.Size);
        Assert.Equal(PayloadMebi, entry.Chunks);
        Assert.Equal(Convert.ToHexStringLower(expected.GetHashAndReset()), entry.Sha256);
        var path = Path.Combine(directory, "payloads", id.ToString("D") + ".bin");
        Assert.Equal((long)PayloadMebi * Mebi, new FileInfo(path).Length);
        Assert.Equal(entry.Sha256, SnapshotFiles.Sha256Of(path));
        Assert.True(reports >= 10, $"the export reported progress {reports} times while writing {PayloadMebi} MiB");

        // a chunk read from the engine, the one being written, buffers, and slack: a fraction of the payload
        Assert.True(peak < 32L * Mebi, $"live memory grew by {peak / (double)Mebi:F1} MiB while exporting a {PayloadMebi} MiB payload");
    }
}
