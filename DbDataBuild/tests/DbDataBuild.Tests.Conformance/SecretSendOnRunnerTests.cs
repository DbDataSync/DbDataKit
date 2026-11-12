using DbDataBuild.Cli.Runner;
using DbDataBuild.Cli.Server;
using DbDataKit.Cli;
using DbDataKit.Connections;
using DbDataKit.Runner.Client;
using DbDataKit.Secrets;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Plan 28c E19, the rest of it, against a real runner (in process, its own server and secret store, the real paired HTTPS) and a real engine: <c>dbdatabuild secret send --to runner --check</c> sends the server's write login, and the runner's own
/// check run resolves it for the role sent and opens a connection with it; the local copy is deleted only after that. A runner whose secret is gone answers the same check naming the slot, and a read request is never served with the write login.
/// </summary>
public class SecretSendOnRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ddb-e19-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* temp */ }
    }

    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    /// <summary>The connection file the runner keeps for `wh`: its write login is the secret the verb sends, its read login a secret nobody sends.</summary>
    private static string ConnectionFile(string engine) =>
        $"schema: connection/1\nname: wh\nengine: {engine}\ntls:\n  encrypt: false\n  trust_server_certificate: true\n" +
        "read_login: { connection_string_secret: 'secret:dbdatakit:build:connection:wh:read' }\n" +
        "write_login: { connection_string_secret: 'secret:dbdatakit:build:connection:wh:write' }\n";

    private static async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> SendAsync(ProjectPublishOnRunnerTests.Runner runner, string client, DbDataKit.Secrets.Store.EncryptedFileSecretStore store, params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await SecretSendVerb.RunAsync(["send", "--to", "runner", "wh", "--server", runner.Url, "--code", runner.Code(), "--client-dir", client, .. args], context, store);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_runners_check_connects_with_the_login_that_was_sent_and_only_then_is_the_local_copy_deleted_and_a_removed_secret_fails_naming_the_slot(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var project = ProjectPublishOnRunnerTests.Project();
        var client = Path.Combine(project, "client");
        await using var runner = await ProjectPublishOnRunnerTests.Runner.StartAsync();
        Directory.CreateDirectory(Path.Combine(runner.Settings.Directory, "connections"));
        File.WriteAllText(Path.Combine(runner.Settings.Directory, "connections", "wh.yaml"), ConnectionFile(name));
        var version = ProjectPublishOnRunnerTests.Data(ProjectPublishOnRunnerTests.Publish(project, client, "--server", runner.Url, "--code", runner.Code(), "--channel", "dev", "--bind", "sqlserver=wh"))["version"]!.GetValue<int>();
        await runner.WaitActiveAsync("sales", "dev", version);

        // the Build server's store holds the write login (and a read login, which stays)
        var store = ServerSecrets.Open(_root);
        using (var w = SecretValue.From(engine.ConnectionString)) await store.SetAsync(ServerSecrets.NameOf("wh", LoginRole.Write), w);
        using (var r = SecretValue.From(engine.ConnectionString)) await store.SetAsync(ServerSecrets.NameOf("wh", LoginRole.Read), r);

        // before anything is sent the runner has no login to open: the check says so, naming the slot (nothing was sent by a failed check, so this is the runner alone)
        var link = new RunnerPublishClient(new RunnerClientStore(client), _ => null);
        var before = await link.TestBindingAsync(runner.Url, null, "sales", "dev", "sqlserver", "write");
        Assert.False(before.Ok);
        Assert.Contains("'sqlserver'", before.Message);

        // sent with a check: the runner resolves the write login and opens a connection, then the local copy goes
        var sent = await SendAsync(runner, client, store, "--check", "sales", "--channel", "dev", "--slot", "sqlserver", "--delete-local");
        Assert.True(sent.Exit == 0, sent.Out + sent.Err);
        Assert.Contains("the runner's check passed (connected)", sent.Out);
        Assert.False(await store.ExistsAsync(ServerSecrets.NameOf("wh", LoginRole.Write)));
        Assert.True(await store.ExistsAsync(ServerSecrets.NameOf("wh", LoginRole.Read)));
        Assert.DoesNotContain(engine.ConnectionString, sent.Out + sent.Err);
        Assert.Contains("dbdatakit:build:connection:wh:write", await link.SecretNamesAsync(runner.Url, null, "dbdatakit:build:connection:wh:"));

        // the runner's own answer, asked again: connected, with the write login
        var again = await link.TestBindingAsync(runner.Url, null, "sales", "dev", "sqlserver", "write");
        Assert.True(again.Ok, again.Message);
        Assert.True(again.Connected);

        // a read request is never served with the write login: the read secret was never sent, so it fails naming the slot (and the role), not by using the other role's login
        var read = await link.TestBindingAsync(runner.Url, null, "sales", "dev", "sqlserver", "read");
        Assert.False(read.Ok);
        Assert.Contains("'sqlserver'", read.Message);

        // the secret is removed on the runner: the same check fails naming the slot, never falling back to the read login
        await link.RemoveSecretAsync(runner.Url, null, "dbdatakit:build:connection:wh:write");
        var gone = await link.TestBindingAsync(runner.Url, null, "sales", "dev", "sqlserver", "write");
        Assert.False(gone.Ok);
        Assert.False(gone.Connected);
        Assert.Contains("'sqlserver'", gone.Message);
        Assert.DoesNotContain(engine.ConnectionString, gone.Message);

        // a send whose check fails keeps the local copy: the login is sent again with a value that does not connect
        using (var wrong = SecretValue.From("Server=127.0.0.1,1;Database=nope;User Id=nobody;Password=wrong;Connect Timeout=2")) await store.SetAsync(ServerSecrets.NameOf("wh", LoginRole.Write), wrong);
        var failed = await SendAsync(runner, client, store, "--check", "sales", "--channel", "dev", "--slot", "sqlserver", "--delete-local");
        Assert.NotEqual(0, failed.Exit);
        Assert.Contains("failed at the", failed.Err);
        Assert.Contains("the local copy was kept", failed.Err);
        Assert.True(await store.ExistsAsync(ServerSecrets.NameOf("wh", LoginRole.Write)));
        Assert.DoesNotContain("Password=wrong", failed.Out + failed.Err);
    }
}
