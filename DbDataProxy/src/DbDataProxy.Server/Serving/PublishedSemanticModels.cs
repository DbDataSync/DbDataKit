using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;

namespace DbDataProxy.Server.Serving;

/// <summary>What a published version says about itself, kept beside its catalog.</summary>
public sealed record PublishedVersion(int Number, string? ManagedBy, string? PublishedBy, DateTimeOffset PublishedAt, Dictionary<string, string> Sources, Dictionary<string, string> Ssas);

/// <summary>
/// The semantic models published to this server through the management API (plan 19, J13), kept in <c>semantic-models/&lt;name&gt;/</c> of the data directory: every version as <c>v0001.yaml</c> (the catalog) and
/// <c>v0001.json</c> (who published it and what its sources map to), and <c>current</c>, the number of the version being served. Versions are kept, so a rollback is a change of that one file. A version is written
/// whole before <c>current</c> moves, so a failure at any step leaves the previous version serving.
/// </summary>
public sealed partial class PublishedSemanticModels(string dataDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    private string Root => Path.Combine(dataDirectory, "semantic-models");

    public static bool ValidName(string name) => NamePattern().IsMatch(name);

    public IReadOnlyList<string> Names() =>
        Directory.Exists(Root) ? [.. Directory.EnumerateDirectories(Root).Select(d => Path.GetFileName(d)).Where(ValidName).OrderBy(n => n, StringComparer.Ordinal)] : [];

    public int? Current(string name)
    {
        var file = Path.Combine(Root, name, "current");
        return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var number) ? number : null;
    }

    public IReadOnlyList<PublishedVersion> Versions(string name) =>
        Directory.Exists(Path.Combine(Root, name))
            ? [.. Directory.EnumerateFiles(Path.Combine(Root, name), "v*.json").Select(f => Meta(f)).OfType<PublishedVersion>().OrderBy(v => v.Number)]
            : [];

    public string? CatalogText(string name, int number) => File.Exists(CatalogFile(name, number)) ? File.ReadAllText(CatalogFile(name, number)) : null;

    public PublishedVersion? Version(string name, int number) => Meta(Path.Combine(Root, name, $"v{number:D4}.json"));

    /// <summary>Writes the next version and makes it the current one. The caller has checked it loads.</summary>
    public PublishedVersion Publish(string name, string catalogText, string? managedBy, string? publishedBy, Dictionary<string, string> sources, Dictionary<string, string> ssas, DateTimeOffset at)
    {
        lock (_gate)
        {
            var folder = Path.Combine(Root, name);
            Directory.CreateDirectory(folder);
            var number = (Versions(name).Select(v => v.Number).DefaultIfEmpty(0).Max()) + 1;
            var version = new PublishedVersion(number, managedBy, publishedBy, at, sources, ssas);
            WriteWhole(CatalogFile(name, number), catalogText);
            WriteWhole(Path.Combine(folder, $"v{number:D4}.json"), JsonSerializer.Serialize(version, Json));
            Point(name, number);
            return version;
        }
    }

    /// <summary>Makes an earlier version the current one again.</summary>
    public void Point(string name, int number)
    {
        lock (_gate) WriteWhole(Path.Combine(Root, name, "current"), number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Delete(string name)
    {
        lock (_gate) { if (Directory.Exists(Path.Combine(Root, name))) Directory.Delete(Path.Combine(Root, name), recursive: true); }
    }

    /// <summary>The model a version is: its catalog with its mapping. Throws what the catalog loader throws.</summary>
    public SemanticModel Load(string name, int number)
    {
        var meta = Version(name, number) ?? throw new InvalidOperationException($"Model '{name}' has no version {number}.");
        return new SemanticModel(CatalogLoader.LoadText(CatalogText(name, number)!, CatalogFile(name, number)), meta.Sources, meta.Ssas);
    }

    /// <summary>Adds each published model's current version to <paramref name="models"/>; one that does not load is reported and left out (it must not stop the server, or the old one could not be rolled back to).</summary>
    public void LoadInto(SemanticModels models, Action<string> problem)
    {
        foreach (var name in Names())
        {
            try
            {
                if (Current(name) is not { } number) continue;
                if (models.Find(name) is not null) { problem($"Published model '{name}' has the name of a model in the configuration and is not served."); continue; }
                models.Set(Load(name, number));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or UnauthorizedAccessException)
            {
                problem($"Published model '{name}' cannot be loaded: {ex.Message}");
            }
        }
    }

    private string CatalogFile(string name, int number) => Path.Combine(Root, name, $"v{number:D4}.yaml");

    private static PublishedVersion? Meta(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<PublishedVersion>(File.ReadAllText(file), Json) : null; }
        catch (JsonException) { return null; }
    }

    private static void WriteWhole(string file, string text)
    {
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();
}
