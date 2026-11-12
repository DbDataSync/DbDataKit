using System.Collections.Concurrent;
using System.Text.Json;
using DbDataKit.DataModel;
using DbDataKit.State;

namespace DbDataGateway.Server.Serving;

/// <summary>An entity model as an instance serves it: the model (its entities, attributes, keys, references, rules and approval), the data source its tables are on, and the version.</summary>
public sealed record ServedEntityModel(DataModel Model, string DataSource, int Version, string? ManagedBy);

/// <summary>
/// The master data models a DbDataStore server publishes to the group (plan 20 O10): kept as the published definition <c>entity-model/&lt;name&gt;</c> in the state the group shares, so every instance serves the same
/// model and enforces the same rules, with the store's server stopped or not. The content is the <c>model/1</c> file and the data source its tables are on (a data source the same client published, O04b). Changes to
/// the structure stay in the store's server, which plans and applies them; what it publishes is the model its tables have. Each instance loads a version before it serves it, so one that does not load leaves the version
/// served, and is reported.
/// </summary>
public sealed class PublishedEntityModels(DefinitionStore store, Action<string>? problem = null) : IDefinitionSubscriber
{
    public const string DefinitionKind = "entity-model";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, ServedEntityModel> _served = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Version, string Reason)> _failed = new(StringComparer.Ordinal);

    /// <summary>A version's content in the store.</summary>
    /// <param name="Slots">The data sources the version names as slots (plan 33 SW01): each is bound in the channel by the group (an administrator for a data source made by hand) before the version can serve, which takes the place of the per-publish <c>adopt</c>. Absent when the model names none, so a version published without slots reads as before.</param>
    public sealed record Content(string Model, string DataSource, string? ManagedBy, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Slots = null);

    public string Kind => DefinitionKind;

    /// <summary>Format 1 and format 2 of <c>model</c> (the sections <c>cache</c> and <c>checks</c>, plan 24a UC03): what <see cref="DataModelLoader"/> reads.</summary>
    public DefinitionFormats Formats => DefinitionFormats.Of(DataModelLoader.Version);

    /// <summary>The model this instance serves under the name; null when it serves none.</summary>
    public ServedEntityModel? Find(string name) => _served.TryGetValue(name, out var served) ? served : null;

    /// <summary>The models this instance serves.</summary>
    public IReadOnlyCollection<ServedEntityModel> All => [.. _served.Values];

    /// <summary>The version this instance could not load (and why), while it is the one the group serves.</summary>
    public (int Version, string Reason)? Failed(string name) => _failed.TryGetValue(name, out var failed) ? failed : null;

    public DefinitionVersion? Current(string name) => store.Current(DefinitionKind, name);

    public IReadOnlyList<DefinitionVersion> Versions(string name) => store.Versions(DefinitionKind, name);

    public IReadOnlyList<DefinitionVersion> Published() => store.CurrentOfKind(DefinitionKind);

    public static Content Parse(DefinitionVersion version) =>
        JsonSerializer.Deserialize<Content>(version.Content, Json) ?? throw new InvalidDataException($"Version {version.Version} of '{version.Name}' is not a published entity model.");

    /// <summary>Stores the next version (or answers the one served, when it is the same) and serves it here at once; the other instances serve it when they next look. The caller has checked it loads. Its <c>depends_on</c> is <c>data-source:name</c>, the registry's <c>kind:name</c> (as <see cref="PublishedSemanticModels"/> writes it, plan 32 DBR11); rows of earlier versions say <c>data-source/name</c> and are never parsed.</summary>
    /// <param name="format">The format of the model file (<c>schemaVersion</c>, 24a UC03: 2 when it has a <c>cache</c> or <c>checks</c> section).</param>
    public DefinitionVersion Publish(string name, string modelText, string dataSource, string? managedBy, string by, int format = 1)
    {
        var version = store.Publish(DefinitionKind, name, JsonSerializer.Serialize(new Content(modelText, dataSource, managedBy), Json), by, !store.UsesRegistry || store.Current(PublishedDataSources.DefinitionKind, dataSource) is not null ? ["data-source:" + dataSource] : [], format: format);
        ServeNow(name);
        return version;
    }

    /// <summary>Stops the group serving the model (its versions are kept), here at once.</summary>
    public void Retire(string name, string by)
    {
        store.Retire(DefinitionKind, name, by);
        ServeNow(name);
    }

    /// <summary>The follower's call: serves the version the group now serves under the name, once it loads; null takes the model out of service.</summary>
    public void Apply(string name, DefinitionVersion? current)
    {
        if (current is null)
        {
            _served.TryRemove(name, out _);
            _failed.TryRemove(name, out _);
            return;
        }
        if (_served.TryGetValue(name, out var serving) && serving.Version == current.Version) return;
        try
        {
            var content = Parse(current);
            var loaded = DataModelLoader.Load(content.Model, $"{DefinitionKind}/{name} v{current.Version}");
            if (!loaded.Ok) throw new InvalidDataException(string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
            if (loaded.Model!.Name != name) throw new InvalidDataException($"The model is named '{loaded.Model.Name}', not '{name}'.");
            _served[name] = new ServedEntityModel(loaded.Model, content.DataSource, current.Version, content.ManagedBy);
            _failed.TryRemove(name, out _);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            _failed[name] = (current.Version, ex.Message);
            problem?.Invoke(_served.TryGetValue(name, out var kept)
                ? $"Published entity model '{name}' version {current.Version} cannot be loaded, so version {kept.Version} keeps serving here: {ex.Message}"
                : $"Published entity model '{name}' version {current.Version} cannot be loaded and is not served here: {ex.Message}");
            throw;
        }
    }

    /// <summary>Serves what the group serves under the name on this instance now (after a change made here).</summary>
    public void ServeNow(string name)
    {
        try { Apply(name, store.Current(DefinitionKind, name)); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { /* reported by Apply */ }
    }
}
