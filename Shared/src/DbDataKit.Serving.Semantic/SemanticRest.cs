using System.Text.Json;
using System.Text.Json.Nodes;
using Apache.Arrow;

namespace DbDataKit.Serving.Semantic;

/// <summary>
/// The JSON a REST caller of the semantic layer reads (plan 22a GP35c): the models a caller may use, one model's catalog and the answer to a query, in the shapes <c>@dbdatakit/semantic-client</c> expects
/// (<c>docs/apps.md</c>, "The semantic route"). Every call goes through the core services as the caller, so the host's access rules, limits and channel apply; nothing here reaches around them. A refusal is a
/// <see cref="ServingException"/> and the host turns it into a status.
/// </summary>
public static class SemanticRest
{
    /// <summary>The most rows a REST query returns: the client's own cap (<c>limit</c> 1 to 10,000).</summary>
    public const int MaxRows = 10_000;

    /// <summary><c>{models: [{name, title, version}]}</c> for the models named in <paramref name="allowed"/> that the host serves, by name.</summary>
    public static JsonObject Models(SemanticModels models, IEnumerable<string> allowed)
    {
        var list = new JsonArray();
        foreach (var name in allowed.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            if (models.Find(name) is { } found) list.Add(new JsonObject { ["name"] = found.Name, ["title"] = null, ["version"] = null });
        return new JsonObject { ["models"] = list };
    }

    /// <summary><c>{model, measures: [{name, title, description, type}], attributes: [...]}</c>: what the caller's own view of the model offers (the catalog's <c>describe</c> request, as the caller).</summary>
    public static async Task<JsonObject> CatalogAsync(IServingServices services, ServingContext context, string model, CancellationToken cancellationToken)
    {
        var answer = await services.Queries.RunAsync(context, new ServingRequest.Semantic(SemanticModels.SourcePrefix + model, """{"kind":"describe"}"""), cancellationToken).ConfigureAwait(false);
        await using var _ = answer;
        var measures = new JsonArray();
        var attributes = new JsonArray();
        await foreach (var batch in answer.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            using var held = batch;
            var kind = (StringArray)batch.Column("kind");
            var name = (StringArray)batch.Column("name");
            var type = (StringArray)batch.Column("type");
            var description = (StringArray)batch.Column("description");
            for (var i = 0; i < batch.Length; i++)
            {
                var member = new JsonObject { ["name"] = name.GetString(i), ["title"] = null, ["description"] = description.GetString(i), ["type"] = type.GetString(i) };
                switch (kind.GetString(i))
                {
                    case "measure": measures.Add(member); break;
                    case "column": attributes.Add(member); break;
                }
            }
        }
        return new JsonObject { ["model"] = model, ["measures"] = measures, ["attributes"] = attributes };
    }

    /// <summary>
    /// Runs the request envelope of <c>docs/semantic/query.md</c> (<c>{spec_version, kind: "query", query}</c>) as the caller and answers <c>{status, columns: [{name, type}], rows (arrays in column order), warnings, request_hash}</c>.
    /// Only a query is run here (a registration, an explain or a members request is not what an app asks); a request the engine will not run is answered with status <c>unsupported</c> and its reason.
    /// </summary>
    public static async Task<JsonObject> QueryAsync(IServingServices services, ServingContext context, string model, string requestJson, CancellationToken cancellationToken)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(requestJson); }
        catch (JsonException e) { throw new ServingInvalidException($"The request is not valid JSON: {e.Message}"); }
        if (parsed is not JsonObject document) throw new ServingInvalidException("A semantic request is a JSON object.");
        if (document["kind"]?.GetValue<string>() != "query") throw new ServingInvalidException("This route runs queries: {\"spec_version\": \"0.3\", \"kind\": \"query\", \"query\": {...}}.");
        if (document["query"]?["limit"] is not JsonValue limit || !limit.TryGetValue<int>(out var rows) || rows < 1 || rows > MaxRows)
            throw new ServingInvalidException($"A semantic query states a limit from 1 to {MaxRows:N0}.");

