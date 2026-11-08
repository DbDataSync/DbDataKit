using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>Plan 33 UE07: <c>GET</c> and <c>PUT /api/replications/{name}/checks</c>, the checks of a replication as a screen edits them: read back as written, validated by the strict reader with file and line, quoted by version, and for an administrator.</summary>
public sealed class ReplicationChecksEndpointTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<string> MakeReplicationAsync()
    {
        var name = $"checks-{Guid.NewGuid():N}"[..20];
        (await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name, Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "MsSqlCdc" }, Cache = new CacheConfig { Kind = "MsSqlStagingTable" }, Writer = new WriterConfig { Kind = "MsSqlMerge" } },
        }, Options)).EnsureSuccessStatusCode();
        return name;
    }

    private string TaskFile(string name) => Path.Combine(factory.RepoRoot, "config", "replications", name, "task.yaml");

    private async Task<JsonNode> ReadAsync(string name) => JsonNode.Parse(await _client.GetStringAsync($"/api/replications/{name}/checks"))!;

    private Task<HttpResponseMessage> PutAsync(string name, string version, params object[] checks) =>
        _client.PutAsJsonAsync($"/api/replications/{name}/checks", new { checks, version });

    private static object Freshness(string id = "orders_fresh", string age = "10m") => new { id, kind = "freshness", dataset = "dbo.orders", max_age = age, severity = "block" };

    [Fact]
    public async Task A_freshness_check_is_saved_and_read_back_with_a_version_and_the_replication_still_loads()
    {
        var name = await MakeReplicationAsync();
        var empty = await ReadAsync(name);
        Assert.Empty(empty["checks"]!.AsArray());

        var response = await PutAsync(name, (string)empty["version"]!, Freshness());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var back = await ReadAsync(name);
        Assert.Equal((string)saved["version"]!, (string)back["version"]!);
        var check = Assert.Single(back["checks"]!.AsArray())!;
        Assert.Equal(("orders_fresh", "freshness", "dbo.orders", "10m", "block", "alert"), ((string)check["id"]!, (string)check["kind"]!, (string)check["dataset"]!, (string)check["max_age"]!, (string)check["severity"]!, (string)check["on_error"]!));
        Assert.Contains("max_age: 10m", (string)back["text"]!);
        Assert.False((bool)back["hasComments"]!);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/replications/{name}")).StatusCode);     // the replication itself still reads
    }

    [Fact]
    public async Task The_comments_of_the_file_stay_and_a_section_with_comments_says_so()
    {
        var name = await MakeReplicationAsync();
        File.WriteAllText(TaskFile(name), "# the sales replication\n" + File.ReadAllText(TaskFile(name)).TrimEnd() + "\n\n# what the business relies on\nchecks:\n  - {id: a, kind: freshness, dataset: dbo.orders, max_age: 1h}\n");
        var read = await ReadAsync(name);
        Assert.True((bool)read["hasComments"]!);

        (await PutAsync(name, (string)read["version"]!, Freshness("a", "2h"))).EnsureSuccessStatusCode();

        Assert.StartsWith("# the sales replication", File.ReadAllText(TaskFile(name)));
        Assert.Equal("2h", (string)(await ReadAsync(name))["checks"]![0]!["max_age"]!);
    }

    [Fact]
    public async Task A_bad_kind_is_refused_with_the_reader_s_file_line_and_suggestion_and_nothing_is_written()
    {
        var name = await MakeReplicationAsync();
        var version = (string)(await ReadAsync(name))["version"]!;
        var before = File.ReadAllText(TaskFile(name));

        var response = await PutAsync(name, version, new { id = "b", kind = "freshnes", dataset = "dbo.orders", max_age = "6h" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var fault = Assert.Single(body["diagnostics"]!.AsArray())!;
        Assert.Equal(("DBD-QLT-001", "task.yaml"), ((string)fault["code"]!, (string)fault["file"]!));
        Assert.True((int)fault["line"]! > 0);
        Assert.Contains("Did you mean `freshness`?", (string)fault["message"]!);
        Assert.Equal(before, File.ReadAllText(TaskFile(name)));

        var twice = await PutAsync(name, version, Freshness("x"), Freshness("x"));
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.Contains("DBD-QLT-005", await twice.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_that_carries_the_current_checks_and_a_save_with_no_version_is_refused()
    {
        var name = await MakeReplicationAsync();
        var first = (string)(await ReadAsync(name))["version"]!;
        (await PutAsync(name, first, Freshness("a", "1h"))).EnsureSuccessStatusCode();

        var stale = await PutAsync(name, first, Freshness("a", "2h"));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = JsonNode.Parse(await stale.Content.ReadAsStringAsync())!;
        Assert.Equal("1h", (string)body["current"]!["checks"]![0]!["max_age"]!);
        Assert.Equal("1h", (string)(await ReadAsync(name))["checks"]![0]!["max_age"]!);
        var none = await _client.PutAsJsonAsync($"/api/replications/{name}/checks", new { checks = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("version", await none.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_section_broken_by_hand_is_a_422_with_its_faults_and_a_missing_replication_is_not_found()
    {
        var name = await MakeReplicationAsync();
        File.AppendAllText(TaskFile(name), "\nchecks:\n  - {id: a, kind: nope, dataset: dbo.orders}\n");

        var broken = await _client.GetAsync($"/api/replications/{name}/checks");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, broken.StatusCode);
        Assert.Contains("DBD-QLT-001", await broken.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/replications/no-such-one/checks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/replications/no-such-one/checks", new { checks = Array.Empty<object>(), version = "v" })).StatusCode);
    }
}

/// <summary>Who may do what: a viewer reads the checks, saving them needs an administrator, nobody signed in gets nothing.</summary>
public sealed class ReplicationChecksEndpointRolesTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    [Fact]
    public async Task A_viewer_reads_the_checks_and_cannot_save_them_and_an_administrator_can()
    {
        var admin = await factory.SignedInAsAsync(UserRole.Admin);
        var viewer = await factory.SignedInAsAsync(UserRole.Viewer);
        var name = "roles-checks";
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        (await admin.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name, Enabled = false, Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "MsSqlCdc" }, Cache = new CacheConfig { Kind = "MsSqlStagingTable" }, Writer = new WriterConfig { Kind = "MsSqlMerge" } },
        }, options)).EnsureSuccessStatusCode();
        var version = (string)JsonNode.Parse(await viewer.GetStringAsync($"/api/replications/{name}/checks"))!["version"]!;

        var refused = await viewer.PutAsJsonAsync($"/api/replications/{name}/checks", new { checks = Array.Empty<object>(), version });
        var allowed = await admin.PutAsJsonAsync($"/api/replications/{name}/checks", new { checks = new[] { new { id = "a", kind = "freshness", dataset = "dbo.orders", max_age = "1h" } }, version });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/replications/{name}/checks")).StatusCode);
    }
}
