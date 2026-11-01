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
public sealed record StoragePlan(string Engine, string Schema, IReadOnlyList<StorageStatement> Statements, IReadOnlyList<string> Tables, IReadOnlyList<string> Notes);

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

    /// <summary>The table beside a model's entities that records every write: who, when, what changed (plan 15 section 5).</summary>
    public const string AuditTable = "_audit";

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

    public static StoragePlan Plan(DataModel model, string engine)
    {
        TableDdl ddl = engine switch
        {
            "sqlserver" => new TSqlTableDdl("sqlserver"),
            "postgres" => new PostgresTableDdl(),
            _ => throw new StorageException($"Models are stored on SQL Server (`sqlserver`) and PostgreSQL (`postgres`); `{engine}` is not one of them yet."),
        };
        var dialect = DialectCatalog.For(new EngineId(engine));
        var schema = model.Name;
        var statements = new List<StorageStatement>();
        var notes = new List<string>();
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
            columns.Add(new NativeColumn(VersionColumn, dialect.ColumnType(CanonicalType.Of(CanonicalKind.Int64)).Require(), null, false,
                new ColumnShape(VersionColumn, dialect.ColumnType(CanonicalType.Of(CanonicalKind.Int64)).Require().Split('(')[0], null, null, null, false, null)));
            statements.Add(new($"{schema}.{entity.Name}", $"create table {schema}.{entity.Name}", ddl.CreateTable(schema, entity.Name, columns)));
            constraints.Add(new($"{schema}.{entity.Name}", $"primary key of {schema}.{entity.Name}", ddl.AddPrimaryKey(schema, entity.Name, Name("pk", entity.Name), entity.Key)));
            foreach (var unique in entity.Attributes.Where(a => a.Unique && !(entity.Key is [var only] && only == a.Name)))
                constraints.Add(new($"{schema}.{entity.Name}", $"unique {unique.Name} of {schema}.{entity.Name}", ddl.AddUnique(schema, entity.Name, Name("uq", entity.Name, unique.Name), [unique.Name])));
        }

        var auditColumns = AuditColumns.Select(c =>
        {
            var declaration = dialect.ColumnType(c.Type).Require();
            return new NativeColumn(c.Name, declaration, null, c.Nullable, new ColumnShape(c.Name, declaration.Split('(')[0], c.Type.Kind == CanonicalKind.String ? c.Type.Length ?? -1 : null, null, null, c.Nullable, null));
        }).ToList();
        statements.Add(new($"{schema}.{AuditTable}", $"create table {schema}.{AuditTable}", ddl.CreateTable(schema, AuditTable, auditColumns)));
        constraints.Add(new($"{schema}.{AuditTable}", $"primary key of {schema}.{AuditTable}", ddl.AddPrimaryKey(schema, AuditTable, "pk__audit", ["audit_id"])));
        constraints.Add(new($"{schema}.{AuditTable}", $"index of {schema}.{AuditTable}", ddl.CreateIndex(schema, AuditTable, new IndexSpec("ix__audit_entity_at", false, ["entity", "at"], []))));

        foreach (var entity in model.Entities)
            foreach (var attribute in entity.Attributes.Where(a => a.References is not null))
            {
                var reference = attribute.References!;
                if (reference.Model is not null)
                {
                    notes.Add($"`{entity.Name}.{attribute.Name}` points at `{reference.Model}.{reference.Entity}`, an entity of another model: it is checked on a write, with no foreign key between the two.");
                    continue;
                }
                var target = model.Entity(reference.Entity)!;
                var targetAttribute = reference.Attribute ?? target.Key[0];
                if (declarations[(entity.Name, attribute.Name)] != declarations[(target.Name, targetAttribute)])
                    throw new StorageException($"`{entity.Name}.{attribute.Name}` is `{attribute.Type}` and points at `{target.Name}.{targetAttribute}`, which is `{target.Attribute(targetAttribute)!.Type}`: a reference needs the same type at both ends.");
                constraints.Add(new($"{schema}.{entity.Name}", $"foreign key {attribute.Name} of {schema}.{entity.Name}",
                    ddl.AddForeignKey(schema, entity.Name, Name("fk", entity.Name, attribute.Name), [attribute.Name], schema, target.Name, [targetAttribute])));
            }
        statements.AddRange(constraints);
        return new StoragePlan(engine, schema, statements, [.. model.Entities.Select(e => $"{schema}.{e.Name}"), $"{schema}.{AuditTable}"], notes);
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