        var answer = await services.Queries.RunAsync(context, new ServingRequest.Semantic(SemanticModels.SourcePrefix + model, requestJson, new ServingLimits(MaxRows: rows)), cancellationToken).ConfigureAwait(false);
        await using var _ = answer;
        return await ResultAsync(answer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ResultAsync(IServingAnswer answer, CancellationToken cancellationToken)
    {
        var metadata = answer.Schema.Metadata ?? new Dictionary<string, string>();
        string? Meta(string key) => metadata.TryGetValue(SemanticAnswer.MetadataPrefix + key, out var v) ? v : null;
        var status = Meta("status");
        var hash = Meta("request_hash");
        var warnings = new JsonArray();
        if (Meta("warnings") is { } w && JsonNode.Parse(w) is JsonArray listed)
            foreach (var item in listed) warnings.Add(item is JsonValue v && v.TryGetValue<string>(out var text) ? text : item?.ToJsonString());

        if (status != "complete")
        {
            var remaining = Meta("remaining");
            var reason = status == "partial"
                ? "The engine left steps this host does not run" + (Meta("reason_code") is { } code ? $" ({code})" : "") + ". Ask for the measures and attributes without them."
                : $"The engine answered '{status}'.";
            return new JsonObject { ["status"] = "unsupported", ["reason"] = reason, ["columns"] = new JsonArray(), ["rows"] = new JsonArray(), ["warnings"] = warnings, ["request_hash"] = hash, ["remaining"] = remaining is null ? null : JsonNode.Parse(remaining) };
        }

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Meta("descriptor") is { } descriptor && JsonNode.Parse(descriptor)?["schema"]?["fields"] is JsonArray fields)
            foreach (var f in fields) if (f?["name"]?.GetValue<string>() is { } n) types[n] = f["type"]?.GetValue<string>() ?? "string";
        var columns = new JsonArray();
        foreach (var field in answer.Schema.FieldsList)
            columns.Add(new JsonObject { ["name"] = field.Name, ["type"] = types.TryGetValue(field.Name, out var t) ? t : TypeOf(field.DataType) });

        var rowsOut = new JsonArray();
        await foreach (var batch in answer.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            using var held = batch;
            for (var r = 0; r < batch.Length; r++)
            {
                var row = new JsonArray();
                for (var c = 0; c < batch.ColumnCount; c++) row.Add(ValueAt(batch.Column(c), r));
                rowsOut.Add(row);
            }
        }
        return new JsonObject { ["status"] = "ok", ["columns"] = columns, ["rows"] = rowsOut, ["warnings"] = warnings, ["request_hash"] = hash };
    }

    private static string TypeOf(Apache.Arrow.Types.IArrowType type) => type switch
    {
        Apache.Arrow.Types.Int8Type or Apache.Arrow.Types.Int16Type or Apache.Arrow.Types.Int32Type or Apache.Arrow.Types.Int64Type => "integer",
        Apache.Arrow.Types.FloatType or Apache.Arrow.Types.DoubleType or Apache.Arrow.Types.Decimal128Type => "number",
        Apache.Arrow.Types.BooleanType => "boolean",
        _ => "string",
    };

    private static JsonNode? ValueAt(IArrowArray array, int row)
    {
        if (array.IsNull(row)) return null;
        return array switch
        {
            StringArray a => a.GetString(row),
            Int64Array a => a.GetValue(row),
            Int32Array a => a.GetValue(row),
            Int16Array a => a.GetValue(row),
            DoubleArray a => a.GetValue(row) is { } d && double.IsFinite(d) ? d : null,
            FloatArray a => a.GetValue(row) is { } f && float.IsFinite(f) ? (double)f : null,
            BooleanArray a => a.GetValue(row),
            _ => null,
        };
    }
}
