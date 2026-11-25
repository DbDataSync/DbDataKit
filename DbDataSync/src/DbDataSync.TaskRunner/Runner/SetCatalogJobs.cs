using System.Data.Common;
using System.Text.Json;
using DbDataKit.Catalog;
using DbDataKit.Dialect;
using DbDataKit.Metadata;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Libraries;
using DbDataSync.Provisioning;
using DbDataSync.Scripting;
using MetadataTableRef = DbDataKit.Metadata.TableRef;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>What the set's <c>discover</c> is started with: the set's connection to read, and the schemas (a JSON list), a table-name pattern (SQL <c>LIKE</c>) or one table of the one schema given that narrow it.</summary>
public sealed record SetDiscoverInput(string Connection = "", string Schemas = "", string Pattern = "", string Table = "", string RepoDir = "repo");

/// <summary>What the set's <c>describe</c> is started with: the set's connection the query reads, and the query.</summary>
public sealed record SetDescribeInput(string Connection = "", string Sql = "", string RepoDir = "repo");

/// <summary>One column of a described query: its name, its type as the engine names it, and whether it may be null.</summary>
public sealed record SetDescribedColumn(string Name, string Type, bool Nullable);

/// <summary>The answer of the set's <c>describe</c>: the engine, how the columns were found, and the columns in order.</summary>
public sealed record SetDescribeAnswer(string Engine, string Strategy, IReadOnlyList<SetDescribedColumn> Columns);

/// <summary>
/// The names, inputs and steps of the set's <c>discover</c> and <c>describe</c>. Their answers have the shape of the Runner's built-in package <c>dbdatakit-metadata</c>: <c>discover</c> streams one connection of
/// <c>catalog-cache/2</c> without its <c>read</c>, <c>describe</c> keeps <c>{"engine", "strategy", "columns"}</c>, each in a step named after the task and keyed by the run's id, so a console reads either package's answer the same way.
/// Design: docs/authoring-metadata.md#a-sync-project
/// </summary>
public static class SetCatalogSteps
{
    /// <summary>The read of a connection's databases, schemas, tables and columns.</summary>
    public const string Discover = "discover";

    /// <summary>The description of a query's columns.</summary>
    public const string Describe = "describe";

    /// <summary>The longest query the task describes, as the console sends at most.</summary>
    public const int MaxSqlChars = 100_000;

    /// <summary>The <c>discover</c> task's inputs as the manifest declares them.</summary>
    public static IReadOnlyList<TaskInput> DiscoverInputs(string repoDir) =>
    [
        new("connection", DbDataKit.State.ProgressType.Text, Default: "", Description: "The set's connection to read"),
        new("schemas", DbDataKit.State.ProgressType.Text, Default: "", Description: "The schemas to read, as a JSON list; empty reads every schema"),
        new("pattern", DbDataKit.State.ProgressType.Text, Default: "", Description: "A pattern over table names (SQL LIKE)"),
        new("table", DbDataKit.State.ProgressType.Text, Default: "", Description: "One table of the one schema given"),
        new("repo_dir", DbDataKit.State.ProgressType.Text, Default: repoDir, Description: "Where the definition is in the package"),
    ];

    /// <summary>The <c>describe</c> task's inputs as the manifest declares them.</summary>
    public static IReadOnlyList<TaskInput> DescribeInputs(string repoDir) =>
    [
        new("connection", DbDataKit.State.ProgressType.Text, Default: "", Description: "The set's connection the query reads"),
        new("sql", DbDataKit.State.ProgressType.Text, Default: "", Description: "The query whose columns are described; it is never run"),
        new("repo_dir", DbDataKit.State.ProgressType.Text, Default: repoDir, Description: "Where the definition is in the package"),
    ];

    /// <summary>The schemas input read: a JSON list of names, or none.</summary>
    internal static IReadOnlyList<string>? SchemasOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(text);
            return list is { Count: > 0 } ? list : null;
        }
        catch (JsonException) { throw new ArgumentException($"The input `schemas` is a JSON list of schema names, such as [\"dbo\"], not '{(text.Length > 60 ? text[..60] + "..." : text)}'."); }
    }
}

/// <summary>The step <c>discover</c> keeps its answer in: a JSON file, keyed by the run's id.</summary>
public static class SetDiscoverSteps
{
    public static readonly StreamStep<string> Discover = Step.Stream<string>(SetCatalogSteps.Discover, run => run, "application/json");
}

