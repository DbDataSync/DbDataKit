using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataKit.Connections;
using DbDataKit.Secrets;

namespace DbDataBuild.Tests.Unit;

/// <summary>The Build server's secrets (plan 13, R04): the logins stored for the server reach each command as the variables the command line reads, one role at a time, never one in place of the other.</summary>
public sealed class BuildServerSecretsTests : IDisposable
{
    private readonly string serverDir = Path.Combine(Path.GetTempPath(), "ddb-secrets-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project = Path.Combine(Path.GetTempPath(), "ddb-secrets-p-" + Guid.NewGuid().ToString("N")[..8]);

    public BuildServerSecretsTests()
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", project, "--format", "json"], new StringWriter(), new StringWriter()));
    }

    public void Dispose()
    {
        foreach (var d in new[] { serverDir, project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private async Task Store(string connection, LoginRole role, string value) =>
        await ServerSecrets.Open(serverDir).SetAsync(ServerSecrets.NameOf(connection, role), SecretValue.From(value));

    [Fact]
    public async Task Each_stored_login_becomes_its_own_variable_and_nothing_else_does()
    {
        await Store("sqlserver", LoginRole.Read, "r1");
        await Store("my-dw", LoginRole.Write, "w1");
        await ServerSecrets.Open(serverDir).SetAsync(new SecretName("build", "connection", "sqlserver", "note"), SecretValue.From("not a login"));
        await ServerSecrets.Open(serverDir).SetAsync(new SecretName("build", "other", "x", "read"), SecretValue.From("another kind"));

        var logins = await ServerSecrets.LoginsAsync(ServerSecrets.Open(serverDir), CancellationToken.None);

        Assert.Equal(["DBDATA_BUILD_MY_DW_WRITE=w1", "DBDATA_BUILD_SQLSERVER_READ=r1"], logins.Select(l => $"{l.Key}={l.Value}").Order().ToList());
    }

    [Fact]
    public async Task A_server_with_no_secrets_hands_over_no_logins()
    {
        Assert.Empty(await ServerSecrets.LoginsAsync(ServerSecrets.Open(serverDir), CancellationToken.None));
    }

    private async Task<JsonNode> Status(Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> logins)
    {
        Assert.Null(Environment.GetEnvironmentVariable("DBDATA_BUILD_SQLSERVER_READ"));    // a developer's own login would hide what this tests
        var result = await new ChildProcessCommandRunner(logins: logins).RunAsync(["connection", "status", "--project", project], project, CancellationToken.None);
        return JsonNode.Parse(result.Out)!;
    }

    private static bool MissingRead(JsonNode document) =>
        document["diagnostics"]!.AsArray().Any(d => (string)d!["code"]! == "DDB-501" && ((string)d["found"]!).Contains("DBDATA_BUILD_SQLSERVER_READ"));

    [Fact]
    public async Task A_command_logs_in_with_the_stored_secret()
    {
        var store = ServerSecrets.Open(serverDir);
        Assert.True(MissingRead(await Status(token => ServerSecrets.LoginsAsync(store, token))));

        await Store("sqlserver", LoginRole.Read, "not-a-connection-string");

        // the login is found now; whatever the command makes of it, it no longer says it has none
        Assert.False(MissingRead(await Status(token => ServerSecrets.LoginsAsync(store, token))));
    }

    [Fact]
    public async Task A_write_login_never_stands_in_for_a_read_login()
    {
        await Store("sqlserver", LoginRole.Write, "not-a-connection-string");
        var store = ServerSecrets.Open(serverDir);

        Assert.True(MissingRead(await Status(token => ServerSecrets.LoginsAsync(store, token))));
    }

    [Fact]
    public async Task The_secret_verbs_work_on_the_servers_store_and_print_no_value()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var stdin = new StringReader("s3cret-value\n");

        Assert.Equal(0, await ServerCli.RunAsync(["secret", "set", ServerSecrets.NameOf("sqlserver", LoginRole.Read).ToString(), "--stdin", "--repo", serverDir], o, e, stdin));
        Assert.Equal(0, await ServerCli.RunAsync(["secret", "list", "--repo", serverDir], o, e, TextReader.Null));

        Assert.Contains("dbdata:build:connection:sqlserver:read", o.ToString());
        Assert.DoesNotContain("s3cret-value", o.ToString() + e);
        Assert.Equal("s3cret-value", (await ServerSecrets.LoginsAsync(ServerSecrets.Open(serverDir), CancellationToken.None))["DBDATA_BUILD_SQLSERVER_READ"]);
    }
}
