using System.Text;
using DbDataKit.Secrets.Store;

namespace DbDataKit.Secrets.Cli;

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
/// The shared <c>secret</c> commands. A plain class: a product mounts it under its <c>secret</c> noun by passing the arguments after <c>secret</c> to <see cref="Execute"/>. Exit codes: 0 ok, 1 failed
/// or something did not resolve, 2 usage error. <b>No command prints a secret's value, and none accepts one on the command line</b>: a value comes from a hidden prompt or stdin, a passphrase
/// likewise.
/// </summary>
/// <param name="resolver">Resolves references for <c>check</c>.</param>
/// <param name="store">Writes and deletes secrets.</param>
/// <param name="admin">Inventory, rotation, export and import; without it <c>list</c> shows names only and those verbs say they are not available.</param>
/// <param name="references">The references the product's configuration holds, for <c>check</c> without arguments.</param>
/// <param name="protect">Makes the inline form of a value for a single field (<c>dpapi:</c> on Windows); without it <c>protect</c> says it is not available on this platform.</param>
public sealed class SecretCli(
    ISecretResolver resolver,
    ISecretStore store,
    ISecretAdmin? admin = null,
    Func<IEnumerable<SecretRef>>? references = null,
    Func<SecretValue, string>? protect = null)
{
    public const string Usage =
        "usage: secret <command>\n" +
        "  set <name> [--stdin]            store a secret; the value comes from a hidden prompt or stdin, never the command line\n" +
        "  list [prefix]                   list secret names with when each was written (never values)\n" +
        "  delete <name>                   delete a secret\n" +
        "  check [ref...]                  resolve each reference (those given, or every one in the configuration) and say which do not\n" +
        "  rotate-key                      a new data key; every value is encrypted again under it and the old key is deleted\n" +
        "  export <file> [--prefix p] [--passphrase-stdin]   write the secrets, encrypted under a passphrase, to move a server\n" +
        "  import <file> [--overwrite] [--passphrase-stdin]  read an export made by `export`\n" +
        "  protect [--stdin]               an inline, encrypted form of one value for a single field (Windows: dpapi:)\n" +
        "names look like dbdata:<product>:<kind>:<name>[:<part>]; refs like secret:<name>, env:X, file:/p. There is no command that prints a value.";

    public int Execute(string[] args, IConsole console, CancellationToken ct = default) =>
        ExecuteAsync(args, console, ct).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(string[] args, IConsole console, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
        var rest = args.Skip(1).ToArray();
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        for (var i = 0; i < rest.Length; i++)
        {
            if (!rest[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(rest[i]); continue; }
            // --prefix takes the next argument; the others are switches
            flags[rest[i]] = rest[i].Equals("--prefix", StringComparison.OrdinalIgnoreCase) && i + 1 < rest.Length ? rest[++i] : null;
        }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "set" => await SetAsync(positional, flags, console, ct).ConfigureAwait(false),
                "list" => await ListAsync(positional, console, ct).ConfigureAwait(false),
                "delete" or "rm" or "remove" => await DeleteAsync(positional, console, ct).ConfigureAwait(false),
                "check" or "test" => await CheckAsync(positional, console, ct).ConfigureAwait(false),
                "rotate-key" => await RotateAsync(positional, console, ct).ConfigureAwait(false),
                "export" => await ExportAsync(positional, flags, console, ct).ConfigureAwait(false),
                "import" => await ImportAsync(positional, flags, console, ct).ConfigureAwait(false),
                "protect" => Protect(positional, flags, console),
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

    private static string? ReadValue(IConsole console, IDictionary<string, string?> flags, string prompt, bool confirm, string noun)
    {
        string? text;
        if (flags.ContainsKey("--stdin") || console.IsInputRedirected)
        {
            text = console.ReadStdin();
            if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
            else if (text.EndsWith('\n')) text = text[..^1];
            return text;
        }
        text = console.ReadSecret(prompt);
        if (text is null) { console.WriteError($"no terminal for a prompt; pipe the {noun} on stdin instead."); return null; }
        if (confirm && console.ReadSecret("Confirm: ") != text) { console.WriteError($"the {noun}s do not match; nothing done."); return null; }
        return text;
    }

    /// <summary>The passphrase of an export or import: the first line of stdin with <c>--passphrase-stdin</c>, otherwise a hidden prompt (twice when it is being chosen).</summary>
    private static SecretValue? ReadPassphrase(IConsole console, IDictionary<string, string?> flags, bool confirm)
    {
        string? text;
        if (flags.ContainsKey("--passphrase-stdin"))
        {
            text = console.ReadStdin().Split('\n')[0].TrimEnd('\r');
        }
        else
        {
            text = console.ReadSecret("Passphrase: ");
            if (text is null) { console.WriteError("no terminal for a prompt; use --passphrase-stdin and pipe the passphrase."); return null; }
            if (confirm && console.ReadSecret("Confirm passphrase: ") != text) { console.WriteError("the passphrases do not match; nothing done."); return null; }
        }
        return SecretValue.From(text);
    }

    private async Task<int> SetAsync(List<string> positional, IDictionary<string, string?> flags, IConsole console, CancellationToken ct)
    {
        if (positional.Count > 1)
            return Usage_(console, "set takes only a name. The value is read from a hidden prompt or stdin; it is never accepted on the command line.");
        if (positional.Count == 0 || !SecretName.TryParse(positional[0], out var name))
            return Usage_(console, "set needs a secret name like dbdata:<product>:<kind>:<name>[:<part>].");

        var text = ReadValue(console, flags, "Secret value: ", confirm: true, "value");
        if (text is null) return 1;
        if (text.Length == 0) { console.WriteError("empty value; nothing stored."); return 1; }

        using var value = SecretValue.From(text);
        await store.SetAsync(name, value, ct).ConfigureAwait(false);
        console.WriteLine($"stored {name}");
        return 0;
    }

    private async Task<int> ListAsync(List<string> positional, IConsole console, CancellationToken ct)
    {
        if (positional.Count > 1) return Usage_(console, "list takes at most one prefix.");
        var prefix = positional.Count == 1 ? positional[0] : "dbdata:";
        var count = 0;
        if (admin is not null)
        {
            foreach (var info in await admin.ListInfoAsync(prefix, ct).ConfigureAwait(false))
            {
                console.WriteLine($"{info.Name}  written {(info.Updated == default ? "(unknown)" : info.Updated.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture))}  key {info.KeyId}");
                count++;
            }
        }
        else
        {
            await foreach (var n in store.ListAsync(prefix, ct).ConfigureAwait(false)) { console.WriteLine(n.ToString()); count++; }
        }
        if (count == 0) console.WriteError("no secrets found.");
        return 0;
    }

    private async Task<int> DeleteAsync(List<string> positional, IConsole console, CancellationToken ct)
    {
        if (positional.Count != 1 || !SecretName.TryParse(positional[0], out var name)) return Usage_(console, "delete needs one secret name.");
        await store.DeleteAsync(name, ct).ConfigureAwait(false);
        console.WriteLine($"deleted {name}");
        return 0;
    }

    private async Task<int> CheckAsync(List<string> positional, IConsole console, CancellationToken ct)
    {
        var refs = new List<SecretRef>();
        foreach (var text in positional)
        {
            if (!TryRef(text, out var r)) return Usage_(console, $"'{text}' is not a secret name or reference.");
            refs.Add(r);
        }
        if (positional.Count == 0)
        {
            if (references is null) return Usage_(console, "check needs references to resolve; this product gives none from its configuration.");
            refs.AddRange(references());
        }
        var missing = 0;
        foreach (var r in refs.DistinctBy(x => x.ToString()))
        {
            try
            {
                using var _ = await resolver.ResolveAsync(r, ct).ConfigureAwait(false);
                console.WriteLine($"ok: {r.ToAuditString()}");
            }
            catch (SecretNotFoundException)
            {
                console.WriteError($"not found: {r.ToAuditString()}");
                missing++;
            }
        }
        if (refs.Count == 0) console.WriteLine("no secret references in the configuration.");
        else if (missing > 0) console.WriteError($"{missing} of {refs.Count} reference(s) do not resolve.");
        return missing == 0 ? 0 : 1;
    }

    private async Task<int> RotateAsync(List<string> positional, IConsole console, CancellationToken ct)
    {
        if (positional.Count != 0) return Usage_(console, "rotate-key takes no arguments.");
        if (admin is null) { console.WriteError("rotate-key is not available here."); return 1; }
        var result = await admin.RotateKeyAsync(ct: ct).ConfigureAwait(false);
        console.WriteLine($"rotated: {result.Reencrypted} value(s) now under key {result.NewKeyId}; {result.RemovedKeyIds.Count} old key(s) deleted{(result.Resumed ? " (an interrupted rotation was finished)" : "")}");
        return 0;
    }

    private async Task<int> ExportAsync(List<string> positional, IDictionary<string, string?> flags, IConsole console, CancellationToken ct)
    {
        if (positional.Count != 1) return Usage_(console, "export needs one file to write.");
        if (admin is null) { console.WriteError("export is not available here."); return 1; }
        using var passphrase = ReadPassphrase(console, flags, confirm: true);
        if (passphrase is null) return 1;
        var count = await admin.ExportAsync(positional[0], passphrase, flags.TryGetValue("--prefix", out var prefix) ? prefix ?? "" : "", ct).ConfigureAwait(false);
        console.WriteLine($"exported {count} secret(s) to {positional[0]}; keep the passphrase apart from the file");
        return 0;
    }

    private async Task<int> ImportAsync(List<string> positional, IDictionary<string, string?> flags, IConsole console, CancellationToken ct)
    {
        if (positional.Count != 1) return Usage_(console, "import needs one export file.");
        if (admin is null) { console.WriteError("import is not available here."); return 1; }
        using var passphrase = ReadPassphrase(console, flags, confirm: false);
        if (passphrase is null) return 1;
        var result = await admin.ImportAsync(positional[0], passphrase, flags.ContainsKey("--overwrite"), ct).ConfigureAwait(false);
        console.WriteLine($"imported {result.Imported}, overwritten {result.Overwritten}, kept {result.Skipped} that were already there");
        return 0;
    }

    private int Protect(List<string> positional, IDictionary<string, string?> flags, IConsole console)
    {
        if (positional.Count != 0) return Usage_(console, "protect takes no arguments. The value is read from a hidden prompt or stdin.");
        if (protect is null) { console.WriteError("protect is not available on this platform (it makes a DPAPI value on Windows)."); return 1; }
        var text = ReadValue(console, flags, "Value to protect: ", confirm: true, "value");
        if (text is null) return 1;
        if (text.Length == 0) { console.WriteError("empty value; nothing done."); return 1; }
        using var value = SecretValue.From(text);
        console.WriteLine(protect(value));
        return 0;
    }

    private static bool TryRef(string text, out SecretRef r)
    {
        if (SecretRef.TryParse(text, out var parsed)) { r = parsed; return true; }
        if (SecretName.TryParse(text, out var n)) { r = n.ToRef(); return true; }
        r = null!;
        return false;
    }
}
