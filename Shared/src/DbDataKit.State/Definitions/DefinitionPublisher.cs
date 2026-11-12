using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>One file a publish names: its path within the version and its content hash. <paramref name="PayloadId"/> is the stored payload when the caller has it; otherwise the publisher finds it by hash.</summary>
public sealed record ArtifactRef(string Path, string Sha256, long Size, string? MediaType, Guid? PayloadId = null);

/// <summary>A file of a stored version.</summary>
public sealed record DefinitionArtifact(string Path, string Sha256, long Size, string? MediaType, Guid PayloadId);

/// <summary>What a channel has of a definition: the active version (null when none), the one before it, and the staged one.</summary>
public sealed record DefinitionHead(string Kind, string Name, string Channel, int? Active, int? Previous, int? Staged);

/// <summary>What a slot is bound to in a channel.</summary>
public sealed record DefinitionBinding(string Channel, string Kind, string Name, string Slot, string BoundTo, int Revision, string BoundBy, DateTimeOffset BoundAt);

/// <summary>A publish: the manifest and files of the next version, where it goes and how.</summary>
/// <param name="Manifest">What the version is, as text (the kind's format); stored as the version's content.</param>
/// <param name="Mode">Null takes the kind's default.</param>
/// <param name="ExpectedVersion">The highest version number the definition has, as the caller saw it (0 for a first publish); null does not check.</param>
/// <param name="Format">The format of the manifest (24a UC01, numbered from 1 per kind); null takes what the kind reads from the manifest (<see cref="DefinitionKind.FormatOf"/>), or 1.</param>
public sealed record PublishRequest(string Kind, string Name, string Manifest, string By, string Channel = DefinitionStore.Production, PublishMode? Mode = null, IReadOnlyList<string>? DependsOn = null,
    string? Note = null, IReadOnlyList<ArtifactRef>? Artifacts = null, string? Progress = null, int? ExpectedVersion = null, int? Format = null, string? Signature = null, string? Signer = null);

/// <summary>What a publish did: the version, whether anything changed (the same content again changes nothing), and the mode it took.</summary>
public sealed record PublishResult(DefinitionVersion Version, bool Changed, PublishMode Mode);

/// <summary>The outcome of <see cref="DefinitionPublisher.ActivateIfAllReady"/>: activated, or not, with the progress that decided it. <paramref name="AlreadyDone"/> is true when another caller activated the staged version first (nothing was done here).</summary>
public sealed record ActivationResult(bool Activated, StageProgress Progress, DefinitionVersion? Version, bool AlreadyDone = false);

/// <summary>
/// What a pointer change hands the work that must happen in the same transaction (plan 30 section 3.5): the connection and transaction the change is written on, and the versions it moves between
/// (<paramref name="From"/> null when nothing was active). The work runs after the pointer moved and before the transaction commits; if it throws, the pointer does not move.
/// </summary>
public sealed record DefinitionChange(DbConnection Connection, DbTransaction Transaction, string Kind, string Name, string Channel, int? From, int To);

/// <summary>
/// Plan 30's life cycle on the <c>definitions</c> module (R15): publish (serve or stage), activate, switch, rollback, retire and bind, each in one transaction with an event, each with an
/// optimistic <c>expected_version</c>. The registry <see cref="DefinitionKinds"/> says what each kind validates, which slots it needs bound in a channel and whether it needs approval.
/// <para>
/// <b>expected_version.</b> For a publish it is the highest version number the definition has (so two publishers that both looked at version 4 cannot both write version 5); for activate, switch,
/// rollback and retire it is the version the channel has active (0 for none). A stale one is a <see cref="DefinitionConflictException"/> and changes nothing. The same content again is checked first,
/// so a retried publish whose first attempt succeeded returns the same version instead of a conflict.
/// </para>
/// <para>
/// <b>Compatibility.</b> <see cref="DefinitionStore"/> publishes, switches, rolls back and retires through this class with no registry (<c>kinds</c> null): any kind name is accepted, nothing
/// is checked for dependencies or slots, and every publish serves. The rows are the same the life cycle reads.
/// </para>
/// </summary>
public sealed partial class DefinitionPublisher
{
    private const string Module = DefinitionsModule.Name;

    private readonly StateDatabase _database;
    private readonly DefinitionKinds? _kinds;
    private readonly PayloadStore? _payloads;
    private readonly string? _targetType;
    private readonly DefinitionStore _store;

    /// <param name="database">A database with the <c>definitions</c> module.</param>
    /// <param name="kinds">The registry; null is the compatibility mode of the older <see cref="DefinitionStore"/> (see the class remarks).</param>
    /// <param name="payloads">Where artifacts' bytes are; when given, a named payload is checked to exist and to have the hash named.</param>
    /// <param name="targetType">The target type this publisher serves (<c>serving</c>, <c>runner</c>); a kind that does not list it is refused.</param>
    public DefinitionPublisher(StateDatabase database, DefinitionKinds? kinds, PayloadStore? payloads = null, string? targetType = null)
    {
        _database = database;
        _kinds = kinds;
        _payloads = payloads;
        _targetType = targetType;
        _store = new DefinitionStore(database);
        Status = new InstanceStatusStore(database);
    }

    /// <summary>What this publisher's program reads of a kind (24a UC01); format 1 only when it has no registry or the kind is not in it.</summary>
    public DefinitionFormats FormatsOf(string kind) => _kinds?.Find(kind)?.Formats ?? DefinitionFormats.One;

    /// <summary>The per-instance status rows of this group.</summary>
    public InstanceStatusStore Status { get; }

    [GeneratedRegex("^([a-z0-9][a-z0-9_-]{0,63}):([A-Za-z0-9][A-Za-z0-9_.-]{0,63})(?:@([0-9]+))?$")]
    private static partial Regex Dependency();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    // ---- publish

