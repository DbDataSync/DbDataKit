using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Views;

/// <summary>What the validator of a view kind reads besides the document: the published model versions it depends on and the bytes of the files of the version.</summary>
/// <param name="Model">The content of a published <c>semantic-model</c> version by name and number (the Gateway's JSON with the catalog text), or null when there is no such version.</param>
/// <param name="Bytes">The bytes of a file of the version being published; null when the host cannot read them (the size and the type are still checked, the first bytes are not).</param>
public sealed record ViewPublishingSources(Func<string, int, string?> Model, Func<ArtifactRef, byte[]?>? Bytes = null)
{
    /// <summary>The sources a group's state gives: models from <paramref name="store"/> (any channel: a version is checked, not served), file bytes from <paramref name="payloads"/>.</summary>
    public static ViewPublishingSources From(DefinitionStore store, PayloadStore? payloads = null) => new(
        (name, version) => store.Get(ViewDefinitions.ModelKind, name.ToLowerInvariant(), version)?.Content,
        payloads is null ? null : file => file.PayloadId is { } id ? ReadAll(payloads, id) : null);

    private static byte[]? ReadAll(PayloadStore payloads, Guid id)
    {
        using var reader = payloads.OpenRead(id);
        using var memory = new MemoryStream();
        // never more than the cap and one byte: the size is checked against the cap, the bytes are for the first bytes
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > ImageCheck.MaxBytes) break;
        }
        return memory.ToArray();
    }
}

/// <summary>
/// The kinds <c>dashboard</c> and <c>report</c> in plan 30's registry (plan 32 DBR10): one core, one format number (<see cref="ViewsFormat"/>), and for each kind
/// <list type="bullet">
/// <item><b>FormatOf</b>: the document's <c>schemaVersion</c>; a newer one is refused by the publisher naming kind, name and both numbers, and the follower refuses it the same way;</item>
/// <item><b>the validator</b>: the loader (<see cref="ViewsLoader"/>), then the catalog check (<see cref="CatalogCheck"/>, the engine's validator over each model version the document depends on), the saved queries and the control
/// attributes the models have, and the images the version holds (a raster by its first bytes, at most 1 MiB, named by hash among the version's files);</item>
/// <item><b>DependsOn</b>: <c>semantic-model:name@version</c> for the models the widgets query (<see cref="DependsOn"/> pins them to what a channel serves, the publisher's checks keep them active); the registry's
/// <see cref="DefinitionPublisher.PromoteWithDependencies"/> and <see cref="DefinitionPublisher.RollbackWithDependencies"/> move a document and its models together.</item>
/// </list>
/// Both kinds are accepted by <c>serving</c> targets and serve at once (a document is read, not code).
/// </summary>
public static partial class ViewDefinitions
{
    public const string Dashboard = "dashboard";
    public const string Report = "report";

    /// <summary>The kind of the model versions a document depends on (the Gateway's published models).</summary>
    public const string ModelKind = "semantic-model";

    public static IReadOnlyList<string> Kinds { get; } = [Dashboard, Report];

    /// <summary>What this program reads of both kinds: <see cref="ViewsFormat.Previous"/> to <see cref="ViewsFormat.Current"/>.</summary>
    public static DefinitionFormats Formats { get; } = new(ViewsFormat.Previous, ViewsFormat.Current);

    public static ViewKind KindOf(string kind) => kind == Report ? ViewKind.Report : ViewKind.Dashboard;

    [GeneratedRegex("^semantic-model:([A-Za-z0-9][A-Za-z0-9_.-]{0,63})(?:@([0-9]+))?$")]
    private static partial Regex ModelDependency();

    /// <summary>Registers the two kinds; the validator reads models and files through <paramref name="sources"/>.</summary>
    public static DefinitionKinds Register(DefinitionKinds kinds, ViewPublishingSources sources)
    {
        foreach (var name in Kinds)
            kinds.Register(new DefinitionKind(name, ["serving"], PublishMode.Serve) { Formats = Formats, FormatOf = FormatOf, Validator = candidate => Validate(candidate, sources) });
        return kinds;
    }

