using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using DbDataKit.Dialect;
using DbDataKit.Schema;
using DbDataKit.Schema.Ddl;

namespace DbDataKit.DataModel.Storage;

/// <summary>One statement of a storage plan: what it makes and the text to run.</summary>
public sealed record StorageStatement(string Object, string Description, string Text);

/// <summary>
/// What it takes to store a model on an engine: a schema named like the model, a table for each entity, a primary key, unique constraints and the foreign keys the model's references imply. <see cref="Tables"/> are
/// the qualified names (<c>sales_territories.territory</c>); <see cref="Notes"/> say where the engine's type is not the model's exactly, and which references are left to the write rules.
/// </summary>
/// <summary>The statements that store a model, and <paramref name="TableColumns"/>: the columns each table has in the plan, by table name (the entities and the audit table), in the engine's own types: what a migration compares the live tables with.</summary>
public sealed record StoragePlan(string Engine, string Schema, IReadOnlyList<StorageStatement> Statements, IReadOnlyList<string> Tables, IReadOnlyList<string> Notes, IReadOnlyDictionary<string, IReadOnlyList<DbDataKit.Schema.Ddl.NativeColumn>>? TableColumns = null);

/// <summary>A column of a table as the catalog reports it.</summary>
public sealed record LiveColumn(string Table, string Column, bool Nullable);

/// <summary>
/// What it takes to bring an instance's tables up to the model without removing or changing anything (plan 26, Z13; "expand" of expand-then-contract): the statements, the things that need a planned change of their own
/// (<see cref="Blocked"/>), and what was noticed. Nothing is dropped, narrowed or retyped here.
/// </summary>
public sealed record ExpandPlan(string Schema, IReadOnlyList<StorageStatement> Statements, IReadOnlyList<string> Blocked, IReadOnlyList<string> Notes)
{
    public bool UpToDate => Statements.Count == 0 && Blocked.Count == 0;
}

/// <summary>One step of the contract of an instance's tables: <paramref name="Description"/> (<c>drop column territory.note</c>, <c>make territory.region required</c>), the text to run, and for a column that becomes required the count of rows that have no value (<paramref name="NullCheck"/>), which must be zero.</summary>
public sealed record ContractStep(string Object, string Description, string Text, string? NullCheck = null, string? Table = null, string? DroppedColumn = null);

/// <summary>
/// What the contract step of expand-then-contract does to an instance's tables (plan 26 Z13b; plan 21 decision 4): the columns the model no longer has are dropped, the columns it requires and the table keeps nullable are made required.
/// <see cref="Hash"/> names this exact plan: applying needs the hash of a plan that was read (a planned and approved change), and a plan that changed since is refused. What is not here is in <see cref="Blocked"/> and <see cref="Notes"/>.
/// </summary>
public sealed record ContractPlan(string Schema, IReadOnlyList<ContractStep> Steps, IReadOnlyList<string> Blocked, IReadOnlyList<string> Notes, string Hash)
{
    public bool Nothing => Steps.Count == 0 && Blocked.Count == 0;
}

/// <summary>The model cannot be stored on the engine, or something of it is already there; the message says which.</summary>
public sealed class StorageException(string message) : Exception(message);

/// <summary>
/// Storage for new models on SQL Server and PostgreSQL, create only (plan 19 Z03, plan 15 section 4). The statements are <see cref="TableDdl"/>'s, with the columns in the engine's own types from the
/// shared dialect, so a later slice that changes a structure (the planner of <c>DbDataKit.Schema</c>) starts from the same text. Nothing is changed that exists: a table that is already there refuses the whole creation.
/// What each rule of the model does on a write (ranges, patterns, allowed values, defaults) is the write path's (Z04); the database holds the shape and the keys.
/// </summary>
public static class ModelStorage
{
    /// <summary>The column every entity table has beside its attributes: the version a write names, raised by one with each change (plan 15 section 5, optimistic concurrency).</summary>
    public const string VersionColumn = "_version";

    /// <summary>The column of every table of a model that keeps its instances in shared tables (<see cref="InstanceMode.Column"/>): the instance a row belongs to, the first part of every key (plan 26 section 6.2).</summary>
    public const string InstanceColumn = "_instance";

    /// <summary>The longest instance id; the column holds exactly that.</summary>
    public const int InstanceLength = 64;

