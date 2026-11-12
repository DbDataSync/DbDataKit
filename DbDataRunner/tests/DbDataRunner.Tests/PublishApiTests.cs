using DbDataKit.Runner.Api;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>A server client paired with a real runner as a Publisher (the pairing code made by <c>server pair --max-role publisher</c>), calling the publish API with tokens it signs itself.</summary>
internal sealed class PublisherClient : IDisposable
{
    private readonly ConsoleIdentity _self;
    private readonly PairedServer _paired;
    private readonly HttpClient _http;

    private PublisherClient(ConsoleIdentity self, PairedServer paired, HttpClient http) => (_self, _paired, _http) = (self, paired, http);

    public string ClientId => _self.Id;

    public static async Task<PublisherClient> PairAsync(RunnerHarness h, string id, ServerRole ceiling = ServerRole.Publisher)
    {
        var self = new ConsoleIdentity(id, ConsoleSigningKey.Generate());
        var paired = await PairingClient.PairAsync(h.Url, h.NewCode(ceiling), self, ClientKind.Server, "ci " + id);
        return new PublisherClient(self, paired, new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = h.Url });
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, HttpContent? content = null, ServerRole role = ServerRole.Publisher, string subject = "ci")
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, _self.Issuer().Issue(new ConsoleTokenRequest(_paired.InstanceId, subject, subject, role)));
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>The raw answer, for a caller that reads a stream or hands the response on (a promotion's link).</summary>
    public async Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, HttpContent? content, ServerRole role)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, _self.Issuer().Issue(new ConsoleTokenRequest(_paired.InstanceId, "ci", "ci", role)));
        return await _http.SendAsync(request);
    }

    public Task<(HttpStatusCode Status, JsonNode? Body)> HeadAsync(string sha) => SendAsync(HttpMethod.Head, $"/api/v1/artifacts/{sha}");

    public Task<(HttpStatusCode Status, JsonNode? Body)> PutAsync(string sha, byte[] bytes) => SendAsync(HttpMethod.Put, $"/api/v1/artifacts/{sha}", new ByteArrayContent(bytes));

    public Task<(HttpStatusCode Status, JsonNode? Body)> JsonAsync(HttpMethod method, string path, object body, ServerRole role = ServerRole.Publisher) => SendAsync(method, path, JsonContent.Create(body), role);

    public void Dispose() => _http.Dispose();
}

/// <summary>A small package for the API tests: a manifest and a few files given as bytes (the launch assembly is a stand-in, so a version of it is stored and projected but never run).</summary>
internal sealed class FakePackage(string name, Dictionary<string, byte[]> files, PackageTask[] taskList)
{
    public string Name { get; } = name;

    public Dictionary<string, byte[]> Files { get; } = files;

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static FakePackage Make(string name, string version = "1", params PackageTask[] tasks) =>
        new(name, new Dictionary<string, byte[]> { ["DbDataKit.Runner.TestPackage.dll"] = Encoding.UTF8.GetBytes("dll " + version), ["notes.txt"] = Encoding.UTF8.GetBytes("notes " + version) }, tasks.Length == 0 ? [SamplePackage.Task("ok")] : tasks);

    public string Manifest(BuiltAgainst? built = null, string? launch = null) => PackageManifestWriter.Write(new PackageManifest(Name, null,
        new PackageLaunch(LaunchKind.Assembly, Assembly: launch ?? "DbDataKit.Runner.TestPackage.dll"), built ?? new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), taskList));

    public object Body(string channel, string mode, int? expected = null, BuiltAgainst? built = null, string? launch = null, bool leaveOut = false) => new
    {
        manifest = Manifest(built, launch),
        channel,
        mode,
        expected_version = expected,
        artifacts = Files.Where(f => !leaveOut || f.Key != "notes.txt").Select(f => new { path = f.Key, sha256 = Sha(f.Value), size = f.Value.Length }).ToArray(),
    };

    public async Task UploadAllAsync(PublisherClient client)
    {
        foreach (var file in Files)
        {
            var sha = Sha(file.Value);
            if ((await client.HeadAsync(sha)).Status == HttpStatusCode.OK) continue;
            var (status, body) = await client.PutAsync(sha, file.Value);
            Assert.True(status == HttpStatusCode.OK, body?.ToJsonString());
        }
    }
}

