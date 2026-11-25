using System.Text;
using DbDataKit.Connections;

namespace DbDataKit.Server;

/// <summary>
/// The copy of a draft's connection that Set up for development puts on a Runner: the draft's file as written, renamed to the copy's name, with its logins replaced by one login: the user the person typed, as plain text, and the
/// password named by the secret the Runner holds. Everything else (the engine, the address, the extensions) is the draft's. The copy holds references only, never a value.
/// Design: docs/authoring-metadata.md#how-a-screen-sends-a-development-login
/// </summary>
public static class DevelopmentCopy
{
    /// <summary>The top-level keys of a connection file the copy replaces: its name and its logins.</summary>
    private static readonly string[] Replaced = ["name", "auth", "read_login", "write_login"];

    /// <summary>The longest name a Runner gives a copy of a connection.</summary>
    public const int MaxNameChars = 64;

    /// <summary>
    /// The copy's name on the Runner: <c>&lt;package&gt;-&lt;slot&gt;-&lt;channel&gt;</c>. A name longer than a Runner takes keeps its first characters and ends with a short hash of the whole, so two set-ups never share a name; a
    /// package name that does not start with a letter gets <c>c-</c> in front, as a Runner's copy names do.
    /// </summary>
    public static string NameOf(string package, string slot, string channel)
    {
        var name = $"{package}-{slot}-{channel}";
        if (!char.IsAsciiLetter(name[0])) name = "c-" + name;
        if (name.Length <= MaxNameChars) return name;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        return name[..(MaxNameChars - 9)] + "-" + hash;
    }

    /// <summary>
    /// The copy's text, or why the draft's file cannot make one: <paramref name="draft"/> (the text of <paramref name="file"/>) renamed <paramref name="name"/>, with <c>auth</c> set to <paramref name="user"/> and the password
    /// <c>secret:</c><paramref name="secret"/>, and its <c>read_login</c> and <c>write_login</c> left out. A <paramref name="user"/> of null takes the user the draft's <c>auth</c> names. The text is checked with the strict loader the
    /// Runner uses. <see cref="Result.Warnings"/> says what the copy lacks to open a connection (no address).
    /// </summary>
    public static Result Write(string draft, string file, string name, string? user, string secret)
    {
        var load = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(draft, file);
        if (!load.Ok) return Result.Refused(422, $"The draft's {file} does not load: {Errors(load)}");
        var definition = load.Definition!;
        if (definition.IsOutside) return Result.Refused(422, $"The draft's {file} is a connection of the kind {definition.KindName}; Set up for development stores a database login.");
        var login = user ?? definition.Auth?.User;
        if (string.IsNullOrEmpty(login)) return Result.Refused(400, $"Type the login's user: the draft's {file} names none.");

        var lines = draft.Replace("\r\n", "\n").Split('\n');
        var text = new StringBuilder();
        var skipping = false;
        var named = false;
        foreach (var line in lines)
        {
            var top = line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#';
            if (top) skipping = Replaced.Any(k => line.StartsWith(k + ":", StringComparison.Ordinal));
            if (skipping) continue;
            text.Append(line).Append('\n');
            // the copy's name goes right under the schema line, where a connection file names itself
            if (top && !named && line.StartsWith("schema:", StringComparison.Ordinal)) { text.Append("name: ").Append(Quoted(name)).Append('\n'); named = true; }
        }
        var copy = text.ToString().TrimEnd('\n') + "\n";
        if (!named) copy = $"name: {Quoted(name)}\n" + copy;
        copy += $"auth:\n  mode: sql\n  user: {Quoted(login)}\n  password_secret: {Quoted("secret:" + secret)}\n";

        var check = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(copy, file);
        if (!check.Ok) return Result.Refused(422, $"The copy of the draft's {file} does not load: {Errors(check)}");
        var warnings = new List<string>();
        if (check.Definition!.Address is null && check.Definition.ConnectionString is null && check.Definition.ConnectionStringSecret is null)
            warnings.Add($"Warning: the draft's {file} names no address or connection string, so a run that uses '{name}' cannot open it until the file names one and the set-up is sent again.");
        return new Result(copy, login, null, warnings);
    }

    /// <summary>The copy's text and the user it names, or the refusal (<see cref="Error"/> and the <see cref="Status"/> to answer it with); never a value.</summary>
    public sealed record Result(string? Text, string? User, string? Error, IReadOnlyList<string> Warnings, int Status = 200)
    {
        public static Result Refused(int status, string error) => new(null, null, error, [], status);
    }

    /// <summary>A scalar in single quotes, so a user or a reference is read as written.</summary>
    private static string Quoted(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Errors(ConnectionLoadResult load) =>
        string.Join(" ", load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.Message));
}
