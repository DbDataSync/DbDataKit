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

        var explaining = false;
        if (isSql && text.TrimStart().StartsWith("EXPLAIN ", StringComparison.OrdinalIgnoreCase)) { explaining = true; text = text.TrimStart()[8..]; }
        var document = isSql ? TranslateOrCall(text, model, BoundParameters(request, text), UserFinder(context, model)) : Parse(text);
        if (!isSql && document["kind"]?.GetValue<string>() == "explain" && document["query"] is JsonObject explained)
        {
            // {"kind":"explain","query":{...}} is the same request as a query, planned and not run
            explaining = true;
            document = new JsonObject { ["kind"] = "query", ["query"] = explained.DeepClone() };
            foreach (var (key, value) in (JsonObject)Parse(text)) if (key is not ("kind" or "query")) document[key] = value?.DeepClone();
        }
        if (document["kind"]?.GetValue<string>() == "members") return MembersAsync(context, model, document, text, cancellationToken);
        if (document["kind"]?.GetValue<string>() == "describe") return DescribeAsync(context, model, text, cancellationToken);
        if (document["kind"]?.GetValue<string>() != "query") throw new ServingInvalidException("This server answers semantic queries ('kind': 'query'); registrations and the other request kinds are not served yet.");
        var limit = request.Asked?.MaxRows;
        var provenance = new ServingProvenance(isSql ? text : Canonical(document, model.Name, text), null, context.Channel, SemanticModels.SourcePrefix + model.Name);

        if (explaining) return Task.Run<IServingAnswer>(() =>
        {
            var scans = new List<ExplainedScan>();
            var engine = new QisEngine(model.Catalog, new ServingSourceExecutor(inner, catalog, context, model, models, cancellationToken, scans), new Validator(model.Catalog), models.Level, rowCap: 1);
            var result = engine.Run(document);
            if (result.Status == "failed") throw new ServingInvalidException(Describe(result));
            return new ExplainAnswer(scans, provenance);
        }, cancellationToken);

        // The engine blocks on the host while it runs the scans, so it runs on a thread of its own
        return Task.Run<IServingAnswer>(() =>
        {
            var engine = new QisEngine(model.Catalog, new ServingSourceExecutor(inner, catalog, context, model, models, cancellationToken), new Validator(model.Catalog), models.Level, rowCap: (int)Math.Min(limit ?? 1_000_000, 1_000_000));
            Result result;
            try { result = engine.Run(document); }
            catch (SandboxLimitException stopped) { throw new ServingLimitException(stopped.Message, stopped.Limit); }
            if (result.Status == "failed") throw new ServingInvalidException(Describe(result));
            return new SemanticAnswer(result, provenance);
        }, cancellationToken);
    }

    /// <summary>
    /// A statement on a model is prepared by reading it: the text must translate or parse, and what it leaves to be bound is reported as the parameter schema (a name, the attribute it is for, a type, whether it takes a list).
    /// Nothing is sent to a source. A client binds the values by those names (or positionally for a <c>?</c> marker) and executes.
    /// </summary>
    /// <summary>
    /// <c>{"kind": "members", "attribute": "customers.region", "search": "ea", "limit": 50}</c>: the distinct values of an attribute to choose a parameter's value from, one column <c>member</c>. They are read through the host as the caller, so the caller sees
    /// only what their access reaches; a catalog that lists its members (<c>members</c>) answers without a read.
    /// </summary>
    private Task<IServingAnswer> MembersAsync(ServingContext context, SemanticModel model, JsonObject request, string text, CancellationToken cancellationToken)
    {
        var attribute = request["attribute"]?.GetValue<string>() ?? throw new ServingInvalidException("A members request names an attribute: {\"kind\": \"members\", \"attribute\": \"customers.region\"}.");
        var search = request["search"]?.GetValue<string>();
        int? limit = request["limit"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        var provenance = new ServingProvenance(text, null, context.Channel, SemanticModels.SourcePrefix + model.Name);
        return Task.Run<IServingAnswer>(() =>
        {
            var engine = new QisEngine(model.Catalog, new ServingSourceExecutor(inner, catalog, context, model, models, cancellationToken), new Validator(model.Catalog), models.Level);
            (List<string> members, bool truncated, List<DbDataKit.Semantic.Abstractions.QisError>? errors) result;
            try { result = engine.Members(attribute, search, limit); }
            catch (SandboxLimitException stopped) { throw new ServingLimitException(stopped.Message, stopped.Limit); }
            if (result.errors is { Count: > 0 } failed) throw new ServingInvalidException(string.Join("; ", failed.Select(e => $"{e.Code}: {e.Message}")));
            return new MembersAnswer(result.members, result.truncated, provenance);
        }, cancellationToken);
    }

    /// <summary><c>{"kind": "describe"}</c>: the model's objects as a table (<see cref="DescribeAnswer"/>), the same the source's description lists, under the caller's own view of the catalog.</summary>
    private async Task<IServingAnswer> DescribeAsync(ServingContext context, SemanticModel model, string text, CancellationToken cancellationToken)
    {
        var objects = new List<ServingObject>();
        await foreach (var o in new SemanticCatalog(catalog, models).DescribeAsync(context, [], source: SemanticModels.SourcePrefix + model.Name, cancellationToken: cancellationToken).ConfigureAwait(false)) objects.Add(o);
        return new DescribeAnswer(objects, new ServingProvenance(text, null, context.Channel, SemanticModels.SourcePrefix + model.Name));
    }

    public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default)
    {
        if (!SemanticModels.IsModelSource(statement.Source)) return inner.PrepareAsync(context, statement, cancellationToken);
        var model = Model(statement.Source);
        var text = statement.Text.TrimStart();
        var isSql = !text.StartsWith('{');
        if (isSql && text.StartsWith("EXPLAIN ", StringComparison.OrdinalIgnoreCase)) text = text[8..];
        if (isSql && SemanticSqlTranslator.TranslateCall(text, model.Catalog, null, describing: true, userQueries: UserFinder(context, model)) is (true, _, var called))
        {
            // a saved query called with markers: what it will be asked for is each marker, with what the saved query says of the parameter it fills
            if (!called.Ok) throw new ServingInvalidException(string.Join("; ", called.Errors.Select(e => $"{e.Code}: {e.Message}")));
            var fills = called.Request!["parameters"] as JsonObject ?? new JsonObject();
            var definitions = called.Request["parameter_definitions"] as JsonObject ?? new JsonObject();
            var asked = fills.Where(p => p.Value is JsonObject { Count: 1 } m && m["param"] is not null).Select(p =>
            {
                var d = definitions[p.Key] as JsonObject;
                return new ServingParameterInfo(((JsonObject)p.Value!)["param"]!.GetValue<string>(), d?["for"]?.GetValue<string>(), d?["type"]?.GetValue<string>(), d?["many"]?.GetValue<bool>() ?? false);
            }).ToList();
            return Task.FromResult(new ServingStatementInfo(asked.Count, asked));
        }
        var document = isSql ? Translate(text, model, null) : Parse(text);
        if (document["kind"]?.GetValue<string>() == "explain" && document["query"] is JsonObject query) document = new JsonObject { ["kind"] = "query", ["query"] = query.DeepClone(), ["parameter_definitions"] = document["parameter_definitions"]?.DeepClone() };
        var shapes = QueryParameters.Describe(document, model.Catalog);
        return Task.FromResult(new ServingStatementInfo(shapes.Count, [.. shapes.Select(s => new ServingParameterInfo(s.Name, s.For, s.Type, s.Many))]));
    }

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

    /// <summary>The values bound with a statement (a prepared statement's parameters), as the request's <c>parameters</c>: by name, or "1", "2", ... for the unnamed ones in order.</summary>
    private static JsonObject? BoundParameters(ServingRequest request, string text)
    {
        var bound = request switch { ServingRequest.Sql s => s.Parameters, ServingRequest.SemanticSql q => q.Parameters, _ => null };
        if (bound is not { Count: > 0 }) return null;
        var markers = Markers(text);
        var result = new JsonObject();
        for (int i = 0; i < bound.Count; i++)
        {
            var parameter = bound[i];
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            result[NameOf(parameter, i, markers)] = parameter.Type switch
            {
                "list" => JsonNode.Parse(parameter.Text ?? "[]"),
                "null" => null,
                "bool" => JsonValue.Create(string.Equals(parameter.Text, "true", StringComparison.OrdinalIgnoreCase)),
                "int64" => JsonValue.Create(long.Parse(parameter.Text ?? "0", inv)),
                "double" => JsonValue.Create(double.Parse(parameter.Text ?? "0", inv)),
                "decimal" => JsonValue.Create(decimal.Parse(parameter.Text ?? "0", inv)),
                _ => JsonValue.Create(parameter.Text),
            };
        }
        return result;
    }

    /// <summary>The marker a bound value is for: its own name when the statement has a marker of that name, else the position a client's default name stands for (<c>p0</c>, <c>parameter_0</c>: the first marker), else its place in the list.</summary>
    private static string NameOf(ServingParameter parameter, int index, HashSet<string> markers)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (parameter.Name is { } name)
        {
            if (markers.Contains(name)) return name;
            var digits = name.StartsWith("parameter_", StringComparison.Ordinal) ? name[10..] : name.StartsWith('p') ? name[1..] : "";
            if (int.TryParse(digits, System.Globalization.NumberStyles.None, inv, out var position) && markers.Contains((position + 1).ToString(inv))) return (position + 1).ToString(inv);
            return name;
        }
        return (index + 1).ToString(inv);
    }

    private static HashSet<string> Markers(string text)
    {
        try
        {
            var translation = SemanticSqlTranslator.Translate(text);
            if (translation.Request is null) return [];
            return [.. QueryParameters.Describe(translation.Request, null).Select(s => s.Name)];
        }
        catch (Exception e) when (e is not OperationCanceledException) { return []; }
    }

    /// <summary>The text as a call of a saved query of the model when it is one (J26), else as semantic SQL.</summary>
    private Func<string, SavedQueryDef?>? UserFinder(ServingContext context, SemanticModel model) =>
        models.UserQueries is { } kept ? name => kept.Find(context.Caller, model.Name, name) : null;

    private static JsonObject TranslateOrCall(string sql, SemanticModel model, JsonObject? parameters, Func<string, SavedQueryDef?>? userQueries)
    {
        var (isCall, _, call) = SemanticSqlTranslator.TranslateCall(sql, model.Catalog, parameters, userQueries: userQueries);
        if (!isCall) return Translate(sql, model, parameters);
        return call.Ok ? call.Request! : throw new ServingInvalidException(string.Join("; ", call.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    private static JsonObject Translate(string sql, SemanticModel model, JsonObject? parameters)
    {
        var translation = SemanticSqlTranslator.Translate(sql, model.Name, parameters);
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
        // the saved queries (J26): each is selected as SELECT * FROM <name>(arguments); the properties say how it is called
        foreach (var (queryName, saved) in doc.Queries)
        {
            var properties = new Dictionary<string, string> { ["saved_query"] = "true", ["call"] = $"SELECT * FROM {queryName}({string.Join(", ", saved.Parameters.Select(p => p.Key))})" };
            if (saved.Parameters.Count > 0) properties["parameters"] = saved.Parameters.ToJsonString();
            yield return new ServingObject(ServingObjectKind.View, [name, queryName], saved.Description, Properties: properties);
        }
        // and the ones this caller keeps or has been given (J27), with who owns each; the model's own names win
        foreach (var kept in models.UserQueries?.List(context.Caller, name) ?? [])
        {
            if (doc.Queries.ContainsKey(kept.Name)) continue;
            var properties = new Dictionary<string, string> { ["saved_query"] = "true", ["owner"] = kept.Owner, ["call"] = $"SELECT * FROM {kept.Name}({string.Join(", ", kept.Query.Parameters.Select(p => p.Key))})" };
            if (kept.Query.Parameters.Count > 0) properties["parameters"] = kept.Query.Parameters.ToJsonString();
            yield return new ServingObject(ServingObjectKind.View, [name, kept.Name], kept.Query.Description, Properties: properties);
        }
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