    /// <summary>The table beside a model's entities that records every write: who, when, what changed (plan 15 section 5).</summary>
    public const string AuditTable = "_audit";

    /// <summary>The columns of an effective-dated entity beside its attributes (plan 19 Z05): when this version of the row starts to hold, and when it stopped (null while it holds). A change made from a date closes the open version at it and adds the next.</summary>
    public const string ValidFromColumn = "_valid_from";
    public const string ValidToColumn = "_valid_to";

    /// <summary>The columns of the audit table, in order: name and canonical type. The write path (<c>DbDataKit.Store</c>) fills them.</summary>
    public static IReadOnlyList<(string Name, CanonicalType Type, bool Nullable)> AuditColumns { get; } =
    [
        ("audit_id", CanonicalType.Of(CanonicalKind.Guid), false),
        ("at", CanonicalType.Timestamp(null, tz: true), false),
        ("actor_id", CanonicalType.String(200), false),
        ("actor_name", CanonicalType.String(200), true),
        ("entity", CanonicalType.String(128), false),
        ("row_key", CanonicalType.String(2000), false),
        ("action", CanonicalType.String(10), false),
        ("version_before", CanonicalType.Of(CanonicalKind.Int64), true),
        ("version_after", CanonicalType.Of(CanonicalKind.Int64), true),
        ("before_json", CanonicalType.String(), true),
        ("after_json", CanonicalType.String(), true),
    ];

    private const int NameLimit = 63;          // PostgreSQL's; SQL Server allows more, and one name is stored the same on both

