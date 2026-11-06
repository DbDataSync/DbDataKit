using DbDataKit.Monitoring;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q07: the notification feed on its module, with the old store's tests carried over and the shared-database cases added.</summary>
public sealed class NotificationFeedStoreTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine, TimeProvider? time = null) => _engines.Provision(engine).Open([NotificationsModule.Module], time: time);

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(NotificationsModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_raised_notification_is_listed_with_what_it_is_about(string engine)
    {
        var store = new NotificationFeedStore(Open(engine), "sync");
        var runId = Guid.NewGuid();

        store.Raise(NotificationKinds.RunFailed, "Replication 'crm' failed", taskName: "crm", mappingName: "orders", runId: runId);

        var notification = Assert.Single(store.List());
        Assert.Equal(NotificationKinds.RunFailed, notification.Kind);
        Assert.Equal("crm", notification.TaskName);
        Assert.Equal("orders", notification.MappingName);
        Assert.Equal(runId, notification.RunId);
        Assert.Equal("Replication 'crm' failed", notification.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Listing_since_an_id_returns_only_what_is_newer_oldest_first(string engine)
    {
        var store = new NotificationFeedStore(Open(engine), "sync");
        store.Raise(NotificationKinds.CertificateExpiring, "one");
        store.Raise(NotificationKinds.CertificateExpiring, "two");
        store.Raise(NotificationKinds.CertificateExpired, "three");
        var all = store.List();

        Assert.Equal(["one", "two", "three"], all.Select(n => n.Message));
        Assert.Equal(["two", "three"], store.List(sinceId: all[0].Id).Select(n => n.Message));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Each_reader_has_a_position_that_only_moves_forward(string engine)
    {
        var store = new NotificationFeedStore(Open(engine), "sync");
        store.Raise(NotificationKinds.CertificateExpiring, "one");
        store.Raise(NotificationKinds.CertificateExpiring, "two");
        var ids = store.List().Select(n => n.Id).ToArray();

        Assert.Null(store.GetCursor("ada"));
        store.MarkSeen("ada", ids[1]);
        store.MarkSeen("ada", ids[0]);   // the older tab arriving second
        store.MarkSeen("grace", ids[0]);

        Assert.Equal(ids[1], store.GetCursor("ada"));
        Assert.Equal(ids[0], store.GetCursor("grace"));
        Assert.Equal(0, store.UnreadCount(store.GetCursor("ada")));
        Assert.Equal(1, store.UnreadCount(store.GetCursor("grace")));
        Assert.Equal(2, store.UnreadCount(null));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_services_on_one_database_each_read_their_own_and_the_console_reads_both(string engine)
    {
        var database = Open(engine);
        var sync = new NotificationFeedStore(database, "sync");
        var proxy = new NotificationFeedStore(database, "proxy");
        var console = new NotificationFeedStore(database, "console", allServices: true);
        sync.Raise(NotificationKinds.RunFailed, "sync one");
        proxy.Raise(NotificationKinds.LoginLockedOut, "proxy one");
        sync.Raise(NotificationKinds.RunFailed, "sync two");

        Assert.Equal(["sync one", "sync two"], sync.List().Select(n => n.Message));
        Assert.Equal(["proxy one"], proxy.List().Select(n => n.Message));
        Assert.Equal(["sync one", "proxy one", "sync two"], console.List().Select(n => n.Message));
        Assert.Equal(2, sync.UnreadCount(null));
        Assert.Equal(3, console.UnreadCount(null));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Raising_inside_a_transaction_joins_it_and_rolls_back_with_it(string engine)
    {
        var database = Open(engine);
        var store = new NotificationFeedStore(database, "sync");
        using (var connection = database.OpenConnection())
        {
            using var transaction = connection.BeginTransaction();
            NotificationFeedStore.Insert(database, connection, transaction, "sync", NotificationKinds.RunFailed, "never happened", null, null, null);
            transaction.Rollback();
        }

        Assert.Empty(store.List());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_retention_job_removes_notifications_after_thirty_days_and_keeps_positions(string engine)
    {
        var clock = new StateTestClock(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
        var database = Open(engine, clock);
        var store = new NotificationFeedStore(database, "sync");
        store.Raise(NotificationKinds.CertificateExpiring, "old");
        store.MarkSeen("ada", store.List()[0].Id);
        clock.Advance(TimeSpan.FromDays(40));
        store.Raise(NotificationKinds.CertificateExpiring, "new");

        new RetentionJob(database).Run();

        Assert.Equal(["new"], store.List().Select(n => n.Message));
        Assert.NotNull(store.GetCursor("ada"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Pruning_removes_old_notifications_and_null_keeps_everything(string engine)
    {
        var clock = new StateTestClock(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
        var store = new NotificationFeedStore(Open(engine, clock), "sync");
        store.Raise(NotificationKinds.CertificateExpiring, "old");
        clock.Advance(TimeSpan.FromDays(30));
        store.Raise(NotificationKinds.CertificateExpiring, "new");

        Assert.Equal(0, store.PruneNotifications(null));
        Assert.Equal(1, store.PruneNotifications(TimeSpan.FromDays(7)));
        Assert.Equal(["new"], store.List().Select(n => n.Message));
    }
}
