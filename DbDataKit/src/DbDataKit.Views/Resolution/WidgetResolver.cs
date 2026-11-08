using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Serving;

namespace DbDataKit.Views;

/// <summary>
/// The resolution of a layout as the viewer (plan 32 section 2.6 steps b to d, DBR04). For a document, a layout, the viewer's parameter values and optionally the ids to resolve (<c>only</c>: a parameter changed), it binds the
/// values, makes each widget's request (derived by <see cref="DeriveRequest"/>, or the written form), runs it through <see cref="IServingQueries"/> with the viewer's <see cref="ServingContext"/> (so access, limits and audit
/// are the host's and nothing here knows a rule), and shapes each answer into a rendered widget: a metric, a table (server cell text, alignment, total row), a matrix or chart (the engine's <c>ViewRenderer</c>), text with its
/// placeholders substituted, an image, the values a control lists. A widget that fails (denied, over a limit, refused by the engine, not drawable) is an <b>error entry</b>; the others render.
/// <para>
/// What each query form does here: <b>derived</b> and <b>semantic</b> (a request) run as <see cref="ServingRequest.Semantic"/> with the values bound beside it; <b>semantic SQL</b> as <see cref="ServingRequest.SemanticSql"/> with
/// its markers bound by name; <b>saved</b> as <c>SELECT * FROM name(arg := :arg)</c> on the saved query's model (the host says which: <see cref="ResolverOptions.SavedQueryModel"/>); <b>store</b> through
/// <see cref="IServingEntities"/> (the read's `limit` is applied; its `columns` are the file's declaration for the binding check, not a projection; a <c>filter</c> or <c>sort</c> is not run here, the entity read takes neither: <see cref="ResolveCodes.NotRunHere"/>); <b>ref</b> is not run here (a held result is
/// the assistant's session's, DBR12 and plan 16). A filter on a parameter the viewer left unset is dropped; a required parameter with no value is an error entry of the widgets that use it.
/// </para>
/// </summary>
public sealed class WidgetResolver
{
    private readonly IServingQueries queries;
    private readonly IServingEntities? entities;
    private readonly Func<string, CatalogRoles?> roles;
    private readonly ResolverOptions options;

    /// <param name="queries">The serving core's query service: the one place access rules are applied.</param>
    /// <param name="roles">The roles of a semantic model's members (what <see cref="DeriveRequest"/> needs), by model name; null for a model the host has no catalog of.</param>
    /// <param name="entities">The Store's reads, or null when the host offers none.</param>
    /// <param name="options">Limits and what the host supplies; the defaults when null.</param>
    public WidgetResolver(IServingQueries queries, Func<string, CatalogRoles?> roles, ResolverOptions? options = null, IServingEntities? entities = null)
    {
        this.queries = queries;
        this.roles = roles;
        this.entities = entities;
        this.options = options ?? new ResolverOptions();
    }

    /// <summary>The document parameters a widget reads, so a changed parameter resolves again only the widgets that use it.</summary>
    public static IReadOnlySet<string> UsedParameters(ViewWidget widget) => ParameterValues.Used(widget);

