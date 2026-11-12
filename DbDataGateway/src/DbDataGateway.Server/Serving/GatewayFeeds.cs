using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Management;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Serving;

/// <summary>The formats a feed answers in, chosen by <c>?format=</c> or the <c>Accept</c> header (plan 14a SL31).</summary>
public enum FeedFormat
{
    Json,
    Csv,
    Arrow,
}

/// <summary>
/// The <c>feeds</c> module's routes (plan 14a SL31, was J28, route R2 of <c>docs/bi-tools-on-semantic-models.md</c>): <c>GET|POST /api/v1/queries/{model}/{name}</c> runs a saved query of the model (or of the caller) with the parameters
/// of the query string or the body, and <c>POST /api/v1/query</c> runs a semantic request or semantic SQL (<c>{model, sql | request, parameters?, limit?}</c>). The answer is JSON (the REST shape of every semantic route), CSV (RFC 4180, a
/// header row, the shape SSRS and Power BI read) or an Arrow IPC stream (<c>application/vnd.apache.arrow.stream</c>, the engine's metadata in the schema). The caller is the identity home's person behind a personal access token, an
/// access token the home issued for the Gateway, or a user name with a personal access token as the password (a report server's data source); a token of any other audience (an app's, a console's) is refused. The statement runs through
/// the same semantic module as Flight SQL, so access, limits, the run record (<c>qis.status</c>, the request hash, the caller) and the data source groups are the other protocols'. Paging is <c>limit</c> only. Every call is audited with
/// the names of its parameters and never a value.
/// </summary>
public sealed class GatewayFeeds(IOptionsMonitor<GatewayOptions> options, IServingServices serving, SemanticModels models, HomeCallers homeCallers, IAuditLog audit, TimeProvider? clock = null)
{
    public const string ArrowType = "application/vnd.apache.arrow.stream";
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly string[] Reserved = ["format", "limit"];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Text("Healthy"));
        app.MapMethods("/api/v1/queries/{model}/{name}", ["GET", "POST"], (HttpContext context, string model, string name) => SavedQueryAsync(context, model, name));
        app.MapPost("/api/v1/query", (HttpContext context, CancellationToken _) => RequestAsync(context));
        app.MapFallback(() => Results.Json(new { error = "There is no such route. A saved query is GET or POST /api/v1/queries/<model>/<name>; a request is POST /api/v1/query.", code = "not_found" }, statusCode: 404));
    }

    /// <summary>How a call asks for its answer: <c>?format=json|csv|arrow</c>, else the first of the <c>Accept</c> types this module knows, else JSON. Null when <c>format</c> names something else.</summary>
    public static FeedFormat? FormatOf(string? format, string? accept)
    {
        if (!string.IsNullOrWhiteSpace(format))
            return format.Trim().ToLowerInvariant() switch { "json" => FeedFormat.Json, "csv" => FeedFormat.Csv, "arrow" => FeedFormat.Arrow, _ => null };
        foreach (var part in (accept ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var type = part.Split(';')[0].Trim().ToLowerInvariant();
            if (type is "text/csv") return FeedFormat.Csv;
            if (type is ArrowType or "application/vnd.apache.arrow.file") return FeedFormat.Arrow;
            if (type is "application/json") return FeedFormat.Json;
        }
        return FeedFormat.Json;
    }

    /// <summary>A result as CSV: one header row of the column names, then a row each, quoted where RFC 4180 says (a comma, a quote or a line break), a missing value empty, numbers and booleans in the invariant culture.</summary>
    public static string Csv(JsonArray columns, JsonArray rows)
    {
        var text = new StringBuilder();
        static string Cell(JsonNode? value)
        {
            var s = value switch
            {
                null => "",
                JsonValue v when v.TryGetValue<string>(out var t) => t,
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                JsonValue v when v.TryGetValue<long>(out var n) => n.ToString(CultureInfo.InvariantCulture),
                JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("R", CultureInfo.InvariantCulture),
                _ => value.ToJsonString(),
            };
            return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
        }
        text.Append(string.Join(',', columns.Select(c => Cell(c?["name"])))).Append("\r\n");
        foreach (var row in rows) text.Append(string.Join(',', ((JsonArray)row!).Select(Cell))).Append("\r\n");
        return text.ToString();
    }

    private static IResult Problem(int status, string code, string message) => Results.Json(new { error = message, code }, statusCode: status);

    private async Task<(GatewayCaller? Caller, UserLabel Who, IResult? Refusal)> AuthenticateAsync(HttpContext context)
    {
        var callers = await homeCallers.GetAsync(context.RequestAborted).ConfigureAwait(false);
        if (callers.Identify(context.Request.Headers.Authorization.ToString()) is not { } user)
        {
            context.Response.Headers.WWWAuthenticate = new Microsoft.Extensions.Primitives.StringValues(["Bearer", "Basic realm=\"DbDataGateway\", charset=\"UTF-8\""]);
            return (null, new UserLabel("", null), Problem(401, "unauthenticated", "Send a personal access token (Authorization: Bearer), or a user name with a personal access token as the password (Basic), or an access token the identity home issued for this server."));
        }
        return (new GatewayCaller(HomePrincipals.Create(callers, user, "rest", options.CurrentValue)), new UserLabel(user.Id, user.DisplayName), null);
    }

    private sealed record UserLabel(string Id, string? Name);

    private int LimitOf(HttpContext context, JsonObject? body, out IResult? refusal)
    {
        refusal = null;
        var rest = options.CurrentValue.Rest;
        string? asked = context.Request.Query["limit"].FirstOrDefault();
        if (asked is null && body?["limit"] is JsonValue v) asked = v.ToJsonString();
        if (asked is null) return Math.Min(rest.DefaultLimit, rest.MaxRows);
        if (!int.TryParse(asked, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < 1 || limit > rest.MaxRows)
        {
            refusal = Problem(400, "invalid_limit", $"`limit` is a whole number from 1 to {rest.MaxRows:N0} (paging is limit only).");
            return 0;
        }
        return limit;
    }

    private async Task<IResult> SavedQueryAsync(HttpContext context, string model, string name)
    {
        var (caller, who, refusal) = await AuthenticateAsync(context).ConfigureAwait(false);
        if (refusal is not null) return refusal;
        if (!Regex.IsMatch(name, "^[a-z][a-z0-9_]*$")) return Problem(400, "invalid_name", "A saved query is called by lower-case letters, digits and _, starting with a letter.");
        if (models.Find(model) is not { } found) return Problem(404, "not_found", $"There is no semantic model '{model}' on this server.");

        JsonObject? body = null;
        if (context.Request.Method == "POST" && context.Request.ContentLength is not 0)
        {
            try { body = await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false); }
            catch (System.Text.Json.JsonException) { return Problem(400, "invalid_body", "The body is a JSON object: {\"parameters\": {...}, \"limit\": N}."); }
        }
        var limit = LimitOf(context, body, out var badLimit);
        if (badLimit is not null) return badLimit;
        if (FormatOf(context.Request.Query["format"].FirstOrDefault(), context.Request.Headers.Accept.ToString()) is not { } format)
            return Problem(400, "invalid_format", "`format` is json, csv or arrow.");

        // the values: the body's `parameters`, then the query string's (every name that is not `format` or `limit`; a name given twice is a list)
        var values = new JsonObject();
        if (body?["parameters"] is JsonObject given) foreach (var (key, value) in given) values[key] = value?.DeepClone();
        var declared = found.Catalog.Doc.Queries.TryGetValue(name, out var own) ? own.Parameters : null;
        foreach (var key in context.Request.Query.Keys.Where(k => !Reserved.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            var texts = context.Request.Query[key].Where(t => t is not null).Select(t => t!).ToList();
            var type = declared?[key]?["type"]?.GetValue<string>();
            var many = declared?[key]?["many"]?.GetValue<bool>() == true || texts.Count > 1;
            var typed = new List<JsonNode?>();
            foreach (var text in texts)
            {
                if (Typed(text, type) is not { } one) return Problem(400, "invalid_parameter", $"The parameter '{key}' is {type}; '{text}' is not.");
                typed.Add(one.Node);
            }
            values[key] = many ? new JsonArray([.. typed]) : typed[0];
        }
        foreach (var key in values.Select(v => v.Key))
            if (!Identifier.IsMatch(key)) return Problem(400, "invalid_parameter", $"A parameter is named by letters, digits and _ ('{key}' is not): a positional parameter cannot be given by name here.");

        var call = $"SELECT * FROM {name}" + (values.Count == 0 ? "" : "(" + string.Join(", ", values.Select(v => $"{v.Key} := :{v.Key}")) + ")");
        var request = new ServingRequest.SemanticSql(SemanticModels.SourcePrefix + found.Name, call, new ServingLimits(MaxRows: limit), GatewaySemanticRuns.Bind(values));
        return await AnswerAsync(context, caller!, who, request, format, limit, $"{found.Name}/{name}", values.Select(v => v.Key)).ConfigureAwait(false);
    }

    private async Task<IResult> RequestAsync(HttpContext context)
    {
        var (caller, who, refusal) = await AuthenticateAsync(context).ConfigureAwait(false);
        if (refusal is not null) return refusal;
        JsonObject? body;
        try { body = await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false); }
        catch (System.Text.Json.JsonException) { body = null; }
        var model = body?["model"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : null;
        var sql = body?["sql"] is JsonValue s && s.TryGetValue<string>(out var sqlText) ? sqlText : null;
        var requestDocument = body?["request"] as JsonObject;
        if (string.IsNullOrWhiteSpace(model) || (sql is null) == (requestDocument is null))
            return Problem(400, "invalid_body", "The body is {\"model\": \"<name>\", \"sql\": \"<semantic sql>\"} or {\"model\": ..., \"request\": {...}} (one of them), with optional \"parameters\" and \"limit\".");
        if (models.Find(model) is not { } found) return Problem(404, "not_found", $"There is no semantic model '{model}' on this server.");
        var limit = LimitOf(context, body, out var badLimit);
        if (badLimit is not null) return badLimit;
        if (FormatOf(context.Request.Query["format"].FirstOrDefault(), context.Request.Headers.Accept.ToString()) is not { } format)
            return Problem(400, "invalid_format", "`format` is json, csv or arrow.");
        var values = body?["parameters"] as JsonObject;

        ServingRequest request;
        var limits = new ServingLimits(MaxRows: limit);
        if (sql is not null) request = new ServingRequest.SemanticSql(SemanticModels.SourcePrefix + found.Name, GatewaySemanticRuns.WithLimit(sql, limit), limits, GatewaySemanticRuns.Bind(values));
        else
        {
            var document = requestDocument!.DeepClone().AsObject();
            if (document["query"] is JsonObject inner && inner["limit"] is null) inner["limit"] = limit;
            if (values is { Count: > 0 } && document["parameters"] is null) document["parameters"] = values.DeepClone();
            request = new ServingRequest.Semantic(SemanticModels.SourcePrefix + found.Name, document.ToJsonString(), limits);
        }
        return await AnswerAsync(context, caller!, who, request, format, limit, found.Name, values?.Select(v => v.Key) ?? []).ConfigureAwait(false);
    }

    /// <summary>A text from a query string as the value its definition says (<c>integer</c>, <c>number</c>, <c>boolean</c>), or as a number or a boolean when it reads as one and the query has no definition; null when it is not of the declared type.</summary>
    private static (JsonNode? Node, bool Ok)? Typed(string text, string? type)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case "string": return (JsonValue.Create(text), true);
            case "integer": return long.TryParse(text, NumberStyles.AllowLeadingSign, inv, out var n) ? (JsonValue.Create(n), true) : null;
            case "number": return double.TryParse(text, NumberStyles.Float, inv, out var d) ? (JsonValue.Create(d), true) : null;
            case "boolean": return text is "true" or "false" ? (JsonValue.Create(text == "true"), true) : null;
        }
        if (Regex.IsMatch(text, @"^-?(0|[1-9][0-9]{0,17})$") && long.TryParse(text, NumberStyles.AllowLeadingSign, inv, out var any)) return (JsonValue.Create(any), true);
        if (text is "true" or "false") return (JsonValue.Create(text == "true"), true);
        return (JsonValue.Create(text), true);
    }

    private async Task<IResult> AnswerAsync(HttpContext context, GatewayCaller caller, UserLabel who, ServingRequest request, FeedFormat format, int limit, string target, IEnumerable<string> parameterNames)
    {
        var names = string.Join(",", parameterNames.Order(StringComparer.Ordinal));
        void Record(string outcome) => audit.Append(new AuditEntry(_clock.GetUtcNow(), "rest", "REST listener", who.Id, who.Name, false, "feed.run", target, outcome + (names.Length > 0 ? $" (parameters: {names})" : ""), ""));
        try
        {
            var servingContext = new ServingContext(caller, ClientAddress: context.Connection.RemoteIpAddress?.ToString());
            await using var answer = await serving.Queries.RunAsync(servingContext, request, context.RequestAborted).ConfigureAwait(false);
            var status = SemanticRest.StatusOf(answer);
            if (format == FeedFormat.Arrow && status is "complete" or "explained")
            {
                // read once, straight into the stream: the engine's batch is gone after one read
                var (bytes, count, cut) = await ArrowAsync(answer, limit, context.RequestAborted).ConfigureAwait(false);
                Headers(context, count, cut, answer);
                Record($"ok, {count} rows, arrow");
                return Results.Bytes(bytes, ArrowType);
            }
            var result = await SemanticRest.ResultAsync(answer, context.RequestAborted).ConfigureAwait(false);
            if ((string?)result["status"] == "unsupported")
            {
                Record("unsupported");
                return Results.Json(result, statusCode: 422);
            }
            var rows = result["rows"]!.AsArray();
            var truncated = rows.Count > limit;
            while (rows.Count > limit) rows.RemoveAt(rows.Count - 1);
            result["truncated"] = truncated;
            Headers(context, rows.Count, truncated, null, result["request_hash"]?.ToJsonString().Trim('"'));
            Record($"ok, {rows.Count} rows, {format.ToString().ToLowerInvariant()}");
            return format == FeedFormat.Csv ? Results.Text(Csv(result["columns"]!.AsArray(), rows), "text/csv; charset=utf-8") : Results.Json(result);
        }
        catch (ServingException ex)
        {
            var (status, code) = ex switch
            {
                ServingNotFoundException => (404, "not_found"),
                ServingDeniedException => (403, "denied"),
                ServingUnauthenticatedException => (401, "unauthenticated"),
                ServingBackendException => (502, "unavailable"),
                ServingLimitException l => (422, l.Limit),
                _ => (400, SemanticRest.CodeOf(ex.Message) ?? "invalid"),
            };
            Record($"refused: {code}");
            return Problem(status, code, ex.Message);
        }
    }

    private static void Headers(HttpContext context, int rows, bool truncated, IServingAnswer? answer, string? hash = null)
    {
        context.Response.Headers["X-Row-Count"] = rows.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Truncated"] = truncated ? "true" : "false";
        if (hash is null && answer?.Schema.Metadata is { } m && m.TryGetValue("qis.request_hash", out var fromSchema)) hash = fromSchema;
        if (hash is not null) context.Response.Headers["X-Request-Hash"] = hash;
    }

    /// <summary>The answer as an Arrow IPC stream (schema, then the batches, cut at <paramref name="limit"/> rows): the schema carries the engine's metadata (status, request hash, warnings).</summary>
    private static async Task<(byte[] Bytes, int Rows, bool Truncated)> ArrowAsync(IServingAnswer answer, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var written = 0;
        var truncated = false;
        using (var writer = new ArrowStreamWriter(buffer, answer.Schema, leaveOpen: true))
        {
            await writer.WriteStartAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var batch in answer.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
            {
                using var held = batch;
                var left = limit - written;
                if (left <= 0) { truncated = batch.Length > 0; break; }
                if (batch.Length <= left) { await writer.WriteRecordBatchAsync(batch, cancellationToken).ConfigureAwait(false); written += batch.Length; continue; }
                using var cut = Slice(batch, left);
                await writer.WriteRecordBatchAsync(cut, cancellationToken).ConfigureAwait(false);
                written += left;
                truncated = true;
            }
            await writer.WriteEndAsync(cancellationToken).ConfigureAwait(false);
        }
        return (buffer.ToArray(), written, truncated);
    }

    private static RecordBatch Slice(RecordBatch batch, int length) => new(batch.Schema, batch.Arrays.Select(a => ArrowArrayFactory.Slice(a, 0, length)).ToList(), length);
}
