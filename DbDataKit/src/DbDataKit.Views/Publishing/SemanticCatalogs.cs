using System.Text.Json.Nodes;
using DbDataKit.Semantic;

namespace DbDataKit.Views;

/// <summary>
/// The production <see cref="IRequestValidator"/>: the engine's <c>Validator.ValidateRequest</c> over one model's catalog. The engine resolves a request's parameters before it validates (<c>{"param": name}</c>
/// stands for a value), so the adapter does the same with a placeholder of the type the attribute takes: what is checked is the shape of the request against the model, never a value (the viewer's values are
/// checked when the widget runs). The engine's schema check lists every branch of each <c>oneOf</c>; <see cref="CatalogCheck"/> keeps the first <c>schema_invalid</c> and counts the rest.
/// </summary>
public sealed class SemanticRequestValidator(Catalog catalog) : IRequestValidator
{
    private readonly Validator validator = new(catalog);

    public IReadOnlyList<RequestProblem> Validate(JsonObject request)
    {
        var bound = (JsonObject)request.DeepClone();
        Bind(bound, null);
        return validator.ValidateRequest(bound).Select(e => new RequestProblem(e.Code, e.Path, e.Message)).ToList();
    }

    private static bool IsMarker(JsonNode? n) => n is JsonObject { Count: 1 } o && o.ContainsKey("param");

    private JsonNode Placeholder(string? attribute) => attribute is not null && catalog.Doc.Attributes.ContainsKey(attribute)
        ? catalog.TypeOf(attribute) switch { "date" => "2026-01-01", "datetime" => "2026-01-01T00:00:00Z", "integer" => 1, "number" => 1.5, "boolean" => true, _ => "x" }
        : 100;

    private void Bind(JsonNode? node, string? attribute)
    {
        switch (node)
        {
            case JsonObject o:
                var here = o["attribute"] is JsonValue a && a.TryGetValue<string>(out var name) ? name : attribute;
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (IsMarker(o[key])) o[key] = key == "values" ? new JsonArray(Placeholder(here)) : Placeholder(null);
                    else Bind(o[key], here);
                }
                break;
            case JsonArray list:
                for (var i = 0; i < list.Count; i++)
                    if (IsMarker(list[i])) list[i] = Placeholder(attribute); else Bind(list[i], attribute);
                break;
        }
    }
}

/// <summary>A published model version as the views need it: its catalog, the roles of the catalog's names, the engine's validator over it and its saved queries.</summary>
public sealed class ModelCatalog
{
    public ModelCatalog(string name, int version, Catalog catalog)
    {
        Name = name;
        Version = version;
        Catalog = catalog;
        Roles = new CatalogRoles(name, n => catalog.IsMeasure(n) ? CatalogRole.Measure : catalog.IsAttribute(n) ? CatalogRole.Attribute : CatalogRole.Unknown);
        Binding = new CatalogBinding(Roles, new SemanticRequestValidator(catalog));
    }

    public string Name { get; }

    public int Version { get; }

    public Catalog Catalog { get; }

    /// <summary>What <see cref="DeriveRequest"/> reads: whether a name is a measure or an attribute of this model.</summary>
    public CatalogRoles Roles { get; }

    /// <summary>The roles and the validator, as <see cref="CatalogCheck"/> takes them.</summary>
    public CatalogBinding Binding { get; }

    public bool HasSavedQuery(string name) => Catalog.Doc.Queries.ContainsKey(name);

    public bool HasAttribute(string name) => Catalog.IsAttribute(name);

    /// <summary>
    /// A model from the content of a published <c>semantic-model</c> version (a JSON object whose <c>catalog</c> is the catalog's text, as the Proxy stores it); throws what the catalog loader throws.
    /// </summary>
    public static ModelCatalog FromPublished(string name, int version, string content)
    {
        var root = JsonNode.Parse(content) as JsonObject ?? throw new InvalidOperationException($"The content of {name} version {version} is not a JSON object.");
        var text = (root["catalog"] ?? root["Catalog"])?.GetValue<string>() ?? throw new InvalidOperationException($"The content of {name} version {version} has no catalog.");
        return new ModelCatalog(name, version, CatalogLoader.LoadText(text, $"semantic-model/{name} v{version}"));
    }
}
