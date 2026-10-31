using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.Connections;

namespace DbDataConsole.Server;

/// <summary>What copying a connection to another server makes: the text of the new file and what the person should know about it, or why there is none.</summary>
/// <param name="Text">The <c>connection/1</c> file for the target; null when <paramref name="Problem"/> is set.</param>
/// <param name="Notes">What differs from the source or still needs doing on the target, in words.</param>
/// <param name="Secrets">The secret references the copy names (never their values): the target must hold each one.</param>
public sealed record CopyPlan(string? Text, string? Problem, IReadOnlyList<string> Notes, IReadOnlyList<string> Secrets);

/// <summary>
/// Copying a connection from one server to another (plan 13, E05). Every product keeps a connection as the same <c>connection/1</c> file, so a copy is the source's file with only what belongs to the source's own server changed, and the target
/// decides whether it can use the result: it reads the file with its own strict loader and refuses what it does not support, naming the key and line. Nothing is converted or guessed.
/// <list type="bullet">
/// <item><c>name</c> is the new name (the file name on the target is the name).</item>
/// <item><c>managed_by</c> is dropped: it names a client of the source's server, which means nothing on the target (the person who copies is its administrator).</item>
/// <item><c>extensions.&lt;product&gt;</c> of another product is dropped, since the target does not read it; the target's own (a copy between two Sync servers, or two Proxies) is kept.</item>
/// <item>A password is a reference, never a value, and is not copied. Sync keeps a connection's password under <c>secret:dbdata:sync:connection:&lt;name&gt;</c> only, so a copy to Sync names that for the new name (and a Proxy's copy of a Sync connection names
/// <c>secret:dbdata:proxy:connection:&lt;name&gt;</c>, not Sync's store); every other reference is kept as written. The copy says which references the target must hold.</item>
/// </list>
/// The text is edited in place (<see cref="YamlEditor"/>), so the source's comments, key order and quoting stay.
/// </summary>
public static partial class ConnectionCopy
{
    public const string Sync = "dbdatasync";
    public const string Proxy = "dbdataproxy";

    /// <summary>The products the Connections view lists and copies between, and the key each reads under <c>extensions</c>.</summary>
    public static string? ExtensionKey(string? product) => product switch { Sync => "sync", Proxy => "proxy", _ => null };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    private static CopyPlan Refused(string problem) => new(null, problem, [], []);

    public static CopyPlan Plan(string sourceText, string sourceProduct, string targetProduct, string newName)
    {
        if (ExtensionKey(targetProduct) is not { } targetKey) return Refused($"Connections cannot be copied to a {targetProduct} server.");
        if (!IsValidName(newName)) return Refused($"'{newName}' is not a connection name: a letter, then letters, digits, '-', '_' or '.', at most 64 characters.");

        var loaded = ConnectionDefinitionLoader.Load(sourceText, "source", addressRequired: false);
        if (!loaded.Ok)
            return Refused("The connection cannot be read, so it is not copied: " + string.Join(" ", loaded.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => $"line {d.Line}: {d.Message}")));
        var source = loaded.Definition!;

        var notes = new List<string>();
        var text = sourceText;
        string? Edit(Func<string, EditResult> edit)
        {
            var result = edit(text);
            if (!result.Ok) return result.Problem!.Message;
            text = result.Text!;
            return null;
        }

        string? problem = Edit(t => YamlEditor.Set(t, "name", newName));
        if (problem is null && !string.IsNullOrEmpty(source.ManagedBy))
        {
            problem = Edit(t => YamlEditor.Remove(t, "managed_by"));
            notes.Add($"managed_by ({source.ManagedBy}) was left out: it names a client of {SourceName(sourceProduct)}, not of the target.");
        }

        var foreign = source.Extensions.Items.Keys.Where(k => k != targetKey).Order(StringComparer.Ordinal).ToList();
        if (problem is null && foreign.Count > 0)
        {
            // when nothing of the target's own remains the whole block goes, so no empty `extensions:` is left behind
            problem = source.Extensions.Items.ContainsKey(targetKey)
                ? foreign.Select(k => Edit(t => YamlEditor.Remove(t, "extensions." + k))).FirstOrDefault(p => p is not null)
                : Edit(t => YamlEditor.Remove(t, "extensions"));
            notes.Add($"The settings under extensions.{string.Join(", extensions.", foreign)} were left out: the target does not read them.");
        }

        // Sync's loader refuses the settings only a Proxy reads; they go, and the copy says which
        var dropped = new List<string>();
        if (targetProduct == Sync)
        {
            var map = StrictYamlReader.Read(sourceText, "source", []) as YamlMapping;
            foreach (var key in new[] { "tls", "read_login", "write_login", "connection_string_secret", "access" })
                if (problem is null && map?.GetEntry(key) is not null)
                {
                    problem = Edit(t => YamlEditor.Remove(t, key));
                    dropped.Add(key);
                }
            if (dropped.Count > 0) notes.Add($"{string.Join(", ", dropped)} {(dropped.Count == 1 ? "was" : "were")} left out: Sync does not use {(dropped.Count == 1 ? "it" : "them")}.");
        }

        // a password is named by reference; Sync reads one name only, and a Sync reference means nothing in a Proxy's store
        var logins = new[] { ("auth", source.Auth), ("read_login", dropped.Contains("read_login") ? null : source.ReadLogin), ("write_login", dropped.Contains("write_login") ? null : source.WriteLogin) };
        var secrets = new List<string>();
        foreach (var (path, login) in logins)
        {
            if (login is null) continue;
            if (login.PasswordSecret is { } reference)
            {
                var named = reference;
                if (targetProduct == Sync && path == "auth") named = $"secret:dbdata:sync:connection:{newName}";
                else if (targetProduct == Proxy && reference.StartsWith("secret:dbdata:sync:", StringComparison.OrdinalIgnoreCase)) named = $"secret:dbdata:proxy:connection:{newName}";
                if (named != reference && problem is null) problem = Edit(t => YamlEditor.Set(t, path + ".password_secret", named));
                secrets.Add(named);
            }
            if (login.ConnectionStringSecret is { } whole) secrets.Add(whole);
            if (login.TokenSecret is { } token) secrets.Add(token);
        }
        if (!dropped.Contains("connection_string_secret") && source.ConnectionStringSecret is { } wholeString) secrets.Add(wholeString);

        if (problem is not null) return Refused("The file could not be changed for the target: " + problem);

        var check = ConnectionDefinitionLoader.Load(text, "copy", addressRequired: false);
        if (!check.Ok)
            return Refused("The copy would not be a valid connection file: " + string.Join(" ", check.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => $"line {d.Line}: {d.Message}")));

        if (secrets.Count > 0)
            notes.Add("The password itself is never copied. The copy refers to " + string.Join(", ", secrets.Distinct().Select(s => $"`{s}`")) + ", which the target must hold.");
        if (targetProduct == Sync && source.Extensions.Items.TryGetValue("sync", out var sync) && sync is YamlMapping mapping && (mapping.Get("scripts") is not null || mapping.Get("hooks") is not null))
            notes.Add("The connection's scripts and hooks are kept; the scripts they name must exist on the target.");

        return new CopyPlan(text, null, notes, [.. secrets.Distinct()]);
    }

    private static string SourceName(string product) => product == Sync ? "a Sync server" : product == Proxy ? "a Proxy" : "its server";
}
