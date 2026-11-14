using System.Net;
using System.Text.Json.Nodes;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>The harness of the proofs of plan 28c E06d and E12 that need every program real: one Sync server and one runner with the extra replications (<see cref="SyncOnRunner.Extras"/>), shared by the tests of the collection <see cref="Name"/> because setting them up is most of the cost.</summary>
public sealed class SyncOnRunnerExtrasFixture : IAsyncLifetime
{
    public const string Name = "SyncOnRunner extras";

    internal SyncOnRunner? Harness { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (SyncOnRunner.EnginesMissing is null) Harness = await SyncOnRunner.StartAsync(extras: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Harness is not null) await Harness.DisposeAsync();
    }
}

[CollectionDefinition(SyncOnRunnerExtrasFixture.Name)]
public sealed class SyncOnRunnerExtrasCollection : ICollectionFixture<SyncOnRunnerExtrasFixture>;

/// <summary>What the tests of the collection do with the harness: calls to the Sync API and the runner that fail with the servers' own words, and waiting for rows.</summary>
public abstract class SyncOnRunnerExtrasTest(SyncOnRunnerExtrasFixture fixture)
{
    internal SyncOnRunner H => fixture.Harness ?? throw new InvalidOperationException("no harness");

    protected static void RequireEngines()
    {
        if (SyncOnRunner.EnginesMissing is { } missing) Assert.Skip(missing);
    }

    internal async Task<JsonNode> Ok(Task<(HttpStatusCode Status, JsonNode? Body)> call, string what, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, body) = await call;
        Assert.True(status == expected, $"{what}: {status} {body}\n{string.Join('\n', H.Steps)}\n{(status == expected ? "" : await H.Diagnose())}");
        return body ?? new JsonObject();
    }

    protected Task<JsonNode> RunNow(string replication) => Ok(H.Sync(HttpMethod.Post, $"/api/replications/{replication}/runs"), $"run now {replication}", HttpStatusCode.Accepted);

    protected async Task<List<JsonNode>> RunnerRuns(string replication) =>
        [.. (await Ok(H.Runner(HttpMethod.Get, $"/runs?package={SyncOnRunner.Package}&channel={SyncOnRunner.Channel}&task={replication}&limit=100"), $"runner runs of {replication}"))["runs"]!.AsArray().Select(r => r!)];

    protected async Task<bool> WorkerRunning(string replication) => (bool)(await Ok(H.Sync(HttpMethod.Get, $"/api/replications/{replication}/status"), $"status of {replication}"))["running"]!;

    protected Task WaitForRows(string table, long expected, string what) =>
        H.WaitAsync(async () => await H.TargetAsync(table) is var (rows, _) && rows == expected, $"{table}: {expected} rows in the target ({what})", 240);
}
