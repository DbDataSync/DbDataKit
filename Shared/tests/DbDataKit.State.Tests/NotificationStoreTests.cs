using DbDataKit.Monitoring;
namespace DbDataKit.State.Tests;

public sealed class NotificationStoreTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_raised_notification_is_listed_with_what_it_is_about(string engine)
    {
        var store = new NotificationStore(_engines.Open(engine));
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
        var store = new NotificationStore(_engines.Open(engine));
        store.Raise(NotificationKinds.CertificateExpiring, "one");
        store.Raise(NotificationKinds.CertificateExpiring, "two");
        store.Raise(NotificationKinds.CertificateExpired, "three");
        var all = store.List();

        var newer = store.List(sinceId: all[0].Id);

        Assert.Equal(["one", "two", "three"], all.Select(n => n.Message));
        Assert.Equal(["two", "three"], newer.Select(n => n.Message));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Each_user_has_their_own_read_cursor_that_only_moves_forward(string engine)
    {
        var store = new NotificationStore(_engines.Open(engine));
        store.Raise(NotificationKinds.CertificateExpiring, "one");
        store.Raise(NotificationKinds.CertificateExpiring, "two");
        var ids = store.List().Select(n => n.Id).ToArray();

        Assert.Null(store.GetCursor("ada"));
        store.MarkSeen("ada", ids[1]);
        store.MarkSeen("ada", ids[0]);
        store.MarkSeen("grace", ids[0]);

        Assert.Equal(ids[1], store.GetCursor("ada"));
        Assert.Equal(ids[0], store.GetCursor("grace"));
        Assert.Equal(0, store.UnreadCount(store.GetCursor("ada")));
        Assert.Equal(1, store.UnreadCount(store.GetCursor("grace")));
        Assert.Equal(2, store.UnreadCount(null));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Pruning_removes_old_notifications_and_null_keeps_everything(string engine)
    {
        var database = _engines.Open(engine);
        var store = new NotificationStore(database);
        store.Raise(NotificationKinds.CertificateExpiring, "old");
        store.Raise(NotificationKinds.CertificateExpiring, "new");
        var oldId = store.List()[0].Id;
        using (var connection = database.OpenConnection())
        {
            using var command = database.Command(connection, "UPDATE Notifications SET CreatedAtUtc = $created WHERE Id = $id;");
            command.Bind(database, "created", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
            command.Bind(database, "id", oldId);
            command.ExecuteNonQuery();
        }

        Assert.Equal(0, store.PruneNotifications(null));
        Assert.Equal(1, store.PruneNotifications(TimeSpan.FromDays(7)));
        Assert.Equal(["new"], store.List().Select(n => n.Message));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_consoles_read_one_feed_through_the_contract_and_advance_independently(string engine)
    {
        INotificationFeed feed = new NotificationStore(_engines.Open(engine));
        feed.Raise(NotificationKinds.CertificateExpiring, "one");
        feed.Raise(NotificationKinds.CertificateExpired, "two");
        var ids = feed.List().Select(n => n.Id).ToArray();

        feed.MarkSeen("console:a", ids[1]);
        feed.MarkSeen("console:b", ids[0]);

        Assert.Equal(0, feed.UnreadCount(feed.GetCursor("console:a")));
        Assert.Equal(1, feed.UnreadCount(feed.GetCursor("console:b")));
        Assert.Equal(2, feed.UnreadCount(feed.GetCursor("console:c")));
    }
}
