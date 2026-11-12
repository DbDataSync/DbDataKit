using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Serving;

namespace DbDataKit.Views;

/// <summary>A custom view's rows could not be resolved: the serving core's own code and words (or the resolver's), naming the query.</summary>
public sealed class CustomViewException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed partial class WidgetResolver
{
    /// <summary>The entry of a custom widget that its manifest entry or its parameters do not allow: a component of another app, or one the host serves no manifest for.</summary>
    public const string CustomNotServed = "custom_not_served";

    /// <summary>
    /// A custom widget (plan 32 section 9f, DBR31): the host runs the queries its manifest entry declares, as the viewer (the same <see cref="IServingQueries"/> and <see cref="IServingEntities"/> every widget runs through, so access, limits and
    /// audit are the host's), and gives the code the rows, by the query's name: <c>{type: custom, component, params, data: {name: {columns, rows}}}</c>. The code never fetches. A query that fails (denied, over a limit, refused) fails
    /// the widget with that query's own code and words, naming the query: a view that drew with some of its rows missing would show a wrong figure, and the rest of the page still draws. The widget's <c>params</c> (a literal or <c>$params.name</c> of
    /// the document) are what the manifest's <c>{param: name}</c> markers read; a filter on a marker with no value drops out as it does for a standard widget.
    /// </summary>
    private async Task<JsonObject> CustomAsync(ServingContext context, ViewWidget widget, ParameterValues values, CancellationToken cancellationToken)
    {
        var component = widget.Raw["component"]?.GetValue<string>() ?? "";
        var view = options.CustomViews?.Invoke(component)
            ?? throw new WidgetProblem(CustomNotServed, $"The widget `{widget.Id}` is drawn by `{component}`, which this host does not serve: a custom widget is drawn by the app that owns it.", $"widgets.{widget.Id}.component");
        if (view.Kind != CustomViewKind.Widget)
            throw new WidgetProblem(CustomNotServed, $"`{component}` is a page of the app, not a widget a dashboard can place.", $"widgets.{widget.Id}.component");

        // the values the code's parameters have: the widget's `params` (a literal or a document parameter), else the parameter's default is the code's to apply
        var given = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (widget.Raw["params"] is JsonObject map)
            foreach (var (name, node) in map)
                given[name] = node is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith("$params.", StringComparison.Ordinal) ? values.Get(text[8..])?.DeepClone() : node?.DeepClone();
        var entry = await RunCustomViewAsync(context, view, component, given, $"widgets.{widget.Id}.params", cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    /// The rows of a custom view's declared queries as the viewer, for a **page** (the export of a custom page, plan 32 DBR32/DBR33): the same run a widget gets, from the values the address gave. A query that fails throws <see cref="CustomViewException"/> with the
    /// core's code and words, naming the query; a required parameter with no value is <c>DBD-VIEW-050</c>. The result is <c>{type: custom, component, params, data}</c>.
    /// </summary>
    public async Task<JsonObject> ResolveCustomViewAsync(ServingContext context, CustomView view, string component, IReadOnlyDictionary<string, JsonNode?> parameters, CancellationToken cancellationToken = default)
    {
        try { return await RunCustomViewAsync(context, view, component, parameters.ToDictionary(p => p.Key, p => p.Value?.DeepClone()), "params", cancellationToken).ConfigureAwait(false); }
        catch (WidgetProblem p) { throw new CustomViewException(p.Code, p.Message); }
    }

    private async Task<JsonObject> RunCustomViewAsync(ServingContext context, CustomView view, string component, Dictionary<string, JsonNode?> given, string path, CancellationToken cancellationToken)
    {
        foreach (var p in view.Params)
            if (p.Required && !HasValue(given.GetValueOrDefault(p.Name)))
                throw new WidgetProblem(ResolveCodes.RequiredParameter, $"The parameter `{p.Name}` is required by `{component}` and has no value.", $"{path}.{p.Name}");

        var data = new JsonObject();
        var limits = new ServingLimits(MaxRows: options.MaxRows, Timeout: options.WidgetTimeout);
        foreach (var query in view.Queries)
        {
            try { data[query.Name] = await RunCustomQueryAsync(context, view, query, given, limits, cancellationToken).ConfigureAwait(false); }
            catch (WidgetProblem p) { throw new WidgetProblem(p.Code, $"The query `{query.Name}` of `{component}`: {p.Message}", p.Path); }
            catch (ServingException e)
            {
                var (code, message) = e switch
                {
                    ServingDeniedException => ("denied", e.Message),
                    ServingUnauthenticatedException => ("unauthenticated", e.Message),
                    ServingNotFoundException => ("not_found", e.Message),
                    ServingLimitException l => ($"limit_{l.Limit}", e.Message),
                    ServingBackendException b => ("backend_" + b.Failure.Kind.ToString().ToLowerInvariant(), e.Message),
                    _ => EngineCode(e.Message),
                };
                throw new WidgetProblem(code, $"The query `{query.Name}` of `{component}`: {message}");
            }
        }
        var entry = new JsonObject { ["type"] = "custom", ["component"] = component, ["data"] = data };
        var shown = new JsonObject();
        foreach (var (name, v) in given) if (HasValue(v)) shown[name] = v!.DeepClone();
        if (shown.Count > 0) entry["params"] = shown;
        return entry;
    }

    private static bool HasValue(JsonNode? v) => v is not null && v is not JsonArray { Count: 0 };

    private async Task<JsonObject> RunCustomQueryAsync(ServingContext context, CustomView view, CustomViewQuery query, Dictionary<string, JsonNode?> given, ServingLimits limits, CancellationToken cancellationToken)
    {
        var body = query.Body is { } yaml ? ViewsLoader.ToObject(yaml) : throw new WidgetProblem(ViewsCodes.BadQuery, "The manifest's entry carries no body for this query.");
        AnswerRows rows;
        if (query.Form == "semantic")
        {
            var request = body["request"] as JsonObject ?? throw new WidgetProblem(ViewsCodes.BadQuery, "A semantic query has a `request`.");
            var envelope = new JsonObject { ["spec_version"] = DeriveRequest.SpecVersion, ["kind"] = "query", ["query"] = request.DeepClone() };
            BindMarkers(envelope, view.Params, given);
            var source = options.ModelSourcePrefix + query.Model;
            var json = envelope.ToJsonString();
            await using var answer = await queries.RunAsync(context, new ServingRequest.Semantic(source, json, limits), cancellationToken).ConfigureAwait(false);
            rows = await AnswerRows.ReadAsync(answer, options.MaxRows, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (entities is null) throw new WidgetProblem(ResolveCodes.NotRunHere, "This host offers no Store, so a store read is not run.");
            long limit = body["limit"] is JsonValue lv && lv.TryGetValue<long>(out var l) ? l : options.MaxRows;
            await using var answer = await entities.ReadAsync(context, query.Entity!, null, cancellationToken).ConfigureAwait(false);
            rows = await AnswerRows.ReadAsync(answer, Math.Min(limit, options.MaxRows), cancellationToken, truncate: true).ConfigureAwait(false);
        }
        var columns = new JsonArray();
        if (rows.Result["descriptor"]?["schema"]?["fields"] is JsonArray fields)
            foreach (var f in fields) if (f?["name"]?.GetValue<string>() is { } n) columns.Add(n);
        return new JsonObject { ["columns"] = columns, ["rows"] = rows.Result["rows"]!.DeepClone() };
    }

    /// <summary>
    /// The request's <c>{param: name}</c> markers read the code's parameters, as a standard widget's do (<c>RequestBinding</c>): a filter whose marker has no value drops out, and the values travel beside the request as its <c>parameters</c> with the
    /// manifest's declaration as <c>parameter_definitions</c>, so the request's hash, the cache key and a replay are those of the literal form.
    /// </summary>
    private static void BindMarkers(JsonObject envelope, IReadOnlyList<CustomViewParam> declared, Dictionary<string, JsonNode?> given)
    {
        bool Marker(JsonNode? n, out string name)
        {
            name = "";
            return n is JsonObject { Count: 1 } o && o["param"] is JsonValue v && v.TryGetValue<string>(out name!);
        }
        bool Unset(JsonNode? n) => Marker(n, out var name) && !HasValue(given.GetValueOrDefault(name));
        void Prune(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["filters"] is JsonArray filters)
                        for (var i = filters.Count - 1; i >= 0; i--)
                            if (filters[i] is JsonObject f && f["values"] is { } fv)
                            {
                                if (Unset(fv)) { filters.RemoveAt(i); continue; }
                                if (fv is JsonArray list) for (var j = list.Count - 1; j >= 0; j--) if (Unset(list[j])) list.RemoveAt(j);
                                if (fv is JsonArray { Count: 0 }) filters.RemoveAt(i);
                            }
                    if (o["filters"] is JsonArray { Count: 0 }) o.Remove("filters");
                    foreach (var (_, child) in o.ToList()) Prune(child);
                    break;
                case JsonArray a:
                    foreach (var child in a.ToList()) Prune(child);
                    break;
            }
        }
        var parameters = new JsonObject();
        var definitions = new JsonObject();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o when Marker(o, out var name):
                    if (parameters.ContainsKey(name) || given.GetValueOrDefault(name) is not { } value) break;
                    parameters[name] = value.DeepClone();
                    var d = declared.FirstOrDefault(p => p.Name == name);
                    definitions[name] = d is null ? new JsonObject { ["type"] = "string" } : d.Many ? new JsonObject { ["type"] = d.Type, ["many"] = true } : new JsonObject { ["type"] = d.Type };
                    break;
                case JsonObject o: foreach (var (_, child) in o) Walk(child); break;
                case JsonArray a: foreach (var child in a) Walk(child); break;
            }
        }
        Prune(envelope["query"]);
        Walk(envelope["query"]);
        if (parameters.Count > 0) { envelope["parameters"] = parameters; envelope["parameter_definitions"] = definitions; }
    }
}
