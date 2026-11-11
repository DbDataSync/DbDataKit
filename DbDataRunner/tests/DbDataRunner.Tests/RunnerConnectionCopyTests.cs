using System.Net;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 27a IX23: the Runner is a separate server and keeps its own copy of a console connection (<c>connection_copy</c>): the console sends the definition with secret references, never a value; the references resolve on the runner; an http slot resolves from the
/// copy through the same resolver as a database slot; and a non-production channel bound to the connection production uses is refused (409) until an administrator confirms it, recorded in the runner's own <c>posture_choice</c>.
/// </summary>
public sealed class RunnerConnectionCopyTests
{
    private const string KeyValue = "api-key-value-5c1e77a9";
    private const string Variable = "DBDATAKIT_TEST_IX23_API_KEY";

    private static string Yaml(string name = "crm") =>
        $"schema: connection/2\nname: {name}\nkind: http\nendpoint: https://crm.example/api\nsign_in:\n  method: api_key\n  name: X-Key\n  key_secret: 'env:{Variable}'\n";

    [Fact]
    public async Task A_copy_is_kept_with_the_version_both_sides_compute_its_references_are_checked_on_the_runner_and_no_value_is_in_an_answer_or_the_audit()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await h.StartAsync();
            var yaml = Yaml();

            var (put, sent) = await h.CallAsync(HttpMethod.Put, "/api/v1/connection-copies/crm", ServerRole.Admin, new { definition = yaml });
            Assert.Equal(HttpStatusCode.OK, put);
            Assert.Equal(ConnectionCopyVersion.Of(yaml), (string)sent!["version"]!);
            Assert.Equal("http", (string)sent["kind"]!);
            var secret = Assert.Single(sent["secrets"]!.AsArray());
            Assert.False((bool)secret!["resolves"]!);
            Assert.Contains(Variable, (string)secret["problem"]!);
            Assert.Equal(1, (int)sent["unresolved"]!);

            Environment.SetEnvironmentVariable(Variable, KeyValue);
            var (_, listed) = await h.GetAsync("/api/v1/connection-copies");
            var copy = Assert.Single(listed!["copies"]!.AsArray());
            Assert.True((bool)copy!["secrets"]![0]!["resolves"]!);
            Assert.Equal(0, (int)copy["unresolved"]!);
            Assert.False((bool)copy["shadowed_by_file"]!);
            Assert.DoesNotContain(KeyValue, listed.ToJsonString());

            // the same text is the same version; a changed text is another
            Assert.Equal(ConnectionCopyVersion.Of(yaml.Replace("\n", "\r\n", StringComparison.Ordinal)), (string)copy["version"]!);
            Assert.NotEqual((string)copy["version"]!, ConnectionCopyVersion.Of(yaml + "# changed\n"));

