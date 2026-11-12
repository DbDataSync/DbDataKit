using System.Data.Common;

namespace DbDataKit.State;

/// <summary>
/// The <c>releases</c> module (plan 21 CH02): a release is a named, ordered list of per-product promotions. It runs in order, stops at the first failure and says where, and can be resumed; it is a sequence, not a
/// transaction (the console says so). One approval covers the release, given by someone who holds the approval permission for every item in it.
/// </summary>
public static class ReleasesModule
{
    public const string Name = "releases";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- A release: its state is draft (being put together), approved, running, stopped (an item failed or the run was cancelled; resumable), succeeded or abandoned.
            -- stopped_item is the position of the item the run stopped at; stopped_reason says why in words. The approval (who and when) is one for the whole release.
            CREATE TABLE {table:release} (
                id             {{key}} PRIMARY KEY,
                name           {{key}} NOT NULL,
                state          {{key}} NOT NULL,
                created_by     {{key}} NOT NULL,
                created_at     {{time}} NOT NULL,
                approved_by    {{key}} NULL,
                approved_at    {{time}} NULL,
                stopped_item     {{int}} NULL,
                stopped_reason {{text}} NULL,
                updated_at     {{time}} NOT NULL
            );

            -- The items, in the order they run. product says which server's promotion it is (gateway, runner, ...), target which of its groups; the same version goes from one channel to the next.
            CREATE TABLE {table:release_item} (
                release_id    {{key}} NOT NULL,
                position      {{int}} NOT NULL,
                product       {{key}} NOT NULL,
                target        {{key}} NOT NULL,
                kind          {{key}} NOT NULL,
                item_name     {{key}} NOT NULL,
                item_version  {{int}} NOT NULL,
                from_channel  {{key}} NOT NULL,
                to_channel    {{key}} NOT NULL,
                state         {{key}} NOT NULL,
                error         {{text}} NULL,
                finished_at   {{time}} NULL,
                PRIMARY KEY (release_id, position)
            );
            """,
            """
            -- Migration 2 (plan 21 CH02): the server an item's version is read from; null is the target itself (a move between channels of one group).
            ALTER TABLE {table:release_item} {{addcolumn}} source {{key}} NULL;
            """,
        ]);
}

/// <summary>What a release or an item is doing. Kept as these words in the tables.</summary>
public static class ReleaseStates
{
    public const string Draft = "draft";
    public const string Approved = "approved";
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Succeeded = "succeeded";
    public const string Abandoned = "abandoned";

    public const string Pending = "pending";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>One per-product promotion of a release (<c>Source</c> is the server the version is read from, null when it is the target itself): <paramref name="Kind"/> <paramref name="Name"/> at <paramref name="Version"/> from one channel to the next on <paramref name="Product"/>'s <paramref name="Target"/>.</summary>
public sealed record ReleaseItem(int Position, string Product, string Target, string Kind, string Name, int Version, string FromChannel, string ToChannel, string State = ReleaseStates.Pending, string? Error = null, DateTimeOffset? FinishedAt = null, string? Source = null)
{
    /// <summary>How a message names the item: its place in the release and what it moves where.</summary>
    public string Describe() => $"item {Position} ({Product} {Target}: {Kind} '{Name}' version {Version}, {FromChannel} to {ToChannel})";
}

public sealed record ReleaseRecord(string Id, string Name, string State, string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt, int? StoppedAt, string? StoppedReason, DateTimeOffset UpdatedAt, IReadOnlyList<ReleaseItem> Items);

/// <summary>A release move that its state or its approval does not allow; the message says which and names the item when it is one.</summary>
public sealed class ReleaseRefusedException(string message) : InvalidOperationException(message);

/// <summary>Whether an approver holds the approval permission for an item (the host asks its identity home: the permission differs by product and kind).</summary>
public interface IReleaseApprovalPermissions
{
    bool Holds(string approver, ReleaseItem item);
}

/// <summary>What one item's promotion did: it worked, or it failed and says why. A thrown exception (other than a cancellation) is a failure with its message.</summary>
public sealed record ReleaseItemResult(bool Ok, string? Error = null)
{
    public static ReleaseItemResult Success { get; } = new(true);

    public static ReleaseItemResult Failure(string error) => new(false, error);
}

/// <summary>Performs one item's promotion (plan 30 PB06's promotion between groups plugs in here).</summary>
public interface IReleaseItemRunner
{
    Task<ReleaseItemResult> RunAsync(ReleaseRecord release, ReleaseItem item, CancellationToken cancellationToken);
}

/// <summary>The releases of an install: created as drafts, approved once, run in order. The state words are <see cref="ReleaseStates"/>.</summary>
public sealed class ReleaseStore(StateDatabase database)
{
    private const string Module = ReleasesModule.Name;

    public ReleaseRecord Create(string name, string by, IReadOnlyList<ReleaseItem> items)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A release has a name.", nameof(name));
        if (items.Count == 0) throw new ReleaseRefusedException("A release has at least one item.");
        var id = Guid.CreateVersion7().ToString("N");
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:release} (id, name, state, created_by, created_at, updated_at) VALUES ($id, $name, $state, $by, $now, $now);"))
                insert.Bind(database, "id", id).Bind(database, "name", name).Bind(database, "state", ReleaseStates.Draft).Bind(database, "by", by).Bind(database, "now", now).ExecuteNonQuery();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                using var row = database.Command(Module, connection, transaction,
                    "INSERT INTO {table:release_item} (release_id, position, product, target, kind, item_name, item_version, from_channel, to_channel, state, source) VALUES ($id, $position, $product, $target, $kind, $name, $version, $from, $to, $state, $source);");
                row.Bind(database, "id", id).Bind(database, "position", i + 1).Bind(database, "product", item.Product).Bind(database, "target", item.Target).Bind(database, "kind", item.Kind)
                    .Bind(database, "name", item.Name).Bind(database, "version", item.Version).Bind(database, "from", item.FromChannel).Bind(database, "to", item.ToChannel).Bind(database, "state", ReleaseStates.Pending).Bind(database, "source", item.Source, System.Data.DbType.String).ExecuteNonQuery();
            }
            transaction.Commit();
        });
        return Get(id)!;
    }

    public ReleaseRecord? Get(string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Read(connection, id);
        });

    public IReadOnlyList<ReleaseRecord> List() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var ids = new List<string>();
            using (var command = database.Command(Module, connection, "SELECT id FROM {table:release} ORDER BY created_at DESC, id DESC;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) ids.Add(reader.GetString(0));
            return (IReadOnlyList<ReleaseRecord>)[.. ids.Select(id => Read(connection, id)!)];
        });

    private ReleaseRecord? Read(DbConnection connection, string id)
    {
        using var command = database.Command(Module, connection, "SELECT name, state, created_by, created_at, approved_by, approved_at, stopped_item, stopped_reason, updated_at FROM {table:release} WHERE id = $id;");
        command.Bind(database, "id", id);
        (string, string, string, DateTimeOffset, string?, DateTimeOffset?, int?, string?, DateTimeOffset)? head = null;
        using (var reader = command.ExecuteReader())
            if (reader.Read())
                head = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.Time(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.NullableTime(5), reader.NullableInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.Time(8));
        if (head is not { } h) return null;
        var items = new List<ReleaseItem>();
        using (var rows = database.Command(Module, connection, "SELECT position, product, target, kind, item_name, item_version, from_channel, to_channel, state, error, finished_at, source FROM {table:release_item} WHERE release_id = $id ORDER BY position;"))
        {
            rows.Bind(database, "id", id);
            using var reader = rows.ExecuteReader();
            while (reader.Read())
                items.Add(new ReleaseItem(reader.Int32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.Int32(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.NullableTime(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return new ReleaseRecord(id, h.Item1, h.Item2, h.Item3, h.Item4, h.Item5, h.Item6, h.Item7, h.Item8, h.Item9, items);
    }

    /// <summary>
    /// The one approval of a draft release. <paramref name="approver"/> must hold the approval permission for <b>every</b> item; an approver who lacks it for some is refused, naming each item. Approving what is not a draft is refused.
    /// </summary>
    public ReleaseRecord Approve(string id, string approver, IReleaseApprovalPermissions permissions)
    {
        var release = Require(id);
        if (release.State != ReleaseStates.Draft) throw new ReleaseRefusedException($"The release '{release.Name}' is {release.State}; only a draft is approved.");
        var lacking = release.Items.Where(i => !permissions.Holds(approver, i)).ToList();
        if (lacking.Count > 0)
            throw new ReleaseRefusedException($"{approver} does not hold the approval permission for {string.Join("; ", lacking.Select(i => i.Describe()))}, so cannot approve the release '{release.Name}'.");
        Move(id, [ReleaseStates.Draft], ReleaseStates.Approved, $"The release '{release.Name}' changed while it was being approved.", approver: approver);
        return Get(id)!;
    }

    /// <summary>Stops a release that has not run to its end: a draft, an approved one or a stopped one. A running release is stopped by its failure or cancellation, not abandoned under the run.</summary>
    public ReleaseRecord Abandon(string id)
    {
        var release = Require(id);
        if (release.State is not (ReleaseStates.Draft or ReleaseStates.Approved or ReleaseStates.Stopped))
            throw new ReleaseRefusedException($"The release '{release.Name}' is {release.State}; only a draft, an approved or a stopped release is abandoned.");
        Move(id, [release.State], ReleaseStates.Abandoned, $"The release '{release.Name}' changed meanwhile.");
        return Get(id)!;
    }

    internal ReleaseRecord Require(string id) => Get(id) ?? throw new ReleaseRefusedException($"There is no release '{id}'.");

    /// <summary>A conditional move: the state changes only if it still is one of <paramref name="from"/>, so two callers cannot both start or approve the same release.</summary>
    internal void Move(string id, string[] from, string to, string conflict, string? approver = null, int? stoppedAt = null, string? stoppedReason = null, bool clearStop = false)
    {
        var now = database.Time.GetUtcNow();
        var changed = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var states = string.Join(", ", from.Select((_, i) => $"$from{i}"));
            using var command = database.Command(Module, connection,
                "UPDATE {table:release} SET state = $to, updated_at = $now" + (approver is null ? "" : ", approved_by = $approver, approved_at = $now")
                + (clearStop ? ", stopped_item = NULL, stopped_reason = NULL" : stoppedAt is null ? "" : ", stopped_item = $stopped, stopped_reason = $reason") + $" WHERE id = $id AND state IN ({states});");
            command.Bind(database, "to", to).Bind(database, "now", now).Bind(database, "id", id);
            if (approver is not null) command.Bind(database, "approver", approver);
            if (stoppedAt is not null && !clearStop) command.Bind(database, "stopped", stoppedAt.Value).Bind(database, "reason", stoppedReason ?? "");
            for (var i = 0; i < from.Length; i++) command.Bind(database, $"from{i}", from[i]);
            return command.ExecuteNonQuery();
        });
        if (changed == 0) throw new ReleaseRefusedException(conflict);
    }

    internal void SetItem(string id, int position, string state, string? error)
    {
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "UPDATE {table:release_item} SET state = $state, error = $error, finished_at = $at WHERE release_id = $id AND position = $position;");
            command.Bind(database, "state", state).Bind(database, "error", error, System.Data.DbType.String).Bind(database, "at", state == ReleaseStates.Pending ? null : now, System.Data.DbType.DateTimeOffset).Bind(database, "id", id).Bind(database, "position", position).ExecuteNonQuery();
        });
    }
}

/// <summary>
/// Runs approved releases (plan 21 CH02): <see cref="StartAsync"/> runs the items in order and stops at the first that fails, saying which; <see cref="ResumeAsync"/> continues from that item (a failed item is tried again,
/// the ones done are not repeated). It is a sequence, not a transaction: what the earlier items did stays done.
/// </summary>
public sealed class ReleaseRunner(ReleaseStore store, IReleaseItemRunner items)
{
    public async Task<ReleaseRecord> StartAsync(string id, CancellationToken cancellationToken = default)
    {
        var release = store.Require(id);
        if (release.State == ReleaseStates.Draft) throw new ReleaseRefusedException($"The release '{release.Name}' is a draft: it needs its approval before it runs.");
        if (release.State != ReleaseStates.Approved) throw new ReleaseRefusedException($"The release '{release.Name}' is {release.State}; only an approved release is started (a stopped one is resumed).");
        store.Move(id, [ReleaseStates.Approved], ReleaseStates.Running, $"The release '{release.Name}' was started meanwhile.");
        return await RunAsync(id, cancellationToken);
    }

    public async Task<ReleaseRecord> ResumeAsync(string id, CancellationToken cancellationToken = default)
    {
        var release = store.Require(id);
        if (release.State != ReleaseStates.Stopped) throw new ReleaseRefusedException($"The release '{release.Name}' is {release.State}; only a stopped release is resumed.");
        store.Move(id, [ReleaseStates.Stopped], ReleaseStates.Running, $"The release '{release.Name}' was resumed meanwhile.", clearStop: true);
        return await RunAsync(id, cancellationToken);
    }

    private async Task<ReleaseRecord> RunAsync(string id, CancellationToken cancellationToken)
    {
        var release = store.Require(id);
        foreach (var item in release.Items.Where(i => i.State != ReleaseStates.Done))
        {
            ReleaseItemResult result;
            try { result = await items.RunAsync(release, item, cancellationToken); }
            catch (OperationCanceledException)
            {
                store.Move(id, [ReleaseStates.Running], ReleaseStates.Stopped, "", stoppedAt: item.Position, stoppedReason: $"Cancelled while {item.Describe()} ran; resume continues there.");
                throw;
            }
            catch (Exception ex) { result = ReleaseItemResult.Failure(ex.Message); }
            if (!result.Ok)
            {
                var error = result.Error ?? "it failed";
                store.SetItem(id, item.Position, ReleaseStates.Failed, error);
                store.Move(id, [ReleaseStates.Running], ReleaseStates.Stopped, "", stoppedAt: item.Position, stoppedReason: $"{item.Describe()} failed: {error}. The items before it are done and stay done; resume continues here.");
                return store.Require(id);
            }
            store.SetItem(id, item.Position, ReleaseStates.Done, null);
        }
        store.Move(id, [ReleaseStates.Running], ReleaseStates.Succeeded, "");
        return store.Require(id);
    }
}