    /// <summary>The statements that store a model on an engine. <paramref name="schema"/> is where the tables go: the model's name when null; an instance of a model that keeps its instances in schemas passes <see cref="DataModel.SchemaOf"/>.</summary>
    public static StoragePlan Plan(DataModel model, string engine, string? schema = null)
    {
        TableDdl ddl = engine switch
        {
            "sqlserver" => new TSqlTableDdl("sqlserver"),
            "postgres" => new PostgresTableDdl(),
            _ => throw new StorageException($"Models are stored on SQL Server (`sqlserver`) and PostgreSQL (`postgres`); `{engine}` is not one of them yet."),
        };
        var dialect = DialectCatalog.For(new EngineId(engine));
        schema ??= model.Name;
        var shared = model.Instances == InstanceMode.Column;
        string[] Keyed(IEnumerable<string> names) => shared ? [InstanceColumn, .. names] : [.. names];
        var statements = new List<StorageStatement>();
        var notes = new List<string>();
        var tableColumns = new Dictionary<string, IReadOnlyList<NativeColumn>>();
        var declarations = new Dictionary<(string Entity, string Attribute), string>();

        statements.Add(new(schema, $"create schema {schema}", ddl.CreateSchema(schema)));
        var constraints = new List<StorageStatement>();
        foreach (var entity in model.Entities)
        {
            if (entity.Key.Count == 0) throw new StorageException($"Entity `{entity.Name}` has no key, and a table of the store has one.");
            var columns = new List<NativeColumn>();
            foreach (var attribute in entity.Attributes)
            {
                var mapped = dialect.ColumnType(attribute.Canonical);
                if (!mapped.IsMapped) throw new StorageException($"`{entity.Name}.{attribute.Name}` is `{attribute.Type}`, which {engine} has no type for: {mapped.Note}");
                if (mapped.Fidelity != Fidelity.Exact) notes.Add($"`{entity.Name}.{attribute.Name}` ({attribute.Type}) is stored as {mapped.Result} on {engine}: {mapped.Note}");
                var declaration = mapped.Result!;
                declarations[(entity.Name, attribute.Name)] = declaration;
                var nullable = !(attribute.Required || entity.Key.Contains(attribute.Name));
                columns.Add(new NativeColumn(attribute.Name, declaration, null, nullable, ShapeOf(attribute, declaration, nullable)));
            }
            if (shared) columns.Add(InstanceNative(dialect));
            columns.Add(new NativeColumn(VersionColumn, dialect.ColumnType(CanonicalType.Of(CanonicalKind.Int64)).Require(), null, false,
                new ColumnShape(VersionColumn, dialect.ColumnType(CanonicalType.Of(CanonicalKind.Int64)).Require().Split('(')[0], null, null, null, false, null)));
            var dated = entity.History == HistoryMode.EffectiveDated;
            if (dated)
            {
                var instant = dialect.ColumnType(CanonicalType.Timestamp(null, tz: true)).Require();
                var instantType = instant.Split('(')[0];
                columns.Add(new NativeColumn(ValidFromColumn, instant, null, false, new ColumnShape(ValidFromColumn, instantType, null, null, null, false, null)));
                columns.Add(new NativeColumn(ValidToColumn, instant, null, true, new ColumnShape(ValidToColumn, instantType, null, null, null, true, null)));
            }
            tableColumns[entity.Name] = columns;
            statements.Add(new($"{schema}.{entity.Name}", $"create table {schema}.{entity.Name}", ddl.CreateTable(schema, entity.Name, columns)));
            if (dated)
            {
                // each version is a row, so the key alone is not the primary key: the key and when the version starts are; one version of a key is open at a time, and a unique value is unique among the open ones
                constraints.Add(new($"{schema}.{entity.Name}", $"primary key of {schema}.{entity.Name}", ddl.AddPrimaryKey(schema, entity.Name, Name("pk", entity.Name), Keyed([.. entity.Key, ValidFromColumn]))));
                constraints.Add(new($"{schema}.{entity.Name}", $"one open version of each key of {schema}.{entity.Name}", ddl.CreateUniqueIndexWhere(schema, entity.Name, Name("ux", entity.Name, "open"), Keyed(entity.Key), $"{dialect.Quote(ValidToColumn)} IS NULL")));
                foreach (var unique in entity.Attributes.Where(a => a.Unique && !(entity.Key is [var only] && only == a.Name)))
                    constraints.Add(new($"{schema}.{entity.Name}", $"unique {unique.Name} among the open versions of {schema}.{entity.Name}", ddl.CreateUniqueIndexWhere(schema, entity.Name, Name("ux", entity.Name, unique.Name), Keyed([unique.Name]), $"{dialect.Quote(ValidToColumn)} IS NULL")));
            }
            else
            {
                constraints.Add(new($"{schema}.{entity.Name}", $"primary key of {schema}.{entity.Name}", ddl.AddPrimaryKey(schema, entity.Name, Name("pk", entity.Name), Keyed(entity.Key))));
                foreach (var unique in entity.Attributes.Where(a => a.Unique && !(entity.Key is [var only] && only == a.Name)))
                    constraints.Add(new($"{schema}.{entity.Name}", $"unique {unique.Name} of {schema}.{entity.Name}", ddl.AddUnique(schema, entity.Name, Name("uq", entity.Name, unique.Name), Keyed([unique.Name]))));
            }
        }

        IReadOnlyList<(string Name, CanonicalType Type, bool Nullable)> audit = shared ? [(InstanceColumn, CanonicalType.String(InstanceLength), false), .. AuditColumns] : AuditColumns;
        var auditColumns = audit.Select(c =>
        {
            var declaration = dialect.ColumnType(c.Type).Require();
            return new NativeColumn(c.Name, declaration, null, c.Nullable, new ColumnShape(c.Name, declaration.Split('(')[0], c.Type.Kind == CanonicalKind.String ? c.Type.Length ?? -1 : null, null, null, c.Nullable, null));
        }).ToList();
        tableColumns[AuditTable] = auditColumns;
        statements.Add(new($"{schema}.{AuditTable}", $"create table {schema}.{AuditTable}", ddl.CreateTable(schema, AuditTable, auditColumns)));
        constraints.Add(new($"{schema}.{AuditTable}", $"primary key of {schema}.{AuditTable}", ddl.AddPrimaryKey(schema, AuditTable, "pk__audit", ["audit_id"])));
        constraints.Add(new($"{schema}.{AuditTable}", $"index of {schema}.{AuditTable}", ddl.CreateIndex(schema, AuditTable, new IndexSpec("ix__audit_entity_at", false, Keyed(["entity", "at"]), []))));

        foreach (var entity in model.Entities)
            foreach (var attribute in entity.Attributes.Where(a => a.References is not null))
            {
                var reference = attribute.References!;
                if (reference.Model is not null)
                {
                    notes.Add($"`{entity.Name}.{attribute.Name}` points at `{reference.Model}.{reference.Entity}`, an entity of another model: it is checked on a write, with no foreign key between the two.");
                    continue;
                }
                if (shared)
                {
                    notes.Add($"`{entity.Name}.{attribute.Name}` points at `{reference.Entity}`: the tables are shared by the instances, so it is checked on a write within the instance, with no foreign key.");
                    continue;
                }
                var target = model.Entity(reference.Entity)!;
                if (target.History == HistoryMode.EffectiveDated)
                {
                    notes.Add($"`{entity.Name}.{attribute.Name}` points at `{target.Name}`, which keeps its history in versions: the key repeats there, so the reference is checked on a write against the open version, with no foreign key.");
                    continue;
                }
                var targetAttribute = reference.Attribute ?? target.Key[0];
                if (declarations[(entity.Name, attribute.Name)] != declarations[(target.Name, targetAttribute)])
                    throw new StorageException($"`{entity.Name}.{attribute.Name}` is `{attribute.Type}` and points at `{target.Name}.{targetAttribute}`, which is `{target.Attribute(targetAttribute)!.Type}`: a reference needs the same type at both ends.");
                constraints.Add(new($"{schema}.{entity.Name}", $"foreign key {attribute.Name} of {schema}.{entity.Name}",
                    ddl.AddForeignKey(schema, entity.Name, Name("fk", entity.Name, attribute.Name), [attribute.Name], schema, target.Name, [targetAttribute])));
            }
        statements.AddRange(constraints);
        return new StoragePlan(engine, schema, statements, [.. model.Entities.Select(e => $"{schema}.{e.Name}"), $"{schema}.{AuditTable}"], notes, tableColumns);
    }

