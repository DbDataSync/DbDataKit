using System.Globalization;
using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Semantic.Presentation;
using DbDataKit.Serving;

namespace DbDataKit.Views;

/// <summary>A widget that cannot be drawn: the entry it gets instead (its code and words, where in the document, what would fix it). The others still draw.</summary>
internal sealed class WidgetProblem(string code, string message, string? path = null, IReadOnlyList<string>? suggestions = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Path { get; } = path;
    public IReadOnlyList<string>? Suggestions { get; } = suggestions;

    public JsonObject ToEntry()
    {
        var entry = new JsonObject { ["type"] = "error", ["code"] = Code, ["message"] = Message };
        if (Path is not null) entry["path"] = Path;
        if (Suggestions is { Count: > 0 }) entry["suggestions"] = new JsonArray(Suggestions.Select(s => (JsonNode)s).ToArray());
        return entry;
    }
}

/// <summary>An answer read into the form the views read: the result document (<c>descriptor</c> and <c>rows</c> as JSON), and the table over it.</summary>
internal sealed class AnswerRows(JsonObject result, ResultTable table)
{
    public JsonObject Result { get; } = result;
    public ResultTable Table { get; } = table;

    /// <summary>A copy that shares nothing with this one (JSON nodes have one parent), for a cache to hand out.</summary>
    public AnswerRows Copy()
    {
        var result = (JsonObject)Result.DeepClone();
        return new AnswerRows(result, new ResultTable(result));
    }

    /// <summary>
    /// Reads the batches of an answer (at most <paramref name="maxRows"/> rows: over it is an error, never a silently cut table, unless <paramref name="truncate"/> is set for a read that asked for that many). The descriptor is the engine's own when the answer carries it (<c>qis.descriptor</c> in
    /// the schema's metadata: measures and attributes, formats, additivity, the rows that are totals); an answer without one (a store read, a saved query the engine did not describe) is described from its Arrow schema,
    /// numbers as measures that add up and the rest as attributes.
    /// </summary>
    public static async Task<AnswerRows> ReadAsync(IServingAnswer answer, long maxRows, CancellationToken cancellationToken, bool truncate = false)
    {
        var schema = answer.Schema;
        var rows = new JsonArray();
        var full = false;
        await foreach (var batch in answer.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            using (batch)
                for (var i = 0; i < batch.Length && !full; i++)
                {
                    if (rows.Count >= maxRows && truncate) { full = true; break; }
                    if (rows.Count >= maxRows) throw new WidgetProblem(ResolveCodes.TooLarge, $"The answer has more than {maxRows} rows, the most a widget is given. Narrow the query or lower its `limit`.");
                    var row = new JsonObject();
                    foreach (var field in schema.FieldsList) row[field.Name] = Value(batch.Column(field.Name), i);
                    rows.Add(row);
                }
            if (full) break;
        }
        var descriptor = schema.Metadata is { } meta && meta.TryGetValue("qis.descriptor", out var text) && JsonNode.Parse(text) is JsonObject d ? d : Describe(schema);
        // as parsed JSON (the engine's own renderer reads numbers as a parsed document gives them)
        var result = (JsonObject)JsonNode.Parse(new JsonObject { ["descriptor"] = descriptor, ["rows"] = rows }.ToJsonString())!;
        return new AnswerRows(result, new ResultTable(result));
    }

    private static JsonObject Describe(Schema schema)
    {
        var fields = new JsonArray();
        foreach (var field in schema.FieldsList)
        {
            var type = field.DataType switch
            {
                Int8Type or Int16Type or Int32Type or Int64Type or UInt8Type or UInt16Type or UInt32Type or UInt64Type => "integer",
                FloatType or DoubleType or HalfFloatType or Decimal128Type or Decimal256Type => "number",
                BooleanType => "boolean",
                _ => "string",
            };
            var numeric = type is "integer" or "number";
            var f = new JsonObject { ["name"] = field.Name, ["type"] = type, ["x_kind"] = numeric ? "measure" : "attribute", ["x_binding"] = field.Name };
            if (numeric) f["x_additivity"] = "additive";
            fields.Add(f);
        }
        return new JsonObject { ["descriptor_version"] = "1.0", ["name"] = "answer", ["schema"] = new JsonObject { ["fields"] = fields } };
    }

