using DbDataKit.Config.Yaml;
using DbDataKit.Serving;
using DbDataKit.State;

namespace DbDataKit.Views;

/// <summary>
/// A published dashboard or report as an instance serves it (plan 32 DBR10): the document of the version the channel serves, the model versions it was published against (their catalogs, for the roles of
/// <see cref="DeriveRequest"/> and the saved queries' models) and its files (the pictures). <see cref="Resolver"/> is a <see cref="WidgetResolver"/> that has all of that filled in.
/// </summary>
public sealed class PublishedView
{
    private readonly IReadOnlyDictionary<string, ModelCatalog> _models;
    private readonly Func<string, byte[]?> _file;

    internal PublishedView(string kind, string name, DefinitionVersion version, ViewDocument document, IReadOnlyDictionary<string, ModelCatalog> models, Func<string, byte[]?> file)
    {
        Kind = kind;
        Name = name;
        Version = version.Version;
        Document = document;
        DependsOn = version.DependsOn;
        _models = models;
        _file = file;
    }

    public string Kind { get; }

    public string Name { get; }

    public int Version { get; }

    public ViewDocument Document { get; }

    public IReadOnlyList<string> DependsOn { get; }

    /// <summary>The models the version was published against, by name.</summary>
    public IReadOnlyCollection<ModelCatalog> Models => [.. _models.Values];

    public ModelCatalog? Model(string name) => _models.GetValueOrDefault(name);

    /// <summary>The model that has the saved query: the first of the dependencies (in name order) whose catalog defines it.</summary>
    public string? SavedQueryModel(string query) => _models.Values.OrderBy(m => m.Name, StringComparer.Ordinal).FirstOrDefault(m => m.HasSavedQuery(query))?.Name;

    /// <summary>The bytes of a picture held by its hash (<c>sha256:...</c>) among the files of the version; null when it has none. The magic-byte and size check is made again where the bytes are used (<see cref="WidgetResolver"/>).</summary>
    public byte[]? ImageBytes(string hash) => _file(hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash[7..].ToLowerInvariant() : hash.ToLowerInvariant());

    /// <summary><paramref name="basis"/> with what the version supplies: the saved queries' models and the pictures' bytes.</summary>
    public ResolverOptions Options(ResolverOptions? basis = null)
    {
        basis ??= new ResolverOptions();
        return new ResolverOptions
        {
            MaxWidgets = basis.MaxWidgets, MaxRows = basis.MaxRows, MaxParallelism = basis.MaxParallelism, WidgetTimeout = basis.WidgetTimeout, Time = basis.Time,
            MaxImageBytes = basis.MaxImageBytes, ModelSourcePrefix = basis.ModelSourcePrefix, OnUnexpected = basis.OnUnexpected,
            SavedQueryModel = SavedQueryModel, ImageBytes = ImageBytes,
        };
    }

    /// <summary>A resolver for this version's widgets: the catalogs' roles by model, the saved queries' models and the pictures from the version.</summary>
    public WidgetResolver Resolver(IServingQueries queries, ResolverOptions? basis = null, IServingEntities? entities = null) =>
        new(queries, model => Model(model)?.Roles, Options(basis), entities);
}

/// <summary>
/// Reads published dashboards (or reports) through the definition follower (plan 30, DBR10): <see cref="DefinitionFollower"/> hands <see cref="Apply"/> the version a channel serves and this builds a
/// <see cref="PublishedView"/> from it before it replaces the one it served, so a version that cannot be used (a model version that does not load, a file that is gone) leaves the one served and is reported by the
/// follower. A format this program does not read is refused by the follower before it gets here (<see cref="Formats"/>).
/// </summary>
public sealed class PublishedViews(string kind, DefinitionStore store, DefinitionPublisher publisher, PayloadStore? payloads = null) : IDefinitionSubscriber
{
    private readonly Dictionary<string, PublishedView> _served = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string Kind { get; } = kind;

    public DefinitionFormats Formats => ViewDefinitions.Formats;

    /// <summary>What this instance serves, by name.</summary>
    public IReadOnlyList<PublishedView> All { get { lock (_gate) return [.. _served.Values.OrderBy(v => v.Name, StringComparer.Ordinal)]; } }

    public PublishedView? Find(string name) { lock (_gate) return _served.GetValueOrDefault(name); }

    public void Apply(string name, DefinitionVersion? current)
    {
        if (current is null)
        {
            lock (_gate) _served.Remove(name);
            return;
        }
        var view = Build(current);
        lock (_gate) _served[name] = view;
    }

    /// <summary>The view a stored version is; throws what is wrong in words.</summary>
    public PublishedView Build(DefinitionVersion version)
    {
        var file = ViewDefinitions.FileName(Kind, version.Name, version.Content);
        var loaded = ViewsLoader.Load(version.Content, file, ViewDefinitions.KindOf(Kind));
        if (loaded.Document is not { } document)
            throw new InvalidOperationException($"The {Kind} '{version.Name}' version {version.Version} does not load: {string.Join(" | ", loaded.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error))}");

        var models = new Dictionary<string, ModelCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, number) in ViewDefinitions.PinnedModels(version.DependsOn))
        {
            var content = number > 0 ? store.Get(ViewDefinitions.ModelKind, model.ToLowerInvariant(), number)?.Content : null;
            models[model] = content is null
                ? throw new InvalidOperationException($"The {Kind} '{version.Name}' version {version.Version} depends on the model '{model}' version {number}, which this group does not have.")
                : ModelCatalog.FromPublished(model, number, content);
        }

        var files = publisher.Artifacts(Kind, version.Name, version.Version).ToDictionary(a => a.Sha256, StringComparer.OrdinalIgnoreCase);
        byte[]? Bytes(string sha)
        {
            if (payloads is null || !files.TryGetValue(sha, out var artifact)) return null;
            using var reader = payloads.OpenRead(artifact.PayloadId);
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            // one byte past the cap says "too large" to the resolver without reading a whole file of any size
            while ((read = reader.Read(buffer)) > 0 && memory.Length <= ImageCheck.MaxBytes) memory.Write(buffer, 0, read);
            return memory.ToArray();
        }
        return new PublishedView(Kind, version.Name, version, document, models, Bytes);
    }
}
