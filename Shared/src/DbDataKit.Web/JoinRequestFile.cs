using System.Text;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Web.Trust;

/// <summary>A file that cannot be used as a join request; the message names the file and what is wrong.</summary>
public sealed class JoinRequestFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The join a server makes at its next start, <c>join-request.yaml</c> in its data folder (format <c>join-request/1</c>): the console's address and the
/// thumbprint of its certificate, the single-use token, and optionally the address the console should call the server at. The <c>pair</c> command writes
/// it and the service reads it at its join step and deletes it once joined. It holds the token, so on Linux it is written readable by its owner only.
/// </summary>
/// <param name="Console">The console's https address.</param>
/// <param name="ConsoleThumbprint">The SHA-256 of the console's certificate, 64 lower-case hex digits (written <c>sha256:</c> and the digits).</param>
/// <param name="Token">The join token, as <see cref="JoinTokens.Normalize"/> writes it.</param>
/// <param name="Address">The address the console calls this server at; null to let the server say its own.</param>
public sealed record JoinRequestFile(Uri Console, string ConsoleThumbprint, string Token, Uri? Address = null)
{
    public const string Schema = "join-request/1";
    public const string FileName = "join-request.yaml";

    private static readonly string[] Keys = ["schema", "console", "console_thumbprint", "token", "address"];

    /// <summary>A request from what a person typed; throws <see cref="ArgumentException"/> naming the value that is not right.</summary>
    public static JoinRequestFile From(string console, string consoleThumbprint, string token, string? address = null)
    {
        if (JoinService.AddressProblem(console) is { } c) throw new ArgumentException($"The console's address '{console}' cannot be used: {c}.", nameof(console));
        var thumbprint = Thumbprints.Parse(consoleThumbprint) ?? throw new ArgumentException($"The console's thumbprint is written {Thumbprints.Prefix} and 64 hex digits, as the console printed it.", nameof(consoleThumbprint));
        var normal = JoinTokens.Normalize(token) ?? throw new ArgumentException("That is not a join token (26 letters and digits, as the console printed it).", nameof(token));
        if (address is not null && JoinService.AddressProblem(address) is { } a) throw new ArgumentException($"The address '{address}' cannot be used: {a}.", nameof(address));
        return new JoinRequestFile(new Uri(console), thumbprint, normal, address is null ? null : new Uri(address));
    }

    /// <summary>Writes the request to <paramref name="path"/>, replacing one that is there.</summary>
    public void Write(string path)
    {
        var text = new StringBuilder()
            .Append("# A join this server makes at its next start; the service deletes this file once it has joined. It holds a single-use token: do not copy it.\n")
            .Append("schema: ").Append(Schema).Append('\n')
            .Append("console: ").Append(YamlText.Scalar(Text(Console))).Append('\n')
            .Append("console_thumbprint: ").Append(YamlText.Scalar(Thumbprints.Format(ConsoleThumbprint))).Append('\n')
            .Append("token: ").Append(YamlText.Scalar(Token)).Append('\n');
        if (Address is not null) text.Append("address: ").Append(YamlText.Scalar(Text(Address))).Append('\n');

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options)) stream.Write(new UTF8Encoding(false).GetBytes(text.ToString()));
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>The request in <paramref name="path"/>, or null when there is none; throws <see cref="JoinRequestFileException"/> naming the file when it cannot be used.</summary>
    public static JoinRequestFile? Read(string path)
    {
        if (!File.Exists(path)) return null;
        string text;
        try { text = File.ReadAllText(path).TrimStart('﻿'); }
        catch (IOException ex) { throw new JoinRequestFileException($"The join request '{path}' cannot be read: {ex.Message}", ex); }
        var diagnostics = new List<ConfigDiagnostic>();
        if (StrictYamlReader.Read(text, path, diagnostics) is not YamlMapping map)
            throw new JoinRequestFileException(diagnostics.Count > 0 ? $"The join request '{path}' is not valid YAML: {diagnostics[0]}" : $"The join request '{path}' is empty.");

        string? Value(string key) => map.Get(key) switch
        {
            null => null,
            YamlScalar s => s.Value,
            _ => throw new JoinRequestFileException($"The join request '{path}': '{key}' is a single value."),
        };

        var schema = Value("schema");
        if (schema != Schema)
            throw new JoinRequestFileException(schema is null
                ? $"The join request '{path}' has no 'schema'; this program reads {Schema}. Run the pair command again."
                : $"The join request '{path}' is {schema}; this program reads {Schema}. Run the pair command of this program's version again.");
        if (map.Entries.FirstOrDefault(e => !Keys.Contains(e.Key.Value)) is { } unknown)
            throw new JoinRequestFileException($"The join request '{path}' has the key '{unknown.Key.Value}', which {Schema} does not have. Run the pair command again.");
        try
        {
            return From(Value("console") ?? throw Missing("console"), Value("console_thumbprint") ?? throw Missing("console_thumbprint"), Value("token") ?? throw Missing("token"), Value("address"));
        }
        catch (ArgumentException ex) { throw new JoinRequestFileException($"The join request '{path}' cannot be used: {ex.Message.Split(" (Parameter")[0]}", ex); }

        JoinRequestFileException Missing(string key) => new($"The join request '{path}' has no '{key}'. Run the pair command again.");
    }

    /// <summary>Deletes the request at <paramref name="path"/>; nothing when there is none.</summary>
    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    // an address as the join sends it: scheme, host and port, no trailing slash
    internal static string Text(Uri uri) => uri.GetLeftPart(UriPartial.Authority);
}
