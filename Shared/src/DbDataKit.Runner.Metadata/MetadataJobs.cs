using System.Text.Json;
using DbDataKit.Catalog;
using DbDataKit.Metadata;
using DbDataKit.Runner.Kit;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Metadata;

/// <summary>The one slot of each task: the connection the run's channel binds to the slot of the package named in the run's inputs. The runner hands the grant; the task opens it itself.</summary>
public sealed class MetadataSlots : Slots
{
    public Slot<ConnectionGrant> Connection { get; } = new(MetadataPackage.Slot, "database");
}

/// <summary>
/// The inputs of <c>discover</c>. <see cref="Package"/> and <see cref="Slot"/> name whose binding is read (the runner resolves the slot <c>connection</c> through it); <see cref="Role"/> is the login asked for (<c>read</c> or <c>default</c>).
/// <see cref="Schemas"/> (a JSON list, such as <c>["dbo","staging"]</c>), <see cref="Pattern"/> (SQL <c>LIKE</c> over table names) and <see cref="Table"/> (one table of the one schema given) narrow what is read; none: every schema.
/// </summary>
public sealed record DiscoverInput(string Package, string Slot, string Role = "read", IReadOnlyList<string>? Schemas = null, string? Pattern = null, string? Table = null);

/// <summary>The inputs of <c>describe</c>: whose binding is read, as for <c>discover</c>, and the query whose columns are described.</summary>
public sealed record DescribeInput(string Package, string Slot, string Sql, string Role = "read");

/// <summary>One column of a described query: its name, the type text the database gives and whether it may be null.</summary>
public sealed record DescribedColumn(string Name, string Type, bool Nullable);

/// <summary>The answer of <c>describe</c>: the engine, how the columns were found (<c>sp_describe_first_result_set</c>, a prepared statement, a zero-row probe) and the columns in order.</summary>
public sealed record DescribeAnswer(string Engine, string Strategy, IReadOnlyList<DescribedColumn> Columns);

/// <summary>
/// The steps the tasks keep their answers in, each keyed by the run's id so a caller finds the answer of its own run among overlapping ones: <c>discover</c> streams a JSON file (a catalog can be larger than a step's value), <c>describe</c> is a
/// small value.
/// </summary>
public static class MetadataSteps
{
    public static readonly StreamStep<string> Discover = Step.Stream<string>(MetadataPackage.Discover, run => run, "application/json");

    public static readonly ValueStep<string, DescribeAnswer> Describe = Step.Value<string, DescribeAnswer>(MetadataPackage.Describe, run => run);
}

internal static class MetadataInputs
{
    /// <summary>The login the task asks for: <c>read</c> (the connection's read login, else its default one) or <c>default</c>, never <c>write</c>.</summary>
    public static string Role(string role) => role is "read" or "default" ? role : throw new ArgumentException($"The role '{role}' is not one the built-in package reads with: read or default.");
}

/// <summary>
/// <c>discover</c>: the databases, schemas (the empty ones too), tables and views, columns with their type text, nullability and primary key, unique keys and foreign keys of the connection, within the schemas, pattern or table given,
/// answered as one connection of <c>catalog-cache/2</c> (<c>CatalogCache.FromAnswer</c> reads it). Shape only: the catalog is read, never a row.
/// Design: docs/authoring-metadata.md#the-built-in-package-dbdatakit-metadata
/// </summary>
[RunnerTask(MetadataPackage.Discover)]
public sealed class DiscoverJob : RunnerJob<DiscoverInput, NoProgress, MetadataSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var scope = new DiscoverScope(Input.Schemas, Input.Pattern, Input.Table);
        if (scope.Problem() is { } problem) throw new ArgumentException(problem);
        var grant = await Slots.Connection.OpenAsync(MetadataInputs.Role(Input.Role), ct).ConfigureAwait(false);
        Run.Log.LogInformation("Reading the catalog of '{Connection}' (the slot '{Slot}' of {Package}) with its {Role} login.", grant.Connection, Input.Slot, Input.Package, grant.Role);
        CacheConnection answer;
        await using (var connection = await MetadataConnections.OpenAsync(grant, ct).ConfigureAwait(false))
            answer = await CatalogDiscovery.ReadAsync(MetadataConnections.ReaderOf(grant, connection), grant.Engine, scope, ct).ConfigureAwait(false);
        var json = CatalogCache.ToJson(answer);
        json.Remove("read");
        await Step(MetadataSteps.Discover, Run.Id.ToString("D")).Repeatable().StreamAsync(async (stream, token) =>
        {
            await using var writer = new Utf8JsonWriter(stream);
            json.WriteTo(writer);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        Run.Log.LogInformation("Read {Tables} table(s) and view(s) in {Schemas} schema(s).", answer.Tables.Count, answer.Databases.Sum(d => d.Schemas.Count));
    }
}

/// <summary>
/// <c>describe</c>: the columns of a query, found without running it. The read-only policy (<c>QueryProbe.RequireReadOnly</c>) checks the text before the connection opens, then the engine's own describe answers
/// (<c>sp_describe_first_result_set</c> on SQL Server, PostgreSQL's schema-only describe, a wrapped zero-row probe elsewhere). No row is fetched.
/// Design: docs/authoring-metadata.md#the-built-in-package-dbdatakit-metadata
/// </summary>
[RunnerTask(MetadataPackage.Describe)]
public sealed class DescribeJob : RunnerJob<DescribeInput, NoProgress, MetadataSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var role = MetadataInputs.Role(Input.Role);
        var grant = await Slots.Connection.OpenAsync(role, ct).ConfigureAwait(false);
        // the policy first: a query that writes never reaches the database, not even to be described
        QueryProbe.RequireReadOnly(Input.Sql, MetadataConnections.DialectOf(grant));
        QueryShape shape;
        await using (var connection = await MetadataConnections.OpenAsync(grant, ct).ConfigureAwait(false))
            shape = await MetadataConnections.ReaderOf(grant, connection).DescribeQueryAsync(Input.Sql, ct).ConfigureAwait(false);
        var answer = new DescribeAnswer(grant.Engine, shape.Strategy.ToString(), [.. shape.Columns.OrderBy(c => c.Ordinal).Select(c => new DescribedColumn(c.Name, c.NativeType, c.Nullable))]);
        await Step(MetadataSteps.Describe, Run.Id.ToString("D")).Repeatable().RunAsync(_ => Task.FromResult(answer), ct).ConfigureAwait(false);
        Run.Log.LogInformation("Described {Columns} column(s) of the query ({Strategy}).", answer.Columns.Count, answer.Strategy);
    }
}