    /// <summary>A registry of the two kinds alone.</summary>
    public static DefinitionKinds Kind(ViewPublishingSources sources) => Register(new DefinitionKinds(), sources);

    /// <summary>The documents of one kind (or both) a channel has, as the registry lists them: kind, name and what the channel has active, before it and staged.</summary>
    public static IReadOnlyList<DefinitionHead> List(DefinitionPublisher publisher, string? kind = null, string channel = DefinitionStore.Production) =>
        [.. (kind is null ? Kinds : Kinds.Where(k => k == kind)).SelectMany(k => publisher.Heads(k, channel)).OrderBy(h => h.Name, StringComparer.Ordinal).ThenBy(h => h.Kind, StringComparer.Ordinal)];

    /// <summary>The file name a diagnostic says: <c>dashboard/sales.yaml</c> (the extension follows the text: JSON when it starts with a brace).</summary>
    public static string FileName(string kind, string name, string manifest) => $"{kind}/{name}.{(manifest.AsSpan().TrimStart().StartsWith("{") ? "json" : "yaml")}";

    /// <summary>The document's <c>schemaVersion</c>, or null when the text does not say (the loader says why when the version is validated).</summary>
    public static int? FormatOf(string manifest)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = JsonInput.ReadByExtension(manifest, manifest.AsSpan().TrimStart().StartsWith("{") ? "d.json" : "d.yaml", diagnostics);
        return root is YamlMapping { } map && map.Get("schemaVersion") is YamlScalar { Quoted: false } s
            && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : null;
    }

    /// <summary>The models a document's widgets query, in document order, once each.</summary>
    public static IReadOnlyList<string> Models(ViewDocument document) =>
        [.. document.Widgets.Select(w => w.Query?.Model).Where(m => m is not null).Select(m => m!).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The <c>depends_on</c> of a document: <c>semantic-model:name@version</c> for each model its widgets query, at the version <paramref name="versionOf"/> gives (the one a channel serves, say). A model it has
    /// no version for is written without one, which the validator refuses naming it. A document that does not load has no dependencies.
    /// </summary>
    public static IReadOnlyList<string> DependsOn(string kind, string name, string manifest, Func<string, int?> versionOf)
    {
        var loaded = ViewsLoader.Load(manifest, FileName(kind, name, manifest), KindOf(kind));
        if (loaded.Document is not { } document) return [];
        return [.. Models(document).Select(m => versionOf(m) is { } v ? $"{ModelKind}:{m.ToLowerInvariant()}@{v}" : $"{ModelKind}:{m.ToLowerInvariant()}")];
    }

    /// <summary>The model versions a version's <c>depends_on</c> pins, by lower-case name; an entry with no version is returned as 0.</summary>
    public static IReadOnlyDictionary<string, int> PinnedModels(IEnumerable<string> dependsOn)
    {
        var pinned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in dependsOn)
            if (ModelDependency().Match(text) is { Success: true } m)
                pinned[m.Groups[1].Value] = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return pinned;
    }