            var audited = h.Audit.Read(new AuditQuery(VerbPrefix: RunnerConnectionCopyApi.PutVerb));
            var entry = Assert.Single(audited);
            Assert.Equal("crm", entry.Entry.TargetId);
            Assert.DoesNotContain(KeyValue, entry.Entry.Detail ?? "");
            Assert.DoesNotContain(KeyValue, string.Join('\n', h.Log));
        }
        finally { Environment.SetEnvironmentVariable(Variable, null); }
    }

    [Fact]
    public async Task A_secret_written_as_itself_a_wrong_name_and_a_caller_who_is_not_an_administrator_are_refused_and_the_secret_is_not_echoed()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        const string plain = "sk-live-plain-9d2f41";
        var bad = $"schema: connection/2\nname: crm\nkind: http\nendpoint: https://crm.example/api\nsign_in:\n  method: api_key\n  key_secret: {plain}\n";

        var (status, refused) = await h.CallAsync(HttpMethod.Put, "/api/v1/connection-copies/crm", ServerRole.Admin, new { definition = bad });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.DoesNotContain(plain, refused!.ToJsonString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await h.CallAsync(HttpMethod.Put, "/api/v1/connection-copies/other", ServerRole.Admin, new { definition = Yaml("crm") })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, "/api/v1/connection-copies/crm", ServerRole.Admin, new { })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Put, "/api/v1/connection-copies/crm", ServerRole.Operator, new { definition = Yaml() })).Status);
        Assert.Empty((await h.GetAsync("/api/v1/connection-copies")).Body!["copies"]!.AsArray());
        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: RunnerConnectionCopyApi.PutVerb)), e => e.Entry.Outcome == AuditEvent.Refused);
        Assert.DoesNotContain(plain, string.Join('\n', h.Audit.Read(new AuditQuery()).Select(e => e.Entry.Detail ?? "")));
    }

    [Fact]
    public async Task A_slot_bound_to_a_copied_http_connection_resolves_from_the_copy_on_the_runner_and_names_the_reference_when_it_does_not()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            h.Install(SamplePackage.Task("fails"));
            await h.StartAsync();
            await h.CallAsync(HttpMethod.Put, "/api/v1/bindings/dev/integration/sample", ServerRole.Admin, new { slots = new { source = "crm" } });
            const string test = "/api/v1/bindings/dev/integration/sample/test";

            // no copy yet: the runner does not know the connection (it does not read the console's folder)
            var (_, unknown) = await h.CallAsync(HttpMethod.Post, test, ServerRole.Admin, new { slot = "source" });
            Assert.False((bool)unknown!["ok"]!);
            Assert.Equal("connection", (string)unknown["step"]!);

            await h.CallAsync(HttpMethod.Put, "/api/v1/connection-copies/crm", ServerRole.Admin, new { definition = Yaml() });
            var (_, missing) = await h.CallAsync(HttpMethod.Post, test, ServerRole.Admin, new { slot = "source" });
            Assert.False((bool)missing!["ok"]!);
            Assert.Equal("login", (string)missing["step"]!);
            Assert.Contains(Variable, (string)missing["message"]!);

            Environment.SetEnvironmentVariable(Variable, KeyValue);
            var (_, fine) = await h.CallAsync(HttpMethod.Post, test, ServerRole.Admin, new { slot = "source" });
            Assert.True((bool)fine!["ok"]!);
            Assert.DoesNotContain(KeyValue, fine.ToJsonString());

            // the copy says which package slots use it
            var (_, listed) = await h.GetAsync("/api/v1/connection-copies");
            var use = Assert.Single(listed!["copies"]![0]!["used_by"]!.AsArray());
            Assert.Equal(("sample", "dev", "source"), ((string)use!["package"]!, (string)use["channel"]!, (string)use["slot"]!));

            // the grant the resolver gives: the method and the resolved key, from this runner's own store
            var grant = await h.Engine.Resolver!.ResolveAsync("source", "crm", "default", CancellationToken.None);
            Assert.Equal(("http", "api_key", KeyValue), (grant.Engine, grant.AuthMode, grant.Token));
            Assert.Equal("crm.example", grant.Host);
            using var client = h.Engine.Resolver.OpenHttp("source", "crm", new DbDataKit.Connections.Http.HttpConnectionOptions(new RecordingHandler()));
            var response = await client.Client.GetAsync("things");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(KeyValue, RecordingHandler.LastKey);
        }
        finally { Environment.SetEnvironmentVariable(Variable, null); }
    }

    [Fact]
    public async Task A_file_of_the_runners_own_directory_answers_to_a_name_before_a_copy_and_the_list_says_so()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        Directory.CreateDirectory(Path.Combine(h.Directory, "connections"));
        File.WriteAllText(Path.Combine(h.Directory, "connections", "crm.yaml"), "schema: connection/1\nname: crm\nengine: postgres\naddress: { host: db.example, database: d }\n");
        await h.CallAsync(HttpMethod.Put, "/api/v1/connection-copies/crm", ServerRole.Admin, new { definition = Yaml() });
        var (_, listed) = await h.GetAsync("/api/v1/connection-copies");
        Assert.True((bool)listed!["copies"]![0]!["shadowed_by_file"]!);
    }

    [Fact]
    public async Task A_non_production_channel_bound_to_the_connection_production_uses_is_refused_until_confirmed_then_recorded_in_the_runners_posture_table_and_taken_back_when_it_moves()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        const string prod = "/api/v1/bindings/prod/integration/sample";
        const string dev = "/api/v1/bindings/dev/integration/sample";
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Put, prod, ServerRole.Admin, new { slots = new { source = "crm" } })).Status);

        var (refused, answer) = await h.CallAsync(HttpMethod.Put, dev, ServerRole.Admin, new { slots = new { source = "crm" } });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        var need = Assert.Single(answer!["needsConfirmation"]!.AsArray());
        Assert.Equal("bindings/runner/sample/source/dev", (string)need!["scope"]!);
        Assert.Equal(("sample/source", "dev", "crm"), ((string)need["slot"]!, (string)need["release"]!, (string)need["connection"]!));
        Assert.Equal(PostureSetting.ProductionBindingOnNonProduction.Name, (string)answer["setting"]!);
        Assert.Empty(new PostureChoices(h.State).List());

        var (ok, _) = await h.CallAsync(HttpMethod.Put, dev, ServerRole.Admin, new { slots = new { source = "crm" }, confirm = new[] { "bindings/runner/sample/source/dev" }, note = "the test crm is production's" });
        Assert.Equal(HttpStatusCode.OK, ok);
        var choice = Assert.Single(new PostureChoices(h.State).List());
        Assert.Equal(("bindings/runner/sample/source/dev", PostureSetting.ProductionBindingOnNonProduction.Name, "the test crm is production's"), (choice.Scope, choice.Setting, choice.Note));
        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: PostureGuard.ChooseVerb)), e => e.Entry.TargetId == PostureSetting.ProductionBindingOnNonProduction.Name);

        // its own connection takes the choice back; another channel with its own connection never needed one
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Put, dev, ServerRole.Admin, new { slots = new { source = "crm-sandbox" } })).Status);
        Assert.Empty(new PostureChoices(h.State).List());
        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: PostureGuard.RevokeVerb)), e => e.Entry.TargetId == PostureSetting.ProductionBindingOnNonProduction.Name);
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Put, "/api/v1/bindings/test/integration/sample", ServerRole.Admin, new { slots = new { source = "crm-sandbox" } })).Status);

        // production moving onto a connection a non-production channel already uses makes the same case from the other side
        var (again, other) = await h.CallAsync(HttpMethod.Put, prod, ServerRole.Admin, new { slots = new { source = "crm-sandbox" } });
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal(2, other!["needsConfirmation"]!.AsArray().Count);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public static string? LastKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastKey = request.Headers.TryGetValues("X-Key", out var values) ? values.FirstOrDefault() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