    /// <param name="document">The loaded dashboard or report.</param>
    /// <param name="context">The viewer: their identity, roles and attributes, the channel and the preview. Every request runs under it.</param>
    /// <param name="layout">The name of the layout in play; only the widgets it places are run.</param>
    /// <param name="parameters">What the viewer chose, by document parameter name (JSON values; text is read as the parameter's type).</param>
    /// <param name="only">Resolve only these widgets (those of them the layout places); null is every widget the layout places.</param>
    /// <param name="cancellationToken">Stops the whole resolution (a widget's own timeout is an error entry, not this).</param>
    /// <exception cref="ViewResolutionException">The layout does not exist, or a parameter value is not one.</exception>
    public async Task<RenderedWidgets> ResolveAsync(ServingContext context, ViewDocument document, string layout, IReadOnlyDictionary<string, JsonNode?>? parameters = null, IReadOnlyCollection<string>? only = null, CancellationToken cancellationToken = default)
    {
        var chosen = document.Layouts.FirstOrDefault(l => l.Name == layout)
            ?? throw new ViewResolutionException(ResolveCodes.UnknownLayout, $"The document has no layout `{layout}` (it has {string.Join(", ", document.Layouts.Select(l => $"`{l.Name}`"))}).");
        var values = new ParameterValues(document, parameters);
        var ids = chosen.Placed.Where(id => only is null || only.Contains(id)).ToList();

        var entries = new JsonObject?[ids.Count];
        var keys = new string?[ids.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxParallelism));
        await Task.WhenAll(ids.Select(async (id, index) =>
        {
            var widget = document.Widget(id);
            if (widget is null) { entries[index] = new WidgetProblem(ViewsCodes.UnknownWidget, $"The layout places `{id}`, which the document does not define.").ToEntry(); return; }
            if (index >= options.MaxWidgets)
            {
                entries[index] = new WidgetProblem(ResolveCodes.TooManyWidgets, $"The layout places more than {options.MaxWidgets} widgets, the most one resolution runs; this one was not run.").ToEntry();
                return;
            }
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (entries[index], keys[index]) = await ResolveWidgetAsync(context, document, widget, values, cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        var widgets = new Dictionary<string, JsonObject>(ids.Count);
        var cache = new Dictionary<string, string>();
        for (var i = 0; i < ids.Count; i++)
        {
            widgets[ids[i]] = entries[i]!;
            if (keys[i] is { } key) cache[ids[i]] = key;
        }
        return new RenderedWidgets(layout, options.Time.GetUtcNow(), widgets, cache);
    }

    // ---- one widget ----

    private async Task<(JsonObject Entry, string? Key)> ResolveWidgetAsync(ServingContext context, ViewDocument document, ViewWidget widget, ParameterValues values, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.WidgetTimeout, options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        string? key = null;
        try
        {
            switch (widget.Type)
            {
                case "text":
                    return (new JsonObject { ["type"] = "text", ["text"] = values.Substitute(widget.Raw["text"]?.GetValue<string>() ?? "") }, null);
                case "image":
                    return (Image(widget), null);
                case "control":
                    // a control is how a required parameter gets its value, so it never fails for the lack of one
                    return await ControlAsync(context, document, widget, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            }

            foreach (var name in ParameterValues.Used(widget))
                if (values.Definition(name) is { Required: true } && !values.IsSet(name))
                    throw new WidgetProblem(ResolveCodes.RequiredParameter, $"The parameter `{name}` is required and has no value, and this widget uses it.", $"parameters.{name}");

            var run = await PrepareAsync(widget, values, linked.Token).ConfigureAwait(false);
            key = WidgetCacheKey.Of(context, run.Source, run.Canonical);
            // a source that ignores its token is not waited for past the widget's time
            var rows = await run.Run(context, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            var shaped = widget.Type switch
            {
                "metric" => ResultShaper.Metric(widget, rows),
                "table" => ResultShaper.Table(widget, rows),
                "matrix" => ResultShaper.Matrix(widget, rows),
                "chart" => ResultShaper.Chart(widget, rows),
                _ => throw new WidgetProblem(ViewsCodes.ReservedWidget, $"A {widget.Type} widget cannot be resolved."),
            };
            return (shaped, key);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return (new WidgetProblem("timeout", $"The widget took longer than {options.WidgetTimeout.TotalSeconds:0.##} seconds and was stopped.").ToEntry(), key);
        }
        catch (WidgetProblem problem) { return (problem.ToEntry(), key); }
        catch (ServingException e) { return (Entry(e, timeout.IsCancellationRequested), key); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // nothing of an unexpected failure is shown beyond its kind: its words can name tables, servers and files (the host's log has them)
            options.OnUnexpected?.Invoke(widget.Id, e);
            return (new WidgetProblem("error", $"The widget could not be resolved ({e.GetType().Name}).").ToEntry(), key);
        }
    }

    /// <summary>The entry for a failure of the serving core, with the core's own code and words. An engine refusal says <c>code: words</c>; the code is kept as the entry's.</summary>
    private static JsonObject Entry(ServingException e, bool timedOut)
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
        return new WidgetProblem(timedOut ? "timeout" : code, message).ToEntry();
    }

    private static readonly Regex EngineWords = new(@"^(?<code>[a-z][a-z0-9_]*): (?<words>.+)$", RegexOptions.Compiled | RegexOptions.Singleline);

    private static (string Code, string Message) EngineCode(string message)
    {
        // the semantic module reports "code: words; code: words"; the first code is the entry's, all the words are kept
        var m = EngineWords.Match(message);
        return m.Success ? (m.Groups["code"].Value, message) : ("invalid", message);
    }

    // ---- what a widget asks ----

    /// <summary>A prepared widget request: the source it goes to, the request as it runs (what the cache key is made of) and how to run it and read the answer.</summary>
    private sealed record Prepared(string Source, string Canonical, Func<ServingContext, CancellationToken, Task<AnswerRows>> Run);

    private Task<Prepared> PrepareAsync(ViewWidget widget, ParameterValues values, CancellationToken cancellationToken)
    {
        var query = widget.Query ?? throw new WidgetProblem(ViewsCodes.BadQuery, "The widget has no query.", $"widgets.{widget.Id}.query");
        var map = query.Raw["params"] as JsonObject;
        var asked = new ServingLimits(MaxRows: options.MaxRows, Timeout: options.WidgetTimeout);
        var wp = $"widgets.{widget.Id}.query";

        switch (query.Form)
        {
            case "derived":
            {
                var model = query.Model ?? throw new WidgetProblem(ViewsCodes.BadQuery, "A derived query names its model.", $"{wp}.model");
                var catalog = roles(model) ?? throw new WidgetProblem(ResolveCodes.NoCatalog, $"There is no catalog of the model `{model}` here, so the request cannot be derived.", $"{wp}.model");
                var derived = DeriveRequest.Derive(widget, catalog);
                if (derived.Request is null)
                {
                    var d = derived.Diagnostics.First();
                    throw new WidgetProblem(d.Code, d.Message, d.KeyPath ?? wp);
                }
                return Task.FromResult(Semantic(model, RequestBinding.Bind(derived.Request, map, values), asked));
            }
            case "semantic" when query.Raw["semantic"] is JsonObject semantic && semantic["request"] is JsonObject written:
            {
                var model = semantic["model"]!.GetValue<string>();
                var envelope = (JsonObject)JsonNode.Parse(new JsonObject { ["spec_version"] = DeriveRequest.SpecVersion, ["kind"] = "query", ["query"] = written.DeepClone() }.ToJsonString())!;
                return Task.FromResult(Semantic(model, RequestBinding.Bind(envelope, map, values), asked));
            }
            case "semantic_sql" when query.Raw["semantic"] is JsonObject semantic && semantic["sql"] is JsonValue sql && sql.TryGetValue<string>(out var text):
            {
                var model = semantic["model"]!.GetValue<string>();
                var source = options.ModelSourcePrefix + model;
                var bound = RequestBinding.SqlParameters(ParameterValues.SqlMarkerNames(text), map, values);
                return Task.FromResult(new Prepared(source, text + "\n" + System.Text.Json.JsonSerializer.Serialize(bound), (context, ct) => RunAsync(context, new ServingRequest.SemanticSql(source, text, asked, bound), ct)));
            }
            case "saved":
            {
                var name = query.Raw["saved"]!.GetValue<string>();
                var model = options.SavedQueryModel?.Invoke(name)
                    ?? throw new WidgetProblem(ResolveCodes.NotRunHere, $"The saved query `{name}` is not run here: the widget does not say which model it belongs to, and this host did not tell the resolver.", $"{wp}.saved");
                var source = options.ModelSourcePrefix + model;
                var arguments = RequestBinding.SavedArguments(map ?? [], values);
                var text = $"SELECT * FROM {name}({string.Join(", ", arguments.Select(a => $"{a.Name} := :{a.Name}"))})";
                var bound = arguments.Select(a => a.Value).ToList();
                return Task.FromResult(new Prepared(source, text + "\n" + System.Text.Json.JsonSerializer.Serialize(bound), (context, ct) => RunAsync(context, new ServingRequest.SemanticSql(source, text, asked, bound), ct)));
            }
            case "store":
            {
                var store = query.Raw["store"] as JsonObject ?? throw new WidgetProblem(ViewsCodes.BadQuery, "A store query names its read.", $"{wp}.store");
                var entity = store["entity"]!.GetValue<string>();
                if (entities is null) throw new WidgetProblem(ResolveCodes.NotRunHere, "This host offers no Store, so a store read is not run.", $"{wp}.store");
                foreach (var part in new[] { "filter", "sort" })
                    if (store[part] is not null) throw new WidgetProblem(ResolveCodes.NotRunHere, $"A store read's `{part}` is not run here: the Store's read gives an entity's rows whole.", $"{wp}.store.{part}");
                var columns = store["columns"] is JsonArray c ? c.Select(x => x!.GetValue<string>()).ToList() : null;
                long limit = store["limit"] is JsonValue lv && lv.TryGetValue<long>(out var l) ? l : options.MaxRows;
                var canonical = $"store:{store["model"]}.{entity}|{string.Join(',', columns ?? [])}|{limit}";
                return Task.FromResult(new Prepared("store:" + entity, canonical, async (context, ct) =>
                {
                    await using var answer = await entities.ReadAsync(context, entity, null, ct).ConfigureAwait(false);
                    return await AnswerRows.ReadAsync(answer, Math.Min(limit, options.MaxRows), ct, truncate: true).ConfigureAwait(false);
                }));
            }
            default:
                throw new WidgetProblem(ResolveCodes.NotRunHere, query.Form == "ref"
                    ? $"The result `{query.Raw["ref"]}` is not run here: a held result belongs to the session that made it (an assistant's answer), and this host does not hold it."
                    : $"The query form `{query.Form}` is not run here.", wp);
        }
    }

    private Prepared Semantic(string model, JsonObject request, ServingLimits asked)
    {
        var source = options.ModelSourcePrefix + model;
        var json = request.ToJsonString();
        return new Prepared(source, json, (context, ct) => RunAsync(context, new ServingRequest.Semantic(source, json, asked), ct));
    }

    private async Task<AnswerRows> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken)
    {
        await using var answer = await queries.RunAsync(context, request, cancellationToken).ConfigureAwait(false);
        return await AnswerRows.ReadAsync(answer, options.MaxRows, cancellationToken).ConfigureAwait(false);
    }

    // ---- image and control ----

    private JsonObject Image(ViewWidget widget)
    {
        var hash = widget.Raw["hash"]!.GetValue<string>();
        var media = widget.Raw["media_type"]!.GetValue<string>();
        var bytes = options.ImageBytes?.Invoke(hash)
            ?? throw new WidgetProblem(ResolveCodes.ImageNotFound, $"The image `{hash}` is not among the definition's artifacts here.", $"widgets.{widget.Id}.hash");
        if (bytes.Length > options.MaxImageBytes) throw new WidgetProblem(ResolveCodes.TooLarge, $"The image is {bytes.Length} bytes; at most {options.MaxImageBytes} are drawn.", $"widgets.{widget.Id}.hash");
        // a raster by its first bytes, never an SVG or a renamed file (the check the renderers and the publish make, ImageCheck)
        if (ImageCheck.Refusal(bytes, media) is { } unsafeImage) throw new WidgetProblem(ViewsCodes.UnsafeImage, unsafeImage, $"widgets.{widget.Id}.hash");
        return new JsonObject { ["type"] = "image", ["src"] = $"data:{media};base64,{Convert.ToBase64String(bytes)}" };
    }

    /// <summary>
    /// A control lists the values it offers as the viewer sees them (<c>{"kind": "members", "attribute": ...}</c> on the model, under the viewer's access); a control with no list has nothing to resolve. The widget names an
    /// attribute and no model, so the model is the first one a widget of the document queries whose catalog has the attribute (provisional: DBR10 settles it with the document's dependencies).
    /// </summary>
    private async Task<(JsonObject, string?)> ControlAsync(ServingContext context, ViewDocument document, ViewWidget widget, CancellationToken cancellationToken)
    {
        if (widget.Raw["values"] is not JsonObject list) return (new JsonObject { ["type"] = "control" }, null);
        var attribute = list["attribute"]!.GetValue<string>();
        var model = document.Widgets.Select(w => w.Query?.Model).Where(m => m is not null).Distinct()
            .FirstOrDefault(m => roles(m!)?.Of(attribute) == CatalogRole.Attribute)
            ?? throw new WidgetProblem(ResolveCodes.NoCatalog, $"No model of the document has the attribute `{attribute}`, so its values cannot be listed.", $"widgets.{widget.Id}.values.attribute");
        var request = new JsonObject { ["kind"] = "members", ["attribute"] = attribute };
        if (list["limit"] is JsonValue lv && lv.TryGetValue<long>(out var limit)) request["limit"] = limit;
        var source = options.ModelSourcePrefix + model;
        var json = request.ToJsonString();
        await using var answer = await queries.RunAsync(context, new ServingRequest.Semantic(source, json, new ServingLimits(MaxRows: options.MaxRows, Timeout: options.WidgetTimeout)), cancellationToken).ConfigureAwait(false);
        var rows = await AnswerRows.ReadAsync(answer, options.MaxRows, cancellationToken).ConfigureAwait(false);
        var column = rows.Table.Fields.FirstOrDefault(f => f.Name == "member") ?? rows.Table.Fields.FirstOrDefault()
            ?? throw new WidgetProblem(ResolveCodes.AnswerMismatch, "The members answer has no column.", $"widgets.{widget.Id}.values");
        var values = new JsonArray();
        foreach (var row in rows.Table.Rows)
            if (row[column.Name] is { } member) values.Add(new JsonObject { ["value"] = member.DeepClone() });
        return (new JsonObject { ["type"] = "control", ["values"] = values }, WidgetCacheKey.Of(context, source, json));
    }
}