    /// <summary>Validates and stores the next version and serves or stages it, in one transaction. The same content as what the channel serves (or has staged) changes nothing.</summary>
    public PublishResult Publish(PublishRequest request, Func<string, string?>? validate = null)
    {
        DefinitionStore.Check(request.Kind, request.Name, request.Channel);
        var kind = _kinds?.Require(request.Kind);
        var mode = request.Mode ?? kind?.DefaultMode ?? PublishMode.Serve;
        if (kind is not null)
        {
            if (_targetType is not null && !kind.TargetTypes.Contains(_targetType))
                throw new PublishRefusedException(PublishRefusal.WrongTarget, $"The kind '{kind.Name}' is not accepted by a {_targetType} target (it is accepted by {string.Join(", ", kind.TargetTypes)}).");
            if (kind.RequiresApproval && mode == PublishMode.Serve)
                throw new PublishRefusedException(PublishRefusal.ApprovalRequired, $"The {kind.Name} '{request.Name}' is trusted code: publish it staged, have an administrator approve it, then activate it.");
        }

        var format = request.Format ?? kind?.FormatOf?.Invoke(request.Manifest) ?? DefinitionFormat.First;
        if (format < DefinitionFormat.First) throw new PublishRefusedException(PublishRefusal.Format, $"The {request.Kind} '{request.Name}' names format {format}; formats are numbered from {DefinitionFormat.First}.");
        if (kind is not null && DefinitionFormat.Refusal(kind.Name, request.Name, null, format, kind.Formats, "this server") is { } unreadable)
            throw new PublishRefusedException(PublishRefusal.Format, unreadable);

        var depends = request.DependsOn ?? [];
        var artifacts = ResolveArtifacts(request.Artifacts ?? []);
        // the validator is shown the files as resolved (their payloads found and checked), so it can read what a file holds
        var candidate = new DefinitionCandidate(request.Kind, request.Name, request.Manifest, [.. artifacts.Select(a => new ArtifactRef(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId))], depends, format);
        if (validate?.Invoke(request.Manifest) is { } refusal) throw new DefinitionRejectedException(refusal);
        if (kind?.Validator?.Invoke(candidate) is { } kindRefusal)
            throw new PublishRefusedException(PublishRefusal.Invalid, $"The {request.Kind} '{request.Name}' failed its validator: {kindRefusal}");

        var hash = HashOf(request.Manifest, artifacts, format);
        var now = _database.Time.GetUtcNow();
        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var head = ReadHead(connection, transaction, request.Kind, request.Name, request.Channel);
            var active = head is { Active: > 0 } ? _store.ReadVersion(connection, transaction, request.Kind, request.Name, head.Active) : null;
            var staged = head?.Staged is { } s ? _store.ReadVersion(connection, transaction, request.Kind, request.Name, s) : null;

            // the same content as what the channel serves, or already has staged, changes nothing; a serve of what is staged activates that version instead of storing a copy
            if (mode == PublishMode.Serve)
            {
                if (active?.Hash == hash) return new PublishResult(active, false, mode);
            }
            else if (active?.Hash == hash || staged?.Hash == hash)
            {
                return new PublishResult(staged?.Hash == hash ? staged : active!, false, mode);
            }

            // a version some live instance of the group cannot read is never stored (24a D4)
            CheckInstancesRead(connection, transaction, request.Kind, request.Name, null, format);
            var latest = Latest(connection, transaction, request.Kind, request.Name);
            if (request.ExpectedVersion is { } expected && expected != latest)
                throw new DefinitionConflictException($"The {request.Kind} '{request.Name}' is at version {latest}, not {expected}: it was published since you looked. Read it again and publish again.", expected, latest);

            DefinitionVersion stored;
            if (mode == PublishMode.Serve && staged?.Hash == hash)
            {
                stored = staged;
            }
            else
            {
                var number = latest + 1;
                try { Insert(connection, transaction, request, artifacts, hash, number, now, format); }
                catch (Exception ex) when (_database.Dialect.IsUniqueViolation(ex))
                {
                    // another publisher, perhaps through another instance of the group, took this number between the check above and the insert: the version check is the key (plan 20 O19), so this is the same refusal
                    transaction.Rollback();
                    using var other = _database.OpenConnection();
                    var actual = Latest(other, null, request.Kind, request.Name);
                    throw new DefinitionConflictException($"The {request.Kind} '{request.Name}' is at version {actual}, not {request.ExpectedVersion ?? latest}: it was published since you looked. Read it again and publish again.", request.ExpectedVersion ?? latest, actual);
                }
                stored = _store.ReadVersion(connection, transaction, request.Kind, request.Name, number)!;
            }

            if (mode == PublishMode.Serve)
            {
                CheckInstancesRead(connection, transaction, request.Kind, request.Name, stored.Version, stored.Format);
                if (_kinds is not null) CheckCanBeActive(connection, transaction, request.Kind, request.Name, request.Channel, stored, depends: stored.DependsOn);
                var after = new Pointer(stored.Version, head is { Active: > 0 } ? head.Active : null, head?.Staged == stored.Version ? null : head?.Staged, head?.StagedAt, head?.StagedBy);
                Save(connection, transaction, request.Kind, request.Name, request.Channel, after, head is not null, activeChanged: true, request.By, now);
                Log(connection, transaction, "publish", request.Kind, request.Name, request.Channel, stored.Version, request.By, now);
            }
            else
            {
                CheckSlots(connection, transaction, request.Kind, request.Name, request.Channel, stored);
                var after = new Pointer(head?.Active ?? 0, head?.Previous, stored.Version, now, request.By);
                Save(connection, transaction, request.Kind, request.Name, request.Channel, after, head is not null, activeChanged: false, request.By, now);
                Log(connection, transaction, "stage", request.Kind, request.Name, request.Channel, stored.Version, request.By, now);
            }

            transaction.Commit();
            return new PublishResult(stored, true, mode);
        });
    }

    // ---- activate, switch, rollback, retire

    /// <summary>Makes the staged version active; the one it replaces becomes the previous version. Refused when nothing is staged, a dependency is not active, a slot is unbound or approval is missing.</summary>
    /// <param name="inTransaction">Work that must happen in the same transaction as the pointer (a progress migration, a projection rebuild); see <see cref="DefinitionChange"/>.</param>
    public DefinitionVersion Activate(string kind, string name, string by, string channel = DefinitionStore.Production, int? expectedVersion = null, string? note = null, Action<DefinitionChange>? inTransaction = null) =>
        Run(kind, name, channel, connection => ActivateIn(connection.Connection, connection.Transaction, kind, name, channel, by, expectedVersion, "activate", note, inTransaction: inTransaction)
            ?? throw new DefinitionConflictException($"The {kind} '{name}' was activated by someone else while this call was activating it; read it again.", expectedVersion ?? 0, 0));

    /// <summary>Activates the staged version when every instance in <paramref name="live"/> has it ready, in one transaction with the check; otherwise nothing changes and the progress says who is not ready.</summary>
    public ActivationResult ActivateIfAllReady(string kind, string name, IReadOnlyCollection<string> live, string by, string channel = DefinitionStore.Production, Action<DefinitionChange>? inTransaction = null)
    {
        DefinitionStore.Check(kind, name, channel);
        var now = _database.Time.GetUtcNow();
        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var progress = Status.Progress(connection, transaction, kind, name, channel, live);
            if (progress.StagedVersion is null) throw new DefinitionNotFoundException($"The {kind} '{name}' has nothing staged in channel '{channel}'.");
            if (!progress.IsAllReady) return new ActivationResult(false, progress, null);
            var version = ActivateIn(connection, transaction, kind, name, channel, by, null, "activate", $"automatic: {progress.Describe()}", now, inTransaction);
            if (version is null) return new ActivationResult(false, progress, null, AlreadyDone: true);
            transaction.Commit();
            return new ActivationResult(true, progress, version);
        });
    }

    /// <summary>
    /// An administrator's choice to activate the staged version although instances are not ready or failed (the ones that failed keep the previous version and say so). The choice, who made it and what
    /// stood in the way are recorded in the event.
    /// </summary>
    public ActivationResult ActivateAnyway(string kind, string name, IReadOnlyCollection<string> live, string actor, string channel = DefinitionStore.Production, string? reason = null, Action<DefinitionChange>? inTransaction = null, string? extraNote = null)
    {
        DefinitionStore.Check(kind, name, channel);
        var now = _database.Time.GetUtcNow();
        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var progress = Status.Progress(connection, transaction, kind, name, channel, live);
            if (progress.StagedVersion is null) throw new DefinitionNotFoundException($"The {kind} '{name}' has nothing staged in channel '{channel}'.");
            var note = $"anyway: activated by {actor} with {progress.Describe()}{(string.IsNullOrWhiteSpace(reason) ? "" : $"; reason: {reason}")}{(extraNote is null ? "" : "; " + extraNote)}";
            var version = ActivateIn(connection, transaction, kind, name, channel, actor, null, "activate", note, now, inTransaction);
            if (version is null) return new ActivationResult(false, progress, null, AlreadyDone: true);
            transaction.Commit();
            return new ActivationResult(true, progress, version);
        });
    }

    /// <summary>Points the channel at any stored version (promotion). The same version again changes nothing.</summary>
    public DefinitionVersion Switch(string kind, string name, int version, string by, string channel = DefinitionStore.Production, int? expectedVersion = null, Action<DefinitionChange>? inTransaction = null, string? note = null) =>
        Run(kind, name, channel, c => SwitchIn(c, kind, name, version, by, channel, expectedVersion, inTransaction, note));

    private DefinitionVersion SwitchIn(Scope c, string kind, string name, int version, string by, string channel, int? expectedVersion, Action<DefinitionChange>? inTransaction, string? note)
    {
        var head = ReadHead(c.Connection, c.Transaction, kind, name, channel);
        CheckExpected(kind, name, expectedVersion, head);
        if (_store.ReadVersion(c.Connection, c.Transaction, kind, name, version) is not { } wanted) throw new DefinitionNotFoundException($"'{name}' has no version {version}.");
        if (head is { Active: > 0 } already && already.Active == version) return wanted;
        CheckInstancesRead(c.Connection, c.Transaction, kind, name, wanted.Version, wanted.Format);
        if (_kinds is not null) CheckCanBeActive(c.Connection, c.Transaction, kind, name, channel, wanted, wanted.DependsOn);
        var after = new Pointer(version, head is { Active: > 0 } ? head.Active : null, head?.Staged == version ? null : head?.Staged, head?.StagedAt, head?.StagedBy);
        if (!Save(c.Connection, c.Transaction, kind, name, channel, after, head is not null, activeChanged: true, by, c.Now, head is null ? null : new Guard(head.Active, null, false)))
            throw new DefinitionConflictException($"The {kind} '{name}' was changed by someone else while this call switched it; read it again.", expectedVersion ?? 0, head?.Active ?? 0);
        inTransaction?.Invoke(new DefinitionChange(c.Connection, c.Transaction, kind, name, channel, head is { Active: > 0 } ? head.Active : null, version));
        Log(c.Connection, c.Transaction, "switch", kind, name, channel, version, by, c.Now, note);
        return wanted;
    }

    /// <summary>
    /// Promotion that carries what the version depends on (plan 32 DBR10: a dashboard moves with the model version it was published against): the dependencies a version pins (<c>kind:name@version</c>), and theirs,
    /// are pointed at the pinned version first, in the same transaction, and then the version itself; a dependency already at its pinned version is left alone, an unpinned one is only checked (it must be active, as
    /// for any promotion). If any step is refused nothing moves. Returns the versions that moved, dependencies first.
    /// </summary>
    public IReadOnlyList<DefinitionVersion> PromoteWithDependencies(string kind, string name, int version, string by, string channel = DefinitionStore.Production, string? note = null) =>
        Run(kind, name, channel, c =>
        {
            var moved = new List<DefinitionVersion>();
            var seen = new HashSet<(string, string)>();
            void Carry(string k, string n, int v)
            {
                if (!seen.Add((k, n))) return;
                if (_store.ReadVersion(c.Connection, c.Transaction, k, n, v) is not { } wanted) throw new DefinitionNotFoundException($"'{n}' has no version {v}.");
                foreach (var text in wanted.DependsOn)
                {
                    if (Dependency().Match(text) is not { Success: true, Groups: var g } || !g[3].Success) continue;
                    var dk = g[1].Value; var dn = g[2].Value; var dv = int.Parse(g[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (ReadHead(c.Connection, c.Transaction, dk, dn, channel) is { Active: > 0 } head && head.Active == dv) continue;
                    Carry(dk, dn, dv);
                }
                var before = ReadHead(c.Connection, c.Transaction, k, n, channel);
                var done = SwitchIn(c, k, n, v, by, channel, null, null, note);
                if (before is not { Active: > 0 } || before.Active != v) moved.Add(done);
            }
            Carry(kind, name, version);
            return (IReadOnlyList<DefinitionVersion>)moved;
        });

    /// <summary>Returns the channel to the version it had before the current one.</summary>
    public DefinitionVersion Rollback(string kind, string name, string by, string channel = DefinitionStore.Production, int? expectedVersion = null, Action<DefinitionChange>? inTransaction = null, string? note = null) =>
        Run(kind, name, channel, c => RollbackIn(c, kind, name, by, channel, expectedVersion, inTransaction, note));

    private DefinitionVersion RollbackIn(Scope c, string kind, string name, string by, string channel, int? expectedVersion, Action<DefinitionChange>? inTransaction, string? note)
    {
        var head = ReadHead(c.Connection, c.Transaction, kind, name, channel);
        CheckExpected(kind, name, expectedVersion, head);
        var target = head?.Previous ?? throw new DefinitionNotFoundException($"'{name}' on {channel} has no earlier version to return to.");
        var wanted = _store.ReadVersion(c.Connection, c.Transaction, kind, name, target) ?? throw new DefinitionNotFoundException($"'{name}' has no version {target}.");
        CheckInstancesRead(c.Connection, c.Transaction, kind, name, wanted.Version, wanted.Format);
        var after = new Pointer(target, head.Active > 0 ? head.Active : null, head.Staged, head.StagedAt, head.StagedBy);
        if (!Save(c.Connection, c.Transaction, kind, name, channel, after, true, activeChanged: true, by, c.Now, new Guard(head.Active, null, false)))
            throw new DefinitionConflictException($"The {kind} '{name}' was changed by someone else while this call rolled it back; read it again.", expectedVersion ?? 0, head.Active);
        inTransaction?.Invoke(new DefinitionChange(c.Connection, c.Transaction, kind, name, channel, head.Active > 0 ? head.Active : null, target));
        Log(c.Connection, c.Transaction, "rollback", kind, name, channel, target, by, c.Now, note);
        return wanted;
    }

    /// <summary>
    /// The rollback that matches <see cref="PromoteWithDependencies"/>: the version before returns, and each dependency it pins that is not at its pinned version is pointed back to it, in the same transaction.
    /// A dependency that another active definition of the channel pins at the version it has now is not moved (the other would lose what it was published against): the rollback is refused naming that definition.
    /// </summary>
    public IReadOnlyList<DefinitionVersion> RollbackWithDependencies(string kind, string name, string by, string channel = DefinitionStore.Production, string? note = null) =>
        Run(kind, name, channel, c =>
        {
            var restored = RollbackIn(c, kind, name, by, channel, null, null, note);
            var moved = new List<DefinitionVersion> { restored };
            foreach (var text in restored.DependsOn)
            {
                if (Dependency().Match(text) is not { Success: true, Groups: var g } || !g[3].Success) continue;
                var dk = g[1].Value; var dn = g[2].Value; var dv = int.Parse(g[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (ReadHead(c.Connection, c.Transaction, dk, dn, channel) is not { Active: > 0 } head || head.Active == dv) continue;
                var pinnedBy = ActiveDependents(c, dk, dn, head.Active, channel).FirstOrDefault(d => d != (kind, name));
                if (pinnedBy != default)
                    throw new PublishRefusedException(PublishRefusal.DependencyNotActive, $"The {kind} '{name}' cannot go back to version {restored.Version} in channel '{channel}': it was published against {dk}:{dn}@{dv}, which is at version {head.Active}, and the {pinnedBy.Kind} '{pinnedBy.Name}' needs that version.");
                moved.Add(SwitchIn(c, dk, dn, dv, by, channel, null, null, note));
            }
            return (IReadOnlyList<DefinitionVersion>)moved;
        });

    /// <summary>The active definitions of a channel (of every registered kind) whose version pins <c>kind:name@version</c>.</summary>
    private List<(string Kind, string Name)> ActiveDependents(Scope c, string kind, string name, int version, string channel)
    {
        var pin = $"{kind}:{name}@{version}";
        var found = new List<(string, string)>();
        foreach (var registered in _kinds!.All)
        {
            var names = new List<(string Name, int Version)>();
            using (var command = _database.Command(Module, c.Connection, c.Transaction, "SELECT name, version FROM {table:definition_serving} WHERE kind = $kind AND channel = $channel ORDER BY name;"))
            {
                command.Bind(_database, "kind", registered.Name).Bind(_database, "channel", channel);
                using var reader = command.ExecuteReader();
                while (reader.Read()) if (reader.Int32(1) > 0) names.Add((reader.GetString(0), reader.Int32(1)));
            }
            foreach (var (n, v) in names)
                if (_store.ReadVersion(c.Connection, c.Transaction, registered.Name, n, v) is { } active && active.DependsOn.Contains(pin, StringComparer.Ordinal)) found.Add((registered.Name, n));
        }
        return found;
    }

    /// <summary>Stops serving a definition in a channel (and drops what it had staged there); the versions and bindings stay. False when the channel had neither.</summary>
    public bool Retire(string kind, string name, string by, string channel = DefinitionStore.Production, int? expectedVersion = null)
    {
        DefinitionStore.Check(kind, name, channel);
        var now = _database.Time.GetUtcNow();
        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (ReadHead(connection, transaction, kind, name, channel) is not { } head) return false;
            CheckExpected(kind, name, expectedVersion, head);
            using var delete = _database.Command(Module, connection, transaction, "DELETE FROM {table:definition_serving} WHERE kind = $kind AND name = $name AND channel = $channel;");
            delete.Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "channel", channel).ExecuteNonQuery();
            Log(connection, transaction, "retire", kind, name, channel, head.Active, by, now);
            transaction.Commit();
            return true;
        });
    }

    /// <summary>An administrator's approval of a version of a kind that needs it (trusted code); recorded on the version and in the events. Approving again changes nothing.</summary>
    public void Approve(string kind, string name, int version, string by)
    {
        DefinitionStore.Check(kind, name, DefinitionStore.Production);
        var now = _database.Time.GetUtcNow();
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = _store.ReadVersion(connection, transaction, kind, name, version) ?? throw new DefinitionNotFoundException($"'{name}' has no version {version}.");
            if (IsApproved(connection, transaction, kind, name, version)) return;
            using var update = _database.Command(Module, connection, transaction, "UPDATE {table:definition_version} SET approved_by = $by, approved_at = $now WHERE kind = $kind AND name = $name AND version = $version;");
            update.Bind(_database, "by", by).Bind(_database, "now", now).Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "version", (long)existing.Version).ExecuteNonQuery();
            Log(connection, transaction, "approve", kind, name, "-", version, by, now);
            transaction.Commit();
        });
    }

    // ---- bindings

    /// <summary>
    /// Fills a slot for a channel. <paramref name="expectedRevision"/> is the binding's revision as the caller saw it (0 when it had none); null does not check. The same value again changes
    /// nothing. A bind moves the change log, so instances serving the definition see it.
    /// </summary>
    public DefinitionBinding Bind(string channel, string kind, string name, string slot, string boundTo, string by, int? expectedRevision = null)
    {
        DefinitionStore.Check(kind, name, channel);
        if (string.IsNullOrWhiteSpace(slot)) throw new ArgumentException("A slot's name is required.", nameof(slot));
        if (string.IsNullOrWhiteSpace(boundTo)) throw new ArgumentException($"The slot '{slot}' must be bound to something.", nameof(boundTo));
        var now = _database.Time.GetUtcNow();
        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = ReadBinding(connection, transaction, channel, kind, name, slot);
            if (current?.BoundTo == boundTo) return current;
            var revision = current?.Revision ?? 0;
            if (expectedRevision is { } expected && expected != revision)
                throw new DefinitionConflictException($"The slot '{slot}' of {kind} '{name}' in channel '{channel}' is at revision {revision}, not {expected}: it was bound since you looked.", expected, revision);
            using var write = _database.Command(Module, connection, transaction, current is null
                ? "INSERT INTO {table:definition_binding} (channel, kind, name, slot, bound_to, revision, bound_by, bound_at) VALUES ($channel, $kind, $name, $slot, $bound, $revision, $by, $now);"
                : "UPDATE {table:definition_binding} SET bound_to = $bound, revision = $revision, bound_by = $by, bound_at = $now WHERE channel = $channel AND kind = $kind AND name = $name AND slot = $slot;");
            write.Bind(_database, "channel", channel).Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "slot", slot).Bind(_database, "bound", boundTo)
                .Bind(_database, "revision", (long)revision + 1).Bind(_database, "by", by).Bind(_database, "now", now).ExecuteNonQuery();
            var active = ReadHead(connection, transaction, kind, name, channel)?.Active ?? 0;
            Log(connection, transaction, "bind", kind, name, channel, active, by, now, slot);
            transaction.Commit();
            return ReadBinding(connection, null, channel, kind, name, slot)!;
        });
    }

    /// <summary>Takes a slot's binding in a channel away (plan 27a IX23: a runner's slot channel can be emptied); false when it was not bound. Moves the change log like a bind.</summary>
    public bool Unbind(string channel, string kind, string name, string slot, string by)
    {
        DefinitionStore.Check(kind, name, channel);
        var now = _database.Time.GetUtcNow();
        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (ReadBinding(connection, transaction, channel, kind, name, slot) is null) return false;
            using var delete = _database.Command(Module, connection, transaction, "DELETE FROM {table:definition_binding} WHERE channel = $channel AND kind = $kind AND name = $name AND slot = $slot;");
            delete.Bind(_database, "channel", channel).Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "slot", slot).ExecuteNonQuery();
            var active = ReadHead(connection, transaction, kind, name, channel)?.Active ?? 0;
            Log(connection, transaction, "unbind", kind, name, channel, active, by, now, slot);
            transaction.Commit();
            return true;
        });
    }

    public IReadOnlyList<DefinitionBinding> Bindings(string channel, string kind, string name) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = _database.Command(Module, connection, "SELECT channel, kind, name, slot, bound_to, revision, bound_by, bound_at FROM {table:definition_binding} WHERE channel = $channel AND kind = $kind AND name = $name ORDER BY slot;");
            command.Bind(_database, "channel", channel).Bind(_database, "kind", kind).Bind(_database, "name", name);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionBinding>();
            while (reader.Read()) found.Add(ReadBindingRow(reader));
            return (IReadOnlyList<DefinitionBinding>)found;
        });

    /// <summary>Every binding of a kind, of every definition in every channel, including definitions that have no version yet (slots are bound before a first publish).</summary>
    public IReadOnlyList<DefinitionBinding> BindingsOfKind(string kind) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = _database.Command(Module, connection, "SELECT channel, kind, name, slot, bound_to, revision, bound_by, bound_at FROM {table:definition_binding} WHERE kind = $kind ORDER BY name, channel, slot;");
            command.Bind(_database, "kind", kind);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionBinding>();
            while (reader.Read()) found.Add(ReadBindingRow(reader));
            return (IReadOnlyList<DefinitionBinding>)found;
        });

    /// <summary>Every binding of a definition in every channel (the posture check of plan 27a IX17 compares a channel's connection with production's).</summary>
    public IReadOnlyList<DefinitionBinding> BindingsOf(string kind, string name) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = _database.Command(Module, connection, "SELECT channel, kind, name, slot, bound_to, revision, bound_by, bound_at FROM {table:definition_binding} WHERE kind = $kind AND name = $name ORDER BY channel, slot;");
            command.Bind(_database, "kind", kind).Bind(_database, "name", name);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionBinding>();
            while (reader.Read()) found.Add(ReadBindingRow(reader));
            return (IReadOnlyList<DefinitionBinding>)found;
        });

    // ---- reading

    /// <summary>What a channel has of a definition, or null when it has nothing (neither active nor staged).</summary>
    public DefinitionHead? Head(string kind, string name, string channel = DefinitionStore.Production) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            return ReadHead(connection, null, kind, name, channel) is { } head ? ToHead(kind, name, channel, head) : null;
        });

    /// <summary>The heads of every definition of a kind in a channel.</summary>
    public IReadOnlyList<DefinitionHead> Heads(string kind, string channel = DefinitionStore.Production) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = _database.Command(Module, connection, "SELECT name, version, previous_version, staged_version FROM {table:definition_serving} WHERE kind = $kind AND channel = $channel ORDER BY name;");
            command.Bind(_database, "kind", kind).Bind(_database, "channel", channel);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionHead>();
            while (reader.Read())
                found.Add(ToHead(kind, reader.GetString(0), channel, new Pointer(reader.Int32(1), reader.NullableInt32(2), reader.NullableInt32(3), null, null)));
            return (IReadOnlyList<DefinitionHead>)found;
        });

    /// <summary>The files of a stored version.</summary>
    public IReadOnlyList<DefinitionArtifact> Artifacts(string kind, string name, int version) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = _database.Command(Module, connection, "SELECT path, sha256, size, media_type, payload_id FROM {table:definition_artifact} WHERE kind = $kind AND name = $name AND version = $version ORDER BY path;");
            command.Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "version", (long)version);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionArtifact>();
            while (reader.Read()) found.Add(new DefinitionArtifact(reader.GetString(0), reader.GetString(1), reader.Int64(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.Uuid(4)));
            return (IReadOnlyList<DefinitionArtifact>)found;
        });

    /// <summary>The payload that holds a file with this hash, if any version of any definition here has stored it (<c>HEAD /api/v1/artifacts/{sha256}</c>).</summary>
    public Guid? FindArtifact(string sha256)
    {
        var hash = sha256.ToLowerInvariant();
        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            return FindPayload(connection, null, hash);
        });
    }

    public bool IsApproved(string kind, string name, int version) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            return IsApproved(connection, null, kind, name, version);
        });

    // ---- internals

    private sealed record Pointer(int Active, int? Previous, int? Staged, DateTimeOffset? StagedAt, string? StagedBy);

    private readonly record struct Scope(DbConnection Connection, DbTransaction Transaction, DateTimeOffset Now);

    private T Run<T>(string kind, string name, string channel, Func<Scope, T> work)
    {
        DefinitionStore.Check(kind, name, channel);
        var now = _database.Time.GetUtcNow();
        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var result = work(new Scope(connection, transaction, now));
            transaction.Commit();
            return result;
        });
    }

    /// <summary>Moves the staged version to active in the caller's transaction; null when another transaction did it first (the pointer is moved only if it is still where this one read it, so work in <paramref name="inTransaction"/> runs once).</summary>
    private DefinitionVersion? ActivateIn(DbConnection connection, DbTransaction transaction, string kind, string name, string channel, string by, int? expectedVersion, string action, string? note, DateTimeOffset? at = null, Action<DefinitionChange>? inTransaction = null)
    {
        var now = at ?? _database.Time.GetUtcNow();
        var head = ReadHead(connection, transaction, kind, name, channel);
        CheckExpected(kind, name, expectedVersion, head);
        var staged = head?.Staged ?? throw new DefinitionNotFoundException($"The {kind} '{name}' has nothing staged in channel '{channel}'.");
        var version = _store.ReadVersion(connection, transaction, kind, name, staged) ?? throw new DefinitionNotFoundException($"'{name}' has no version {staged}.");
        CheckInstancesRead(connection, transaction, kind, name, version.Version, version.Format);
        if (_kinds is not null) CheckCanBeActive(connection, transaction, kind, name, channel, version, version.DependsOn);
        var after = new Pointer(staged, head!.Active > 0 ? head.Active : null, null, null, null);
        if (!Save(connection, transaction, kind, name, channel, after, true, activeChanged: true, by, now, new Guard(head.Active, staged, true))) return null;
        inTransaction?.Invoke(new DefinitionChange(connection, transaction, kind, name, channel, head.Active > 0 ? head.Active : null, staged));
        Log(connection, transaction, action, kind, name, channel, staged, by, now, note);
        return version;
    }

    private static void CheckExpected(string kind, string name, int? expected, Pointer? head)
    {
        var actual = head?.Active ?? 0;
        if (expected is { } e && e != actual)
            throw new DefinitionConflictException($"The {kind} '{name}' has version {actual} active, not {e}: it changed since you looked. Read it again and decide.", e, actual);
    }

    /// <summary>The checks of becoming active in a channel: approval, dependencies, slots.</summary>
    private void CheckCanBeActive(DbConnection connection, DbTransaction transaction, string kind, string name, string channel, DefinitionVersion version, IReadOnlyList<string> depends)
    {
        var definition = _kinds!.Require(kind);
        if (DefinitionFormat.Refusal(kind, name, version.Version, version.Format, definition.Formats, "this server") is { } unreadable) throw new PublishRefusedException(PublishRefusal.Format, unreadable);
        if (definition.RequiresApproval && !IsApproved(connection, transaction, kind, name, version.Version))
            throw new PublishRefusedException(PublishRefusal.ApprovalRequired, $"The {kind} '{name}' version {version.Version} is trusted code and has not been approved by an administrator, so it cannot become active in channel '{channel}'.");

        var missing = new List<string>();
        foreach (var text in depends)
        {
            var match = Dependency().Match(text);
            if (!match.Success)
                throw new PublishRefusedException(PublishRefusal.Invalid, $"The {kind} '{name}' depends on '{text}', which is not written as kind:name@version.");
            var head = ReadHead(connection, transaction, match.Groups[1].Value, match.Groups[2].Value, channel);
            var wanted = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
            if (head is not { Active: > 0 })
                missing.Add($"{text} (nothing is active)");
            else if (wanted is { } w && w != head.Active)
                missing.Add($"{text} (version {head.Active} is active)");
        }

        if (missing.Count > 0)
            throw new PublishRefusedException(PublishRefusal.DependencyNotActive, $"The {kind} '{name}' version {version.Version} cannot become active in channel '{channel}': it depends on {string.Join(", ", missing)}.");

        CheckSlots(connection, transaction, kind, name, channel, version);
    }

    /// <summary>The live instances of the group, as its instance rows say (the same source the migration guard asks, 29b U02); none when the layout has no source.</summary>
    private IReadOnlyList<GroupInstance> LiveInstances(DbConnection connection, DbTransaction? transaction)
    {
        if (_database.Layout is not { GroupInstances: { } source } layout) return [];
        return source.Live(new GroupInstanceContext(connection, transaction, layout, _database.Time.GetUtcNow(),
            module => layout.Namespaces.Where(n => n.Module.Name == module).Select(n => (int?)n.Module.Version).FirstOrDefault()));
    }

    /// <summary>
    /// Refuses a version, naming the first live instance that cannot read its format (24a D4): the group must never hold a version one of its instances cannot serve. An instance whose row records
    /// no formats predates the column and reads format 1; one that records formats but not this kind does not serve the kind and is not asked. Format 1 is read by every instance, so it asks nobody.
    /// </summary>
    private void CheckInstancesRead(DbConnection connection, DbTransaction? transaction, string kind, string name, int? version, int format)
    {
        if (format <= DefinitionFormat.First) return;
        foreach (var instance in LiveInstances(connection, transaction))
        {
            DefinitionFormats reads;
            if (instance.DefinitionFormats is null) reads = DefinitionFormats.One;
            else if (!instance.DefinitionFormats.TryGetValue(kind, out reads)) continue;
            if (reads.Reads(format)) continue;
            var which = version is { } v ? $"version {v} of the {kind} '{name}'" : $"the {kind} '{name}'";
            throw new PublishRefusedException(PublishRefusal.InstanceFormat,
                $"{(version is null ? "Cannot publish" : "Cannot serve")} {which} in format {format}: the instance {instance.Id}{(instance.Host is null ? "" : $" on {instance.Host}")}{(instance.SoftwareVersion is null ? "" : $" (software {instance.SoftwareVersion})")} is live and reads " +
                $"{(instance.DefinitionFormats is null ? "format 1 only (its row records no formats)" : $"{(reads.Previous == reads.Current ? "format" : "formats")} {reads} of {kind}")}. Upgrade that instance first, or stop it, then publish again.");
        }
    }

    private void CheckSlots(DbConnection connection, DbTransaction transaction, string kind, string name, string channel, DefinitionVersion version)
    {
        if (_kinds is null) return;
        var slots = _kinds.Require(kind).SlotsFor(version.Content);
        if (slots.Count == 0) return;
        var bound = new HashSet<string>(StringComparer.Ordinal);
        using (var command = _database.Command(Module, connection, transaction, "SELECT slot FROM {table:definition_binding} WHERE channel = $channel AND kind = $kind AND name = $name;"))
        {
            command.Bind(_database, "channel", channel).Bind(_database, "kind", kind).Bind(_database, "name", name);
            using var reader = command.ExecuteReader();
            while (reader.Read()) bound.Add(reader.GetString(0));
        }

        var unbound = slots.Where(s => !bound.Contains(s.Name)).Select(s => $"'{s.Name}'").ToList();
        if (unbound.Count > 0)
            throw new PublishRefusedException(PublishRefusal.UnboundSlot, $"The {kind} '{name}' version {version.Version} needs {(unbound.Count == 1 ? "the slot" : "the slots")} {string.Join(", ", unbound)} bound in channel '{channel}', and {(unbound.Count == 1 ? "it has" : "they have")} no binding there.");
    }

    private List<DefinitionArtifact> ResolveArtifacts(IReadOnlyList<ArtifactRef> refs)
    {
        var resolved = new List<DefinitionArtifact>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in refs)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) throw new PublishRefusedException(PublishRefusal.Artifact, "A file of a version has no path.");
            if (!paths.Add(file.Path)) throw new PublishRefusedException(PublishRefusal.Artifact, $"The path '{file.Path}' is named twice in the version's files.");
            var sha = file.Sha256.ToLowerInvariant();
            if (!Sha256Hex().IsMatch(sha)) throw new PublishRefusedException(PublishRefusal.Artifact, $"The file '{file.Path}' names '{file.Sha256}' as its hash, which is not a SHA-256 in hexadecimal.");
            var payload = file.PayloadId ?? _database.Retry(() => { using var connection = _database.OpenConnection(); return FindPayload(connection, null, sha); });
            if (payload is null)
                throw new PublishRefusedException(PublishRefusal.Artifact, $"The file '{file.Path}' (sha256 {sha}) is not stored here; upload it before publishing the version that names it.");
            if (_payloads is not null)
            {
                var info = _payloads.Get(payload.Value);
                if (info is not { Complete: true }) throw new PublishRefusedException(PublishRefusal.Artifact, $"The file '{file.Path}' names payload {payload}, which is not stored completely.");
                if (!string.Equals(info.Sha256, sha, StringComparison.OrdinalIgnoreCase)) throw new PublishRefusedException(PublishRefusal.Artifact, $"The file '{file.Path}' names payload {payload}, whose hash is not {sha}.");
            }

            resolved.Add(new DefinitionArtifact(file.Path, sha, file.Size, file.MediaType, payload.Value));
        }

        return resolved;
    }

    private Guid? FindPayload(DbConnection connection, DbTransaction? transaction, string sha)
    {
        // The candidates are read to the end and the reader closed before the payload store is asked about any of them: PayloadStore.Get opens a connection of its own, and on SQLite a second
        // connection that wants a read lock while this reader holds one deadlocks with any writer waiting to commit (it holds the file's pending lock, so the new reader is refused, and it
        // waits for this reader). Nothing breaks the wait but the 5 s busy timeout, again and again.
        var candidates = new List<Guid>();
        using (var command = _database.Command(Module, connection, transaction, "SELECT payload_id FROM {table:definition_artifact} WHERE sha256 = $sha;"))
        {
            command.Bind(_database, "sha", sha);
            using var reader = command.ExecuteReader();
            while (reader.Read()) candidates.Add(reader.Uuid(0));
        }

        foreach (var id in candidates)
        {
            // a payload that has since been removed is not one to reuse
            if (_payloads is null || _payloads.Get(id) is { Complete: true }) return id;
        }

        return null;
    }

    private static string HashOf(string manifest, IReadOnlyList<DefinitionArtifact> artifacts, int format)
    {
        var text = artifacts.Count == 0 ? manifest : manifest + "\n" + string.Join("\n", artifacts.OrderBy(a => a.Path, StringComparer.Ordinal).Select(a => $"{a.Path}\t{a.Sha256}"));
        // format 1 hashes as it always did, so a version stored before the format column is the same version when it is published again
        if (format != DefinitionFormat.First) text = $"format {format.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{text}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private int Latest(DbConnection connection, DbTransaction? transaction, string kind, string name)
    {
        using var command = _database.Command(Module, connection, transaction, "SELECT MAX(version) FROM {table:definition_version} WHERE kind = $kind AND name = $name;");
        command.Bind(_database, "kind", kind).Bind(_database, "name", name);
        return command.ExecuteScalar() is { } top and not DBNull ? Convert.ToInt32(top, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private void Insert(DbConnection connection, DbTransaction transaction, PublishRequest request, IReadOnlyList<DefinitionArtifact> artifacts, string hash, int number, DateTimeOffset now, int format)
    {
        var withProgress = request.Progress is not null;
        using var insert = _database.Command(Module, connection, transaction,
            $"INSERT INTO {{table:definition_version}} (kind, name, version, hash, content, depends_on, published_by, published_at, note, progress, format, signature, signer) VALUES ($kind, $name, $version, $hash, $content, CAST($depends AS {{{{json}}}}), $by, $now, $note, {(withProgress ? "$progress" : "NULL")}, $format, {(request.Signature is null ? "NULL" : "$signature")}, {(request.Signer is null ? "NULL" : "$signer")});");
        insert.Bind(_database, "format", (long)format);
        insert.Bind(_database, "kind", request.Kind).Bind(_database, "name", request.Name).Bind(_database, "version", (long)number).Bind(_database, "hash", hash).Bind(_database, "content", request.Manifest)
            .Bind(_database, "depends", JsonSerializer.Serialize(request.DependsOn ?? [])).Bind(_database, "by", request.By).Bind(_database, "now", now).Bind(_database, "note", request.Note, System.Data.DbType.String);
        if (withProgress) insert.Bind(_database, "progress", request.Progress);
        if (request.Signature is not null) insert.Bind(_database, "signature", request.Signature);
        if (request.Signer is not null) insert.Bind(_database, "signer", request.Signer);
        insert.ExecuteNonQuery();
        foreach (var file in artifacts)
        {
            using var row = _database.Command(Module, connection, transaction,
                "INSERT INTO {table:definition_artifact} (kind, name, version, path, sha256, size, media_type, payload_id) VALUES ($kind, $name, $version, $path, $sha, $size, $media, $payload);");
            row.Bind(_database, "kind", request.Kind).Bind(_database, "name", request.Name).Bind(_database, "version", (long)number).Bind(_database, "path", file.Path).Bind(_database, "sha", file.Sha256)
                .Bind(_database, "size", file.Size).Bind(_database, "media", file.MediaType, System.Data.DbType.String).Bind(_database, "payload", file.PayloadId).ExecuteNonQuery();
        }
    }

    private Pointer? ReadHead(DbConnection connection, DbTransaction? transaction, string kind, string name, string channel)
    {
        using var command = _database.Command(Module, connection, transaction, "SELECT version, previous_version, staged_version, staged_at, staged_by FROM {table:definition_serving} WHERE kind = $kind AND name = $name AND channel = $channel;");
        command.Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "channel", channel);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Pointer(reader.Int32(0), reader.NullableInt32(1), reader.NullableInt32(2), reader.NullableTime(3), reader.IsDBNull(4) ? null : reader.GetString(4)) : null;
    }

    private static DefinitionHead ToHead(string kind, string name, string channel, Pointer head) =>
        new(kind, name, channel, head.Active > 0 ? head.Active : null, head.Previous, head.Staged);

    /// <summary>The pointer as the caller read it: an update that finds it moved changes nothing (the loser of two activations at once).</summary>
    private sealed record Guard(int Active, int? Staged, bool CheckStaged);

    private bool Save(DbConnection connection, DbTransaction transaction, string kind, string name, string channel, Pointer head, bool exists, bool activeChanged, string by, DateTimeOffset now, Guard? guard = null)
    {
        static string Value(string parameter, bool present) => present ? "$" + parameter : "NULL";
        var previous = Value("previous", head.Previous is not null);
        var staged = Value("staged", head.Staged is not null);
        var stagedAt = Value("staged_at", head.Staged is not null && head.StagedAt is not null);
        var stagedBy = Value("staged_by", head.Staged is not null && head.StagedBy is not null);
        var sql = exists
            ? $"UPDATE {{table:definition_serving}} SET version = $version, previous_version = {previous}, staged_version = {staged}, staged_at = {stagedAt}, staged_by = {stagedBy}{(activeChanged ? ", switched_at = $now, switched_by = $by" : "")} WHERE kind = $kind AND name = $name AND channel = $channel{(guard is null ? "" : " AND version = $guard_version")}{(guard is { CheckStaged: true } ? " AND staged_version = $guard_staged" : "")};"
            : $"INSERT INTO {{table:definition_serving}} (kind, name, channel, version, previous_version, staged_version, staged_at, staged_by, switched_at, switched_by) VALUES ($kind, $name, $channel, $version, {previous}, {staged}, {stagedAt}, {stagedBy}, $now, $by);";
        using var command = _database.Command(Module, connection, transaction, sql);
        command.Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "channel", channel).Bind(_database, "version", (long)head.Active);
        if (activeChanged || !exists) command.Bind(_database, "now", now).Bind(_database, "by", by);
        if (head.Previous is not null) command.Bind(_database, "previous", (long)head.Previous.Value);
        if (head.Staged is not null) command.Bind(_database, "staged", (long)head.Staged.Value);
        if (head.Staged is not null && head.StagedAt is not null) command.Bind(_database, "staged_at", head.StagedAt.Value);
        if (head.Staged is not null && head.StagedBy is not null) command.Bind(_database, "staged_by", head.StagedBy);
        if (guard is not null && exists)
        {
            command.Bind(_database, "guard_version", (long)guard.Active);
            if (guard.CheckStaged) command.Bind(_database, "guard_staged", (long)guard.Staged!.Value);
        }

        return command.ExecuteNonQuery() == 1;
    }

    private void Log(DbConnection connection, DbTransaction transaction, string action, string kind, string name, string channel, int version, string by, DateTimeOffset now, string? note = null)
    {
        using var command = _database.Command(Module, connection, transaction,
            $"INSERT INTO {{table:definition_event}} (action, kind, name, channel, version, actor_id, happened_at, note) VALUES ($action, $kind, $name, $channel, $version, $by, $now, {(note is null ? "NULL" : "$note")});");
        command.Bind(_database, "action", action).Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "channel", channel).Bind(_database, "version", (long)version).Bind(_database, "by", by).Bind(_database, "now", now);
        if (note is not null) command.Bind(_database, "note", note);
        command.ExecuteNonQuery();
    }

    private bool IsApproved(DbConnection connection, DbTransaction? transaction, string kind, string name, int version)
    {
        using var command = _database.Command(Module, connection, transaction, "SELECT approved_by FROM {table:definition_version} WHERE kind = $kind AND name = $name AND version = $version;");
        command.Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "version", (long)version);
        return command.ExecuteScalar() is { } by and not DBNull && by.ToString() is { Length: > 0 };
    }

    private DefinitionBinding? ReadBinding(DbConnection connection, DbTransaction? transaction, string channel, string kind, string name, string slot)
    {
        using var command = _database.Command(Module, connection, transaction, "SELECT channel, kind, name, slot, bound_to, revision, bound_by, bound_at FROM {table:definition_binding} WHERE channel = $channel AND kind = $kind AND name = $name AND slot = $slot;");
        command.Bind(_database, "channel", channel).Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "slot", slot);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBindingRow(reader) : null;
    }

    private static DefinitionBinding ReadBindingRow(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.Int32(5), reader.GetString(6), reader.Time(7));
}
