using DbDataKit.Cli;
using DbDataKit.Connections;
using DbDataKit.Runner.Client;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;
using DbDataBuild.Cli.Server;

namespace DbDataBuild.Cli.Runner;

/// <summary>What the secret verb needs of a runner: store a login, list names. Faked in the unit tests; <see cref="ClientRunnerSecrets"/> is the paired HTTPS.</summary>
internal interface IRunnerSecrets
{
    Task<string> SendAsync(string name, string value, CancellationToken ct);
    Task<IReadOnlyList<string>> NamesAsync(string prefix, CancellationToken ct);
}

/// <summary>The runner's secret API over the pairing the other runner verbs use (E20d's client, unchanged).</summary>
internal sealed class ClientRunnerSecrets(RunnerPublishClient client, string server, string? code) : IRunnerSecrets
{
    public Task<string> SendAsync(string name, string value, CancellationToken ct) => client.SendSecretAsync(server, code, name, value, null, ct);
    public Task<IReadOnlyList<string>> NamesAsync(string prefix, CancellationToken ct) => client.SecretNamesAsync(server, code, prefix, null, ct);
}

/// <summary>
/// <c>dbdatabuild secret send --to runner &lt;connection&gt; --server &lt;runner&gt; [--role write|read] [--delete-local]</c> (plan 28c E19, the Build side of E20d): a stored login of this server over the paired HTTPS to the runner's secret store,
/// under the name the server stores it by (<c>dbdatakit:build:connection:&lt;connection&gt;:&lt;role&gt;</c>), which is the name the runner's <c>connection/1</c> of that connection refers to. The role is <c>write</c> unless <c>--role read</c> says otherwise: a
/// scheduled refresh on the runner writes with the runner's write login, and the server keeps its read logins to plan, preview and report. The value goes into the request body only: it is not printed, logged, put in an error or an audit line.
/// <para>
/// <c>--delete-local</c> removes the write login from this server's store once the runner lists it among its secrets (a read login is never deleted: the server needs it). After that the server's refresh has no write login to give.
/// The verb runs no check task: what is verified is that the runner holds the name; the first scheduled run proves it connects (a missing or removed secret fails the run naming the slot, never falling back to the read login).
/// </para>
/// </summary>
internal static class SecretSendVerb
{
    public const string Usage = "send --to runner <connection> --server <name or https address> [--role write|read] [--delete-local] [--code <pairing code>] [--client-dir <folder>] [--repo <server directory>]";

    public static async Task<int> RunAsync(string[] args, CliContext context, EncryptedFileSecretStore store, Func<string, string?>? environment = null, IRunnerSecrets? runner = null, IAuditLog? audit = null)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "delete-local" }, new HashSet<string> { "to", "server", "role", "code", "client-dir", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 2 || parsed.Words[0] != "send")
        {
            await context.Error.WriteLineAsync($"Usage: secret {Usage}");
            return CliExit.Usage;
        }

        if (parsed.Get("to") != "runner") { await context.Error.WriteLineAsync("--to is `runner`, the only place a login is sent to."); return CliExit.Usage; }
        if (parsed.Get("server") is not { } server) { await context.Error.WriteLineAsync("Name the runner: --server <name or https address> [--code <pairing code>]."); return CliExit.Usage; }
        LoginRole role;
        switch (parsed.Get("role") ?? "write")
        {
            case "write": role = LoginRole.Write; break;
            case "read": role = LoginRole.Read; break;
            case var other: await context.Error.WriteLineAsync($"--role is `write` or `read`, not `{other}`."); return CliExit.Usage;
        }

        if (role == LoginRole.Read && parsed.Has("delete-local")) { await context.Error.WriteLineAsync("--delete-local is for the write login: this server keeps its read logins, so one is never deleted."); return CliExit.Usage; }

        SecretName name;
        try { name = ServerSecrets.NameOf(parsed.Words[1], role); }
        catch (FormatException ex) { await context.Error.WriteLineAsync(ex.Message); return CliExit.Usage; }
        using var value = await store.TryResolveAsync(name.ToRef(), context.Cancellation);
        if (value is null || value.IsEmpty)
        {
            await context.Error.WriteLineAsync($"The secret '{name}' is not set in this server's store: nothing was sent.");
            return CliExit.Failure;
        }

        var env = environment ?? Environment.GetEnvironmentVariable;
        runner ??= new ClientRunnerSecrets(new RunnerPublishClient(new RunnerClientStore(parsed.Get("client-dir") ?? RunnerClientStore.DefaultDirectory(env)), env), server, parsed.Get("code"));
        void Audit(string outcome) => audit?.Append(new AuditEntry(DateTimeOffset.UtcNow, "command", "dbdatabuild secret send", "local:" + Environment.UserName, Environment.UserName, false, "secret.send", name.ToString(), outcome, ""));
        try
        {
            var stored = await runner.SendAsync(name.ToString(), value.RevealString(), context.Cancellation);
            var deleted = false;
            if (parsed.Has("delete-local"))
            {
                // only a name the runner lists is taken as received; the value is never read back
                if (!(await runner.NamesAsync(stored, context.Cancellation)).Contains(stored, StringComparer.Ordinal))
                {
                    await context.Error.WriteLineAsync($"The runner did not list '{stored}' after the send, so the local copy was kept.");
                    Audit("sent, not confirmed");
                    return CliExit.Failure;
                }

                await store.DeleteAsync(name, context.Cancellation);
                deleted = true;
            }

            Audit(deleted ? "sent, local copy deleted" : "sent");
            var roleText = role.ToString().ToLowerInvariant();
            await context.Out.WriteLineAsync($"Sent the {roleText} login of '{parsed.Words[1]}' to the runner as {stored}{(deleted ? "; the local copy was deleted" : "")}. The runner's connection file for '{parsed.Words[1]}' names it as `secret:{stored}`.");
            context.Data["secret"] = stored;
            context.Data["role"] = roleText;
            context.Data["local_deleted"] = deleted;
            return CliExit.Ok;
        }
        catch (RunnerClientException ex)
        {
            Audit("failed");
            await context.Error.WriteLineAsync(ex.Message);
            await context.Error.WriteLineAsync("Nothing was sent. Storing a secret takes the admin role: pair with `dbdatarunner server pair --max-role admin`.");
            return CliExit.Failure;
        }
    }
}
