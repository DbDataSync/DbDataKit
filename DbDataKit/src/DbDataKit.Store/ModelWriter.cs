using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Dialect;

namespace DbDataKit.Store;

/// <summary>Who made a write: the id the sign-in gave, and a name to show.</summary>
public sealed record WriteActor(string Id, string? Name = null);

public enum RowAction { Insert, Update, Delete }

/// <summary>
/// One change to one row. An insert carries all its <paramref name="Values"/>; an update names the <paramref name="Key"/> and the <paramref name="ExpectedVersion"/> it read and carries only what changes; a delete names the key and the
/// version. A value may be a .NET value, text, or a <see cref="JsonElement"/>.
/// </summary>
public sealed record RowChange(RowAction Action, string Entity, IReadOnlyDictionary<string, object?> Key, long? ExpectedVersion, IReadOnlyDictionary<string, object?> Values)
{
    public static RowChange Insert(string entity, IReadOnlyDictionary<string, object?> values) => new(RowAction.Insert, entity, new Dictionary<string, object?>(), null, values);
    public static RowChange Update(string entity, IReadOnlyDictionary<string, object?> key, long expectedVersion, IReadOnlyDictionary<string, object?> changes) => new(RowAction.Update, entity, key, expectedVersion, changes);
    public static RowChange Delete(string entity, IReadOnlyDictionary<string, object?> key, long expectedVersion) => new(RowAction.Delete, entity, key, expectedVersion, new Dictionary<string, object?>());
}

/// <summary>A row as stored: its attribute values and its version.</summary>
public sealed record StoredRow(IReadOnlyDictionary<string, object?> Values, long Version);

/// <summary>
/// The write path of a stored model (plan 19 Z04, plan 15 section 5). A batch of row changes is applied in one transaction: each value is checked against the attribute's rules (type, length, precision, required, pattern, range,
/// allowed values, unique, references), an update must name the version it read, a delete must leave nothing pointing at the row, and after the batch the cross-row rules (<c>sum_equals</c>, <c>no_cycle</c>) are checked on the
/// groups it touched. Every rule that is broken is reported, and then nothing is written. Each change writes an audit record in the same transaction. A cross-row rule that spans a batch needs the whole batch: the rule is
/// checked once at the end, not after each row.
/// </summary>
public sealed class ModelWriter
{
    private readonly DbDataKit.DataModel.DataModel _model;
    private readonly DbConnection _connection;
    private readonly ISqlDialect _dialect;
    private readonly string _engine;
    private readonly TimeProvider _clock;

    public ModelWriter(DbDataKit.DataModel.DataModel model, DbConnection connection, string engine, TimeProvider? clock = null)
    {
        _model = model;
        _connection = connection;
        _engine = engine;
        _dialect = DialectCatalog.For(new EngineId(engine));
        _clock = clock ?? TimeProvider.System;
        if (engine is not ("sqlserver" or "postgres")) throw new StoreNotSupportedException($"Models are stored on SQL Server (`sqlserver`) and PostgreSQL (`postgres`); `{engine}` is not one of them yet.");
        foreach (var entity in model.Entities)
            foreach (var rule in entity.Rules ?? [])
            {
                if (rule.Kind == RuleKind.NoOverlap)
                    throw new StoreNotSupportedException($"`{entity.Name}` has a `no_overlap` rule, which is enforced with effective-dated history (plan 19 Z05); until then the model cannot be written, so the rule is never silently unchecked.");
                if (rule.Kind == RuleKind.NoCycle && entity.Key.Count != 1)
                    throw new StoreNotSupportedException($"`{entity.Name}` has a `no_cycle` rule and a key of {entity.Key.Count} attributes; the rule follows one key.");
            }
    }

    private string Table(string entity) => $"{_dialect.Quote(_model.Name)}.{_dialect.Quote(entity)}";
    private string Col(string name) => _dialect.Quote(name);