    private static JsonNode? Value(IArrowArray array, int i)
    {
        if (array.IsNull(i)) return null;
        var inv = CultureInfo.InvariantCulture;
        return array switch
        {
            Int8Array a => JsonValue.Create((long)a.GetValue(i)!.Value),
            Int16Array a => JsonValue.Create((long)a.GetValue(i)!.Value),
            Int32Array a => JsonValue.Create((long)a.GetValue(i)!.Value),
            Int64Array a => JsonValue.Create(a.GetValue(i)!.Value),
            UInt8Array a => JsonValue.Create((long)a.GetValue(i)!.Value),
            UInt16Array a => JsonValue.Create((long)a.GetValue(i)!.Value),
            UInt32Array a => JsonValue.Create((long)a.GetValue(i)!.Value),
            UInt64Array a => JsonValue.Create((double)a.GetValue(i)!.Value),
            FloatArray a => JsonValue.Create((double)a.GetValue(i)!.Value),
            DoubleArray a => JsonValue.Create(a.GetValue(i)!.Value),
            Decimal128Array a => JsonValue.Create((double)a.GetValue(i)!.Value),
            Decimal256Array a => JsonValue.Create(double.Parse(a.GetString(i) ?? "0", inv)),
            BooleanArray a => JsonValue.Create(a.GetValue(i)!.Value),
            StringArray a => JsonValue.Create(a.GetString(i)),
            LargeStringArray a => JsonValue.Create(a.GetString(i)),
            Date32Array a => JsonValue.Create(a.GetDateOnly(i)!.Value.ToString("yyyy-MM-dd", inv)),
            Date64Array a => JsonValue.Create(a.GetDateOnly(i)!.Value.ToString("yyyy-MM-dd", inv)),
            TimestampArray a => JsonValue.Create(a.GetTimestamp(i)!.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", inv)),
            _ => throw new WidgetProblem(ResolveCodes.AnswerMismatch, $"The answer has a column of the Arrow type {array.Data.DataType.Name}, which a widget cannot draw."),
        };
    }
}

/// <summary>
/// Shapes an answer into the rendered widgets of <c>rendered-widgets</c> (plan 32 DBR04): a metric and a table here, a matrix and a chart through the engine's <see cref="ViewRenderer"/> (the one place they become data).
/// Nothing is computed that the result does not hold, except the one thing a metric's comparison needs: its change, from the two numbers the server gave.
/// </summary>
internal static class ResultShaper
{
    private const string TotalLabel = "Total";

    // ---- metric ----

    public static JsonObject Metric(ViewWidget widget, AnswerRows answer)
    {
        var encodings = widget.Raw["encodings"] as JsonObject ?? [];
        var preset = widget.Raw["format"];
        var row = answer.Table.Rows.FirstOrDefault(answer.Table.IsDetail);
        var value = Number(widget, answer, encodings["value"]?.GetValue<string>(), "encodings.value", row, preset);
        var result = new JsonObject { ["type"] = "metric", ["value"] = value?.Node };
        if (encodings["compare"] is JsonValue compare && compare.TryGetValue<string>(out var compareField))
        {
            var other = Number(widget, answer, compareField, "encodings.compare", row, preset);
            var node = other?.Node;
            if (node is not null && value?.V is { } v && other?.V is { } c && c != 0)
            {
                var change = (v - c) / Math.Abs(c) * 100;
                var rounded = Math.Round(change, 1, MidpointRounding.AwayFromZero);
                var direction = rounded > 0 ? "up" : rounded < 0 ? "down" : "flat";
                node["change"] = new JsonObject
                {
                    ["text"] = (rounded > 0 ? "+" : "") + rounded.ToString("0.#", CultureInfo.InvariantCulture) + "%",
                    ["direction"] = direction,
                };
            }
            result["compare"] = node;
        }
        return result;
    }

    private sealed record Num(JsonObject Node, double? V);

    private static Num? Number(ViewWidget widget, AnswerRows answer, string? field, string where, JsonObject? row, JsonNode? preset)
    {
        if (field is null) return null;
        var resolved = answer.Table.Resolve(field) ?? throw Missing(widget, answer, field, where);
        if (row is null || ResultTable.Number(row[resolved.Name]) is not { } n) return null;
        return new Num(new JsonObject { ["v"] = row[resolved.Name]!.DeepClone(), ["text"] = Text(n, preset, resolved.Format) }, n);
    }

