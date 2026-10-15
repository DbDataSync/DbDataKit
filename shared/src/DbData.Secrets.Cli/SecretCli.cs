using System.Text;

namespace DbData.Secrets.Cli;

/// <summary>Console abstraction so the command set is testable and independent of any CLI framework.</summary>
public interface IConsole
{
    void WriteLine(string text);
    void WriteError(string text);
    /// <summary>True when stdin is piped/redirected (so the secret is read from it, not prompted).</summary>
    bool IsInputRedirected { get; }
    /// <summary>Reads all of stdin.</summary>
    string ReadStdin();
    /// <summary>Prompts with echo disabled; null when no interactive terminal is available.</summary>
    string? ReadSecret(string prompt);
}

/// <summary>Real console: masks prompt input with no echo.</summary>
public sealed class SystemConsole : IConsole
{
    public void WriteLine(string text) => Console.Out.WriteLine(text);
    public void WriteError(string text) => Console.Error.WriteLine(text);
    public bool IsInputRedirected => Console.IsInputRedirected;
    public string ReadStdin() => Console.In.ReadToEnd();

    public string? ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected) return null;
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }
}

/// <summary>
/// <c>secret set|get|list|rm|test</c>. A plain class: a product mounts it under whatever verb it likes by
/// passing the arguments after <c>secret</c> to <see cref="Execute"/>. Exit codes: 0 ok, 1 not found or
/// failed, 2 usage error. <b>Secret values are never accepted from, or echoed into, argv.</b>
/// </summary>
public sealed class SecretCli(ISecretResolver resolver, ISecretStore store)
{
    public const string Usage =
        "usage: secret <command>\n" +
        "  set <name> [--stdin]     store a secret; value comes from a hidden prompt or stdin, never the command line\n" +
        "  get <name|ref> [--reveal] show whether a secret exists; --reveal prints the value\n" +
        "  list [prefix]            list secret names (never values)\n" +
        "  rm <name>                delete a secret\n" +
        "  test <ref>               resolve a reference and report ok / not found without printing it\n" +
        "names look like dbdata:<product>:<kind>:<name>[:<part>]; refs like env:X, file:/p, secret:<name>";

    public int Execute(string[] args, IConsole console, CancellationToken ct = default) =>
        ExecuteAsync(args, console, ct).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(string[] args, IConsole console, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
        var flags = args.Skip(1).Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var positional = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "set" => await SetAsync(positional, flags, console, ct).ConfigureAwait(false),
                "get" => await GetAsync(positional, flags, console, ct).ConfigureAwait(false),
                "list" => await ListAsync(positional, console, ct).ConfigureAwait(false),
                "rm" or "remove" or "delete" => await RemoveAsync(positional, console, ct).ConfigureAwait(false),
                "test" => await TestAsync(positional, console, ct).ConfigureAwait(false),
                _ => Usage_(console, $"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.WriteError($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Usage_(IConsole console, string message)
    {
        console.WriteError(message);
        console.WriteError(Usage);
        return 2;
    }

    private async Task<int> SetAsync(string[] positional, HashSet<string> flags, IConsole console, CancellationToken ct)
    {
        if (positional.Length > 1)
            return Usage_(console, "set takes only a name. The value is read from a hidden prompt or stdin; it is never accepted on the command line.");
        if (positional.Length == 0 || !SecretName.TryParse(positional[0], out var name))
            return Usage_(console, "set needs a secret name like dbdata:<product>:<kind>:<name>[:<part>].");

        string? text;
        if (flags.Contains("--stdin") || console.IsInputRedirected)
        {
            text = console.ReadStdin();
            if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
            else if (text.EndsWith('\n')) text = text[..^1];
        }
        else
        {
            text = console.ReadSecret("Secret value: ");
            if (text is null) { console.WriteError("no terminal for a prompt; pipe the value on stdin instead."); return 1; }
            var again = console.ReadSecret("Confirm value: ");
            if (again != text) { console.WriteError("values do not match; nothing stored."); return 1; }
        }
        if (string.IsNullOrEmpty(text)) { console.WriteError("empty value; nothing stored."); return 1; }

        using var value = SecretValue.From(text);
        await store.SetAsync(name, value, ct).ConfigureAwait(false);
        console.WriteLine($"stored {name}");
        return 0;
    }

    private async Task<int> GetAsync(string[] positional, HashSet<string> flags, IConsole console, CancellationToken ct)
    {
        if (positional.Length != 1 || !TryRef(positional[0], out var r)) return Usage_(console, "get needs one secret name or reference.");
        try
        {
            using var value = await resolver.ResolveAsync(r, ct).ConfigureAwait(false);
            console.WriteLine(flags.Contains("--reveal") ? value.RevealString() : $"{SecretValue.Mask} (found; use --reveal to print)");
            return 0;
        }
        catch (SecretNotFoundException)
        {
            console.WriteError($"not found: {r.ToAuditString()}");
            return 1;
        }
    }

    private async Task<int> ListAsync(string[] positional, IConsole console, CancellationToken ct)
    {
        if (positional.Length > 1) return Usage_(console, "list takes at most one prefix.");
        var count = 0;
        await foreach (var n in store.ListAsync(positional.Length == 1 ? positional[0] : "dbdata:", ct).ConfigureAwait(false))
        {
            console.WriteLine(n.ToString());
            count++;
        }
        if (count == 0) console.WriteError("no secrets found.");
        return 0;
    }

    private async Task<int> RemoveAsync(string[] positional, IConsole console, CancellationToken ct)
    {
        if (positional.Length != 1 || !SecretName.TryParse(positional[0], out var name)) return Usage_(console, "rm needs one secret name.");
        await store.DeleteAsync(name, ct).ConfigureAwait(false);
        console.WriteLine($"removed {name}");
        return 0;
    }

    private async Task<int> TestAsync(string[] positional, IConsole console, CancellationToken ct)
    {
        if (positional.Length != 1 || !TryRef(positional[0], out var r)) return Usage_(console, "test needs one reference or secret name.");
        try
        {
            using var _ = await resolver.ResolveAsync(r, ct).ConfigureAwait(false);
            console.WriteLine($"ok: {r.ToAuditString()}");
            return 0;
        }
        catch (SecretNotFoundException)
        {
            console.WriteError($"not found: {r.ToAuditString()}");
            return 1;
        }
    }

    private static bool TryRef(string text, out SecretRef r)
    {
        if (SecretRef.TryParse(text, out var parsed)) { r = parsed; return true; }
        if (SecretName.TryParse(text, out var n)) { r = n.ToRef(); return true; }
        r = null!;
        return false;
    }
}
