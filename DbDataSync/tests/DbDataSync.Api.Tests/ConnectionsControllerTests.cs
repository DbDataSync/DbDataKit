using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using LibGit2Sharp;
using Xunit;

namespace DbDataSync.Api.Tests;

public sealed class ConnectionsControllerTests : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TestApiFactory _factory;
    private readonly HttpClient _client;

    public ConnectionsControllerTests(TestApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static ConnectionInput MakeInput(string name) => new()
    {
        Name = name,
        DriverType = DriverIds.MsSql,
        Host = "sql01",
        Port = 1433,
        Database = "App",
        AuthMode = AuthMode.SqlAuth,
        UserId = "svc_app",
        Password = "sup3r-s3cr3t",
    };

    /// <summary>
    /// The endpoint a UI builds its reader/cache/writer pickers from. What matters is that every entry
    /// comes from the registered driver's own declarations — so this asserts the capability flags
    /// track the writers' SupportsReconciliation and the readers' ISegmentExpandingReader
    /// implementation, not that a particular hardcoded list came back.
    /// </summary>
    [Fact]
    public async Task Capabilities_ReportsWhatTheRegisteredDriverActuallySupports()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        (await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions)).EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/api/connections/{name}/capabilities");
        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync<DriverCapabilities>(JsonOptions);

        Assert.NotNull(capabilities);
        Assert.Equal(DriverIds.MsSql, capabilities!.DriverType);

        // Only the reload readers — plus the key-diff sweep, which expands Auto segments the same way —
        // can expand an Auto segment into concrete ranges.
        Assert.Equal(
            ["BatchReload", "KeyReconcile", "MsSqlBatchReload"],
            capabilities.Readers.Where(r => r.SupportsSegmentation).Select(r => r.Kind).Order());

        // The change-feed readers report a source delete as one; the scanning readers cannot see a
        // row that is no longer there. TriggerAudit is the engine-neutral one, which is the point of
        // it — it brings delete detection to every engine with triggers rather than one.
        Assert.Equal(
            ["MsSqlCdc", "MsSqlChangeTracking", "TriggerAudit"],
            capabilities.Readers.Where(r => r.DetectsDeletes).Select(r => r.Kind).Order());

        // The SPA decides whether to offer a Test action from this flag alone.
        Assert.True(capabilities.SupportsConnectionTest);

        // The reload writers reconcile, as does the key-diff delete sweep and its SCD2 close ending
        // (phase 129); the incremental MERGE writer is upsert-only.
        Assert.Equal(
            ["DeleteInsert", "KeyReconcileDelete", "KeyReconcileScd2Close", "MsSqlDeleteInsert", "MsSqlMergeReconcile"],
            capabilities.Writers.Where(w => w.SupportsReconciliation).Select(w => w.Kind).Order());
        Assert.False(capabilities.Writers.Single(w => w.Kind == "MsSqlMerge").SupportsReconciliation);

        Assert.NotEmpty(capabilities.StagingProviders);
    }

    [Fact]
    public async Task Capabilities_ForAnUnknownConnection_Is404()
    {
        var response = await _client.GetAsync($"/api/connections/does-not-exist-{Guid.NewGuid():N}/capabilities");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Upsert_ThenGet_RoundTrips()
    {
        var name = $"conn-{Guid.NewGuid():N}";

        var putResponse = await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);
        putResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync($"/api/connections/{name}");
        getResponse.EnsureSuccessStatusCode();
        var connection = await getResponse.Content.ReadFromJsonAsync<ConnectionConfig>(JsonOptions);

        Assert.NotNull(connection);
        Assert.Equal(name, connection!.Name);
        Assert.Equal("sql01", connection.Host);
        Assert.NotNull(connection.CredentialSecretRef);
    }

    [Fact]
    public async Task Upsert_NeverReturnsOrCommitsPlaintextPassword()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        const string password = "sup3r-s3cr3t";

        var putResponse = await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);
        var body = await putResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(password, body);

        using var repo = new Repository(_factory.RepoRoot);
        var blob = (Blob)repo.Head.Tip[$"config/connections/{name}.yaml"].Target;
        Assert.DoesNotContain(password, blob.GetContentText());
    }

    [Fact]
    public async Task List_IncludesUpsertedConnection()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);

        var connections = await _client.GetFromJsonAsync<List<ConnectionConfig>>("/api/connections", JsonOptions);

        Assert.Contains(connections!, c => c.Name == name);
    }

    [Fact]
    public async Task Get_WhenNotFound_Returns404()
    {
        var response = await _client.GetAsync($"/api/connections/does-not-exist-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ThenGet_Returns404()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);

        var deleteResponse = await _client.DeleteAsync($"/api/connections/{name}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/connections/{name}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    // ---- the connection as a file (plan 13, E05: the console copies a connection to another server) ----

    private static string FileText(string name, string? secret = null) =>
        "schema: connection/1\n" + $"name: {name}\n" + "engine: sqlserver\n" + "address: { host: sql01, port: 1433, database: App }\n" +
        $"auth: {{ mode: sql, user: svc_app, password_secret: '{secret ?? $"secret:dbdata:sync:connection:{name}"}' }}\n";

    private async Task<JsonElement> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions));

    [Fact]
    public async Task File_IsTheConnection1TextWithoutAPassword_AndItsVersion()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);

        var response = await _client.GetAsync($"/api/connections/{name}/file");
        response.EnsureSuccessStatusCode();
        var answer = await Json(response);

        var text = answer.GetProperty("text").GetString()!;
        Assert.Contains("schema: connection/1", text);
        Assert.Contains($"password_secret: secret:dbdata:sync:connection:{name}", text);
        Assert.DoesNotContain("sup3r-s3cr3t", text);
        Assert.Equal(64, answer.GetProperty("version").GetString()!.Length);
    }

    [Fact]
    public async Task File_OfAnUnknownConnection_Is404_AndABadNameIs400()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/connections/nope-{Guid.NewGuid():N}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/connections/1bad/file")).StatusCode);
    }

    [Fact]
    public async Task PutFile_CreatesFromAFileAndAPassword_ThenReadsBackAsTheSameConnection()
    {
        var name = $"copy-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/connections/{name}/file", new { text = FileText(name), expectedVersion = (string?)null, password = "typed-once" }, JsonOptions);
        response.EnsureSuccessStatusCode();
        Assert.True((await Json(response)).GetProperty("created").GetBoolean());

        var connection = await _client.GetFromJsonAsync<ConnectionConfig>($"/api/connections/{name}", JsonOptions);
        Assert.Equal("sql01", connection!.Host);
        Assert.Equal("svc_app", connection.UserId);
        Assert.Equal($"dbdata:sync:connection:{name}", connection.CredentialSecretRef);
    }

    [Fact]
    public async Task PutFile_WithoutAPasswordTheTargetHasNotGot_IsRefusedNamingTheReference()
    {
        var name = $"copy-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/connections/{name}/file", new { text = FileText(name), expectedVersion = (string?)null }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"dbdata:sync:connection:{name}", (await Json(response)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/connections/{name}")).StatusCode);
    }

    [Theory]
    [InlineData("access: readonly\n", "not a setting Sync uses")]
    [InlineData("bogus_key: 1\n", "Unknown key `bogus_key`")]
    public async Task PutFile_ThatSyncWouldNotRead_IsRefusedWithDiagnosticsAndWritesNothing(string extra, string expected)
    {
        var name = $"copy-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/connections/{name}/file", new { text = FileText(name) + extra, expectedVersion = (string?)null, password = "x" }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Json(response);
        Assert.False(body.GetProperty("valid").GetBoolean());
        Assert.Contains(body.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("message").GetString()!.Contains(expected));
        Assert.All(body.GetProperty("diagnostics").EnumerateArray(), d => Assert.True(d.GetProperty("line").GetInt32() > 0));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/connections/{name}")).StatusCode);
    }

    [Fact]
    public async Task PutFile_NamingAnotherConnectionsSecret_IsRefused()
    {
        var name = $"copy-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/connections/{name}/file", new { text = FileText(name, "secret:dbdata:sync:connection:other"), expectedVersion = (string?)null, password = "x" }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"dbdata:sync:connection:{name}", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task PutFile_OverAnExistingConnection_NeedsTheVersionItRead()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);
        var read = await Json(await _client.GetAsync($"/api/connections/{name}/file"));
        var text = read.GetProperty("text").GetString()!.Replace("host: sql01", "host: sql02");

        // creating over an existing name, and a stale version, are conflicts that name the current version
        var create = await _client.PutAsJsonAsync($"/api/connections/{name}/file", new { text, expectedVersion = (string?)null }, JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.Equal(read.GetProperty("version").GetString(), (await Json(create)).GetProperty("currentVersion").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PutAsJsonAsync($"/api/connections/{name}/file", new { text, expectedVersion = "0000" }, JsonOptions)).StatusCode);

        var change = await _client.PutAsJsonAsync($"/api/connections/{name}/file", new { text, expectedVersion = read.GetProperty("version").GetString() }, JsonOptions);
        change.EnsureSuccessStatusCode();
        Assert.False((await Json(change)).GetProperty("created").GetBoolean());
        Assert.Equal("sql02", (await _client.GetFromJsonAsync<ConnectionConfig>($"/api/connections/{name}", JsonOptions))!.Host);
    }
}