    // ---- table ----

    public static JsonObject Table(ViewWidget widget, AnswerRows answer)
    {
        var columns = (widget.Raw["encodings"]?["columns"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var table = answer.Table;
        var resolved = new List<(JsonObject Spec, ResultField Field, JsonNode? Preset)>();
        for (var i = 0; i < columns.Count; i++)
        {
            var name = columns[i]["field"]!.GetValue<string>();
            var field = table.Resolve(name) ?? throw Missing(widget, answer, name, $"encodings.columns[{i}].field");
            resolved.Add((columns[i], field, columns[i]["format"]));
        }

        var header = new JsonArray();
        foreach (var (spec, field, _) in resolved)
        {
            var column = new JsonObject { ["field"] = spec["field"]!.GetValue<string>(), ["label"] = spec["label"]?.GetValue<string>() ?? spec["field"]!.GetValue<string>() };
            column["align"] = spec["align"]?.GetValue<string>() ?? (field.Kind == "measure" ? "right" : "left");
            header.Add(column);
        }

        // a total row is a row of the result and says so; the result's own order is kept, with the totals after the rows they total
        var ordered = table.Rows.Where(table.IsDetail).Concat(table.Rows.Where(r => !table.IsDetail(r)));
        var rows = new JsonArray();
        foreach (var source in ordered)
        {
            var total = !table.IsDetail(source);
            var rolled = table.RolledUp(source);
            var labelled = false;
            var cells = new JsonArray();
            foreach (var (_, field, preset) in resolved)
            {
                var node = source[field.Name];
                if (total && field.Kind == "attribute" && rolled.Contains(field.Binding ?? field.Name))
                {
                    cells.Add(new JsonObject { ["v"] = null, ["text"] = labelled ? "" : TotalLabel });
                    labelled = true;
                }
                else cells.Add(Cell(node, field, preset));
            }
            var row = new JsonObject();
            if (total) row["total"] = true;
            row["cells"] = cells;
            rows.Add(row);
        }
        return new JsonObject { ["type"] = "table", ["columns"] = header, ["rows"] = rows };
    }

    private static JsonObject Cell(JsonNode? node, ResultField field, JsonNode? preset)
    {
        if (node is null) return new JsonObject { ["v"] = null, ["text"] = "" };
        var v = node.DeepClone();
        if (ResultTable.Number(node) is { } n) return new JsonObject { ["v"] = v, ["text"] = Text(n, preset, field.Format) };
        return new JsonObject { ["v"] = v, ["text"] = node is JsonValue s && s.TryGetValue<string>(out _) ? ViewFormat.Text(node, preset) : node.ToJsonString() };
    }

    // ---- matrix and chart: the engine's renderer ----

    public static JsonObject Matrix(ViewWidget widget, AnswerRows answer)
    {
        var spec = new JsonObject { ["type"] = "matrix", ["id"] = "v" };
        foreach (var (key, value) in widget.Raw["encodings"] as JsonObject ?? []) spec[key] = value?.DeepClone();
        return Render(widget, answer, spec, "matrix");
    }

    public static JsonObject Chart(ViewWidget widget, AnswerRows answer)
    {
        var encodings = widget.Raw["encodings"] as JsonObject ?? [];
        var encoding = new JsonObject();
        foreach (var (channel, value) in encodings)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var field)) encoding[channel] = new JsonObject { ["field"] = field, ["type"] = ScaleType(answer.Table, field) };
            else if (value is JsonObject o)
            {
                var channelSpec = (JsonObject)o.DeepClone();
                if (channelSpec["type"] is null && channelSpec["field"] is JsonValue fv && fv.TryGetValue<string>(out var f)) channelSpec["type"] = ScaleType(answer.Table, f);
                encoding[channel] = channelSpec;
            }
            // a tooltip's list is resolved after the renderer (it reads one channel per object)
        }
        var vega = new JsonObject { ["mark"] = widget.Raw["mark"]?.DeepClone(), ["encoding"] = encoding };
        var rendered = Render(widget, answer, new JsonObject { ["type"] = "chart", ["id"] = "v", ["vega_lite"] = vega }, "chart");
        if (encodings["tooltip"] is JsonArray tips && rendered["vega_lite"]?["encoding"] is JsonObject drawn)
        {
            var list = new JsonArray();
            foreach (var tip in tips)
            {
                var name = tip is JsonValue tv && tv.TryGetValue<string>(out var s) ? s : tip?["field"]?.GetValue<string>();
                if (name is null) continue;
                var column = answer.Table.Resolve(name) ?? throw Missing(widget, answer, name, "encodings.tooltip");
                list.Add(new JsonObject { ["field"] = column.Name, ["type"] = ScaleType(answer.Table, name) });
            }
            drawn["tooltip"] = list;
        }
        return rendered;
    }

