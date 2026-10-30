using System.Text.Json;

namespace DbDataKit.Scripting;

/// <summary>
/// The hash of every package one script or library uses, kept in the configuration repository beside it (<c>library.lock</c> next to <c>library.csx</c>, <c>&lt;name&gt;.lock</c> next to <c>&lt;name&gt;.csx</c>) and committed with it.
/// nuget.org does not publish package hashes, so this file is the only strong source of truth: the first download is trusted and recorded, and every later one must match (a pin). A repository without the file keeps working; the first
/// resolution creates it. The reader is strict: a key it does not know is an error naming the file and the key's path.
/// </summary>
public sealed class LockFile
{
    public const string Schema = "scripting-lock/1";

    private readonly SortedDictionary<string, string> _pins = new(StringComparer.Ordinal);

    /// <summary>"id/version" (lower case, normalised version) to the base64 SHA-512 of the .nupkg.</summary>
    public IReadOnlyDictionary<string, string> Pins => _pins;

    /// <summary>How messages name this file (as the person knows it, for example <c>scripts/clean.lock</c>); empty for a lock that is not backed by a file.</summary>
    public string Name { get; private set; } = "";

    public static string KeyOf(string id, string version) => $"{id}/{version}".ToLowerInvariant();

    public void Pin(string id, string version, string sha512) => _pins[KeyOf(id, version)] = sha512;

    /// <summary>The pins of exactly these packages (a package that is no longer used is not kept).</summary>
    public void Replace(IEnumerable<ResolvedPackage> packages)
    {
        _pins.Clear();
        foreach (var p in packages) Pin(p.Id, p.Version.ToNormalizedString(), p.Sha512);
    }

    public static LockFile Read(string path, string? displayName = null)
    {
        var name = displayName ?? path;
        var result = new LockFile { Name = name };
        if (!File.Exists(path)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("schema", out var schema) || schema.GetString() != Schema)
                throw new InvalidDataException($"{name} is not a {Schema} file. Delete it to start a new one.");
            foreach (var top in doc.RootElement.EnumerateObject())
                if (top.Name is not ("schema" or "packages")) throw new InvalidDataException($"{name}: unknown key `{top.Name}` at $.{top.Name} (a {Schema} file has `schema` and `packages`).");
            if (doc.RootElement.TryGetProperty("packages", out var packages))
            {
                if (packages.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{name}: `packages` at $.packages is not an object.");
                foreach (var p in packages.EnumerateObject())
                    result._pins[p.Name.ToLowerInvariant()] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new InvalidDataException($"{name}: the hash of {p.Name} at $.packages.{p.Name} is not text.");
            }
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
