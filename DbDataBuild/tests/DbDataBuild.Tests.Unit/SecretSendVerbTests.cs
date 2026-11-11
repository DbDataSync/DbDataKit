using DbDataBuild.Cli.Runner;
using DbDataBuild.Cli.Server;
using DbDataKit.Cli;
using DbDataKit.Connections;
using DbDataKit.Runner.Client;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 28c E19: <c>secret send --to runner</c> against a runner that is faked (the real route is the Runner suite's <c>RunnerSecretsApiTests</c>).</summary>
public sealed class SecretSendVerbTests : IDisposable
{
    private const string Write = "Server=db;User Id=writer;Password=WRITE-pw-77f";
    private const string Read = "Server=db;User Id=reader;Password=READ-pw-1c9";
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-send-" + Guid.NewGuid().ToString("N")[..8]);

    private sealed class FakeRunner : IRunnerSecrets
    {
        public Dictionary<string, string> Held { get; } = [];
        public bool Lists { get; set; } = true;
        public bool Refuse { get; set; }
        public Task<string> SendAsync(string name, string value, CancellationToken ct)
        {
            if (Refuse) throw new RunnerClientException("The runner refused the call to store the secret: the pairing does not allow the admin role.");
            Held[name] = value;
            return Task.FromResult(name);
        }

        public Task<IReadOnlyList<string>> NamesAsync(string prefix, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Lists ? [.. Held.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))] : []);
    }

    private async Task<DbDataKit.Secrets.Store.EncryptedFileSecretStore> StoreAsync()
    {
        var store = ServerSecrets.Open(root);
        using (var w = SecretValue.From(Write)) await store.SetAsync(ServerSecrets.NameOf("warehouse", LoginRole.Write), w);
        using (var r = SecretValue.From(Read)) await store.SetAsync(ServerSecrets.NameOf("warehouse", LoginRole.Read), r);
        return store;
    }

    private static async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(DbDataKit.Secrets.Store.EncryptedFileSecretStore store, FakeRunner runner, FileAuditLog? audit, params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await SecretSendVerb.RunAsync(args, context, store, null, runner, audit);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    [Fact]
    public async Task The_write_login_goes_to_the_runner_under_its_own_name_and_the_value_is_in_no_output_or_audit_line()
    {
        var store = await StoreAsync();
        var runner = new FakeRunner();
        var audit = new FileAuditLog(Path.Combine(root, "management-audit.log"));
        var result = await Run(store, runner, audit, "send", "--to", "runner", "warehouse", "--server", "r");
        Assert.True(result.Exit == 0, result.Err);
        Assert.Equal(Write, runner.Held["dbdata:build:connection:warehouse:write"]);
        Assert.Single(runner.Held);                        // the read login stayed
        Assert.Equal(false, result.Data["local_deleted"]);
        Assert.True(await store.ExistsAsync(ServerSecrets.NameOf("warehouse", LoginRole.Write)));
        Assert.DoesNotContain("WRITE-pw", result.Out + result.Err);
        var line = Assert.Single(audit.Read());
        Assert.Equal("secret.send", line.Command);
        Assert.Equal("dbdata:build:connection:warehouse:write", line.Target);
        Assert.DoesNotContain("WRITE-pw", File.ReadAllText(Path.Combine(root, "management-audit.log")));
    }

    [Fact]
    public async Task Delete_local_removes_the_write_login_once_the_runner_lists_it_and_never_a_read_login()
    {
        var store = await StoreAsync();
        var runner = new FakeRunner();
        var moved = await Run(store, runner, null, "send", "--to", "runner", "warehouse", "--server", "r", "--delete-local");
        Assert.True(moved.Exit == 0, moved.Err);
        Assert.False(await store.ExistsAsync(ServerSecrets.NameOf("warehouse", LoginRole.Write)));
        Assert.True(await store.ExistsAsync(ServerSecrets.NameOf("warehouse", LoginRole.Read)));
        // what a child process of the server is given afterwards: the read login only
        var logins = await ServerSecrets.LoginsAsync(store, CancellationToken.None);
        Assert.Equal([LoginEnvironment.Name("build", "warehouse", LoginRole.Read)], logins.Keys);

        var read = await Run(store, runner, null, "send", "--to", "runner", "warehouse", "--server", "r", "--role", "read", "--delete-local");
        Assert.Equal(CliExit.Usage, read.Exit);
        Assert.True(await store.ExistsAsync(ServerSecrets.NameOf("warehouse", LoginRole.Read)));
    }

    [Fact]
    public async Task A_runner_that_does_not_list_the_name_or_refuses_leaves_the_local_login_alone()
    {
        var store = await StoreAsync();
        var silent = await Run(store, new FakeRunner { Lists = false }, null, "send", "--to", "runner", "warehouse", "--server", "r", "--delete-local");
        Assert.Equal(CliExit.Failure, silent.Exit);
        Assert.Contains("kept", silent.Err);
        Assert.True(await store.ExistsAsync(ServerSecrets.NameOf("warehouse", LoginRole.Write)));

        var refused = await Run(store, new FakeRunner { Refuse = true }, null, "send", "--to", "runner", "warehouse", "--server", "r", "--delete-local");
        Assert.Equal(CliExit.Failure, refused.Exit);
        Assert.Contains("admin role", refused.Err);
        Assert.True(await store.ExistsAsync(ServerSecrets.NameOf("warehouse", LoginRole.Write)));
    }

    [Fact]
    public async Task A_login_that_is_not_stored_and_a_wrong_use_send_nothing()
    {
        var store = ServerSecrets.Open(root);
        var runner = new FakeRunner();
        var missing = await Run(store, runner, null, "send", "--to", "runner", "warehouse", "--server", "r");
        Assert.Equal(CliExit.Failure, missing.Exit);
        Assert.Contains("is not set", missing.Err);
        Assert.Equal(CliExit.Usage, (await Run(store, runner, null, "send", "warehouse", "--server", "r")).Exit);
        Assert.Equal(CliExit.Usage, (await Run(store, runner, null, "send", "--to", "runner", "warehouse")).Exit);
        Assert.Equal(CliExit.Usage, (await Run(store, runner, null, "send", "--to", "runner", "warehouse", "--server", "r", "--role", "both")).Exit);
        Assert.Empty(runner.Held);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
}
