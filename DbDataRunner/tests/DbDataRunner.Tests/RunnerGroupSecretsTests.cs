using System.Net;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E20d, group-wide (owner decision 2026-11-15): three runner instances on one shared state database. Two are joined to the group (they are handed the group's key, as the console does when it joins them), the third
/// is not. A login sent to instance A is used by the binding check on instance B; the instance that was not joined cannot read it, cannot write one and says so naming the missing key; a wrong key is refused; what an
/// instance held in its own store before the group had a key still resolves; no value and no key is in a log, the audit or the state in the clear.
/// </summary>
public sealed class RunnerGroupSecretsTests
{
    private const string Name = "dbdatakit:sync:connection:erp:run";
    private const string Value = "gr0up-wide-l0gin-never-in-the-clear";
    private const string Earlier = "dbdatakit:sync:connection:old:run";
    private const string EarlierValue = "h3ld-bef0re-the-gr0up-had-a-key";

    public static TheoryData<string> ServerEngines => [StateEngineIds.MsSql, StateEngineIds.Postgres];

    private static async Task BindAsync(RunnerHarness h, string connection)
    {
        h.Install(SamplePackage.Task("fails"));
        var service = new RunnerPublishing(h.State);
        service.Bind("sample", "dev", new Dictionary<string, string> { ["source"] = connection }, null, RunnerActor.Local());
        var files = service.Publisher.Artifacts(RunnerPublishing.Kind, "sample", 1).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType)).ToList();
        var manifest = PackageManifestWriter.Write(SamplePackage.Manifest(SamplePackage.Task("ok") with { Connections = [new ConnectionSlot("source", "sqlserver")] }));
        service.Publish(new PublishInput("sample", manifest, files, "dev", PublishMode.Stage), RunnerActor.Local());
        await service.ActivateAsync("sample", "dev", 1, RunnerActor.Local(), new ActivateOptions(Anyway: true));
    }

    private static void WriteConnection(RunnerHarness h)
    {
        Directory.CreateDirectory(Path.Combine(h.Directory, "connections"));
        File.WriteAllText(Path.Combine(h.Directory, "connections", "erp.yaml"),
            $"schema: connection/1\nname: erp\nengine: sqlserver\nconnection_string: 'Server=fake;Database=d'\nauth: {{ mode: sql, user: u, password_secret: 'secret:{Name}' }}\n");
    }

    private static string SecretPath(string name) => $"/api/v1/secrets/{Uri.EscapeDataString(name)}";

    private const string Check = "/api/v1/bindings/dev/integration/sample/test";

    [Theory, MemberData(nameof(ServerEngines))]
    public async Task A_login_sent_to_one_instance_is_used_on_another_that_was_joined_and_an_instance_not_joined_says_which_key_it_lacks(string engine)
    {
        await using var a = new RunnerHarness(engine);
        await using var b = new RunnerHarness(engine, sharedState: a.Settings.State);
        await using var c = new RunnerHarness(engine, sharedState: a.Settings.State);
        await BindAsync(a, "erp");
        WriteConnection(b);
        WriteConnection(c);
        await a.StartAsync();
        await b.StartAsync();
        await c.StartAsync();

        // before the group has a key a login stays in the instance's own store, as it always did
        Assert.Equal(HttpStatusCode.OK, (await a.CallAsync(HttpMethod.Put, SecretPath(Earlier), ServerRole.Admin, new { value = EarlierValue })).Status);
        var (_, status) = await a.GetAsync("/api/v1/group/key");
        Assert.False((bool)status!["held"]!);
        Assert.False((bool)status["group_has_key"]!);

        // the console joins A and B: each is handed the group's key
        var key = GroupCipher.NewKey();
        var (joinedA, answerA) = await a.CallAsync(HttpMethod.Put, "/api/v1/group/key", ServerRole.Admin, new { key });
        Assert.Equal(HttpStatusCode.OK, joinedA);
        Assert.False((bool)answerA!["already"]!);
        Assert.Equal(RunnerGroupSecrets.KeySecret, (string)answerA["secret"]!);
        Assert.Equal(HttpStatusCode.OK, (await b.CallAsync(HttpMethod.Put, "/api/v1/group/key", ServerRole.Admin, new { key })).Status);
        Assert.True((bool)(await a.CallAsync(HttpMethod.Put, "/api/v1/group/key", ServerRole.Admin, new { key })).Body!["already"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.CallAsync(HttpMethod.Put, "/api/v1/group/key", ServerRole.Operator, new { key })).Status);

        // a login sent to A
        Assert.Equal(HttpStatusCode.OK, (await a.CallAsync(HttpMethod.Put, SecretPath(Name), ServerRole.Admin, new { value = Value })).Status);

        // B's binding check resolves it: the login step passes and the connect step is tried (nothing listens at `fake`)
        var (_, onB) = await b.CallAsync(HttpMethod.Post, Check, ServerRole.Admin, new { slot = "source" });
        Assert.Equal("connect", (string)onB!["step"]!);
        Assert.DoesNotContain(Value, onB.ToJsonString());
        Assert.Contains(Name, (await b.GetAsync("/api/v1/secrets", ServerRole.Admin)).Body!["names"]!.AsArray().Select(n => (string)n!));

        // C was not joined: it knows the group has a key and that it does not hold it, its check says which key it lacks, and it cannot write
        var (_, onC) = await c.GetAsync("/api/v1/group/key");
        Assert.False((bool)onC!["held"]!);
        Assert.True((bool)onC["group_has_key"]!);
        var (_, checkC) = await c.CallAsync(HttpMethod.Post, Check, ServerRole.Admin, new { slot = "source" });
        Assert.False((bool)checkC!["ok"]!);
        Assert.Equal("login", (string)checkC["step"]!);
        Assert.Contains(RunnerGroupSecrets.KeySecret, (string)checkC["message"]!);
        var (writeC, refusedC) = await c.CallAsync(HttpMethod.Put, SecretPath(Name), ServerRole.Admin, new { value = "other" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, writeC);
        Assert.Contains(RunnerGroupSecrets.KeySecret, (string)refusedC!["error"]!);

        // a key that is not the group's is refused and nothing is kept
        var (wrong, wrongBody) = await c.CallAsync(HttpMethod.Put, "/api/v1/group/key", ServerRole.Admin, new { key = GroupCipher.NewKey() });
        Assert.Equal(HttpStatusCode.Conflict, wrong);
        Assert.Contains(RunnerGroupSecrets.KeySecret, (string)wrongBody!["error"]!);
        Assert.False((bool)(await c.GetAsync("/api/v1/group/key")).Body!["held"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.CallAsync(HttpMethod.Put, "/api/v1/group/key", ServerRole.Admin, new { key = "not-a-key" })).Status);

        // the group's key is not a secret that can be sent, and is never listed
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await a.CallAsync(HttpMethod.Put, SecretPath(RunnerGroupSecrets.KeySecret), ServerRole.Admin, new { value = GroupCipher.NewKey() })).Status);
        Assert.DoesNotContain(RunnerGroupSecrets.KeySecret, (await a.GetAsync("/api/v1/secrets", ServerRole.Admin)).Body!.ToJsonString());

        // what A held in its own store before still resolves on A; the group's login resolves through the chain's last link on B
        var storeA = new RunnerSecretStore(a.Directory);
        var onA = new RunnerGroupSecrets(a.State, () => storeA.Open(), storeA.Provider, () => storeA.Exists);
        using (var earlier = await onA.Provider.TryResolveAsync(new SecretRef(SecretRefKind.Secret, Earlier))) Assert.Equal(EarlierValue, earlier!.RevealString());
        var storeB = new RunnerSecretStore(b.Directory);
        var onBStore = new RunnerGroupSecrets(b.State, () => storeB.Open(), storeB.Provider, () => storeB.Exists);
        using (var login = await onBStore.Provider.TryResolveAsync(new SecretRef(SecretRefKind.Secret, Name))) Assert.Equal(Value, login!.RevealString());

        // the state holds the login encrypted, and the audit names who joined which instance, never a key or a value
        var sealedRows = a.State.Retry(() =>
        {
            using var connection = a.State.OpenConnection();
            using var cmd = a.State.Command(TrustModule.Name, connection, "SELECT value FROM {table:group_secret};");
            using var reader = cmd.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read()) rows.Add(reader.GetString(0));
            return rows;
        });
        Assert.Single(sealedRows);
        Assert.DoesNotContain(Value, sealedRows[0]);
        var joins = a.Audit.Read(new AuditQuery(VerbPrefix: RunnerGroupSecrets.JoinVerb));
        Assert.Equal(3, joins.Count(e => e.Entry.Outcome == AuditEvent.Ok));
        Assert.Contains(joins, e => e.Entry.Outcome == AuditEvent.Refused && e.Entry.TargetId!.Contains(c.Engine.InstanceId!.Value.ToString()));
        Assert.All(joins, e => Assert.Equal("ada", e.Entry.ActorId));
        var everything = string.Join('\n', a.Audit.Read(new AuditQuery()).Select(e => e.Entry.Detail + e.Entry.TargetId)) + string.Join('\n', a.Log) + string.Join('\n', b.Log) + string.Join('\n', c.Log);
        Assert.DoesNotContain(Value, everything);
        Assert.DoesNotContain(key, everything);
    }
}
