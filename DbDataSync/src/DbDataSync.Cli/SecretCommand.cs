using DbDataKit.Config.Git;
using DbDataKit.Runner.Client;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli;

/// <summary>
/// Thin wrappers over <see cref="SecretStore.Store"/>/<see cref="SecretStore.TryResolve"/>/
/// <see cref="SecretStore.Delete"/> — the same three calls <c>ConfigRepository.SaveConnection</c>
/// already makes for a connection's own credential, reachable here without going through a
/// connection's save flow. This is what "easily settable using the CLI tool" means concretely for
/// phase 79's one standardized ref:
/// <code>dbdatasync secret set dbdatakit:sync:config:stateConnectionString "Password=..."</code>
/// </summary>
public static class SecretCommand
{
    /// <summary>The one ref this build defines a fixed name for (see <see cref="SecretRefs.ForAppSetting"/>)
    /// — what <c>list</c> checks when given no ref of its own to look at.</summary>
    private static readonly string[] KnownRefs = [SecretRefs.ForAppSetting("stateConnectionString")];

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var root = DbDataSyncRoot.Resolve(args);
        var secrets = SecretStore.OpenInRepository(root);
        args = WithoutRepoOption(args);

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "set" => Set(args, secrets),
                "list" => List(args, secrets),
                "remove" => Remove(args, secrets),
                var other => Unknown(other),
            };
        }
        catch (FormatException ex)
        {
            // a name that is not dbdatakit:sync:<kind>:<name> (the old dbdatasync:... form among them); the message never holds a value
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// <c>dbdatasync secret send --to runner &lt;connection&gt; --server &lt;name or https address&gt; [--code &lt;pairing code&gt;]</c> (plan 28c E20d): the connection's run login over the paired HTTPS to the runner's secret store, under
    /// <see cref="SecretRefs.ForConnectionRun"/> (the name the runner's connection file names in its <c>auth</c>). The run login is the connection's second login when it has one and its only login otherwise; the read login of a
    /// connection that has both is never sent. The value is read from this repository's store and goes into the request body only: it is not printed, logged or put in an error. The pairing must allow the admin role.
    /// </summary>
    public static async Task<int> SendAsync(string[] args)
    {
        if (CliOptions.Read(args, "--to") != "runner") { Console.Error.WriteLine("--to is `runner`, the only place a login is sent to."); return 2; }
        var server = CliOptions.Read(args, "--server");
        if (server is null) { Console.Error.WriteLine("Name the runner: --server <name or https address> [--code <pairing code>]."); return 2; }
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--to" or "--server" or "--code" or "--repo" or "--client-dir") { i++; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) positional.Add(args[i]);
        }

        if (positional.Count != 1) { Console.Error.WriteLine("Usage: dbdatasync secret send --to runner <connection> --server <name or https address> [--code <pairing code>] [--repo <path>]"); return 2; }
        var name = positional[0];
        var root = DbDataSyncRoot.Resolve(args);
        var secrets = SecretStore.OpenInRepository(root);
        ConnectionConfig connection;
        try { connection = new ConfigRepository(Path.Combine(root, "config"), new GitCommitService(root), secrets).LoadConnection(name); }
        catch (FileNotFoundException) { Console.Error.WriteLine($"There is no connection '{name}' in {root}."); return 1; }
        var reference = connection.RunCredentialSecretRef ?? connection.CredentialSecretRef;
        if (string.IsNullOrWhiteSpace(reference)) { Console.Error.WriteLine($"The connection '{name}' has no login in the secret store (no credential reference), so there is nothing to send."); return 1; }
        if (!secrets.TryResolve(reference, out var value)) { Console.Error.WriteLine($"The secret '{reference}' of the connection '{name}' is not set in this repository's store: nothing was sent."); return 1; }
        Func<string, string?> environment = Environment.GetEnvironmentVariable;
        var client = new RunnerPublishClient(new RunnerClientStore(CliOptions.Read(args, "--client-dir") ?? RunnerClientStore.DefaultDirectory(environment)), environment);
        try
        {
            var stored = await client.SendSecretAsync(server, CliOptions.Read(args, "--code"), SecretRefs.ForConnectionRun(name), value);
            Console.WriteLine($"Sent the run login of '{name}' ({(connection.HasRunLogin ? "its run login" : "its only login")}) to the runner as {stored}. The runner's connection file for '{name}' names it as `password_secret: secret:{stored}`.");
            return 0;
        }
        catch (RunnerClientException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Nothing was sent. Storing a secret takes the admin role: pair with `dbdatarunner server pair --max-role admin`.");
            return 1;
        }
    }

    private static string[] WithoutRepoOption(string[] args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo") { i++; continue; }
            rest.Add(args[i]);
        }
        return [.. rest];
    }

    private static int Set(string[] args, SecretStore secrets)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: dbdatasync secret set <ref> <value>");
            return 1;
        }

        secrets.Store(args[1], args[2]);
        Console.WriteLine($"Stored '{args[1]}'.");
        return 0;
    }

    /// <summary>
    /// Ref names and whether each currently resolves — never the value itself.
    /// <para>
    /// <see cref="SecretStore"/> offers no enumeration (the shared store lists names, but the environment and
    /// the credentials directory behind it cannot), so this cannot show "everything stored" the way a
    /// directory listing would. Given no ref, it checks <see cref="KnownRefs"/> — right now, just the
    /// one fixed ref this phase's starter file documents. Given one or more refs, it checks exactly
    /// those instead, which is how a script or an operator who already knows a connection's ref
    /// (<c>dbdatakit:sync:connection:&lt;name&gt;</c>) checks it without this command having to know
    /// connections exist.
    /// </para>
    /// </summary>
    private static int List(string[] args, SecretStore secrets)
    {
        var refs = args.Length > 1 ? args[1..] : KnownRefs;
        foreach (var reference in refs)
            Console.WriteLine($"{reference}  {(secrets.TryResolve(reference, out _) ? "set" : "not set")}");

        return 0;
    }

    private static int Remove(string[] args, SecretStore secrets)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: dbdatasync secret remove <ref>");
            return 1;
        }

        secrets.Delete(args[1]);
        Console.WriteLine($"Removed '{args[1]}'.");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown secret command '{command}'. Use set, list, or remove.");
        return 1;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            """
            Usage: dbdatasync secret set <ref> <value> [--repo <path>]
                   dbdatasync secret list [<ref> ...] [--repo <path>]
                   dbdatasync secret remove <ref> [--repo <path>]
                   dbdatasync secret send --to runner <connection> --server <name or https address> [--code <pairing code>] [--repo <path>]
            """);
}
