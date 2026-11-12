// Pulls the pages of a REST API into a staging table. The code is yours from here on: change the path, the paging style, the columns and the key.
// What it uses of the kit: OutsideHttp (retries, rate limit, failure classes), CursorPaging, StagingLander (idempotent batches through the write gate).
using System.Net.Http.Headers;
using System.Text.Json;
using DbDataKit.Dialect;
using DbDataKit.Runner.Kit.Http;
using DbDataKit.Runner.Kit.Landing;
using DbDataKit.Transfer;

namespace RestPull;

public sealed class OrdersProgress : Progress
{
    /// <summary>Where the last run stopped: the next run asks for the pages after it. A page that was landed before is not written again (its key is its cursor).</summary>
    [State] public string? Cursor { get; set; }
}

public sealed class OrdersSlots : Slots
{
    /// <summary>The API: a connection of kind http, bound per channel (a sandbox endpoint in a preview channel).</summary>
    public Slot<object> Api { get; } = new("api", kind: "http");

    /// <summary>The database that holds the staging table.</summary>
    public Slot<object> Staging { get; } = new("staging", kind: "database");
}

/// <summary>Named after the job (<c>PullOrders</c> + <c>Counters</c>): that is how the kit finds what a job counts, for the manifest and the console.</summary>
public static class PullOrdersCounters
{
    public static readonly Counter Pages = new("pages");
    public static readonly Counter Items = new("items");
    public static readonly Counter Landed = new("landed");
}

public static class OrdersTable
{
    public static readonly TableName Table = new(null, "orders_raw");

    /// <summary>A staging table takes what arrived (every column may be missing but the key); a model downstream enforces the rules.</summary>
    public static readonly TransferSchema Schema = new(
    [
        new("id", CanonicalType.Of(CanonicalKind.Int64), Nullable: false),
        new("customer", CanonicalType.String(200)),
        new("total", new CanonicalType(CanonicalKind.Decimal, Precision: 18, Scale: 2)),
        new("placed_at", CanonicalType.Of(CanonicalKind.Timestamp)),
    ]);

    /// <summary>One item of the API's answer as the values of the columns, in the order of <see cref="Schema"/>.</summary>
    public static IReadOnlyList<object?> ValuesOf(JsonElement item) =>
    [
        item.GetProperty("id").GetInt64(),
        Text(item, "customer"),
        item.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetDecimal() : null,
        Text(item, "placed_at"),
    ];

    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

[RunnerTask("pull-orders")]
public sealed class PullOrders : RunnerJob<NoInput, OrdersProgress, OrdersSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        using var http = await Outside.OpenApiAsync(Slots.Api, Run, ct);
        // a dry run reads the API and writes nothing: its gate lists the statements and runs none, and opens no database
        await using var gate = Run.DryRun ? Outside.DryRunGate(Run, "pull-orders") : await Outside.OpenStagingAsync(Slots.Staging, "pull-orders", ct);
        var lander = new StagingLander(gate, Outside.Dialect, OrdersTable.Table, OrdersTable.Schema);
        await lander.EnsureTableAsync(ct);

