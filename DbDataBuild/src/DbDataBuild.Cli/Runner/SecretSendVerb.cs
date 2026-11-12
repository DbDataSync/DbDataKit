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

    /// <summary>The runner's check run of the binding the login serves (plan 28c E19): does the login resolve and the connection open, for this role.</summary>
    Task<RunnerPublishClient.BindingCheck> CheckAsync(string package, string channel, string slot, string role, CancellationToken ct);
}

/// <summary>The runner's secret API over the pairing the other runner verbs use (E20d's client, unchanged).</summary>
internal sealed class ClientRunnerSecrets(RunnerPublishClient client, string server, string? code) : IRunnerSecrets
{
    public Task<string> SendAsync(string name, string value, CancellationToken ct) => client.SendSecretAsync(server, code, name, value, null, ct);
    public Task<IReadOnlyList<string>> NamesAsync(string prefix, CancellationToken ct) => client.SecretNamesAsync(server, code, prefix, null, ct);
    public Task<RunnerPublishClient.BindingCheck> CheckAsync(string package, string channel, string slot, string role, CancellationToken ct) => client.TestBindingAsync(server, code, package, channel, slot, role, null, ct);
}

/// <summary>
/// <c>dbdatabuild secret send --to runner &lt;connection&gt; --server &lt;runner&gt; [--role write|read] [--delete-local]</c> (plan 28c E19, the Build side of E20d): a stored login of this server over the paired HTTPS to the runner's secret store,
/// under the name the server stores it by (<c>dbdatakit:build:connection:&lt;connection&gt;:&lt;role&gt;</c>), which is the name the runner's <c>connection/1</c> of that connection refers to. The role is <c>write</c> unless <c>--role read</c> says otherwise: a
/// scheduled refresh on the runner writes with the runner's write login, and the server keeps its read logins to plan, preview and report. The value goes into the request body only: it is not printed, logged, put in an error or an audit line.
/// <para>
/// <c>--check &lt;package&gt; --channel &lt;name&gt; [--slot &lt;slot&gt;]</c> asks the runner for its own check run of the binding after the send (the runner resolves the login for the role and opens the connection, as a run would: nothing is run and
/// nothing is changed); a check that fails is a failed send that keeps the local copy and says which step failed. <c>--delete-local</c> removes the write login from this server's store once the runner lists it among its secrets (a read login is never
/// deleted: the server needs it), and with <c>--check</c> only once the check has succeeded. After that the server's refresh has no write login to give. Without <c>--check</c> what is verified is that the runner holds the name; the first scheduled run
/// proves it connects (a missing or removed secret fails the run naming the slot, never falling back to the read login).
/// </para>
/// </summary>
internal static class SecretSendVerb
{
    public const string Usage = "send --to runner <connection> --server <name or https address> [--role write|read] [--delete-local] [--check <package> --channel <name> [--slot <slot>]] [--code <pairing code>] [--client-dir <folder>] [--repo <server directory>]";

    public static async Task<int> RunAsync(string[] args, CliContext context, EncryptedFileSecretStore store, Func<string, string?>? environment = null, IRunnerSecrets? runner = null, IAuditLog? audit = null)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "delete-local" }, new HashSet<string> { "to", "server", "role", "code", "client-dir", "repo", "check", "channel", "slot" });
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

        var checkPackage = parsed.Get("check");
        if (checkPackage is null && (parsed.Get("channel") is not null || parsed.Get("slot") is not null)) { await context.Error.WriteLineAsync("--channel and --slot belong to --check <package>: name the package the binding is of."); return CliExit.Usage; }
        if (checkPackage is not null && parsed.Get("channel") is not { Length: > 0 }) { await context.Error.WriteLineAsync("--check <package> needs the channel the binding is in: --channel <name>."); return CliExit.Usage; }
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
            string? checkedAs = null;
            if (checkPackage is not null)
            {
                // the runner opens the connection with the login it now holds, for the role sent: a login that does not connect is found here, while the local copy still exists
                var slot = parsed.Get("slot") ?? parsed.Words[1];
                var check = await runner.CheckAsync(checkPackage, parsed.Get("channel")!, slot, role.ToString().ToLowerInvariant(), context.Cancellation);
                if (!check.Ok)
                {
                    Audit($"sent, check failed at {check.Step}");
                    await context.Error.WriteLineAsync($"The runner's check of '{slot}' in {checkPackage}/{parsed.Get("channel")} failed at the {check.Step} step: {check.Message}");
                    await context.Error.WriteLineAsync(parsed.Has("delete-local") ? "The login was sent and the local copy was kept." : "The login was sent.");
                    context.Data["secret"] = stored;
                    context.Data["check"] = new { ok = false, step = check.Step, message = check.Message };
                    return CliExit.Failure;
                }

                checkedAs = check.Connected ? "connected" : check.Step;
                context.Data["check"] = new { ok = true, step = check.Step, message = check.Message, connected = check.Connected };
            }

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

            Audit((checkedAs is null ? "sent" : $"sent, checked ({checkedAs})") + (deleted ? ", local copy deleted" : ""));
            var roleText = role.ToString().ToLowerInvariant();
            await context.Out.WriteLineAsync($"Sent the {roleText} login of '{parsed.Words[1]}' to the runner as {stored}{(checkedAs is null ? "" : $"; the runner's check passed ({checkedAs})")}{(deleted ? "; the local copy was deleted" : "")}. The runner's connection file for '{parsed.Words[1]}' names it as `secret:{stored}`.");
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
