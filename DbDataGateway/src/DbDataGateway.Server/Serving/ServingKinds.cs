using System.Text.Json;
using DbDataKit.Connections;
using DbDataKit.DataModel;
using DbDataKit.Semantic;
using DbDataKit.State;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The serving kinds in plan 30's registry (PB01): <c>semantic-model</c>, <c>data-source</c>, <c>entity-model</c> and the apps' <c>app</c>, <c>app-instance</c> and <c>app-nav</c>, next to the views' <c>dashboard</c> and <c>report</c> (those register
/// themselves in <see cref="GatewayViewsPublishing"/>). Each has the validator the per-kind endpoint runs before it publishes, the formats its program reads (24a UC01) and target type <c>serving</c>, mode <c>serve</c>. Slots: none for any of them
/// yet; a data source's secrets are pushed to the instance, and become bindings with PB02. A store from <see cref="StoreOf"/> publishes through this registry.
/// </summary>
public static class ServingKinds
{
    public const string TargetType = "serving";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DefinitionKinds Register(DefinitionKinds kinds)
    {
        kinds.Register(new DefinitionKind(PublishedSemanticModels.DefinitionKind, [TargetType], PublishMode.Serve) { Formats = DefinitionFormats.Of(2), Validator = ValidateModel, FormatOf = ModelFormat });
        kinds.Register(new DefinitionKind(PublishedDataSources.DefinitionKind, [TargetType], PublishMode.Serve) { Formats = DefinitionFormats.Of(ConnectionDefinition.CurrentVersion), Validator = ValidateDataSource, FormatOf = text => ConnectionDefinitionLoader.FormatOfText(text) });
        kinds.Register(new DefinitionKind(PublishedEntityModels.DefinitionKind, [TargetType], PublishMode.Serve) { Formats = DefinitionFormats.Of(DataModelLoader.Version), Validator = ValidateEntityModel, FormatOf = EntityModelFormat });
        return DbDataKit.Apps.AppDefinitions.Register(kinds);
    }

    /// <summary>The registry of the serving kinds published through <see cref="DefinitionStore"/> (the views' two have their own publisher, with the validator's sources).</summary>
    public static DefinitionKinds All { get; } = Register(new DefinitionKinds());

    /// <summary>The store over the group's state whose publishes go through <see cref="All"/>.</summary>
    public static DefinitionStore StoreOf(StateDatabase state) => new DefinitionStore(state).WithRegistry(All, TargetType);

    // what the format of a manifest is when a publish (the shared API) does not say: what the per-kind routes record, read from the content
    private static int? ModelFormat(string manifest)
    {
        try { return PublishedSemanticModels.ContentOf(manifest) is { } content ? CatalogLoader.LoadText(content.Catalog, "semantic-model").Format : null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static int? EntityModelFormat(string manifest)
    {
        try { return JsonSerializer.Deserialize<PublishedEntityModels.Content>(manifest, Json) is { Model: { Length: > 0 } model } ? DataModelLoader.Load(model, "model.yaml").Model?.SchemaVersion : null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static string? ValidateModel(DefinitionCandidate candidate)
    {
        try
        {
            var content = JsonSerializer.Deserialize<ModelContent>(candidate.Manifest, Json);
            if (content?.Catalog is null) return "The content is not a published model.";
            CatalogLoader.LoadText(content.Catalog, $"{candidate.Kind}/{candidate.Name}");
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "The catalog does not load: " + ex.Message; }
    }

    private sealed record ModelContent(string? Catalog);

    private static string? ValidateDataSource(DefinitionCandidate candidate)
    {
        var loaded = ConnectionDefinitionLoader.LoadDatabaseConnection(candidate.Manifest, candidate.Name + ".yaml");
        return loaded.Ok ? null : "The data source is not valid: " + string.Join("; ", loaded.Diagnostics.Select(d => d.Message));
    }

    private static string? ValidateEntityModel(DefinitionCandidate candidate)
    {
        try
        {
            var content = JsonSerializer.Deserialize<PublishedEntityModels.Content>(candidate.Manifest, Json);
            if (content is null || string.IsNullOrEmpty(content.Model)) return "The content is not a published entity model.";
            var loaded = DataModelLoader.Load(content.Model, candidate.Name + ".yaml");
            return loaded.Ok ? null : "The model does not load: " + string.Join("; ", loaded.Diagnostics.Select(d => d.ToString()));
        }
        catch (JsonException ex) { return "The content is not a published entity model: " + ex.Message; }
    }
}
