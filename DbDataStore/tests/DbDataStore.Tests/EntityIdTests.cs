using DbDataStore.Server;

namespace DbDataStore.Tests;

public class EntityIdTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void An_id_is_version_7_and_says_when_it_was_made()
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, 123, TimeSpan.Zero);
        var id = EntityIds.New(new Clock(at));
        Assert.Equal(7, id.Version);
        Assert.Equal(at, EntityIds.MadeAt(id));
        Assert.Null(EntityIds.MadeAt(Guid.NewGuid()));
    }

    [Fact]
    public void Ids_made_later_sort_later_as_the_database_sorts_them()
    {
        var clock = new Clock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ids = new List<Guid>();
        for (var i = 0; i < 500; i++) { ids.Add(EntityIds.New(clock)); clock.Now = clock.Now.AddMilliseconds(1 + i % 3); }
        // the text form of a UUID orders like its bytes; PostgreSQL orders a uuid that way (SQL Server orders a uniqueidentifier in its own byte order, which a slice that stores these ids must account for)
        var strings = ids.Select(i => i.ToString()).ToList();
        Assert.Equal(strings.Order(StringComparer.Ordinal).ToList(), strings);
    }

    [Fact]
    public void Two_instances_making_ids_in_the_same_millisecond_do_not_collide()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var ids = Enumerable.Range(0, 20000).Select(_ => EntityIds.New(clock)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
