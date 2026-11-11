using System.Net;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Client;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E20d over the management API of a real server: a product sends the login a replication runs with (<c>PUT /api/v1/secrets/{name}</c>, administrator); the runner keeps it in its encrypted store, the connection's
/// reference resolves to it as the last provider of the chain, and the value is in no file, no audit line and no answer. It needs no engine.
/// </summary>
public sealed class RunnerSecretsApiTests
{
    private const string Name = "dbdata:sync:connection:crm-src:run";
    private const string Value = "p4ss-w0rd-that-must-not-leak";

    private static SecretRef Ref => new(SecretRefKind.Secret, Name);

    [Fact]
    public async Task An_administrator_stores_a_login_that_the_runner_resolves_and_that_is_in_no_file_in_the_clear_and_no_audit_line()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();

        var (status, body) = await h.CallAsync(HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(Name)}", ServerRole.Admin, new { value = Value });
        var (listStatus, list) = await h.GetAsync("/api/v1/secrets?prefix=dbdata:sync", ServerRole.Admin);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Name, (string)body!["name"]!);
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal([Name], list!["names"]!.AsArray().Select(n => (string)n!));
        Assert.DoesNotContain(Value, list.ToJsonString());
        var resolved = await new RunnerSecretStore(h.Directory).Provider.TryResolveAsync(Ref);
        Assert.Equal(Value, resolved!.RevealString());
        foreach (var file in Directory.EnumerateFiles(h.Directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                Assert.DoesNotContain(Value, System.Text.Encoding.Latin1.GetString(memory.ToArray()));
            }
            catch (IOException) { /* a file the server holds open exclusively */ }
        }

        var audit = h.Audit.Read(new AuditQuery(VerbPrefix: RunnerSecretsApi.SetVerb)).Single();
        Assert.Equal((Name, AuditEvent.Ok), (audit.Entry.TargetId, audit.Entry.Outcome));
        Assert.DoesNotContain(Value, audit.Entry.Detail ?? "");
    }

    [Fact]
    public async Task Only_an_administrator_stores_lists_or_removes_and_a_refusal_is_audited_and_stores_nothing()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        var path = $"/api/v1/secrets/{Uri.EscapeDataString(Name)}";

        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Put, path, ServerRole.Operator, new { value = Value })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.GetAsync("/api/v1/secrets", ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Delete, path, ServerRole.Operator)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.CallAsync(HttpMethod.Put, path, ServerRole.Admin, new { value = Value }, token: false)).Status);

        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: RunnerSecretsApi.SetVerb)), e => e.Entry.Outcome == AuditEvent.Refused);
        Assert.Null(await new RunnerSecretStore(h.Directory).Provider.TryResolveAsync(Ref));
    }

    [Fact]
    public async Task A_bad_name_or_value_is_refused_a_removed_secret_no_longer_resolves_and_the_paired_client_sends_it()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, "/api/v1/secrets/not-a-name", ServerRole.Admin, new { value = Value })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(Name)}", ServerRole.Admin, new { value = "" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(Name)}", ServerRole.Admin, new { other = 1 })).Status);

        var directory = Directory.CreateTempSubdirectory("dbdr-secret-client-").FullName;
        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(directory), _ => null);
            var server = h.Url.ToString();
            var code = h.NewCode(ServerRole.Admin);

            var stored = await client.SendSecretAsync(server, code, Name, Value);
            var names = await client.SecretNamesAsync(server, null);
            var provider = new RunnerSecretStore(h.Directory).Provider;
            var found = await provider.TryResolveAsync(Ref);
            await client.RemoveSecretAsync(server, null, Name);
            var gone = await provider.TryResolveAsync(Ref);

            Assert.Equal(Name, stored);
            Assert.Equal([Name], names);
            Assert.Equal(Value, found!.RevealString());
            Assert.Null(gone);
            var refused = await Assert.ThrowsAsync<RunnerClientException>(() => client.SendSecretAsync(server, null, "nonsense", Value));
            Assert.DoesNotContain(Value, refused.Message);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* temp */ }
        }
    }
}