/// <summary>The step <c>describe</c> keeps its answer in: a small value, keyed by the run's id.</summary>
public static class SetDescribeSteps
{
    public static readonly ValueStep<string, SetDescribeAnswer> Describe = Step.Value<string, SetDescribeAnswer>(SetCatalogSteps.Describe, run => run);
}

/// <summary>
/// Reads a connection of the set with Sync's own drivers: <see cref="DiscoverAsync"/> through Sync's metadata service (a connection's <c>metadataProvider</c> script answers when one is bound, else the driver's catalog, a YAML driver's
/// <c>information_schema</c> or its own queries), and <see cref="DescribeAsync"/> through a zero-row probe of the query after the read-only policy. Shape only: no row is read.
/// Design: docs/authoring-metadata.md#a-sync-project
/// </summary>
internal sealed class SetCatalogRead(ConfigRepository config, IConnectionFactory connections, ScriptedMetadata scripted)
{
    /// <summary>How long a description may take before the database is asked to stop.</summary>
    private static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The connection as one connection of <c>catalog-cache/2</c>: the database the connection opens (another database of the same server is read through a connection of its own), with the schemas of its tables, and the tables in scope with their columns, type text, nullability and primary key. A
    /// driver's catalog lists no view apart and no unique or foreign key, and no schema without a table; what it does not list is not in the answer.
    /// </summary>
    public async Task<CacheConnection> DiscoverAsync(string connectionName, IReadOnlyList<string>? schemas, string? pattern, string? table, CancellationToken ct)
    {
        if (table is { Length: > 0 } && schemas is not { Count: 1 }) throw new ArgumentException($"The table '{table}' is read in one schema: give exactly one schema with it.");
        if (table is { Length: > 0 } && pattern is { Length: > 0 }) throw new ArgumentException("Give a table or a pattern, not both.");
        var connection = Load(connectionName);
        var wantedSchemas = schemas is { Count: > 0 } ? new HashSet<string>(schemas, StringComparer.Ordinal) : null;
        var nameFilter = pattern is { Length: > 0 } ? new TableFilter(NamePattern: pattern).Compile() : null;
        bool Wanted(TableMetadata t) =>
            (wantedSchemas is null || wantedSchemas.Contains(t.Schema))
            && (table is not { Length: > 0 } || string.Equals(t.Table, table, StringComparison.Ordinal))
            && (nameFilter is null || nameFilter(new MetadataTableRef(null, t.Schema, t.Table)));

        var (opened, listed, read) = await new MetadataService(connections, scripted).ReadCatalogAsync(connectionName, Wanted, ct).ConfigureAwait(false);
        var database = opened.Length > 0 ? opened : connection.Database ?? "";

        var tables = read.Where(r => r.Columns.Count > 0).Select(r => new CacheTable(r.Table.Schema, r.Table.Table, "table",
            [.. r.Columns.Select(c => new CacheColumn(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey))]) { Database = database }).ToList();
        var schemasListed = listed.Select(t => t.Schema).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new CacheConnection(EngineOf(connection), tables)
        {
            Databases = database.Length > 0 ? [new CacheDatabase(database, schemasListed)] : [],
        };
    }

    /// <summary>
    /// The columns of <paramref name="sql"/>, found without fetching a row: the read-only policy checks the text before the connection opens, then <c>SELECT * FROM (query) t WHERE 1 = 0</c> is asked for its result's schema; a query that
    /// cannot be a derived table is asked with the provider's schema-only describe instead.
    /// </summary>
    public async Task<SetDescribeAnswer> DescribeAsync(string connectionName, string sql, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("Give the query to describe: the input `sql`.");
        if (sql.Length > SetCatalogSteps.MaxSqlChars) throw new ArgumentException($"The query is longer than {SetCatalogSteps.MaxSqlChars} characters.");
        var connection = Load(connectionName);
        var engine = EngineOf(connection);
        var dialect = DialectCatalog.Builtin.TryGet(new EngineId(engine), out var known) && known is not null ? known : DialectCatalog.Builtin.Get("ansi");
        // the policy first: a query that writes never reaches the database, not even to be described
        QueryProbe.RequireReadOnly(sql, dialect);
        var (opened, _) = await connections.OpenAsync(connectionName, CredentialRole.Read, ct).ConfigureAwait(false);
        await using (opened)
        {
            QueryShape shape;
            try { shape = await QueryProbe.WrappedZeroRowAsync(opened, dialect, sql, DescribeTimeout, ct).ConfigureAwait(false); }
            catch (DbException) { shape = await QueryProbe.SchemaOnlyAsync(opened, dialect, sql, DescribeTimeout, ct).ConfigureAwait(false); }
            return new SetDescribeAnswer(engine, shape.Strategy.ToString(), [.. shape.Columns.OrderBy(c => c.Ordinal).Select(c => new SetDescribedColumn(c.Name, c.NativeType, c.Nullable))]);
        }
    }