    private static NativeColumn InstanceNative(ISqlDialect dialect)
    {
        var declaration = dialect.ColumnType(CanonicalType.String(InstanceLength)).Require();
        return new NativeColumn(InstanceColumn, declaration, null, false, new ColumnShape(InstanceColumn, declaration.Split('(')[0], InstanceLength, null, null, false, null));
    }

    private static TableDdl DdlFor(string engine) => engine switch
    {
        "sqlserver" => new TSqlTableDdl("sqlserver"),
        "postgres" => new PostgresTableDdl(),
        _ => throw new StorageException($"Models are stored on SQL Server (`sqlserver`) and PostgreSQL (`postgres`); `{engine}` is not one of them yet."),
    };

    /// <summary>
    /// A fingerprint of the structure a model asks for on an engine, the same for every instance of it (the schema name is left out). An instance records the one it was made or last migrated at; a different one now means it is behind the model.
    /// </summary>
    public static string DefinitionHash(DataModel model, string engine)
    {
        var plan = Plan(model, engine, "_");
        var text = string.Join("\n", plan.Statements.Select(s => s.Text)) + "\n" + string.Join("\n", plan.Tables);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>The columns of every table in a schema, as the catalog reports them.</summary>
    public static async Task<IReadOnlyList<LiveColumn>> ReadLiveAsync(DbConnection connection, string schema, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT table_name, column_name, is_nullable FROM information_schema.columns WHERE table_schema = @schema";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@schema";
        parameter.Value = schema;
        command.Parameters.Add(parameter);
        var found = new List<LiveColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) found.Add(new LiveColumn(reader.GetString(0), reader.GetString(1), string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase)));
        return found;
    }

    /// <summary>
    /// What it takes to bring the tables of a schema up to the model by adding only: a table that is missing, a column that is missing (added as nullable, because rows may exist; the writer still requires it). Anything that would
    /// remove or change something (a column the model no longer has, a column the model now keeps only in a way the table cannot take) is left alone, and said: a contract step is a planned change of its own. Column types are not compared.
    /// </summary>
    public static ExpandPlan PlanExpand(DataModel model, string engine, string? schema, IReadOnlyList<LiveColumn> live)
    {
        schema ??= model.Name;
        var full = Plan(model, engine, schema);
        var ddl = DdlFor(engine);
        var statements = new List<StorageStatement>();
        var blocked = new List<string>();
        var notes = new List<string>();
        var liveTables = live.GroupBy(c => c.Table, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(c => c.Column).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        if (liveTables.Count == 0) return new ExpandPlan(schema, [], [$"Nothing of the model is in schema `{schema}` yet: create its storage first."], []);

        var structural = new HashSet<string>(StringComparer.Ordinal) { InstanceColumn, VersionColumn, ValidFromColumn, ValidToColumn };
        foreach (var (table, columns) in full.TableColumns!)
        {
            if (!liveTables.TryGetValue(table, out var have))
            {
                statements.AddRange(full.Statements.Where(st => st.Object == $"{schema}.{table}"));
                notes.Add($"`{table}` is new: its table, keys and constraints are created.");
                continue;
            }
            foreach (var column in columns.Where(c => !have.Contains(c.Name)))
            {
                if (structural.Contains(column.Name))
                {
                    blocked.Add($"`{table}` has no `{column.Name}`, which is part of how the model now keeps its rows (history or instances). That changes the keys of a table that holds rows: it is a planned change of its own, not an addition.");
                    continue;
                }
                statements.Add(new($"{schema}.{table}", $"add column {table}.{column.Name}", ddl.AddColumn(schema, table, column with { Nullable = true })));
                if (!column.Nullable) notes.Add($"`{table}.{column.Name}` is required in the model; it is added as nullable because rows may exist. The writer requires it from now on; a later change makes the column NOT NULL once every row has a value.");
            }
            var wanted = columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var extra in have.Where(c => !wanted.Contains(c)).Order(StringComparer.Ordinal))
                notes.Add($"`{table}.{extra}` is in the table and not in the model any more; it is left in place. Removing it is a contract step of its own.");
        }
        foreach (var table in liveTables.Keys.Where(t => !full.TableColumns!.ContainsKey(t) && t != DraftStorage.Table).Order(StringComparer.Ordinal))
            notes.Add($"`{table}` is in the schema and not in the model; it is left in place.");
        return new ExpandPlan(schema, statements, blocked, notes);
    }

    /// <summary>
    /// The contract step for the tables of a schema (plan 26 Z13b): what <see cref="PlanExpand"/> leaves alone and a person may choose to do once nothing reads it any more. A column the table has and the model does not is dropped; a column the model
    /// requires and the table keeps nullable (an expand adds it that way) is made required, if no row lacks a value. The columns that keep rows as history or per instance (<c>_instance</c>, <c>_valid_from</c>, ...) and a table the model does not have are never touched:
    /// that is a change of how rows are kept, a planned change of its own and not built. Column types are not compared (a retype is not here either).
    /// </summary>
    public static ContractPlan PlanContract(DataModel model, string engine, string? schema, IReadOnlyList<LiveColumn> live)
    {
        schema ??= model.Name;
        var full = Plan(model, engine, schema);
        var ddl = DdlFor(engine);
        var steps = new List<ContractStep>();
        var blocked = new List<string>();
        var notes = new List<string>();
        var liveTables = live.GroupBy(c => c.Table, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Column, c => c.Nullable, StringComparer.Ordinal), StringComparer.Ordinal);
        if (liveTables.Count == 0) return new ContractPlan(schema, [], [$"Nothing of the model is in schema `{schema}`: there is nothing to contract."], [], Hash(schema, []));
        var structural = new HashSet<string>(StringComparer.Ordinal) { InstanceColumn, VersionColumn, ValidFromColumn, ValidToColumn };
        foreach (var (table, columns) in full.TableColumns!.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!liveTables.TryGetValue(table, out var have)) continue;
            var wanted = columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            foreach (var extra in have.Keys.Where(c => !wanted.ContainsKey(c)).Order(StringComparer.Ordinal))
            {
                if (structural.Contains(extra)) { blocked.Add($"`{table}.{extra}` is how the table keeps its rows (history or instances) and the model does not use it now. That changes the keys of a table that holds rows: a planned change of its own, not a contract step."); continue; }
                steps.Add(new($"{schema}.{table}", $"drop column {table}.{extra}", ddl.DropColumn(schema, table, extra), Table: table, DroppedColumn: extra));
            }
            foreach (var column in columns.Where(c => !c.Nullable && have.TryGetValue(c.Name, out var nullable) && nullable && !structural.Contains(c.Name)))
                steps.Add(new($"{schema}.{table}", $"make {table}.{column.Name} required", ddl.AlterColumn(schema, table, column),
                    $"SELECT COUNT(*) FROM {ddl.Qualified(schema, table)} WHERE {ddl.Quote(column.Name)} IS NULL"));
        }
        foreach (var table in liveTables.Keys.Where(t => !full.TableColumns!.ContainsKey(t) && t != DraftStorage.Table).Order(StringComparer.Ordinal))
            notes.Add($"`{table}` is in the schema and not in the model; it is left in place.");
        return new ContractPlan(schema, steps, blocked, notes, Hash(schema, steps));
    }

    private static string Hash(string schema, IReadOnlyList<ContractStep> steps) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema + "\n" + string.Join("\n", steps.Select(s => s.Text))))).ToLowerInvariant();

    /// <summary>
    /// Runs a contract plan in one transaction, for the person who approved it: <paramref name="approvedHash"/> is the <see cref="ContractPlan.Hash"/> of the plan they read, and a plan that is not that one is refused, so what runs is what was reviewed. A column that
    /// becomes required is checked inside the transaction and the whole plan is refused, naming the column and how many rows have no value, if any row lacks one. A plan with blocked items runs nothing.
    /// </summary>
    public static async Task ApplyContractAsync(DbConnection connection, ContractPlan plan, string approvedHash, CancellationToken cancellationToken = default)
    {
        if (plan.Blocked.Count > 0) throw new StorageException(string.Join(" ", plan.Blocked));
        if (!string.Equals(plan.Hash, approvedHash, StringComparison.OrdinalIgnoreCase))
            throw new StorageException($"The plan to apply is {plan.Hash[..12]}... and the approval is for {(approvedHash.Length > 12 ? approvedHash[..12] + "..." : approvedHash)}: the tables or the model changed since the plan was read. Read it again and approve that one.");
        if (plan.Steps.Count == 0) return;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var step in plan.Steps)
            {
                if (step.NullCheck is not null)
                {
                    await using var count = connection.CreateCommand();
                    count.Transaction = transaction;
                    count.CommandText = step.NullCheck;
                    var missing = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                    if (missing > 0) throw new StorageException($"{step.Description}: {missing} row{(missing == 1 ? " has" : "s have")} no value for it. Give them one first; nothing was changed.");
                }
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = step.Text;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs an expand plan in one transaction. Refuses a plan with blocked items, writing nothing.</summary>
    public static async Task ApplyExpandAsync(DbConnection connection, ExpandPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.Blocked.Count > 0) throw new StorageException(string.Join(" ", plan.Blocked));
        if (plan.Statements.Count == 0) return;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var statement in plan.Statements)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement.Text;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The tables of the plan that exist already (its schema's tables with the entities' names).</summary>
    public static async Task<IReadOnlyList<string>> ExistingAsync(DbConnection connection, StoragePlan plan, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@schema";
        parameter.Value = plan.Schema;
        command.Parameters.Add(parameter);
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) found.Add(reader.GetString(0));
        return [.. plan.Tables.Where(t => found.Contains(t[(plan.Schema.Length + 1)..]))];
    }

    /// <summary>Creates what the plan describes in one transaction. Refuses, writing nothing, when any of its tables exists.</summary>
    public static async Task CreateAsync(DbConnection connection, StoragePlan plan, CancellationToken cancellationToken = default)
    {
        var existing = await ExistingAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
            throw new StorageException($"{string.Join(", ", existing.Select(t => $"`{t}`"))} {(existing.Count == 1 ? "exists" : "exist")} already, so nothing was created: storage here only creates, and a changed structure is a planned change.");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var statement in plan.Statements)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement.Text;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>What a catalog reports for the column once it exists, so a later plan can say which shape the table must have.</summary>
    private static ColumnShape ShapeOf(ModelAttribute attribute, string declaration, bool nullable)
    {
        var type = attribute.Canonical;
        var name = declaration.Contains('(') ? declaration[..declaration.IndexOf('(')].Trim() : declaration;
        int? length = type.Kind is CanonicalKind.String or CanonicalKind.Binary ? type.Length ?? -1 : null;
        return new ColumnShape(attribute.Name, name, length, type.Kind == CanonicalKind.Decimal ? type.Precision : null, type.Kind == CanonicalKind.Decimal ? type.Scale : null, nullable, null);
    }

    /// <summary>A constraint name: its prefix and the entity and attribute, cut with a hash at the end when it is longer than the engines allow.</summary>
    private static string Name(string prefix, params string[] parts)
    {
        var full = prefix + "_" + string.Join("_", parts);
        if (full.Length <= NameLimit) return full;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        return full[..(NameLimit - 9)] + "_" + hash;
    }
}
