using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;

namespace DbDataGateway.Server.Serving;

/// <summary>What a published version says about itself besides its catalog.</summary>
public sealed record PublishedVersion(int Number, string? ManagedBy, string? PublishedBy, DateTimeOffset PublishedAt, Dictionary<string, string> Sources, Dictionary<string, string> Ssas);

/// <summary>
/// The semantic models published to the group through the management API (plan 19 J13), kept as the published definition <c>semantic-model/&lt;name&gt;</c> in the state the group shares (plan 20 O03): every version, and
/// the one the channel serves. A publish through any instance, or a rollback, reaches every instance (<see cref="DefinitionFollower"/> hands it to <see cref="Apply"/>), and an instance that starts while the publisher is
/// stopped serves what was published. Each instance builds a version before it serves it, so one it cannot load leaves the version it served and is reported.
/// </summary>
public sealed partial class PublishedSemanticModels(DefinitionStore store, SemanticModels models, Action<string>? problem = null) : IDefinitionSubscriber
{
    public const string DefinitionKind = "semantic-model";

    /// <summary>The folder the published models were kept in before they were in the state (plan 19 J13); read in once and renamed <c>.imported</c>.</summary>
    public const string LegacyFolder = "semantic-models";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // what this instance serves of the published models, by name, and the version it could not load
    private readonly ConcurrentDictionary<string, int> _served = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Version, string Reason)> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A version's content in the store: the catalog and what serves it. It is also the manifest of the shared publish API (<c>camelCase</c>: <c>catalog</c>, <c>managedBy</c>, <c>sources</c>, <c>ssas</c>), so a client that publishes through <c>PUT /api/v1/definitions/semantic-model/{name}</c> sends exactly this.</summary>
    public sealed record Content(string Catalog, string? ManagedBy, Dictionary<string, string> Sources, Dictionary<string, string> Ssas);

    /// <summary>The content a manifest of the shared API carries, or null when it is not one.</summary>
    public static Content? ContentOf(string manifest)
    {
        try { return JsonSerializer.Deserialize<Content>(manifest, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The owner a model's current version names in its content (<c>managed_by</c>), null for a model made by an administrator or none.</summary>
    public string? OwnerOf(string name) => Current(name) is { } number ? Version(name, number)?.ManagedBy : null;

    public string Kind => DefinitionKind;

    /// <summary>Format 1 of the semantic package, and format 2 (the sections <c>cache</c> and <c>checks</c> of the catalog, plan 24a UC03).</summary>
    public DefinitionFormats Formats => DefinitionFormats.Of(2);

    public static bool ValidName(string name) => NamePattern().IsMatch(name);

    // model names are case-insensitive; the store's key is the name in lower case (the catalog keeps its own spelling)
    private static string Key(string name) => name.ToLowerInvariant();

    public IReadOnlyList<string> Names() => [.. store.CurrentOfKind(DefinitionKind).Select(v => v.Name)];

    public int? Current(string name) => store.Current(DefinitionKind, Key(name))?.Version;

    /// <summary>The version this instance serves, or null when it serves none of the name.</summary>
    public int? Served(string name) => _served.TryGetValue(name, out var version) ? version : null;

    /// <summary>The version this instance could not load (and why), while it is the one the group serves.</summary>
    public (int Version, string Reason)? Failed(string name) => _failed.TryGetValue(name, out var failed) ? failed : null;

    public IReadOnlyList<PublishedVersion> Versions(string name) => [.. store.Versions(DefinitionKind, Key(name)).Select(Meta)];

    public string? CatalogText(string name, int number) => store.Get(DefinitionKind, Key(name), number) is { } version ? Parse(version).Catalog : null;

    public PublishedVersion? Version(string name, int number) => store.Get(DefinitionKind, Key(name), number) is { } version ? Meta(version) : null;

    /// <summary>Stores the next version and makes it the one the group serves. The caller has checked it loads.</summary>
    /// <param name="format">The format of the package (24a UC03): <see cref="Catalog.Format"/> of the catalog, 2 when it carries a <c>cache</c> or <c>checks</c> section; a publish is refused while a live instance of the group cannot read it.</param>
    public PublishedVersion Publish(string name, string catalogText, string? managedBy, string? publishedBy, Dictionary<string, string> sources, Dictionary<string, string> ssas, DateTimeOffset at, int format = 1)
    {
        var content = JsonSerializer.Serialize(new Content(catalogText, managedBy, sources, ssas), Json);
        // what the model reads, so a dependency check needs no change of format later (plan 21). Written as the registry reads a dependency, kind:name (no version: the model needs the source,
        // not a version of it), the form a dashboard's depends_on has too (plan 32 DBR11); the form data-source/name of earlier versions is never parsed and stays as stored
        var dependsOn = sources.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(s => !store.UsesRegistry || store.Current(PublishedDataSources.DefinitionKind, s) is not null).Select(s => "data-source:" + s).ToList();
        // with the registry a dependency must be a definition the channel serves: a data source set up by hand in the configuration is not one, so it is not named (PB01)
        return Meta(store.Publish(DefinitionKind, Key(name), content, publishedBy ?? "management", dependsOn, format: format));
    }

    /// <summary>Makes an earlier version the one the group serves again.</summary>
    public void Point(string name, int number, string by) => store.Switch(DefinitionKind, Key(name), number, by);

    /// <summary>Stops the group serving the model; its versions are kept.</summary>
    public void Delete(string name, string by) => store.Retire(DefinitionKind, Key(name), by);

    /// <summary>The model a version is: its catalog with its mapping. Throws what the catalog loader throws.</summary>
    public SemanticModel Load(string name, int number)
    {
        var version = store.Get(DefinitionKind, Key(name), number) ?? throw new InvalidOperationException($"Model '{name}' has no version {number}.");
        return Build(version);
    }

    private static SemanticModel Build(DefinitionVersion version)
    {
        var content = Parse(version);
        return new SemanticModel(CatalogLoader.LoadText(content.Catalog, $"{DefinitionKind}/{version.Name} v{version.Version}"), content.Sources, content.Ssas) { Version = version.Version };
    }

    /// <summary>
    /// Serves what the group now serves under <paramref name="name"/> (the follower's call): the version is built first and replaces the model only once it loads, so a version that does not leaves the one served and is
    /// reported; null takes the model out of service. A model set up in <c>server.yaml</c> under the name is left alone.
    /// </summary>
    public void Apply(string name, DefinitionVersion? current)
    {
        if (current is null)
        {
            _failed.TryRemove(name, out _);
            if (_served.TryRemove(name, out _)) models.Remove(name);
            return;
        }
        if (_served.TryGetValue(name, out var serving) && serving == current.Version) return;
        SemanticModel model;
        try { model = Build(current); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failed[name] = (current.Version, ex.Message);
            problem?.Invoke(_served.TryGetValue(name, out var kept)
                ? $"Published model '{name}' version {current.Version} cannot be loaded, so version {kept} keeps serving here: {ex.Message}"
                : $"Published model '{name}' version {current.Version} cannot be loaded and is not served here: {ex.Message}");
            throw;
        }
        if (!_served.ContainsKey(model.Name) && models.Find(model.Name) is not null)
        {
            problem?.Invoke($"Published model '{model.Name}' has the name of a model in the configuration and is not served.");
            return;
        }
        models.Set(model);
        _served[model.Name] = current.Version;
        _failed.TryRemove(model.Name, out _);
    }

    /// <summary>Serves what the group serves under the name on this instance now, rather than at the next poll (after a change made here). A version that does not load is reported and the one served stays.</summary>
    public void ServeNow(string name)
    {
        try { Apply(Key(name), store.Current(DefinitionKind, Key(name))); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* reported by Apply */ }
    }

    /// <summary>Serves again each published model this instance does not serve, as when a model of the same name has left the configuration.</summary>
    public void Refresh()
    {
        foreach (var current in store.CurrentOfKind(DefinitionKind).Where(v => !_served.ContainsKey(v.Name)))
        {
            try { Apply(current.Name, current); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* reported by Apply; the next change tries again */ }
        }
    }

    /// <summary>
    /// Reads the models an instance published before they were kept in the state (<c>semantic-models/&lt;name&gt;/v0001.yaml</c>, <c>v0001.json</c>, <c>current</c>) into it, once: each version in order, then the one that
    /// was current, after which the folder is renamed <c>semantic-models.imported</c>. A model the group already has (another instance imported it) is left as the group has it. The number of models read in.
    /// </summary>
    public int ImportFolder(string dataDirectory)
    {
        var root = Path.Combine(dataDirectory, LegacyFolder);
        if (!Directory.Exists(root)) return 0;
        var imported = 0;
        foreach (var folder in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            if (!ValidName(name) || store.Versions(DefinitionKind, Key(name)).Count > 0) continue;
            var versions = Directory.EnumerateFiles(folder, "v*.json")
                .Select(f => (Meta: JsonSerializer.Deserialize<PublishedVersion>(File.ReadAllText(f), Json), Catalog: Path.ChangeExtension(f, ".yaml")))
                .Where(v => v.Meta is not null && File.Exists(v.Catalog)).OrderBy(v => v.Meta!.Number).ToList();
            if (versions.Count == 0) continue;
            var numbers = new Dictionary<int, int>();
            foreach (var (meta, catalog) in versions)
                numbers[meta!.Number] = Publish(name, File.ReadAllText(catalog), meta.ManagedBy, meta.PublishedBy, meta.Sources, meta.Ssas, meta.PublishedAt).Number;
            var currentFile = Path.Combine(folder, "current");
            if (File.Exists(currentFile) && int.TryParse(File.ReadAllText(currentFile).Trim(), out var was) && numbers.TryGetValue(was, out var now))
                store.Switch(DefinitionKind, Key(name), now, "import");
            else
                store.Retire(DefinitionKind, Key(name), "import");
            imported++;
        }
        Directory.Move(root, root + ".imported" + (Directory.Exists(root + ".imported") ? "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) : ""));
        return imported;
    }

    private static Content Parse(DefinitionVersion version) =>
        JsonSerializer.Deserialize<Content>(version.Content, Json) ?? throw new InvalidDataException($"Version {version.Version} of '{version.Name}' is not a published model.");

    private static PublishedVersion Meta(DefinitionVersion version)
    {
        var content = Parse(version);
        return new PublishedVersion(version.Version, content.ManagedBy, version.PublishedBy, version.PublishedAt, content.Sources, content.Ssas);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();
}