    /// <summary>The engine a cache entry names: the connection file's engine for a built-in driver, the driver's id for one the set carries.</summary>
    internal static string EngineOf(ConnectionConfig connection) => DriverFailures.EngineFor(connection.DriverType).Value;

    private ConnectionConfig Load(string connectionName)
    {
        if (string.IsNullOrWhiteSpace(connectionName)) throw new ArgumentException("Name the set's connection to read: the input `connection`.");
        try { return config.LoadConnection(connectionName); }
        catch (FileNotFoundException) { throw new ArgumentException($"The set has no connection '{connectionName}' (config/connections/{connectionName}.yaml)."); }
    }
}

/// <summary>
/// The set's manual, read-only task <c>discover</c> (Design: docs/authoring-metadata.md#a-sync-project): the shape of one of the set's connections, read with the login the run's channel binds to its slot, through Sync's own driver of
/// the connection. It answers what the built-in package <c>dbdatakit-metadata</c> answers for an engine it opens, for a connection whose driver it cannot run (a YAML driver, a driver the set carries) or whose
/// <c>metadataProvider</c> script answers the catalog.
/// </summary>
[RunnerTask(SetCatalogSteps.Discover)]
[RunnerDeclarations(typeof(SetDiscoverSteps))]
public sealed class SetDiscoverJob : RunnerJob<SetDiscoverInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var read = Compose(Input.RepoDir);
        Status($"{SetCatalogSteps.Discover} {Input.Connection}");
        CacheConnection answer;
        try { answer = await read.DiscoverAsync(Input.Connection, SetCatalogSteps.SchemasOf(Input.Schemas), Input.Pattern, Input.Table, ct).ConfigureAwait(false); }
        catch (ArgumentException ex) { throw new TaskFailureException(TaskFailureKind.Permanent, ex.Message); }
        var json = CatalogCache.ToJson(answer);
        json.Remove("read");
        await Step(SetDiscoverSteps.Discover, Run.Id.ToString("D")).Repeatable().StreamAsync(async (stream, token) =>
        {
            await using var writer = new Utf8JsonWriter(stream);
            json.WriteTo(writer);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal static SetCatalogRead Compose(string repoDir)
    {
        var (repoRoot, scratch) = PreviewTasks.Folders(repoDir);
        var secrets = SecretStore.ForProviders();
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), secrets);
        var engine = EngineComposition.Compose(config, repoRoot, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")));
        return new SetCatalogRead(config, new RunLoginConnections(config, engine.Drivers, engine.Libraries, repoRoot, secrets), new ScriptedMetadata(engine.Scripts, config));
    }
}

/// <summary>
/// The set's manual, read-only task <c>describe</c> (Design: docs/authoring-metadata.md#a-sync-project): the columns of a query over one of the set's connections, through Sync's own driver of the connection, with the login the run's
/// channel binds to its slot. The query is described, never run for its rows; a query that writes fails with the read-only policy's reason before the connection opens.
/// </summary>
[RunnerTask(SetCatalogSteps.Describe)]
[RunnerDeclarations(typeof(SetDescribeSteps))]
public sealed class SetDescribeJob : RunnerJob<SetDescribeInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var read = SetDiscoverJob.Compose(Input.RepoDir);
        Status($"{SetCatalogSteps.Describe} {Input.Connection}");
        SetDescribeAnswer answer;
        try { answer = await read.DescribeAsync(Input.Connection, Input.Sql, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new TaskFailureException(TaskFailureKind.Permanent, ex.Message); }
        await Step(SetDescribeSteps.Describe, Run.Id.ToString("D")).Repeatable().RunAsync(_ => Task.FromResult(answer), ct).ConfigureAwait(false);
    }
}
