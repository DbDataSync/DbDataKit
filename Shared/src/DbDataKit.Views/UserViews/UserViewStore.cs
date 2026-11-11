using System.Data.Common;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Views;

/// <summary>A person's document as stored: the current version (the owner's name is lower case; the kind is <c>dashboard</c> or <c>report</c>).</summary>
public sealed record UserView(Guid Id, string Owner, string OwnerName, string Kind, string Name, int Version, string Title, string Text, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>A document in a list: who it belongs to, whether it is the caller's own, and (for the owner) who it is shared with.</summary>
public sealed record UserViewSummary(string Owner, string OwnerName, string Kind, string Name, string Title, int Version, DateTimeOffset UpdatedAt, bool Own, IReadOnlyList<string> SharedWith);

/// <summary>One version of a document's history; <paramref name="Current"/> is the version that is the document now.</summary>
public sealed record UserViewVersion(int Version, string Text, string SavedBy, DateTimeOffset SavedAt, bool Current);

/// <summary>A document promoted to a file: where it goes in an app's source or a Build project, and its text (which the views loader reads).</summary>
public sealed record UserViewFile(string Path, string Text);

/// <summary>The document was changed since the version the caller read, already exists when it was to be created, or is not there to be changed. <see cref="CurrentVersion"/> is what is stored (null: nothing).</summary>
public sealed class UserViewConflictException(string message, int? currentVersion) : Exception(message)
{
    public int? CurrentVersion { get; } = currentVersion;
}

/// <summary>
/// Who is asking: the person's <b>id</b> (the owner of what they make: an id is unique and stays, a display name is neither), their name (what other people share with, as a <see cref="Handle"/>) and their groups (the <c>group:</c> grantees:
/// the roles of a caller, or the groups of the identity home).
/// </summary>
public sealed record UserViewViewer(string Id, string Name, IReadOnlyCollection<string> Groups)
{
    /// <summary>The name as a grantee writes it: lower case, runs of white space as <c>-</c> (<c>Ada Lovelace</c> is <c>ada-lovelace</c>), so a person can be named in a share.</summary>
    public static string Handle(string name) => System.Text.RegularExpressions.Regex.Replace(name.Trim().ToLowerInvariant(), @"\s+", "-");

    /// <summary>The grantees that name this viewer: <c>*</c>, <c>user:&lt;id&gt;</c>, <c>user:&lt;handle&gt;</c> and <c>group:&lt;group&gt;</c> for each group, lower case.</summary>
    public IReadOnlyList<string> Grantees => [.. new[] { "*", "user:" + Id.ToLowerInvariant(), "user:" + Handle(Name) }.Where(g => g.Length > 5 || g == "*").Distinct(), .. Groups.Select(r => "group:" + r.ToLowerInvariant()).Distinct()];
}

/// <summary>
/// User-owned dashboards and reports (plan 32 DBR12) in the serving group's state database (<see cref="UserViewsModule"/>), on whatever engine that is: SQLite, SQL Server or PostgreSQL. A person saves a document under a
/// name, **every save is a version** (the replaced one goes to the history, so an edit can be undone and an undo undone), shares it with a person, a role or everyone, and promotes it to a file.
/// <para>
/// <b>Who sees what.</b> The store decides which <b>documents</b> a viewer may open: their own and the ones shared with them (<see cref="Find"/>, <see cref="List"/>); never another person's otherwise. It decides nothing
/// about <b>rows</b>: the document's requests run through the serving core as whoever opens it (the host does that), so a document shared with a person whose access is narrower shows them only the rows their access gives.
/// Only the owner changes, shares, restores, promotes or deletes a document (those methods take the owner, and the host passes the caller).
/// </para>
/// <para>A save quotes the version the caller read: a stale one, or a second create of one name, is a <see cref="UserViewConflictException"/> that says where the document is.</para>
/// </summary>
public sealed partial class UserViewStore(StateDatabase database)
{
    private const string Module = UserViewsModule.Name;

    private const string Select = "SELECT id, owner, owner_name, kind, name, version, title, doc_text, created_at, updated_at, updated_by FROM {table:user_view}";

    /// <summary>The longest a document's name is.</summary>
    public const int MaxNameLength = 64;

    public static bool IsName(string name) => name.Length <= MaxNameLength && NamePattern().IsMatch(name);

    public static bool IsGrantee(string grantee) => GranteePattern().IsMatch(grantee);

    private static string Owner(string owner) => owner.ToLowerInvariant();

    private static void CheckKind(string kind)
    {
        if (kind is not (ViewDefinitions.Dashboard or ViewDefinitions.Report)) throw new ArgumentException($"A document is a dashboard or a report, not '{kind}'.", nameof(kind));
    }

    private static void CheckName(string name)
    {
        if (!IsName(name)) throw new ArgumentException($"A document's name is lower case letters, digits, '_' and '-', starting with a letter, at most {MaxNameLength} characters; '{name}' is not.", nameof(name));
    }

    // ---------- save, restore, delete ----------

    /// <summary>
    /// Creates the owner's document (<paramref name="expectedVersion"/> null; one of that name is a conflict) or saves a new version of it (the version the caller read: another is a conflict). The text is stored as it
    /// is: the host has validated it, the store does not read it.
    /// </summary>
    public UserView Save(string owner, string kind, string name, string title, string text, string savedBy, int? expectedVersion = null, string? ownerName = null)
    {
        CheckKind(kind);
        CheckName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(text);
        owner = Owner(owner);
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            var current = Read(connection, transaction, owner, kind, name);
            if (current is null)
            {
                if (expectedVersion is not null) throw new UserViewConflictException($"There is no {kind} '{name}' of yours to change; it was deleted since you opened it.", null);
                using var insert = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore("{table:user_view}",
                    "id, owner, owner_name, kind, name, version, title, doc_text, created_at, updated_at, updated_by", "$id, $owner, $ownerName, $kind, $name, 1, $title, $text, $now, $now, $by", "owner, kind, name"));
                insert.Bind(database, "id", database.NewId()).Bind(database, "owner", owner).Bind(database, "ownerName", ownerName ?? owner).Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "title", title)
                    .Bind(database, "text", text).Bind(database, "now", now).Bind(database, "by", savedBy);
                if (insert.ExecuteNonQuery() != 1)
                    throw new UserViewConflictException($"You already have a {kind} '{name}' (another save made it first); open it and save on its version.", Read(connection, transaction, owner, kind, name)?.Version);
            }
            else
            {
                if (current.Version != expectedVersion)
                    throw new UserViewConflictException(expectedVersion is null
                        ? $"You already have a {kind} '{name}' (version {current.Version}); open it and save on that version."
                        : $"The {kind} '{name}' is at version {current.Version}, not {expectedVersion}; open it again and save on that version.", current.Version);
                // the update is the guard: it moves the row only while it is still at the version that was read (a second writer's change makes it touch nothing, and the transaction is undone)
                using var update = database.Command(Module, connection, transaction,
                    "UPDATE {table:user_view} SET version = $next, title = $title, doc_text = $text, updated_at = $now, updated_by = $by, owner_name = $ownerName WHERE id = $id AND version = $expected;");
                update.Bind(database, "next", current.Version + 1).Bind(database, "ownerName", ownerName ?? current.OwnerName).Bind(database, "title", title).Bind(database, "text", text).Bind(database, "now", now).Bind(database, "by", savedBy)
                    .Bind(database, "id", current.Id).Bind(database, "expected", current.Version);
                if (update.ExecuteNonQuery() != 1)
                    throw new UserViewConflictException($"The {kind} '{name}' was changed while you saved; open it again and save on its new version.", Read(connection, transaction, owner, kind, name)?.Version);
                using var history = database.Command(Module, connection, transaction, "INSERT INTO {table:user_view_version} (view_id, version, doc_text, saved_by, saved_at) VALUES ($id, $version, $text, $by, $at);");
                history.Bind(database, "id", current.Id).Bind(database, "version", current.Version).Bind(database, "text", current.Text).Bind(database, "by", current.UpdatedBy).Bind(database, "at", current.UpdatedAt);
                history.ExecuteNonQuery();
            }
            transaction.Commit();
            return Read(connection, null, owner, kind, name)!;
        });
    }

    /// <summary>Makes an earlier version the document again, **as a new version** (the history only grows). <paramref name="expectedVersion"/> is the version the caller sees as current.</summary>
    public UserView Restore(string owner, string kind, string name, int version, string savedBy, int expectedVersion, Func<string, string> titleOf, string? ownerName = null)
    {
        var current = Get(owner, kind, name) ?? throw new UserViewConflictException($"There is no {kind} '{name}' of yours.", null);
        var earlier = History(owner, kind, name).FirstOrDefault(v => v.Version == version) ?? throw new UserViewConflictException($"The {kind} '{name}' has no version {version}.", current.Version);
        return Save(owner, kind, name, titleOf(earlier.Text), earlier.Text, savedBy, expectedVersion, ownerName);
    }

    /// <summary>Deletes the owner's document with its history and its shares; false when there was none.</summary>
    public bool Delete(string owner, string kind, string name)
    {
        owner = Owner(owner);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            var current = Read(connection, transaction, owner, kind, name);
            if (current is null) return false;
            foreach (var table in new[] { "user_view_share", "user_view_version" })
            {
                using var children = database.Command(Module, connection, transaction, $"DELETE FROM {{table:{table}}} WHERE view_id = $id;");
                children.Bind(database, "id", current.Id).ExecuteNonQuery();
            }
            using var view = database.Command(Module, connection, transaction, "DELETE FROM {table:user_view} WHERE id = $id;");
            view.Bind(database, "id", current.Id).ExecuteNonQuery();
            transaction.Commit();
            return true;
        });
    }

    // ---------- reads ----------

    /// <summary>The owner's document, or null. Not a check of who is asking: the host calls it for the owner, and <see cref="Find"/> for anyone else.</summary>
    public UserView? Get(string owner, string kind, string name)
    {
        owner = Owner(owner);
        return database.Retry(() => { using var connection = database.OpenConnection(); return Read(connection, null, owner, kind, name); });
    }

    /// <summary>The document as <paramref name="viewer"/> may open it: their own, or one shared with them (<c>*</c>, <c>user:</c> their name, <c>group:</c> one of their roles); null when there is none or it is not for them.</summary>
    public UserView? Find(UserViewViewer viewer, string owner, string kind, string name)
    {
        owner = Owner(owner);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var view = Read(connection, null, owner, kind, name);
            if (view is null) return null;
            if (string.Equals(view.Owner, Owner(viewer.Id), StringComparison.Ordinal)) return view;
            return Shared(connection, view.Id, viewer) ? view : null;
        });
    }

    /// <summary>The viewer's own documents (<paramref name="own"/>) or the ones shared with them by others (<paramref name="shared"/>), of a kind or both, by owner, kind and name.</summary>
    public IReadOnlyList<UserViewSummary> List(UserViewViewer viewer, string? kind = null, bool own = true, bool shared = true)
    {
        var me = Owner(viewer.Id);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var found = new List<UserViewSummary>();
            if (own)
            {
                using var mine = database.Command(Module, connection, $"{Select} WHERE owner = $owner{(kind is null ? "" : " AND kind = $kind")} ORDER BY kind, name;");
                mine.Bind(database, "owner", me);
                if (kind is not null) mine.Bind(database, "kind", kind);
                var views = ReadAll(mine);
                var shares = SharesOf(connection, views.Select(v => v.Id).ToList());
                found.AddRange(views.Select(v => new UserViewSummary(v.Owner, v.OwnerName, v.Kind, v.Name, v.Title, v.Version, v.UpdatedAt, true, shares.GetValueOrDefault(v.Id) ?? [])));
            }
            if (shared)
            {
                var grantees = viewer.Grantees;
                var names = string.Join(", ", grantees.Select((_, i) => "$g" + i));
                using var others = database.Command(Module, connection, $"{Select} WHERE owner <> $owner{(kind is null ? "" : " AND kind = $kind")} AND id IN (SELECT view_id FROM {{table:user_view_share}} WHERE grantee IN ({names})) ORDER BY owner, kind, name;");
                others.Bind(database, "owner", me);
                if (kind is not null) others.Bind(database, "kind", kind);
                for (var i = 0; i < grantees.Count; i++) others.Bind(database, "g" + i, grantees[i]);
                found.AddRange(ReadAll(others).Select(v => new UserViewSummary(v.Owner, v.OwnerName, v.Kind, v.Name, v.Title, v.Version, v.UpdatedAt, false, [])));
            }
            return (IReadOnlyList<UserViewSummary>)found;
        });
    }

    /// <summary>Every version of the owner's document, newest first; the first is the current one.</summary>
    public IReadOnlyList<UserViewVersion> History(string owner, string kind, string name)
    {
        owner = Owner(owner);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var current = Read(connection, null, owner, kind, name);
            if (current is null) return (IReadOnlyList<UserViewVersion>)[];
            var versions = new List<UserViewVersion> { new(current.Version, current.Text, current.UpdatedBy, current.UpdatedAt, true) };
            using var command = database.Command(Module, connection, "SELECT version, doc_text, saved_by, saved_at FROM {table:user_view_version} WHERE view_id = $id ORDER BY version DESC;");
            command.Bind(database, "id", current.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) versions.Add(new UserViewVersion(reader.Int32(0), reader.GetString(1), reader.GetString(2), reader.Time(3), false));
            return versions;
        });
    }

    // ---------- shares ----------

    /// <summary>Shares the owner's document with <c>user:alice</c>, <c>group:sales</c> or <c>*</c>; sharing twice is no change.</summary>
    public void Share(string owner, string kind, string name, string grantee, string sharedBy)
    {
        if (!IsGrantee(grantee)) throw new ArgumentException("A grantee is user:<name>, group:<role> or *.", nameof(grantee));
        grantee = grantee.ToLowerInvariant();
        owner = Owner(owner);
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var view = Read(connection, null, owner, kind, name) ?? throw new UserViewConflictException($"There is no {kind} '{name}' of yours to share.", null);
            using var insert = database.Command(Module, connection, database.Dialect.InsertOrIgnore("{table:user_view_share}", "view_id, grantee, shared_by, shared_at", "$id, $grantee, $by, $now", "view_id, grantee"));
            insert.Bind(database, "id", view.Id).Bind(database, "grantee", grantee).Bind(database, "by", sharedBy).Bind(database, "now", now).ExecuteNonQuery();
        });
    }

    public void Unshare(string owner, string kind, string name, string grantee)
    {
        grantee = grantee.ToLowerInvariant();
        owner = Owner(owner);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var view = Read(connection, null, owner, kind, name);
            if (view is null) return;
            using var command = database.Command(Module, connection, "DELETE FROM {table:user_view_share} WHERE view_id = $id AND grantee = $grantee;");
            command.Bind(database, "id", view.Id).Bind(database, "grantee", grantee).ExecuteNonQuery();
        });
    }

    /// <summary>Who the owner's document is shared with, in order.</summary>
    public IReadOnlyList<string> Shares(string owner, string kind, string name)
    {
        owner = Owner(owner);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var view = Read(connection, null, owner, kind, name);
            return view is null ? [] : SharesOf(connection, [view.Id]).GetValueOrDefault(view.Id) ?? (IReadOnlyList<string>)[];
        });
    }

    // ---------- promote ----------

    /// <summary>
    /// The document as a file for an app's source or a Build project (plan 32 section 9, DBR29): <c>dashboards/&lt;name&gt;.yaml</c> or <c>reports/&lt;name&gt;.yaml</c> (the extension the text has: JSON stays <c>.json</c>), with the stored text.
    /// The text is read with the same loader the files are (<see cref="ViewsLoader"/>) first, and a document that does not load is refused with its findings: a promoted file always loads. The owner's document is not changed or removed.
    /// </summary>
    public UserViewFile Promote(string owner, string kind, string name)
    {
        var view = Get(owner, kind, name) ?? throw new UserViewConflictException($"There is no {kind} '{name}' of yours to promote.", null);
        var extension = view.Text.AsSpan().TrimStart().StartsWith("{") ? "json" : "yaml";
        var path = $"{(kind == ViewDefinitions.Report ? "reports" : "dashboards")}/{name}.{extension}";
        var loaded = ViewsLoader.Load(view.Text, path, ViewDefinitions.KindOf(kind));
        var errors = loaded.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).ToList();
        if (loaded.Document is null || errors.Count > 0)
            throw new InvalidOperationException($"The {kind} '{name}' does not load as a file ({string.Join(" | ", errors.Take(3).Select(d => d.ToString()))}); it is not promoted.");
        return new UserViewFile(path, view.Text.EndsWith('\n') ? view.Text : view.Text + "\n");
    }

    // ---------- rows ----------

    private UserView? Read(DbConnection connection, DbTransaction? transaction, string owner, string kind, string name)
    {
        using var command = database.Command(Module, connection, transaction, $"{Select} WHERE owner = $owner AND kind = $kind AND name = $name;");
        command.Bind(database, "owner", owner).Bind(database, "kind", kind).Bind(database, "name", name);
        return ReadAll(command).FirstOrDefault();
    }

    private static List<UserView> ReadAll(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var all = new List<UserView>();
        while (reader.Read())
            all.Add(new UserView(reader.Uuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.Int32(5), reader.GetString(6), reader.GetString(7), reader.Time(8), reader.Time(9), reader.GetString(10)));
        return all;
    }

    private bool Shared(DbConnection connection, Guid id, UserViewViewer viewer)
    {
        var grantees = viewer.Grantees;
        var names = string.Join(", ", grantees.Select((_, i) => "$g" + i));
        using var command = database.Command(Module, connection, $"SELECT COUNT(*) FROM {{table:user_view_share}} WHERE view_id = $id AND grantee IN ({names});");
        command.Bind(database, "id", id);
        for (var i = 0; i < grantees.Count; i++) command.Bind(database, "g" + i, grantees[i]);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private Dictionary<Guid, IReadOnlyList<string>> SharesOf(DbConnection connection, List<Guid> ids)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        if (ids.Count == 0) return result;
        var names = string.Join(", ", ids.Select((_, i) => "$v" + i));
        using var command = database.Command(Module, connection, $"SELECT view_id, grantee FROM {{table:user_view_share}} WHERE view_id IN ({names}) ORDER BY grantee;");
        for (var i = 0; i < ids.Count; i++) command.Bind(database, "v" + i, ids[i]);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.Uuid(0);
            result[id] = [.. result.GetValueOrDefault(id) ?? [], reader.GetString(1)];
        }
        return result;
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(\*|user:[^\s]+|group:[^\s]+)$")]
    private static partial Regex GranteePattern();
}