    private static string ScaleType(ResultTable table, string field)
    {
        var resolved = table.Resolve(field);
        if (resolved is null) return "nominal";
        if (resolved.Kind == "measure") return "quantitative";
        return resolved.Type switch { "date" or "datetime" or "timestamp" => "temporal", "integer" or "number" => "ordinal", _ => "nominal" };
    }

    private static JsonObject Render(ViewWidget widget, AnswerRows answer, JsonObject viewSpec, string type)
    {
        var document = new JsonObject
        {
            ["spec_version"] = "0.3", ["kind"] = "view", ["result"] = new JsonObject { ["ref"] = "result.widget" },
            ["views"] = new JsonArray(viewSpec),
        };
        var rendered = new ViewRenderer().Render(document, answer.Result);
        if (rendered["status"]?.GetValue<string>() != "complete" || rendered["views"] is not JsonArray { Count: 1 } views || views[0] is not JsonObject view)
        {
            var first = rendered["errors"]?.AsArray().FirstOrDefault();
            var suggestions = first?["suggestions"] is JsonArray s ? s.Select(x => x!.GetValue<string>()).ToList() : null;
            throw new WidgetProblem(first?["code"]?.GetValue<string>() ?? ResolveCodes.AnswerMismatch, first?["message"]?.GetValue<string>() ?? "The answer could not be drawn.",
                DocumentPath(widget, type, first?["path"]?.GetValue<string>()), suggestions);
        }
        view["id"] = widget.Id;
        return (JsonObject)view.DeepClone();
    }

    /// <summary>The renderer's place in its view (<c>$.views[0].rows[1]</c>, <c>$.views[0].vega_lite.encoding.x.field</c>) as the place in the document the person wrote.</summary>
    private static string DocumentPath(ViewWidget widget, string type, string? path)
    {
        var wp = $"widgets.{widget.Id}.encodings";
        if (path is null) return wp;
        const string root = "$.views[0]";
        if (!path.StartsWith(root, StringComparison.Ordinal)) return wp;
        var rest = path[root.Length..];
        if (type == "chart")
        {
            const string encoding = ".vega_lite.encoding";
            if (rest.StartsWith(encoding, StringComparison.Ordinal)) rest = rest[encoding.Length..];
            else rest = "";
            return wp + (rest.EndsWith(".field", StringComparison.Ordinal) ? rest[..^".field".Length] : rest);
        }
        return wp + rest;
    }

    private static WidgetProblem Missing(ViewWidget widget, AnswerRows answer, string field, string where) =>
        new(ResolveCodes.AnswerMismatch, $"{field} is not a column of the answer.", $"widgets.{widget.Id}.{where}", answer.Table.Suggest(field));

    /// <summary>A number as text: a mapping is the shared formatter's (<see cref="ViewFormat"/>), a preset is the engine's, as before.</summary>
    private static string Text(double n, JsonNode? format, JsonObject? catalog) =>
        format is JsonObject ? ViewFormat.Number(n, format, catalog) : MatrixView.Format(n, Preset(format), catalog);

    /// <summary>The number preset a widget names (<c>catalog</c>, <c>full</c>, <c>compact</c>, <c>percent</c>); the catalog's own format is the default and is what <c>full</c> reads.</summary>
    private static string Preset(JsonNode? format) => format is JsonValue v && v.TryGetValue<string>(out var s) && s is "compact" or "percent" ? s : "full";
}