    /// <summary>The row with this key, or null.</summary>
    public async Task<StoredRow?> GetAsync(string entityName, IReadOnlyDictionary<string, object?> key, CancellationToken cancellationToken = default)
    {
        var entity = RequireEntity(entityName);
        var violations = new List<RuleViolation>();
        var typed = TypedKey(entity, key, violations);
        if (violations.Count > 0) throw new WriteRefusedException(violations);
        return await ReadRowAsync(null, entity, typed!, forUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    public Task InsertAsync(string entity, IReadOnlyDictionary<string, object?> values, WriteActor actor, CancellationToken cancellationToken = default) =>
        ApplyAsync([RowChange.Insert(entity, values)], actor, cancellationToken);

    public Task UpdateAsync(string entity, IReadOnlyDictionary<string, object?> key, long expectedVersion, IReadOnlyDictionary<string, object?> changes, WriteActor actor, CancellationToken cancellationToken = default) =>
        ApplyAsync([RowChange.Update(entity, key, expectedVersion, changes)], actor, cancellationToken);

    public Task DeleteAsync(string entity, IReadOnlyDictionary<string, object?> key, long expectedVersion, WriteActor actor, CancellationToken cancellationToken = default) =>
        ApplyAsync([RowChange.Delete(entity, key, expectedVersion)], actor, cancellationToken);

    /// <summary>Applies the changes in order in one transaction. Throws <see cref="WriteRefusedException"/> (with every broken rule), <see cref="VersionConflictException"/> or <see cref="RowNotFoundException"/>, and then nothing was written.</summary>
    public async Task ApplyAsync(IReadOnlyList<RowChange> changes, WriteActor actor, CancellationToken cancellationToken = default)
    {
        var violations = new List<RuleViolation>();
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var touched = new List<(ModelEntity Entity, Dictionary<string, object?>? Before, Dictionary<string, object?>? After)>();
            foreach (var change in changes)
            {
                var entity = RequireEntity(change.Entity);
                switch (change.Action)
                {
                    case RowAction.Insert: touched.Add(await InsertRowAsync(transaction, entity, change, actor, violations, cancellationToken).ConfigureAwait(false)); break;
                    case RowAction.Update: touched.Add(await UpdateRowAsync(transaction, entity, change, actor, violations, cancellationToken).ConfigureAwait(false)); break;
                    case RowAction.Delete: touched.Add(await DeleteRowAsync(transaction, entity, change, actor, violations, cancellationToken).ConfigureAwait(false)); break;
                }
            }
            if (violations.Count == 0) await CheckRulesAsync(transaction, touched, violations, cancellationToken).ConfigureAwait(false);
            if (violations.Count > 0) throw new WriteRefusedException(violations);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ---- insert, update, delete ----

    private async Task<(ModelEntity, Dictionary<string, object?>?, Dictionary<string, object?>?)> InsertRowAsync(DbTransaction tx, ModelEntity entity, RowChange change, WriteActor actor, List<RuleViolation> violations, CancellationToken ct)
    {
        var row = new Dictionary<string, object?>();
        var unknown = change.Values.Keys.Where(k => entity.Attribute(k) is null).ToList();
        var key = KeyOf(entity, change.Values);
        foreach (var name in unknown) violations.Add(new("attribute", entity.Name, name, key, $"`{entity.Name}` has no attribute `{name}`."));
        foreach (var attribute in entity.Attributes)
        {
            var given = change.Values.TryGetValue(attribute.Name, out var raw);
            if (!given && attribute.Default is not null) { given = true; raw = attribute.Default; }
            CheckValue(entity, attribute, given ? raw : null, key, row, violations);
        }
        if (violations.Count > 0 && row.Count != entity.Attributes.Count) return (entity, null, null);
        var typedKey = entity.Key.ToDictionary(k => k, k => row.GetValueOrDefault(k));
        if (typedKey.Values.All(v => v is not null))
        {
            if (await ReadRowAsync(tx, entity, typedKey, false, ct).ConfigureAwait(false) is not null) violations.Add(new("key", entity.Name, null, typedKey, $"`{entity.Name}` already has a row {VersionConflictException.Format(typedKey)}."));
            await CheckUniqueAndReferencesAsync(tx, entity, row, typedKey, null, violations, ct).ConfigureAwait(false);
        }
        if (violations.Count > 0) return (entity, null, null);

        var columns = entity.Attributes.Select(a => a.Name).Append(ModelStorage.VersionColumn).ToList();
        var values = entity.Attributes.Select(a => row[a.Name]).Append(1L).ToList();
        await ExecuteAsync(tx, $"INSERT INTO {Table(entity.Name)} ({string.Join(", ", columns.Select(Col))}) VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i))})", values, ct).ConfigureAwait(false);
        await AuditAsync(tx, actor, entity, typedKey, "insert", null, 1, null, row, ct).ConfigureAwait(false);
        return (entity, null, row);
    }

    private async Task<(ModelEntity, Dictionary<string, object?>?, Dictionary<string, object?>?)> UpdateRowAsync(DbTransaction tx, ModelEntity entity, RowChange change, WriteActor actor, List<RuleViolation> violations, CancellationToken ct)
    {
        var typedKey = TypedKey(entity, change.Key, violations);
        if (typedKey is null) return (entity, null, null);
        var current = await ReadRowAsync(tx, entity, typedKey, true, ct).ConfigureAwait(false) ?? throw new RowNotFoundException(entity.Name, typedKey);
        var expected = change.ExpectedVersion ?? throw new WriteRefusedException([new("version", entity.Name, null, typedKey, "An update names the version of the row it read.")]);
        if (current.Version != expected) throw new VersionConflictException(entity.Name, typedKey, expected, current.Version);

        var after = new Dictionary<string, object?>(current.Values);
        foreach (var (name, raw) in change.Values)
        {
            if (entity.Attribute(name) is not { } attribute) { violations.Add(new("attribute", entity.Name, name, typedKey, $"`{entity.Name}` has no attribute `{name}`.")); continue; }
            if (entity.Key.Contains(name)) { violations.Add(new("key_immutable", entity.Name, name, typedKey, $"`{name}` is part of the key of `{entity.Name}` and does not change; delete the row and add another.")); continue; }
            CheckValue(entity, attribute, raw, typedKey, after, violations);
        }
        if (violations.Count > 0) return (entity, null, null);
        var changed = change.Values.Keys.Where(k => !Equals(after[k], current.Values[k])).ToList();
        if (changed.Count == 0) return (entity, null, null);
        await CheckUniqueAndReferencesAsync(tx, entity, after, typedKey, changed, violations, ct).ConfigureAwait(false);
        if (violations.Count > 0) return (entity, null, null);

        var sets = changed.Select((c, i) => $"{Col(c)} = @p{i}").Append($"{Col(ModelStorage.VersionColumn)} = {Col(ModelStorage.VersionColumn)} + 1").ToList();
        var parameters = changed.Select(c => after[c]).ToList();
        var where = Where(entity, typedKey, parameters);
        parameters.Add(expected);
        var affected = await ExecuteAsync(tx, $"UPDATE {Table(entity.Name)} SET {string.Join(", ", sets)} WHERE {where} AND {Col(ModelStorage.VersionColumn)} = @p{parameters.Count - 1}", parameters, ct).ConfigureAwait(false);
        if (affected != 1) throw new VersionConflictException(entity.Name, typedKey, expected, current.Version + 1);
        await AuditAsync(tx, actor, entity, typedKey, "update", expected, expected + 1, current.Values, after, ct).ConfigureAwait(false);
        return (entity, new Dictionary<string, object?>(current.Values), after);
    }

    private async Task<(ModelEntity, Dictionary<string, object?>?, Dictionary<string, object?>?)> DeleteRowAsync(DbTransaction tx, ModelEntity entity, RowChange change, WriteActor actor, List<RuleViolation> violations, CancellationToken ct)
    {
        var typedKey = TypedKey(entity, change.Key, violations);
        if (typedKey is null) return (entity, null, null);
        var current = await ReadRowAsync(tx, entity, typedKey, true, ct).ConfigureAwait(false) ?? throw new RowNotFoundException(entity.Name, typedKey);
        var expected = change.ExpectedVersion ?? throw new WriteRefusedException([new("version", entity.Name, null, typedKey, "A delete names the version of the row it read.")]);
        if (current.Version != expected) throw new VersionConflictException(entity.Name, typedKey, expected, current.Version);

        // nothing may point at the row: every attribute of this model that references this entity
        foreach (var other in _model.Entities)
            foreach (var attribute in other.Attributes.Where(a => a.References is { Model: null } r && r.Entity == entity.Name))
            {
                var targetAttribute = attribute.References!.Attribute ?? entity.Key[0];
                var value = current.Values[targetAttribute];
                if (value is null) continue;
                var count = Convert.ToInt64(await ScalarAsync(tx, $"SELECT COUNT(*) FROM {Table(other.Name)} WHERE {Col(attribute.Name)} = @p0", [value], ct).ConfigureAwait(false));
                if (count > 0) violations.Add(new("referenced", entity.Name, attribute.Name, typedKey, $"{count} row(s) of `{other.Name}` point at this row through `{attribute.Name}`; change or delete them first."));
            }
        if (violations.Count > 0) return (entity, null, null);

        var parameters = new List<object?>();
        var where = Where(entity, typedKey, parameters);
        parameters.Add(expected);
        var affected = await ExecuteAsync(tx, $"DELETE FROM {Table(entity.Name)} WHERE {where} AND {Col(ModelStorage.VersionColumn)} = @p{parameters.Count - 1}", parameters, ct).ConfigureAwait(false);
        if (affected != 1) throw new VersionConflictException(entity.Name, typedKey, expected, current.Version + 1);
        await AuditAsync(tx, actor, entity, typedKey, "delete", expected, null, current.Values, null, ct).ConfigureAwait(false);
        return (entity, new Dictionary<string, object?>(current.Values), null);
    }

    // ---- the rules of one value and of one row ----

    private void CheckValue(ModelEntity entity, ModelAttribute attribute, object? raw, IReadOnlyDictionary<string, object?> key, Dictionary<string, object?> row, List<RuleViolation> violations)
    {
        void Break(string rule, string message) => violations.Add(new(rule, entity.Name, attribute.Name, key, $"`{entity.Name}.{attribute.Name}` {message}"));
        if (!Values.TryCoerce(attribute.Canonical, raw, out var value, out var rule, out var problem)) { Break(rule, problem!); row[attribute.Name] = null; return; }
        row[attribute.Name] = value;
        if (value is null)
        {
            if (attribute.Required || entity.Key.Contains(attribute.Name)) Break("required", "is required.");
            return;
        }
        if (attribute.Pattern is { } pattern && value is string text && !Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1))) Break("pattern", $"is `{text}`, which does not match the pattern `{pattern}`.");
        if (Numeric(value) is { } number)
        {
            if (attribute.Min is { } min && number < min) Break("min", $"is {number}, below the minimum {min}.");
            if (attribute.Max is { } max && number > max) Break("max", $"is {number}, above the maximum {max}.");
        }
        if (attribute.Allowed is { } allowed && !allowed.Contains(Display(value))) Break("allowed", $"is `{Display(value)}`, which is not one of: {string.Join(", ", allowed)}.");
    }

    private async Task CheckUniqueAndReferencesAsync(DbTransaction tx, ModelEntity entity, Dictionary<string, object?> row, IReadOnlyDictionary<string, object?> key, IReadOnlyList<string>? changed, List<RuleViolation> violations, CancellationToken ct)
    {
        foreach (var attribute in entity.Attributes.Where(a => changed is null || changed.Contains(a.Name)))
        {
            var value = row[attribute.Name];
            if (value is null) continue;
            if (attribute.Unique && !(entity.Key is [var only] && only == attribute.Name))
            {
                var parameters = new List<object?> { value };
                var others = changed is null ? "" : " AND NOT (" + Where(entity, key, parameters) + ")";
                if (Convert.ToInt64(await ScalarAsync(tx, $"SELECT COUNT(*) FROM {Table(entity.Name)} WHERE {Col(attribute.Name)} = @p0{others}", parameters, ct).ConfigureAwait(false)) > 0)
                    violations.Add(new("unique", entity.Name, attribute.Name, key, $"`{entity.Name}.{attribute.Name}` is `{Display(value)}`, which another row has."));
            }
            if (attribute.References is { Model: null } reference)
            {
                var target = RequireEntity(reference.Entity);
                var targetAttribute = reference.Attribute ?? target.Key[0];
                if (Convert.ToInt64(await ScalarAsync(tx, $"SELECT COUNT(*) FROM {Table(target.Name)} WHERE {Col(targetAttribute)} = @p0", [value], ct).ConfigureAwait(false)) == 0)
                    violations.Add(new("reference", entity.Name, attribute.Name, key, $"`{entity.Name}.{attribute.Name}` is `{Display(value)}`, and `{target.Name}` has no row with that `{targetAttribute}`."));
            }
        }
    }

    // ---- the rules across rows ----

    private async Task CheckRulesAsync(DbTransaction tx, List<(ModelEntity Entity, Dictionary<string, object?>? Before, Dictionary<string, object?>? After)> touched, List<RuleViolation> violations, CancellationToken ct)
    {
        foreach (var entity in touched.Select(t => t.Entity).Distinct())
            foreach (var rule in entity.Rules ?? [])
            {
                var rows = touched.Where(t => t.Entity == entity).ToList();
                if (rule.Kind == RuleKind.SumEquals) await CheckSumAsync(tx, entity, rule, rows, violations, ct).ConfigureAwait(false);
                else if (rule.Kind == RuleKind.NoCycle) await CheckCycleAsync(tx, entity, rule, rows.Select(r => r.After).Where(a => a is not null).Select(a => a!).ToList(), violations, ct).ConfigureAwait(false);
            }
    }

    private async Task CheckSumAsync(DbTransaction tx, ModelEntity entity, ModelRule rule, List<(ModelEntity Entity, Dictionary<string, object?>? Before, Dictionary<string, object?>? After)> rows, List<RuleViolation> violations, CancellationToken ct)
    {
        var groups = new List<Dictionary<string, object?>>();
        foreach (var image in rows.SelectMany(r => new[] { r.Before, r.After }).Where(i => i is not null))
        {
            var group = rule.GroupBy.ToDictionary(g => g, g => image![g]);
            if (!groups.Any(g => g.All(kv => Equals(kv.Value, group[kv.Key])))) groups.Add(group);
        }
        foreach (var group in groups.DefaultIfEmpty(new Dictionary<string, object?>()))
        {
            var parameters = new List<object?>();
            var conditions = group.Select(kv => kv.Value is null ? $"{Col(kv.Key)} IS NULL" : $"{Col(kv.Key)} = @p{Add(parameters, kv.Value)}").ToList();
            var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
            await using var command = Command(tx, $"SELECT COUNT(*), SUM({Col(rule.Attribute!)}) FROM {Table(entity.Name)}{where}", parameters);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            if (Convert.ToInt64(reader.GetValue(0)) == 0 || reader.IsDBNull(1)) continue;
            var sum = Convert.ToDecimal(reader.GetValue(1));
            if (Math.Abs(sum - rule.Value!.Value) > rule.Tolerance)
            {
                var which = group.Count == 0 ? "all rows" : string.Join(", ", group.Select(kv => $"{kv.Key} = {Display(kv.Value)}"));
                violations.Add(new("sum_equals", entity.Name, rule.Attribute, group, $"`{entity.Name}.{rule.Attribute}` adds up to {sum} for {which}, and it must be {rule.Value}{(rule.Tolerance > 0 ? $" (within {rule.Tolerance})" : "")}."));
            }
        }
    }

    private async Task CheckCycleAsync(DbTransaction tx, ModelEntity entity, ModelRule rule, List<Dictionary<string, object?>> after, List<RuleViolation> violations, CancellationToken ct)
    {
        var keyName = entity.Key[0];
        foreach (var row in after)
        {
            var start = row[keyName];
            var seen = new HashSet<object?> { start };
            var next = row[rule.Attribute!];
            for (var step = 0; next is not null && step < 100_000; step++)
            {
                if (!seen.Add(next)) { if (Equals(next, start)) violations.Add(new("no_cycle", entity.Name, rule.Attribute, new Dictionary<string, object?> { [keyName] = start }, $"`{entity.Name}.{rule.Attribute}` would make `{Display(start)}` its own ancestor.")); break; }
                next = await ScalarAsync(tx, $"SELECT {Col(rule.Attribute!)} FROM {Table(entity.Name)} WHERE {Col(keyName)} = @p0", [next], ct).ConfigureAwait(false);
                if (next is DBNull) next = null;
            }
        }
    }

    // ---- audit ----

    private async Task AuditAsync(DbTransaction tx, WriteActor actor, ModelEntity entity, IReadOnlyDictionary<string, object?> key, string action, long? before, long? after, IReadOnlyDictionary<string, object?>? beforeRow, IReadOnlyDictionary<string, object?>? afterRow, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        object?[] values =
        [
            Guid.CreateVersion7(now), now, actor.Id, actor.Name, entity.Name, JsonSerializer.Serialize(key.ToDictionary(k => k.Key, k => Json(k.Value))), action, before, after,
            beforeRow is null ? null : JsonSerializer.Serialize(beforeRow.ToDictionary(k => k.Key, k => Json(k.Value))),
            afterRow is null ? null : JsonSerializer.Serialize(afterRow.ToDictionary(k => k.Key, k => Json(k.Value))),
        ];
        await ExecuteAsync(tx, $"INSERT INTO {Table(ModelStorage.AuditTable)} ({string.Join(", ", ModelStorage.AuditColumns.Select(c => Col(c.Name)))}) VALUES ({string.Join(", ", values.Select((_, i) => "@p" + i))})", values, ct).ConfigureAwait(false);
    }

    private static object? Json(object? value) => value switch { DateOnly d => d.ToString("yyyy-MM-dd"), TimeOnly t => t.ToString("HH:mm:ss"), DateTimeOffset o => o.ToString("O"), DateTime t => t.ToString("O"), byte[] b => Convert.ToBase64String(b), _ => value };

    // ---- reading and the database ----

    private async Task<StoredRow?> ReadRowAsync(DbTransaction? tx, ModelEntity entity, IReadOnlyDictionary<string, object?> typedKey, bool forUpdate, CancellationToken ct)
    {
        var parameters = new List<object?>();
        var where = Where(entity, typedKey, parameters);
        var columns = entity.Attributes.Select(a => a.Name).Append(ModelStorage.VersionColumn).ToList();
        var lock_ = forUpdate && _engine == "sqlserver" ? " WITH (UPDLOCK, ROWLOCK)" : "";
        var suffix = forUpdate && _engine == "postgres" ? " FOR UPDATE" : "";
        await using var command = Command(tx, $"SELECT {string.Join(", ", columns.Select(Col))} FROM {Table(entity.Name)}{lock_} WHERE {where}{suffix}", parameters);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var values = new Dictionary<string, object?>();
        for (var i = 0; i < entity.Attributes.Count; i++) values[entity.Attributes[i].Name] = Normalise(entity.Attributes[i], reader.IsDBNull(i) ? null : reader.GetValue(i));
        return new StoredRow(values, Convert.ToInt64(reader.GetValue(columns.Count - 1)));
    }

    /// <summary>What the driver gives back, as the type that was written (so a read row compares equal to a coerced value).</summary>
    private static object? Normalise(ModelAttribute attribute, object? value)
    {
        if (value is null) return null;
        return attribute.Canonical.Kind switch
        {
            CanonicalKind.Int8 => Convert.ToInt16(value),
            CanonicalKind.Int16 => Convert.ToInt16(value),
            CanonicalKind.Int32 => Convert.ToInt32(value),
            CanonicalKind.Int64 => Convert.ToInt64(value),
            CanonicalKind.Float => Convert.ToSingle(value),
            CanonicalKind.Double => Convert.ToDouble(value),
            CanonicalKind.Decimal => Convert.ToDecimal(value),
            CanonicalKind.Boolean => Convert.ToBoolean(value),
            CanonicalKind.Date => value is DateTime d ? DateOnly.FromDateTime(d) : value,
            CanonicalKind.Time => value is TimeSpan t ? TimeOnly.FromTimeSpan(t) : value,
            CanonicalKind.Timestamp => value is DateTimeOffset o ? o.ToUniversalTime() : value is DateTime dt && attribute.Canonical.TimeZone == true ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : value is DateTime plain ? DateTime.SpecifyKind(plain, DateTimeKind.Utc) : value,
            _ => value,
        };
    }

    private Dictionary<string, object?>? TypedKey(ModelEntity entity, IReadOnlyDictionary<string, object?> key, List<RuleViolation> violations)
    {
        var typed = new Dictionary<string, object?>();
        var before = violations.Count;
        foreach (var name in entity.Key)
        {
            var attribute = entity.Attribute(name)!;
            if (!key.TryGetValue(name, out var raw) || Values.Unwrap(raw) is null) { violations.Add(new("required", entity.Name, name, key, $"The key of `{entity.Name}` needs `{name}`.")); continue; }
            if (!Values.TryCoerce(attribute.Canonical, raw, out var value, out var rule, out var problem)) { violations.Add(new(rule, entity.Name, name, key, $"`{entity.Name}.{name}` {problem}.")); continue; }
            typed[name] = value;
        }
        foreach (var extra in key.Keys.Where(k => !entity.Key.Contains(k))) violations.Add(new("key", entity.Name, extra, key, $"`{extra}` is not part of the key of `{entity.Name}`."));
        return violations.Count > before ? null : typed;
    }

    private static Dictionary<string, object?> KeyOf(ModelEntity entity, IReadOnlyDictionary<string, object?> values) =>
        entity.Key.ToDictionary(k => k, k => values.TryGetValue(k, out var v) ? Values.Unwrap(v) : null);

    private string Where(ModelEntity entity, IReadOnlyDictionary<string, object?> typedKey, List<object?> parameters) =>
        string.Join(" AND ", entity.Key.Select(k => $"{Col(k)} = @p{Add(parameters, typedKey[k])}"));

    private static int Add(List<object?> parameters, object? value) { parameters.Add(value); return parameters.Count - 1; }

    private ModelEntity RequireEntity(string name) => _model.Entity(name) ?? throw new WriteRefusedException([new("entity", name, null, new Dictionary<string, object?>(), $"The model `{_model.Name}` has no entity `{name}`.")]);

    private DbCommand Command(DbTransaction? tx, string text, IReadOnlyList<object?> parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = text;
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p" + i;
            parameter.Value = ForEngine(parameters[i]) ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    /// <summary>A value as the engine's driver takes it: SQL Server's driver has no date-only or time-only parameter in every version, so they become a date-time and a time span there.</summary>
    private object? ForEngine(object? value) => _engine == "sqlserver" ? value switch { DateOnly d => d.ToDateTime(TimeOnly.MinValue), TimeOnly t => t.ToTimeSpan(), _ => value } : value;

    private async Task<int> ExecuteAsync(DbTransaction tx, string text, IReadOnlyList<object?> parameters, CancellationToken ct)
    {
        await using var command = Command(tx, text, parameters);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<object?> ScalarAsync(DbTransaction tx, string text, IReadOnlyList<object?> parameters, CancellationToken ct)
    {
        await using var command = Command(tx, text, parameters);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static decimal? Numeric(object value) => value switch { sbyte or byte or short or int or long or decimal or float or double => Convert.ToDecimal(value), _ => null };
    private static string Display(object? value) => value switch { null => "null", bool b => b ? "true" : "false", DateOnly d => d.ToString("yyyy-MM-dd"), _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
}