        var from = Progress.Cursor;
        await foreach (var page in http.PagesAsync("orders", new CursorPaging { ItemsPath = "items" }, resumeFrom: from, ct: ct))
        {
            // the key names the page's content: the cursor it was asked with, so a rerun of the same page writes nothing
            var landed = await lander.LandAsync($"orders-{from ?? "first"}", Rows(page.Items), ct);
            Count(PullOrdersCounters.Pages, 1);
            Count(PullOrdersCounters.Items, page.Items.Count);
            if (!landed.AlreadyLanded && !Run.DryRun) Count(PullOrdersCounters.Landed, landed.Rows);          // a dry run wrote nothing
            from = page.Resume;
            Progress.Cursor = from;
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(IEnumerable<JsonElement> items)
    {
        foreach (var item in items) yield return OrdersTable.ValuesOf(item);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Where the job meets the outside world, in one place. A slot opens to what the runner binds: the <see cref="ConnectionGrant"/> of the connection (host, address, login, token), which this turns into a client. A test hands the slot a ready
/// client or gate instead (<c>RunSimulator</c>'s <c>slots:</c>), so the job never needs a network or a database to be tested.
/// </summary>
public static class Outside
{
    /// <summary>The dialect of the staging database's engine (the statements the lander writes are made for it).</summary>
    public static ISqlDialect Dialect { get; } = DialectCatalog.For(EngineId.Postgres);

    public static async Task<OutsideHttp> OpenApiAsync(Slot<object> slot, IRun run, CancellationToken ct) => await slot.OpenAsync(ct) switch
    {
        OutsideHttp ready => ready,
        ConnectionGrant grant => Open(grant, run),
        var other => throw new InvalidOperationException($"The slot '{slot.Name}' opened a {other.GetType().Name}; expected a connection."),
    };

    /// <summary>
    /// The client for the connection the channel binds. The runner hands over what it has today: the address (a connection string without credentials, or host and port), a token or a user name and a password. The sign-in methods
    /// of a connection of kind http (OAuth 2, client certificate) are not handed over yet; when they are, this is the place that changes.
    /// </summary>
    public static OutsideHttp Open(ConnectionGrant grant, IRun run)
    {
        // a connection of kind http: the runner handed over its definition and secrets, and the kit signs in as it says (API key, basic, OAuth 2, client certificate)
        if (grant.HttpDefinition is not null) return OutsideHttp.Open(grant, run, new OutsideHttpOptions { Limit = (5, TimeSpan.FromSeconds(1)) });
        var address = grant.ConnectionString ?? (grant.Host is null ? throw new InvalidOperationException($"The connection '{grant.Connection}' has no address.") : $"https://{grant.Host}{(grant.Port is { } port ? ":" + port : "")}/");
        var client = new HttpClient { BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/") };
        if (grant.Token is { Length: > 0 } token) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else if (grant.User is { Length: > 0 } user) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{grant.Password}")));
        return new OutsideHttp(client, new OutsideHttpOptions { Connection = grant.Connection, Limit = (5, TimeSpan.FromSeconds(1)), DryRun = run.DryRun, OnWouldSend = run.WouldDo });
    }

    /// <summary>
    /// The write gate of a dry run (<c>tasks run --dry-run</c>): it checks and logs every statement the lander makes and executes none, and needs no database connection at all. What it would have run is listed in the run's log as "Dry run, would".
    /// </summary>
    public static WriteGate DryRunGate(IRun run, string command) => new(null, new WriteGateOptions(command, new RunStatementLog(run)) { DryRun = true });

    private sealed class RunStatementLog(IRun run) : IStatementLog
    {
        public void Append(StatementLogEntry entry)
        {
            if (entry.Phase == "dry-run" && entry.Kind is not "Read" and not "Control") run.WouldDo($"{entry.Kind} {entry.StepId}");
        }
    }

    public static async Task<WriteGate> OpenStagingAsync(Slot<object> slot, string command, CancellationToken ct) => await slot.OpenAsync(ct) switch
    {
        WriteGate ready => ready,
        ConnectionGrant grant => throw new NotSupportedException(
            $"This template does not open the database connection '{grant.Connection}' itself: opening one needs the driver of its engine, which the build does not carry. Add `#r \"nuget: Npgsql, <version>\"` (or Microsoft.Data.SqlClient) at the top of this file, "
            + "open the connection with the grant's host, database and login, and return `new WriteGate(new AdoWriteExecutor(connection, Dialect), new WriteGateOptions(command, new MemoryStatementLog()))`."),
        var other => throw new InvalidOperationException($"The slot '{slot.Name}' opened a {other.GetType().Name}; expected a connection."),
    };
}