    private static string Say(IEnumerable<ConfigDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.KeyPath is null ? d.ToString() : $"{d} [{d.KeyPath}]"));

    /// <summary>The validator of both kinds: what is wrong with the candidate in words, or null.</summary>
    public static string? Validate(DefinitionCandidate candidate, ViewPublishingSources sources)
    {
        var file = FileName(candidate.Kind, candidate.Name, candidate.Manifest);
        var loaded = ViewsLoader.Load(candidate.Manifest, file, KindOf(candidate.Kind));
        if (loaded.Document is not { } document) return Say(loaded.Diagnostics);

        // the models: each must be pinned to a version that exists and loads; a model a widget queries and the document does not depend on is the check's own refusal (DBD-VIEW-043)
        var problems = new List<ConfigDiagnostic>();
        var pinned = PinnedModels(candidate.DependsOn);
        var catalogs = new Dictionary<string, ModelCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, version) in pinned)
        {
            if (version == 0)
            {
                problems.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.DependencyMissing, file, 1, 1, $"The document depends on the model `{model}` with no version.", $"Write `{ModelKind}:{model}@<version>` in depends_on."));
                continue;
            }
            var content = sources.Model(model, version);
            if (content is null)
            {
                problems.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.DependencyMissing, file, 1, 1, $"The model `{model}` has no version {version}, which the document depends on.", "Publish the model first, or name a version it has."));
                continue;
            }
            try { catalogs[model] = ModelCatalog.FromPublished(model, version, content); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problems.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.DependencyMissing, file, 1, 1, $"The model `{model}` version {version} does not load: {ex.Message}"));
            }
        }
        foreach (var model in Models(document).Where(m => !pinned.ContainsKey(m)))
            problems.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.DependencyMissing, file, 1, 1, $"The document queries the model `{model}` and does not depend on it (it depends on {(pinned.Count == 0 ? "no model" : string.Join(", ", pinned.Keys.Order().Select(k => $"`{k}`")))}).", $"Publish it with depends_on `{ModelKind}:{model.ToLowerInvariant()}@<version>`."));
        if (problems.Count > 0) return Say(problems);

        var bindings = catalogs.ToDictionary(c => c.Key, c => c.Value.Binding, StringComparer.OrdinalIgnoreCase);
        problems.AddRange(CatalogCheck.Check(document.Widgets, file, bindings));

        var used = Models(document);
        foreach (var widget in document.Widgets)
        {
            if (widget.Query is { Form: "saved" } saved && saved.Raw["saved"]?.GetValue<string>() is { } query && !catalogs.Values.Any(c => c.HasSavedQuery(query)))
                problems.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.SavedQueryNotFound, file, widget.Line, widget.Column, $"No model the document depends on has the saved query `{query}`.", "Publish the model that has it, or name another query.", $"widgets.{widget.Id}.query.saved"));
            if (widget.Type == "control" && widget.Raw["values"] is System.Text.Json.Nodes.JsonObject list && list["attribute"]?.GetValue<string>() is { } attribute && !catalogs.Where(c => used.Contains(c.Key, StringComparer.OrdinalIgnoreCase)).Any(c => c.Value.HasAttribute(attribute)))
                problems.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.ControlAttributeUnknown, file, widget.Line, widget.Column, $"No model the document's widgets query has the attribute `{attribute}`, so the control cannot list its values.", null, $"widgets.{widget.Id}.values.attribute"));
            if (widget.Type == "image") problems.AddRange(CheckImage(file, widget, candidate, sources));
        }
        return problems.Count == 0 ? null : Say(problems);
    }

    private static IEnumerable<ConfigDiagnostic> CheckImage(string file, ViewWidget widget, DefinitionCandidate candidate, ViewPublishingSources sources)
    {
        var path = $"widgets.{widget.Id}";
        var hash = widget.Raw["hash"]?.GetValue<string>() ?? "";
        var media = widget.Raw["media_type"]?.GetValue<string>() ?? "";
        var sha = hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash[7..].ToLowerInvariant() : hash.ToLowerInvariant();
        var held = candidate.Artifacts.FirstOrDefault(a => string.Equals(a.Sha256, sha, StringComparison.OrdinalIgnoreCase));
        if (held is null)
        {
            yield return DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.ImageNotInDefinition, file, widget.Line, widget.Column, $"The image `{hash}` is not among the files of this version.", "Publish the picture as a file of the version, named by its SHA-256.", path + ".hash");
            yield break;
        }
        if (held.MediaType is { } declared && !string.Equals(declared, media, StringComparison.OrdinalIgnoreCase))
        {
            yield return DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.UnsafeImage, file, widget.Line, widget.Column, $"The file `{held.Path}` is stored as `{declared}` and the widget says `{media}`.", null, path + ".media_type");
            yield break;
        }
        if (held.Size > ImageCheck.MaxBytes)
        {
            yield return DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.UnsafeImage, file, widget.Line, widget.Column, $"The image is {held.Size} bytes; a widget's image is at most {ImageCheck.MaxBytes}.", null, path + ".hash");
            yield break;
        }
        if (sources.Bytes?.Invoke(held) is { } bytes && ImageCheck.Refusal(bytes, media) is { } refusal)
            yield return DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.UnsafeImage, file, widget.Line, widget.Column, refusal, null, path + ".hash");
    }
}
