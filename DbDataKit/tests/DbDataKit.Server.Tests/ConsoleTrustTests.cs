using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The console's publisher key in the trust store of the groups it manages: a Runner (the real engine and publish API in this process,
/// <see cref="PairedRunner"/>) and a Gateway (a server answering the shared trust calls for two serving kinds) trust the key once the console has made its
/// pass over them, and a version the console signs with it verifies on the Runner with signatures required. The real programs joined by their printed
/// commands are <c>JoinByTokenTests</c> in the Servers suite.
/// </summary>
public sealed class ConsoleTrustTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-trust-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;

    public async ValueTask InitializeAsync()
    {
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private T Get<T>() where T : notnull => app.Services.GetRequiredService<T>();

    private const string Manifest = "schema: package/1\nname: sample\nlaunch: { kind: assembly, assembly: DbDataKit.Runner.TestPackage.dll }\n"
        + "built_against: { dotnet: \"10.0\", kit: \"1\", protocol: dbdatakit.runner.v1 }\ntasks:\n  - name: ok\n    entry: { type: T }\n";

    private static readonly byte[] Dll = Encoding.UTF8.GetBytes("a stand-in assembly");

    [Fact]
    public async Task The_console_makes_its_publisher_key_once_keeps_it_in_its_secret_store_and_never_uses_its_token_key_for_it()
    {
        var keys = Get<ConsolePublisherKeyStore>();
        var first = await keys.GetOrCreateAsync();
        Assert.Equal(first.Fingerprint, (await keys.GetOrCreateAsync()).Fingerprint);
        // another process on the same store reads the same key
        var store = Get<EncryptedFileSecretStore>();
        Assert.Equal(first.Fingerprint, (await new ConsolePublisherKeyStore(store, store).GetOrCreateAsync()).Fingerprint);
        Assert.NotEqual((await Get<ConsoleKeyStore>().GetOrCreateAsync()).Key.Fingerprint, first.Fingerprint);
        var seed = Url.Encode(first.ExportSeed());
        Assert.DoesNotContain(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories), file => new FileInfo(file).Length < 4_000_000 && ReadShared(file).Contains(seed, StringComparison.Ordinal));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task A_runner_the_console_manages_trusts_its_key_and_a_version_the_console_signs_verifies_there()
    {
        await using var runner = await PairedRunner.StartAsync();
        await runner.PairAsync(app);
        var instance = Get<InstanceRegistry>().All().Single();
        var key = await Get<ConsolePublisherKeyStore>().GetOrCreateAsync();
        var consoleId = (await Get<ConsoleKeyStore>().GetOrCreateAsync()).Id;

        var first = await Get<ConsoleTrust>().EnsureAsync(instance);
        Assert.True(first.Done, first.Problem);
        Assert.Equal(["integration"], first.Added);
        var trusted = runner.Publishing.Trust.Find(RunnerPublishing.Kind, key.Fingerprint);
        Assert.NotNull(trusted);
        Assert.Equal(ConsoleTrust.LabelFor(consoleId), trusted.Label);
        Assert.Equal(consoleId, trusted.AddedBy);   // the console added it as itself, not as a person
        Assert.Contains(Get<AuditLog>().Read(new AuditQuery(VerbPrefix: ConsoleTrust.TrustVerb)), e => e.Entry.Outcome == AuditEvent.Ok && e.Entry.TargetId == instance.InstanceId);

        // a second pass finds it there and adds nothing
        var second = await Get<ConsoleTrust>().EnsureAsync(instance);
        Assert.True(second.Done);
        Assert.Empty(second.Added);
        Assert.Equal(["integration"], second.Already);

        // with signatures required, a version the console signs is accepted as verified, and an instance preparing it verifies it again
        runner.Publishing.SetRequireSignatures(true, new RunnerActor("admin", "Admin", "test", null));
        var relay = Get<ConsoleRelay>();
        var person = Get<IUserAccounts>().CreateUser("Ana", null, UserRole.Admin);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Dll));
        using (var upload = await relay.SendAsAsync(instance, person.Id, person.DisplayName, UserRole.Admin, HttpMethod.Put, $"api/v1/artifacts/{sha}", new ByteArrayContent(Dll), default))
            Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());
        var files = new[] { new ArtifactRef("DbDataKit.Runner.TestPackage.dll", sha, Dll.Length, null) };
        var body = new
        {
            manifest = Manifest, channel = "dev", mode = "serve",
            artifacts = new[] { new { path = "DbDataKit.Runner.TestPackage.dll", sha256 = sha, size = Dll.Length } },
            signature = DefinitionSigning.Sign(key, RunnerPublishing.Kind, "sample", Manifest, files),
        };
        using var publish = await relay.SendAsAsync(instance, person.Id, person.DisplayName, UserRole.Admin, HttpMethod.Put, "api/v1/definitions/integration/sample", JsonContent.Create(body), default);
        var answer = JsonNode.Parse(await publish.Content.ReadAsStringAsync());
        Assert.True(publish.IsSuccessStatusCode, answer?.ToJsonString());
        Assert.Equal("verified", (string?)answer!["signature"]);
        var stored = new DefinitionStore(runner.State).Get(RunnerPublishing.Kind, "sample", 1)!;
        Assert.Equal(key.Fingerprint, stored.Signer);
        Assert.Null(RunnerVersionGate.Create(runner.State)(stored));

        // the same version signed with another key is refused: the group trusts the console's key, not any key
        using var forged = await relay.SendAsAsync(instance, person.Id, person.DisplayName, UserRole.Admin, HttpMethod.Put, "api/v1/definitions/integration/other",
            JsonContent.Create(new { body.manifest, body.channel, body.mode, body.artifacts, signature = DefinitionSigning.Sign(ConsoleSigningKey.Generate(), RunnerPublishing.Kind, "other", Manifest, files) }), default);
        Assert.False(forged.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_key_an_administrator_revoked_is_not_added_again()
    {
        await using var runner = await PairedRunner.StartAsync();
        await runner.PairAsync(app);
        var instance = Get<InstanceRegistry>().All().Single();
        var key = await Get<ConsolePublisherKeyStore>().GetOrCreateAsync();
        Assert.True((await Get<ConsoleTrust>().EnsureAsync(instance)).Done);
        Assert.True(runner.Publishing.Trust.Revoke(RunnerPublishing.Kind, key.Fingerprint, "admin"));

        var again = await Get<ConsoleTrust>().EnsureAsync(instance);
        Assert.True(again.Done);
        Assert.Empty(again.Added);
        Assert.Equal(["integration"], again.Revoked);
        Assert.True(runner.Publishing.Trust.Find(RunnerPublishing.Kind, key.Fingerprint)!.Revoked);
    }

    /// <summary>A Gateway's trust calls (the shared publish API's, for two serving kinds), behind the token check of a real server.</summary>
    private static Action<WebApplication, PairingCodes, PairedClientsFile> GatewayTrust(StateDatabase state) => (server, _, _) =>
    {
        var kinds = new DefinitionKinds()
            .Register(new DefinitionKind("semantic-model", ["serving"], PublishMode.Serve))
            .Register(new DefinitionKind("dashboard", ["serving"], PublishMode.Serve));
        var payloads = new PayloadStore(state);
        var trust = new DefinitionTrustStore(state);
        var api = new DefinitionsApi(new DefinitionPublisher(state, kinds, payloads, "serving"), new DefinitionStore(state), kinds, payloads, new GroupSettings(state), "serving",
            verifier: new TrustStoreVerifier(trust), policy: new SignaturePolicy(new GroupSettings(state)), trust: trust);
        server.MapMethods("/api/v1/{**route}", ["GET", "PUT", "POST"], async (string route, HttpContext context) =>
        {
            var token = context.ManagementToken();
            var roles = ManagementAuthorization.Effective(token);
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var answer = api.Handle(context.Request.Method, route, body.Length == 0 ? null : body,
                new DefinitionsCaller(token.SubjectName ?? token.SubjectId, ServerRoles.Allows(roles, ServerRole.Admin), ServerRoles.Allows(roles, ServerRole.Publisher), ServerRoles.Allows(roles, ServerRole.Viewer), token.Client.Id));
            return Results.Json(answer.Body, statusCode: answer.Status);
        });
    };

    [Fact]
    public async Task A_gateway_trusts_the_key_for_every_kind_it_lists_and_a_console_without_the_admin_role_there_is_told_why()
    {
        var file = Path.Combine(directory, "gateway-state.db");
        Directory.CreateDirectory(directory);
        var state = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, TrustModule.Module], new StateScope(Group: "main")));
        await using var gateway = await PairableServer.StartAsync(product: ServerProducts.Gateway, instanceId: "gateway-1", map: GatewayTrust(state));
        await Get<ConsoleInstances>().PairAsync(gateway.Url, gateway.NewCodeWith(ServerRole.Admin), "Gateway one", default);
        var instance = Get<InstanceRegistry>().All().Single();
        var key = await Get<ConsolePublisherKeyStore>().GetOrCreateAsync();

        var outcome = await Get<ConsoleTrust>().EnsureAsync(instance);
        Assert.True(outcome.Done, outcome.Problem);
        Assert.Equal(["dashboard", "semantic-model"], outcome.Added);
        Assert.All(new[] { "dashboard", "semantic-model" }, kind => Assert.NotNull(new DefinitionTrustStore(state).Find(kind, key.Fingerprint)));

        // a console paired for reading only with another group cannot trust its key there: the pass says why, and the console's audit has the refusal
        var otherState = new StateDatabase(Path.Combine(directory, "gateway-2-state.db"), StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, TrustModule.Module], new StateScope(Group: "other")));
        await using var readOnly = await PairableServer.StartAsync(product: ServerProducts.Gateway, instanceId: "gateway-2", map: GatewayTrust(otherState));
        await Get<ConsoleInstances>().PairAsync(readOnly.Url, readOnly.NewCode(), "Gateway two", default);
        var second = Get<InstanceRegistry>().All().Single(i => i.InstanceId == "gateway-2");
        var refused = await Get<ConsoleTrust>().EnsureAsync(second);
        Assert.False(refused.Done);
        Assert.Contains("403", refused.Problem);
        Assert.Contains(Get<AuditLog>().Read(new AuditQuery(VerbPrefix: ConsoleTrust.TrustVerb)), e => e.Entry.Outcome == AuditEvent.Refused && e.Entry.TargetId == "gateway-2");
    }

    [Fact]
    public async Task The_pass_leaves_a_workspace_alone_and_keeps_a_runner_that_does_not_answer_due_again()
    {
        await using var workspace = await PairableServer.StartAsync(product: ServerProducts.Workspace, instanceId: "workspace-1");
        await Get<ConsoleInstances>().PairAsync(workspace.Url, workspace.NewCodeWith(ServerRole.Admin), "Workspace", default);
        var runner = await PairedRunner.StartAsync();
        await using var _ = runner;
        await runner.PairAsync(app);
        await runner.Server.StopAsync();

        var service = Get<ConsoleTrustService>();
        var next = await service.PassAsync(default);
        Assert.NotNull(next);   // the runner did not answer: it is due again
        Assert.Empty(runner.Publishing.Trust.List(RunnerPublishing.Kind));
    }
}
