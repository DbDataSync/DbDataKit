using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DbDataKit.Semantic;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Sql;

namespace DbDataKit.Serving.Semantic;

/// <summary>Adds the semantic module to a host's core services.</summary>
public static class SemanticServingExtensions
{
    /// <summary>The same services, with semantic requests answered by the engine and the models described in the catalog; every other request and source goes to <paramref name="inner"/> unchanged.</summary>
    public static IServingServices WithSemantic(this IServingServices inner, SemanticModels models) => new SemanticServices(inner, models);
}

internal sealed class SemanticServices(IServingServices inner, SemanticModels models) : IServingServices
{
    public IServingIdentity Identity => inner.Identity;

    public IServingSessions Sessions => inner.Sessions;

    public IServingQueries Queries { get; } = new SemanticQueries(inner.Queries, inner.Catalog, models);

    public IServingCatalog Catalog { get; } = new SemanticCatalog(inner.Catalog, models);

    public IServingEntities? Entities => inner.Entities;

    public IServingUpdates? Updates => inner.Updates;
}

/// <summary>Runs a semantic request: validate and plan in the engine, send each scan to the host as the caller, combine, answer with an Arrow table.</summary>
internal sealed class SemanticQueries(IServingQueries inner, IServingCatalog catalog, SemanticModels models) : IServingQueries
{
    public Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
    {
        var (model, text, isSql) = request switch
        {
            ServingRequest.Sql s when SemanticModels.IsModelSource(s.Source) => (Model(s.Source), s.Text, !s.Text.TrimStart().StartsWith('{')),
            ServingRequest.Semantic j => (Model(j.Source), j.Json, false),
            ServingRequest.SemanticSql q => (Model(q.Source), q.Text, true),
            _ => (null, "", false),
        };
        if (model is null) return inner.RunAsync(context, request, cancellationToken);

        var document = isSql ? Translate(text, model) : Parse(text);
        if (document["kind"]?.GetValue<string>() != "query") throw new ServingInvalidException("This server answers semantic queries ('kind': 'query'); registrations and the other request kinds are not served yet.");
        var limit = request.Asked?.MaxRows;
        var provenance = new ServingProvenance(isSql ? text : Canonical(document, model.Name, text), null, context.Channel, SemanticModels.SourcePrefix + model.Name);

        // The engine blocks on the host while it runs the scans, so it runs on a thread of its own
        return Task.Run<IServingAnswer>(() =>
        {
            var engine = new QisEngine(model.Catalog, new ServingSourceExecutor(inner, catalog, context, model, models, cancellationToken), new Validator(model.Catalog), models.Level, rowCap: (int)Math.Min(limit ?? 1_000_000, 1_000_000));
            var result = engine.Run(document);
            if (result.Status == "failed") throw new ServingInvalidException(Describe(result));
            return new SemanticAnswer(result, provenance);
        }, cancellationToken);
    }

    public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) =>
        SemanticModels.IsModelSource(statement.Source)
            ? throw new ServingInvalidException("A semantic model takes no prepared statements; send the request.")
            : inner.PrepareAsync(context, statement, cancellationToken);

    /// <summary>The query as semantic SQL when it can be written so, otherwise the request as it came.</summary>
    private static string Canonical(JsonObject document, string model, string original)
    {
        try { return SemanticSqlRenderer.Render(document, model); }
        catch (Exception e) when (e is not OperationCanceledException) { return original; }
    }

    private SemanticModel Model(string source) => models.Find(source) ?? throw new ServingNotFoundException($"There is no semantic model '{source}'.");

    private static JsonObject Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? throw new ServingInvalidException("A semantic request is a JSON object."); }
        catch (System.Text.Json.JsonException e) { throw new ServingInvalidException($"The semantic request is not valid JSON: {e.Message}"); }
    }

    private static JsonObject Translate(string sql, SemanticModel model)
    {
        var translation = SemanticSqlTranslator.Translate(sql, model.Name);
        return translation.Ok ? translation.Request! : throw new ServingInvalidException(string.Join("; ", translation.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    private static string Describe(Result result) =>
        string.Join("; ", (result.Errors ?? []).Select(e => $"{e.Code} at {e.Path}: {e.Message}").DefaultIfEmpty($"The request failed ({result.ReasonCode ?? "no reason given"})."));
}

/// <summary>Lists a model's dimensions, attributes, measures and hierarchies under its source name; every other source is the host's.</summary>
internal sealed class SemanticCatalog(IServingCatalog inner, SemanticModels models) : IServingCatalog
{
    public Task<ServingSourceFacts> FactsAsync(ServingContext context, string source, CancellationToken cancellationToken = default) =>
        SemanticModels.IsModelSource(source)
            ? Task.FromResult(models.Find(source) is null ? throw new ServingNotFoundException($"There is no semantic model '{source}'.") : new ServingSourceFacts("DbDataKit semantic model", null, ReadOnly: true))
            : inner.FactsAsync(context, source, cancellationToken);

    public async IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, bool withColumns = false, bool withPrimaryKeys = false, string source = "",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!SemanticModels.IsModelSource(source))
        {
            await foreach (var item in inner.DescribeAsync(context, under, withColumns, withPrimaryKeys, source, cancellationToken).ConfigureAwait(false)) yield return item;
            yield break;
        }

        var model = models.Find(source) ?? throw new ServingNotFoundException($"There is no semantic model '{source}'.");
        var doc = model.Catalog.Doc;
        var name = model.Name;
        yield return new ServingObject(ServingObjectKind.Model, [name]);
        foreach (var (dimension, def) in doc.Dimensions)
        {
            yield return new ServingObject(ServingObjectKind.Dimension, [name, dimension], def.Label);
            foreach (var (key, attribute) in doc.Attributes.Where(a => a.Key.StartsWith(dimension + ".", StringComparison.Ordinal)))
                yield return new ServingObject(ServingObjectKind.Column, [name, dimension, key[(dimension.Length + 1)..]], Properties: new Dictionary<string, string> { ["type"] = attribute.Type });
        }
        foreach (var (measure, def) in doc.Measures.Where(m => m.Value.Base is null && m.Value.Shift is null && m.Value.Window is null && m.Value.Index is null))
        {
            var properties = new Dictionary<string, string> { ["type"] = def.Type };
            if (def.Format is not null) properties["format"] = def.Format.ToJsonString();
            yield return new ServingObject(ServingObjectKind.Measure, [name, measure], Properties: properties);
        }
        foreach (var (hierarchy, def) in doc.Hierarchies)
        {
            yield return new ServingObject(ServingObjectKind.Hierarchy, [name, hierarchy]);
            foreach (var level in def.Levels) yield return new ServingObject(ServingObjectKind.Level, [name, hierarchy, level]);
        }
    }
}
