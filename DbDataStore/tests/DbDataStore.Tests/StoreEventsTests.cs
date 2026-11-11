using DbDataKit.Store;
using DbDataKit.Web.Trust;
using DbDataStore.Server;

namespace DbDataStore.Tests;

/// <summary>The change events file (plan 15 ST03) without a database: sequence numbers that only grow, a reader's cursor, a model filter and the cut that keeps the file small.</summary>
public sealed class StoreEventsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-events-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    private static ValidatedToken Token(string subject) =>
        new(new PairedClient("c", "console", [], ServerRole.Admin), subject, subject.ToUpperInvariant(), false, ServerRole.Author, new Dictionary<string, ServerRole>(), "t", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static IReadOnlyList<RowChange> Changes(params string[] entities) =>
        [.. entities.Select(e => RowChange.Insert(e, new Dictionary<string, object?> { ["a"] = 1 }))];

    [Fact]
    public void Events_get_growing_sequence_numbers_name_the_entities_once_and_are_read_after_a_cursor()
    {
        Assert.Empty(StoreEvents.Read(directory, 0, 10));
        StoreEvents.Record(directory, "m", "default", Changes("a", "b", "a"), Token("ada"), null, DateTimeOffset.UnixEpoch);
        StoreEvents.Record(directory, "n", "default", Changes("c"), Token("agent:x"), "0123456789abcdef0123456789abcdef", DateTimeOffset.UnixEpoch);
        StoreEvents.Record(directory, "m", "eu", Changes("a"), Token("ada"), null, DateTimeOffset.UnixEpoch);

        var all = StoreEvents.Read(directory, 0, 10);
        Assert.Equal([1L, 2L, 3L], all.Select(e => e.Sequence).ToArray());
        Assert.Equal(["a", "b"], all[0].Entities);
        Assert.Equal(3, all[0].Changes);
        Assert.Equal("ADA", all[0].ByName);
        Assert.Equal("0123456789abcdef0123456789abcdef", all[1].ChangeId);
        Assert.Equal("0123456789abcdef0123456789abcdef", all[1].Request);
        Assert.Equal(32, all[0].ChangeId.Length);
        Assert.Equal([3L], StoreEvents.Read(directory, 2, 10).Select(e => e.Sequence).ToArray());
        Assert.Equal([1L, 3L], StoreEvents.Read(directory, 0, 10, "m").Select(e => e.Sequence).ToArray());
        Assert.Equal([1L], StoreEvents.Read(directory, 0, 1).Select(e => e.Sequence).ToArray());
        Assert.Equal("eu", all[2].Instance);
    }

    [Fact]
    public void A_file_past_its_limit_is_cut_to_its_newest_half_and_the_sequence_goes_on()
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, StoreEvents.FileName);
        var filler = string.Join("\n", Enumerable.Range(1, 40).Select(i => $$"""{"seq":{{i}},"change_id":"{{new string('x', 32)}}","model":"m","instance":"default","entities":["a"],"changes":1,"by":"{{new string('y', 200000)}}","by_name":null,"request":null,"at":"2026-11-11T00:00:00.0000000+00:00"}""")) + "\n";
        File.WriteAllText(file, filler);
        Assert.True(new FileInfo(file).Length > StoreEvents.MaxBytes);

        StoreEvents.Record(directory, "m", "default", Changes("a"), Token("ada"), null, DateTimeOffset.UnixEpoch);
        var after = StoreEvents.Read(directory, 0, 1000);
        Assert.Equal(41, after[^1].Sequence);
        Assert.Equal(21, after[0].Sequence);
        Assert.Equal(21, after.Count);
    }
}