/// <summary>Plan 28a R16: the publish API of a real runner (HTTPS, a paired publisher, a console), on SQLite and, when the shared engines are up, on SQL Server and PostgreSQL.</summary>
public sealed class PublishApiTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static string Text(JsonNode? node, string key) => (string)node![key]!;

    private static async Task<(int Exit, string Out, string Error)> Cli(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await RunnerCli.Router(_ => null).RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private static IReadOnlyList<AuditRecord> Audited(RunnerHarness h, string verb, string? outcome = null) =>
        [.. h.Audit.Read(new AuditQuery(Verb: verb, Limit: 200)).Where(r => outcome is null || r.Entry.Outcome == outcome)];

    private static string TaskVersions(RunnerHarness h, string package, string channel = "dev") =>
        string.Join(",", new TaskProjections(h.State).ListByChannel(channel, package).Select(t => t.Version).Distinct());

    [Theory, MemberData(nameof(Engines))]
    public async Task A_publisher_uploads_only_a_changed_file_stages_and_the_staged_version_runs_only_after_activation(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var copy = new TempCopy(SamplePackage.Directory);
        var manifest = SamplePackage.WriteManifest(SamplePackage.Manifest(SamplePackage.Task("ok", TimeSpan.FromSeconds(1))));
        File.WriteAllText(Path.Combine(copy.Path, "notes.txt"), "notes 1");
        var files = Directory.EnumerateFiles(copy.Path, "*", SearchOption.AllDirectories).Count();
        var clientDir = Directory.CreateTempSubdirectory("dbdr-client-").FullName;
        try
        {
            // the first publish pairs with the code, uploads every file and stages
            var first = await Cli("package", "publish", manifest, copy.Path, "--server", h.Url.ToString().TrimEnd('/'), "--code", h.NewCode(ServerRole.Publisher), "--channel", "dev", "--stage", "--client-dir", clientDir);
            Assert.True(first.Exit == 0, first.Error + first.Out);
            Assert.Contains($"{files} file(s) uploaded, 0 already there", first.Out);
            Assert.Contains("Staged sample on dev: version 1", first.Out);

            // staged is not served: nothing is projected and nothing runs
            Assert.Equal("", TaskVersions(h, "sample"));
            await Task.Delay(2500);
            Assert.Empty(await h.RunsAsync("?package=sample"));
            var (_, detail) = await h.GetAsync("/api/v1/definitions/integration/sample");
            var channel = Assert.Single(detail!["channels"]!.AsArray());
            Assert.Equal((1, true), ((int)channel!["staged"]!, channel["active"] is null));

            // an administrator activates it (this harness runs no staging follower, so no runner has reported ready: activating is a recorded "anyway"), and the schedule runs it
            var (activate, _) = await h.PostAsync("/api/v1/definitions/integration/sample/activate", ServerRole.Admin, new { channel = "dev", expected_version = 0, anyway = true });
            Assert.Equal(HttpStatusCode.OK, activate);
            Assert.Equal("1", TaskVersions(h, "sample"));
            await h.WaitAsync(async () => await h.RunsAsync("?package=sample&outcome=succeeded") is { Count: >= 1 } r ? r : null, "a run of the activated version");

            // one file changes: the second publish (the stored pairing, no code) sends that one file and is served at once
            File.WriteAllText(Path.Combine(copy.Path, "notes.txt"), "notes 2");
            var second = await Cli("package", "publish", manifest, copy.Path, "--server", h.Url.ToString().TrimEnd('/'), "--channel", "dev", "--expected-version", "1", "--client-dir", clientDir);
            Assert.True(second.Exit == 0, second.Error + second.Out);
            Assert.Contains($"1 file(s) uploaded, {files - 1} already there", second.Out);
            Assert.Contains("Published sample on dev: version 2", second.Out);
            Assert.Equal("2", TaskVersions(h, "sample"));
            await h.WaitAsync(async () => await h.RunsAsync("?package=sample&outcome=succeeded") is { } r && r.Any(x => Text(x, "package_version") == "2") ? r : null, "a run of version 2");

            // the audit says who did what, to which package and channel, and holds no file content
            var published = Audited(h, RunnerPublishing.PublishVerb, AuditEvent.Ok);
            Assert.Equal(2, published.Count);
            Assert.All(published, r => Assert.Equal(("package", "sample/dev"), (r.Entry.TargetKind, r.Entry.TargetId)));
            Assert.DoesNotContain("notes 2", string.Join(" ", published.Select(r => r.Entry.Detail)));
            Assert.Equal(files + 1, Audited(h, RunnerPublishing.UploadVerb, AuditEvent.Ok).Count);
        }
        finally { Directory.Delete(clientDir, recursive: true); }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task An_upload_whose_bytes_do_not_hash_to_the_name_is_refused_naming_both_hashes(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var named = FakePackage.Sha(Encoding.UTF8.GetBytes("abc"));
        var actual = FakePackage.Sha(Encoding.UTF8.GetBytes("abd"));

        var (status, body) = await publisher.PutAsync(named, Encoding.UTF8.GetBytes("abd"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(named, Text(body, "error"));
        Assert.Contains(actual, Text(body, "error"));
        Assert.Equal(HttpStatusCode.NotFound, (await publisher.HeadAsync(named)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await publisher.HeadAsync(actual)).Status);   // nothing was kept
        Assert.Single(Audited(h, RunnerPublishing.UploadVerb, AuditEvent.Refused));

        // the right bytes are stored, and a second upload of them reads nothing
        Assert.Equal(HttpStatusCode.OK, (await publisher.PutAsync(actual, Encoding.UTF8.GetBytes("abd"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await publisher.HeadAsync(actual)).Status);
        Assert.True((bool)(await publisher.PutAsync(actual, Encoding.UTF8.GetBytes("abd"))).Body!["already_stored"]!);
    }

    [Fact]
    public async Task A_file_past_the_size_cap_is_refused_and_not_kept()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite, maxArtifactBytes: 100);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var big = new byte[1000];
        var sha = FakePackage.Sha(big);

        var (status, body) = await publisher.PutAsync(sha, big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("100", Text(body, "error"));
        Assert.Equal(HttpStatusCode.NotFound, (await publisher.HeadAsync(sha)).Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_paired_client_describes_a_package_and_a_missing_one_answers_the_404_words_whatever_roles_it_holds(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var client = await PublisherClient.PairAsync(h, "sync-like", ServerRole.Viewer | ServerRole.Operator | ServerRole.Publisher | ServerRole.Admin);
        var v1 = FakePackage.Make("orders");
        await v1.UploadAllAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", v1.Body("dev", "stage", expected: 0))).Status);

        // the roles a Sync server's pairing signs with (the pairing's ceiling, administrator included), a viewer, and a publisher that is also a viewer
        foreach (var role in new[] { ServerRole.Viewer | ServerRole.Operator | ServerRole.Publisher | ServerRole.Admin, ServerRole.Viewer, ServerRole.Publisher | ServerRole.Viewer })
        {
            var (missing, missingBody) = await client.SendAsync(HttpMethod.Get, "/api/v1/definitions/integration/nothing-here", role: role);
            Assert.Equal(HttpStatusCode.NotFound, missing);
            Assert.Equal("There is no integration 'nothing-here'.", Text(missingBody, "error"));
            var (found, foundBody) = await client.SendAsync(HttpMethod.Get, "/api/v1/definitions/integration/orders", role: role);
            Assert.Equal(HttpStatusCode.OK, found);
            Assert.Equal("orders", Text(foundBody, "name"));
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_stale_expected_version_is_refused_and_an_invalid_package_is_refused_with_the_reason(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var v1 = FakePackage.Make("orders");
        await v1.UploadAllAsync(publisher);
        var (first, _) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", v1.Body("dev", "stage", expected: 0));
        Assert.Equal(HttpStatusCode.OK, first);

        var v2 = FakePackage.Make("orders", "2");
        await v2.UploadAllAsync(publisher);
        var (stale, body) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", v2.Body("dev", "stage", expected: 0));
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Contains("version 1, not 0", Text(body, "error"));
        Assert.Equal(HttpStatusCode.OK, (await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", v2.Body("dev", "stage", expected: 1))).Status);

        // a launch file that is not among the files, a contract the runner does not serve, a file that was never uploaded
        var (noLaunch, noLaunchBody) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", v2.Body("dev", "stage", launch: "missing.dll"));
        Assert.Equal(HttpStatusCode.BadRequest, noLaunch);
        Assert.Contains("missing.dll", Text(noLaunchBody, "error"));
        var (old, oldBody) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", v2.Body("dev", "stage", built: new BuiltAgainst("8.0", null, "dbdatakit.runner.v1")));
        Assert.Equal(HttpStatusCode.BadRequest, old);
        Assert.Contains("8.0", Text(oldBody, "error"));
        Assert.Contains(RunnerContracts.Dotnet, Text(oldBody, "error"));
        var protocol = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", v2.Body("dev", "stage", built: new BuiltAgainst("10.0", null, "dbdatakit.runner.v9")));
        Assert.Contains("dbdatakit.runner.v9", Text(protocol.Body, "error"));
        var never = FakePackage.Make("orders", "3");
        var (missing, missingBody) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", never.Body("dev", "stage"));
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.Contains("is not stored here", Text(missingBody, "error"));
        // a name in the path that is not the manifest's
        var (wrongName, _) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/other", v2.Body("dev", "stage"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongName);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_publisher_reaches_only_its_own_packages_and_a_viewer_publishes_nothing(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var alice = await PublisherClient.PairAsync(h, "builder-a");
        using var bob = await PublisherClient.PairAsync(h, "builder-b");
        var sample = FakePackage.Make("sample");
        await sample.UploadAllAsync(alice);
        Assert.Equal(HttpStatusCode.OK, (await alice.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/sample", sample.Body("dev", "stage"))).Status);

        // another client's package: refused, audited, nothing stored
        var changed = FakePackage.Make("sample", "2");
        await changed.UploadAllAsync(bob);
        var (status, body) = await bob.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/sample", changed.Body("dev", "stage"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("publisher role", Text(body, "error"));
        var refused = Assert.Single(Audited(h, RunnerPublishing.PublishVerb, AuditEvent.Refused));
        Assert.Equal(("sample/dev", "builder-b", "ci"), (refused.Entry.TargetId, refused.Entry.ViaId, refused.Entry.ActorId));
        var (_, after) = await h.GetAsync("/api/v1/definitions/integration/sample");
        Assert.Equal(1, (int)after!["latest_version"]!);
        Assert.Equal("builder-a", Text(after, "owner"));
        // nor may it bind or take it over by another call
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.JsonAsync(HttpMethod.Put, "/api/v1/bindings/dev/integration/sample", new { slots = new { db = "x" } })).Status);

        // its own package, and the first one's owner may publish its next version
        var own = FakePackage.Make("mine");
        await own.UploadAllAsync(bob);
        Assert.Equal(HttpStatusCode.OK, (await bob.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/mine", own.Body("dev", "stage"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await alice.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/sample", changed.Body("dev", "stage", expected: 1))).Status);

        // a publisher reads nothing and activates nothing; a viewer publishes nothing (403, audited); the administrator is not limited by owner
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.SendAsync(HttpMethod.Get, "/api/v1/definitions/integration")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.JsonAsync(HttpMethod.Post, "/api/v1/definitions/integration/sample/activate", new { channel = "dev" })).Status);
        var (viewer, _) = await h.CallAsync(HttpMethod.Put, "/api/v1/definitions/integration/sample", ServerRole.Viewer, changed.Body("dev", "stage"));
        Assert.Equal(HttpStatusCode.Forbidden, viewer);
        Assert.Equal(2, Audited(h, RunnerPublishing.PublishVerb, AuditEvent.Refused).Count);
        Assert.Single(Audited(h, RunnerPublishing.ActivateVerb, AuditEvent.Refused));
        Assert.Single(Audited(h, RunnerPublishing.BindVerb, AuditEvent.Refused));
        var (admin, _) = await h.CallAsync(HttpMethod.Put, "/api/v1/definitions/integration/sample", ServerRole.Admin, FakePackage.Make("sample", "3").Body("dev", "stage"));
        Assert.Equal(HttpStatusCode.BadRequest, admin);   // allowed by role; refused because its files were never uploaded
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/api/v1/definitions/integration", ServerRole.Viewer)).Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Require_signatures_refuses_an_unsigned_publish_naming_the_missing_signature(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var package = FakePackage.Make("orders");
        await package.UploadAllAsync(publisher);

        // only an administrator sets it
        Assert.Equal(HttpStatusCode.Forbidden, (await publisher.JsonAsync(HttpMethod.Put, "/api/v1/settings/require_signatures", new { value = true })).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Put, "/api/v1/settings/require_signatures", ServerRole.Admin, new { value = true })).Status);
        Assert.True((bool)(await h.GetAsync("/api/v1/settings")).Body!["require_signatures"]!);

        var (status, body) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "stage"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("signature", Text(body, "error"));
        Assert.Contains("orders", Text(body, "error"));
        Assert.Contains("require_signatures", Text(body, "error"));
        Assert.Single(Audited(h, RunnerPublishing.PublishVerb, AuditEvent.Refused));
        Assert.Equal(HttpStatusCode.NotFound, (await h.GetAsync("/api/v1/definitions/integration/orders")).Status);

        // off again: accepted and recorded as unsigned
        await h.CallAsync(HttpMethod.Put, "/api/v1/settings/require_signatures", ServerRole.Admin, new { value = false });
        var (ok, published) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "stage"));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("unsigned", Text(published, "signature"));
        Assert.Contains("unsigned", Audited(h, RunnerPublishing.PublishVerb, AuditEvent.Ok).Single().Entry.Detail);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Rollback_returns_to_the_previous_version_the_projection_follows_and_retire_clears_the_schedule(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        foreach (var version in new[] { "1", "2" })
        {
            var package = FakePackage.Make("orders", version, SamplePackage.Task("ok"), SamplePackage.Task("extra" + version));
            await package.UploadAllAsync(publisher);
            var (status, body) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "serve"));
            Assert.True(status == HttpStatusCode.OK, body?.ToJsonString());
        }

        var projection = new TaskProjections(h.State);
        Assert.Equal("2", TaskVersions(h, "orders"));
        Assert.Equal(["extra2", "ok"], projection.ListByChannel("dev", "orders").Select(t => t.Task));

        // a stale expected version is refused; the right one rolls back
        var (stale, _) = await h.PostAsync("/api/v1/definitions/integration/orders/rollback", ServerRole.Admin, new { channel = "dev", expected_version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Equal("2", TaskVersions(h, "orders"));
        var (rolled, body2) = await h.PostAsync("/api/v1/definitions/integration/orders/rollback", ServerRole.Admin, new { channel = "dev", expected_version = 2 });
        Assert.Equal(HttpStatusCode.OK, rolled);
        Assert.Equal(1, (int)body2!["active"]!);
        Assert.Equal("1", TaskVersions(h, "orders"));
        Assert.Equal(["extra1", "ok"], projection.ListByChannel("dev", "orders").Select(t => t.Task));

        // switch forward again, then retire: the schedule is gone and the versions stay
        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync("/api/v1/definitions/integration/orders/switch", ServerRole.Admin, new { channel = "dev", version = 2 })).Status);
        Assert.Equal("2", TaskVersions(h, "orders"));
        var (retired, _) = await h.PostAsync("/api/v1/definitions/integration/orders/retire", ServerRole.Admin, new { channel = "dev", expected_version = 2 });
        Assert.Equal(HttpStatusCode.OK, retired);
        Assert.Empty(projection.ListByChannel("dev", "orders"));
        var (_, detail) = await h.GetAsync("/api/v1/definitions/integration/orders");
        Assert.Equal(2, detail!["versions"]!.AsArray().Count);
        Assert.Contains(detail["events"]!.AsArray(), e => Text(e, "action") == "retire");
        Assert.Single(Audited(h, RunnerPublishing.RetireVerb, AuditEvent.Ok));
        Assert.Single(Audited(h, RunnerPublishing.SwitchVerb, AuditEvent.Ok));
        Assert.Single(Audited(h, RunnerPublishing.RollbackVerb, AuditEvent.Ok));
        Assert.Single(Audited(h, RunnerPublishing.RollbackVerb, AuditEvent.Refused));   // the stale expected version
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Prune_is_a_dry_run_unless_told_otherwise_keeps_what_a_channel_serves_and_is_audited_when_it_removes(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        foreach (var version in new[] { "1", "2", "3", "4" })
        {
            var package = FakePackage.Make("orders", version, SamplePackage.Task("ok"));
            await package.UploadAllAsync(publisher);
            var (status, body) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "serve"));
            Assert.True(status == HttpStatusCode.OK, body?.ToJsonString());
        }

        // an administrator's call without dry_run says what it would do
        var (dryStatus, dry) = await h.PostAsync("/api/v1/definitions/integration/orders/prune", ServerRole.Admin, new { keep = 1 });
        Assert.Equal(HttpStatusCode.OK, dryStatus);
        Assert.True((bool)dry!["dry_run"]!);
        Assert.Equal([1, 2], dry["remove"]!.AsArray().Select(n => (int)n!));
        Assert.Empty(Audited(h, RunnerPublishing.PruneVerb, AuditEvent.Ok));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync("/api/v1/definitions/integration/orders/prune", ServerRole.Admin, new { })).Status);

        var (status2, done) = await h.PostAsync("/api/v1/definitions/integration/orders/prune", ServerRole.Admin, new { keep = 1, dry_run = false });
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.False((bool)done!["dry_run"]!);
        var (_, detail) = await h.GetAsync("/api/v1/definitions/integration/orders");
        Assert.Equal([4, 3], detail!["versions"]!.AsArray().Select(v => (int)v!["version"]!));
        Assert.Single(Audited(h, RunnerPublishing.PruneVerb, AuditEvent.Ok));
    }

    [Fact]
    public async Task Bindings_are_set_per_channel_by_the_publisher_who_owns_the_package_and_read_back()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var package = FakePackage.Make("orders");
        await package.UploadAllAsync(publisher);
        // the slots of a first version are bound before it exists (a version that declares an unbound slot is refused, plan 28c E07): the publisher who binds a new name owns it, and nobody else can bind it after that
        Assert.Equal(HttpStatusCode.OK, (await publisher.JsonAsync(HttpMethod.Put, "/api/v1/bindings/dev/integration/orders", new { slots = new { source = "erp" } })).Status);
        using var other = await PublisherClient.PairAsync(h, "builder-b");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.JsonAsync(HttpMethod.Put, "/api/v1/bindings/dev/integration/orders", new { slots = new { source = "erp2" } })).Status);
        await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "stage"));

        var (status, body) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/bindings/dev/integration/orders", new { slots = new { source = "erp", target = "warehouse" } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["source", "target"], body!["bindings"]!.AsArray().Select(b => Text(b, "slot")));
        var (_, detail) = await h.GetAsync("/api/v1/definitions/integration/orders");
        Assert.Contains(detail!["bindings"]!.AsArray(), b => Text(b, "slot") == "source" && Text(b, "bound_to") == "erp" && Text(b, "channel") == "dev");
        var audited = Audited(h, RunnerPublishing.BindVerb, AuditEvent.Ok);   // the first bind (before the package existed) and this one; the other publisher's refused attempt is a refusal
        Assert.Equal(2, audited.Count);
        Assert.Single(Audited(h, RunnerPublishing.BindVerb, AuditEvent.Refused));
        Assert.All(audited, audit => Assert.DoesNotContain("erp", audit.Entry.Detail));   // slot names are audited, not what they are bound to
    }

    [Fact]
    public async Task A_read_slot_a_channel_leaves_unbound_reads_prods_binding_and_a_write_slot_is_refused_naming_slot_and_channel()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var task = SamplePackage.Task("load") with { Connections = [new ConnectionSlot("source", "sqlserver", Access: SlotAccess.Read), new ConnectionSlot("target", "postgres")] };
        var package = FakePackage.Make("orders", "1", task);
        await package.UploadAllAsync(publisher);
        await publisher.JsonAsync(HttpMethod.Put, "/api/v1/bindings/prod/integration/orders", new { slots = new { source = "erp", target = "warehouse" } });

        // dev binds nothing: the read slot is satisfied by prod's, the write slot is not
        var (refused, body) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "stage"));
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        var message = (string)body!["error"]!;
        Assert.Contains("'target'", message);
        Assert.DoesNotContain("'source'", message);
        Assert.Contains("channel 'dev'", message);
        Assert.Contains("No write binding in this channel", message);

        // the channel's own binding of the write slot is all it needs
        await publisher.JsonAsync(HttpMethod.Put, "/api/v1/bindings/dev/integration/orders", new { slots = new { target = "warehouse_dev" } });
        var (status, published) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "stage"));
        Assert.True(status == HttpStatusCode.OK, published?.ToJsonString());

        var (_, detail) = await h.GetAsync("/api/v1/definitions/integration/orders");
        var slots = detail!["slots"]!.AsArray().Where(x => Text(x, "channel") == "dev").ToDictionary(x => Text(x, "slot"), x => Text(x, "access"));
        Assert.Equal("read", slots["source"]);
        Assert.Equal("write", slots["target"]);
    }

    [Fact]
    public async Task The_client_command_refuses_with_the_servers_reason_and_an_unpaired_server_needs_a_code()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        var manifest = SamplePackage.WriteManifest(SamplePackage.Manifest(SamplePackage.Task("ok")));
        var clientDir = Directory.CreateTempSubdirectory("dbdr-client-").FullName;
        try
        {
            var unpaired = await Cli("package", "publish", manifest, SamplePackage.Directory, "--server", h.Url.ToString().TrimEnd('/'), "--channel", "dev", "--client-dir", clientDir);
            Assert.Equal(CliExit.Failure, unpaired.Exit);
            Assert.Contains("not paired", unpaired.Error);

            // a client paired as a viewer only cannot publish: the runner says so
            var viewerCode = h.NewCode(ServerRole.Viewer);
            var refused = await Cli("package", "publish", manifest, SamplePackage.Directory, "--server", h.Url.ToString().TrimEnd('/'), "--code", viewerCode, "--channel", "dev", "--client-dir", clientDir);
            Assert.Equal(CliExit.Failure, refused.Exit);
            Assert.Contains("403", refused.Error);
        }
        finally { Directory.Delete(clientDir, recursive: true); }
    }

    [Fact]
    public async Task Package_install_is_the_same_code_path_in_process_and_is_audited_as_an_install()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        var installed = h.Install(SamplePackage.Task("ok"));
        Assert.Equal(("sample", 1, true), (installed.Package, installed.Version, installed.Changed));
        Assert.Equal("1", TaskVersions(h, "sample"));
        Assert.Single(Audited(h, RunnerPublishing.InstallVerb, AuditEvent.Ok));
        Assert.False(h.Install(SamplePackage.Task("ok")).Changed);

        // the contract check is the same one: a package built for another runtime is refused by install too
        var old = SamplePackage.WriteManifest(new PackageManifest("sample", null, new PackageLaunch(LaunchKind.Assembly, Assembly: "DbDataKit.Runner.TestPackage.dll"), new BuiltAgainst("8.0", null, "dbdatakit.runner.v1"), [SamplePackage.Task("ok")]));
        var ex = Assert.Throws<PackageInstallException>(() => RunnerPackageInstaller.Install(h.State, old, SamplePackage.Directory, "dev", "test"));
        Assert.Contains("8.0", ex.Message);
        Assert.Contains(RunnerContracts.Dotnet, ex.Message);
    }

    [Fact]
    public async Task One_runtime_installs_once_per_platform_side_by_side_without_a_version_clash_and_one_with_no_platform_is_refused_naming_what_to_do()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        var work = Directory.CreateTempSubdirectory("dbdr-runtime-").FullName;
        try
        {
            PackageInstalled Install(string? platform, string content, string version = "2.1.0")
            {
                var directory = Directory.CreateDirectory(Path.Combine(work, platform ?? "none", content)).FullName;
                Directory.CreateDirectory(Path.Combine(directory, "files", "bin"));
                File.WriteAllText(Path.Combine(directory, "files", "bin", "engine"), content);
                var manifest = new PackageManifest("engine", null, new PackageLaunch(LaunchKind.Executable, Command: "bin/engine"), new BuiltAgainst(null, null, "dbdatakit.runner.v1"), [], IsRuntime: true, Version: version, Platform: platform);
                var path = SamplePackage.WriteManifest(manifest, directory);
                return RunnerPackageInstaller.Install(h.State, path, Path.Combine(directory, "files"), "dev", "test");
            }

            var linux = Install("linux-x64", "linux 1");
            var windows = Install("win-x64", "windows 1");

            // the same runtime name and the same engine version, two packages: each is version 1 of its own, both stay active in the channel
            Assert.Equal(("engine-linux-x64", 1), (linux.Package, linux.Version));
            Assert.Equal(("engine-win-x64", 1), (windows.Package, windows.Version));
            var publisher = new DefinitionPublisher(h.State, RunnerPackages.Kinds(), new PayloadStore(h.State), "runner");
            Assert.Equal(1, publisher.Head(RunnerPackages.Kind, "engine-linux-x64", "dev")!.Active);
            Assert.Equal(1, publisher.Head(RunnerPackages.Kind, "engine-win-x64", "dev")!.Active);

            // a new Linux build is version 2 of the Linux package and leaves the Windows one alone
            Assert.Equal(2, Install("linux-x64", "linux 2", "2.2.0").Version);
            Assert.Equal(2, publisher.Head(RunnerPackages.Kind, "engine-linux-x64", "dev")!.Active);
            Assert.Equal(1, publisher.Head(RunnerPackages.Kind, "engine-win-x64", "dev")!.Active);

            // a runtime with no platform is refused, naming the key and what to do
            var ex = Assert.Throws<PackageInstallException>(() => Install(null, "legacy"));
            Assert.Contains("`platform`", ex.Message);
            Assert.Contains("published again with one", ex.Message);
        }
        finally { try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ } }
    }
}

/// <summary>A copy of a directory in the temporary folder, removed when disposed.</summary>
internal sealed class TempCopy : IDisposable
{
    public TempCopy(string source)
    {
        Path = Directory.CreateTempSubdirectory("dbdr-pkg-").FullName;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(Path, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }
}
