using System.Data.Common;
using DbDataKit.Dialect;
using DbDataKit.Schema;
using DbDataKit.Schema.Ddl;

namespace DbDataKit.DataModel.Storage;

/// <summary>
/// The table of a model's draft layer (plan 21 CH01): the writes a preview or a channel made that are not data yet, kept apart from the entity tables and from <c>_audit</c> (never history unless submitted and approved). One row is one
/// accepted write call: the draft it belongs to (a preview or channel id), its order, who wrote it and the changes as the JSON of the wire (<c>RowJson</c>). Reading a draft replays the rows on top of the real data; discarding it deletes them.
/// The table is made the first time a draft is written (an expand step of its own: the model's <see cref="ModelStorage.DefinitionHash"/> and the copies' status do not change because a draft was used).
/// </summary>
public static class DraftStorage
{
    /// <summary>The table beside a model's entities that holds the drafts.</summary>
    public const string Table = "_draft";

    /// <summary>The longest draft id.</summary>
    public const int IdLength = 200;

    public static IReadOnlyList<(string Name, CanonicalType Type, bool Nullable)> Columns { get; } =
    [
        ("draft_id", CanonicalType.String(IdLength), false),
        ("seq", CanonicalType.Of(CanonicalKind.Int64), false),
        ("at", CanonicalType.Timestamp(null, tz: true), false),
        ("actor_id", CanonicalType.String(200), false),
        ("actor_name", CanonicalType.String(200), true),
        ("change_json", CanonicalType.String(), false),
    ];

    /// <summary>The statements that make the draft table: with the model's instance column when its instances share their tables.</summary>
    public static IReadOnlyList<StorageStatement> Plan(string engine, string schema, bool shared)
    {
        var ddl = DdlFor(engine);
        var dialect = DialectCatalog.For(new EngineId(engine));
        IReadOnlyList<(string Name, CanonicalType Type, bool Nullable)> columns = shared ? [(ModelStorage.InstanceColumn, CanonicalType.String(ModelStorage.InstanceLength), false), .. Columns] : Columns;
        var native = columns.Select(c =>
        {
            var declaration = dialect.ColumnType(c.Type).Require();
            return new NativeColumn(c.Name, declaration, null, c.Nullable, new ColumnShape(c.Name, declaration.Split('(')[0], c.Type.Kind == CanonicalKind.String ? c.Type.Length ?? -1 : null, null, null, c.Nullable, null));
        }).ToList();
        string[] key = shared ? [ModelStorage.InstanceColumn, "draft_id", "seq"] : ["draft_id", "seq"];
        return
        [
            new($"{schema}.{Table}", $"create table {schema}.{Table}", ddl.CreateTable(schema, Table, native)),
            new($"{schema}.{Table}", $"primary key of {schema}.{Table}", ddl.AddPrimaryKey(schema, Table, "pk__draft", key)),
        ];
    }

    /// <summary>Whether the schema has the draft table.</summary>
    public static async Task<bool> ExistsAsync(DbConnection connection, string schema, CancellationToken cancellationToken = default, DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table";
        foreach (var (name, value) in new[] { ("@schema", schema), ("@table", Table) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
    }

    /// <summary>Makes the draft table when it is not there (the schema must exist: the model's storage was created).</summary>
    public static async Task EnsureAsync(DbConnection connection, string engine, string schema, bool shared, CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(connection, schema, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            foreach (var statement in Plan(engine, schema, shared))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement.Text;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DbException)
        {
            // another writer made it first
            if (!await ExistsAsync(connection, schema, cancellationToken).ConfigureAwait(false)) throw;
        }
    }

    private static TableDdl DdlFor(string engine) => engine switch
    {
        "sqlserver" => new TSqlTableDdl("sqlserver"),
        "postgres" => new PostgresTableDdl(),
        _ => throw new StorageException($"Models are stored on SQL Server (`sqlserver`) and PostgreSQL (`postgres`); `{engine}` is not one of them yet."),
    };
}
