using System.Text.Json;

namespace DbDataKit.Scripting;

/// <summary>
/// The hash of every package a set of scripts or libraries uses, kept beside the configuration (<c>library.lock</c>) and written by the command that adds a library. nuget.org does not publish package hashes, so this file is the only strong
/// source of truth: the first download is trusted and recorded, and every later one must match (a pin). Without it a package is trusted on first use.
/// </summary>
public sealed class LockFile
{
    public const string Schema = "scripting-lock/1";

    private readonly SortedDictionary<string, string> _pins = new(StringComparer.Ordinal);

    /// <summary>"id/version" (lower case, normalised version) to the base64 SHA-512 of the .nupkg.</summary>
    public IReadOnlyDictionary<string, string> Pins => _pins;

    public static string KeyOf(string id, string version) => $"{id}/{version}".ToLowerInvariant();

    public void Pin(string id, string version, string sha512) => _pins[KeyOf(id, version)] = sha512;

    public static LockFile Read(string path, string? displayName = null)
    {
        var name = displayName ?? path;
        var result = new LockFile();
        if (!File.Exists(path)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("schema", out var schema) || schema.GetString() != Schema)
                throw new InvalidDataException($"{name} is not a {Schema} file. Delete it to start a new one.");
            if (doc.RootElement.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
                foreach (var p in packages.EnumerateObject())
                    result._pins[p.Name.ToLowerInvariant()] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new InvalidDataException($"{name}: the hash of {p.Name} is not text.");
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException($"{name} is not valid JSON ({ex.Message}).", ex); }
    }

    public string ToJson() => JsonSerializer.Serialize(new { schema = Schema, packages = _pins }, new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>Writes the file only when it would change, so a lock file in a git repository does not show a change for nothing.</summary>
    public void Write(string path)
    {
        var text = ToJson();
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }
}
