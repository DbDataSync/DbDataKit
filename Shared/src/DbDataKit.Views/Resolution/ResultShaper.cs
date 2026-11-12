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

    /// <summary>The locale the widget's numbers and dates are written in (plan 32 DBR46); null is English. Given by the resolver after the answer is taken from the cache (<see cref="WithLocale"/>), so it is never part of a cache key.</summary>
    public string? Locale { get; private init; }

    /// <summary>The same answer read for a locale; shares the result and the table (the shapers only read them), so a cached answer is never changed.</summary>
    public AnswerRows WithLocale(string? locale) => new(Result, Table) { Locale = locale };

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

    /// <summary>
    /// The first column of the schema whose Arrow type <see cref="Value"/> cannot read (a time of day, a binary, a list, a struct, a half float), as its name and the type's name; null when every column can be read. Asked before the batches are read,
    /// so a caller that cannot draw the answer can still hand the table over (plan 14a D38): the check and <see cref="Value"/> are the same list.
    /// </summary>
    public static (string Name, string Type)? UnreadableColumn(Schema schema)
    {
        foreach (var field in schema.FieldsList)
            if (field.DataType is not (Int8Type or Int16Type or Int32Type or Int64Type or UInt8Type or UInt16Type or UInt32Type or UInt64Type or FloatType or DoubleType or Decimal128Type or Decimal256Type
                or BooleanType or StringType or LargeStringType or Date32Type or Date64Type or TimestampType))
                return (field.Name, field.DataType.Name);
        return null;
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
internal static partial class ResultShaper
{
    private const string TotalLabel = "Total";

    /// <summary>
    /// The name an encoded field has in the answer (plan 32 DBR50): a field name, or the object form's <c>alias</c> (the output of a filtered or repeated measure) and else its <c>field</c>.
    /// </summary>
    internal static string? Ref(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var name) ? name
        : node is JsonObject o ? (o["alias"] is JsonValue a && a.TryGetValue<string>(out var alias) && alias.Length > 0 ? alias : o["field"] is JsonValue f && f.TryGetValue<string>(out var field) ? field : null)
        : null;

    // ---- metric ----

    public static JsonObject Metric(ViewWidget widget, AnswerRows answer)
    {
        var encodings = widget.Raw["encodings"] as JsonObject ?? [];
        var preset = widget.Raw["format"];
        var row = answer.Table.Rows.FirstOrDefault(answer.Table.IsDetail);
        var value = Number(widget, answer, Ref(encodings["value"]), "encodings.value", row, FormatOf(encodings["value"], preset));
        var result = new JsonObject { ["type"] = "metric", ["value"] = value?.Node };
        if (Ref(encodings["compare"]) is { } compareField)
        {
            var other = Number(widget, answer, compareField, "encodings.compare", row, FormatOf(encodings["compare"], preset));
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

    /// <summary>The format of an entry in its object form (DBR50), else the one the widget has.</summary>
    private static JsonNode? FormatOf(JsonNode? entry, JsonNode? fallback) => entry is JsonObject o && o["format"] is { } f ? f : fallback;

    private sealed record Num(JsonObject Node, double? V);

    private static Num? Number(ViewWidget widget, AnswerRows answer, string? field, string where, JsonObject? row, JsonNode? preset)
    {
        if (field is null) return null;
        var resolved = answer.Table.Resolve(field) ?? throw Missing(widget, answer, field, where);
        if (row is null || ResultTable.Number(row[resolved.Name]) is not { } n) return null;
        return new Num(new JsonObject { ["v"] = row[resolved.Name]!.DeepClone(), ["text"] = Text(n, preset, resolved.Format, answer.Locale) }, n);
    }

    // ---- table ----

    public static JsonObject Table(ViewWidget widget, AnswerRows answer)
    {
        var columns = (widget.Raw["encodings"]?["columns"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var table = answer.Table;
        var resolved = new List<(JsonObject Spec, ResultField Field, JsonNode? Preset)>();
        for (var i = 0; i < columns.Count; i++)
        {
            var name = Ref(columns[i])!;
            var field = table.Resolve(name) ?? throw Missing(widget, answer, name, $"encodings.columns[{i}].field");
            resolved.Add((columns[i], field, columns[i]["format"]));
        }

        var header = new JsonArray();
        foreach (var (spec, field, _) in resolved)
        {
            var column = new JsonObject { ["field"] = Ref(spec)!, ["label"] = spec["label"]?.GetValue<string>() ?? Ref(spec)! };
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
            foreach (var (spec, field, preset) in resolved)
            {
                var node = source[field.Name];
                if (total && field.Kind == "attribute" && rolled.Contains(field.Binding ?? field.Name))
                {
                    cells.Add(new JsonObject { ["v"] = null, ["text"] = labelled ? "" : TotalLabel });
                    labelled = true;
                }
                else
                {
                    var cell = Cell(node, field, preset, answer.Locale);
                    if (!total) DecorateColumn(cell, spec, node);
                    cells.Add(cell);
                }
            }
            var row = new JsonObject();
            if (total) row["total"] = true;
            row["cells"] = cells;
            rows.Add(row);
        }
        return new JsonObject { ["type"] = "table", ["columns"] = header, ["rows"] = rows };
    }

    private static JsonObject Cell(JsonNode? node, ResultField field, JsonNode? preset, string? locale)
    {
        if (node is null) return new JsonObject { ["v"] = null, ["text"] = "" };
        var v = node.DeepClone();
        if (ResultTable.Number(node) is { } n) return new JsonObject { ["v"] = v, ["text"] = Text(n, preset, field.Format, locale) };
        return new JsonObject { ["v"] = v, ["text"] = node is JsonValue s && s.TryGetValue<string>(out _) ? ViewFormat.Text(node, preset, field.Format, locale) : node.ToJsonString() };
    }

    // ---- matrix and chart: the engine's renderer ----

    public static JsonObject Matrix(ViewWidget widget, AnswerRows answer)
    {
        var encodings = widget.Raw["encodings"] as JsonObject ?? [];
        // attributes among the values (DBR51): the engine draws a synthetic measure in their place, the cells are filled from the rows that name their values afterwards
        (answer, encodings, var cellPlan) = PrepareCellAttributes(widget, answer, encodings);
        var spec = new JsonObject { ["type"] = "matrix", ["id"] = "v" };
        // the engine's view reads field names, a sort per axis and the presets: the widget's `{field, format, sort}` entries and a value's mapping format are split off here, and applied to the labels and cells it gives back
        var axisFormats = new Dictionary<string, List<JsonNode?>> { ["rows"] = [], ["columns"] = [] };
        var axisSorts = new Dictionary<string, JsonNode?>();
        var valueFormats = new List<JsonNode?>();
        foreach (var (key, value) in encodings)
        {
            if (key is "rows" or "columns" && value is JsonArray axis)
            {
                var names = new JsonArray();
                foreach (var entry in axis)
                {
                    if (entry is JsonObject o)
                    {
                        names.Add(o["field"]?.DeepClone());
                        axisFormats[key].Add(o["format"]?.DeepClone());
                        if (o["sort"] is { } sort) axisSorts[key] = sort.DeepClone();
                    }
                    else { names.Add(entry?.DeepClone()); axisFormats[key].Add(null); }
                }
                spec[key] = names;
            }
            else if (key == "values" && value is JsonArray values)
            {
                var list = new JsonArray();
                foreach (var entry in values)
                {
                    var copy = entry is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
                    // the engine's matrix reads the column of the answer: a filtered or repeated measure is the column its alias names (DBR50)
                    if (Ref(copy) is { } output) copy["field"] = output;
                    copy.Remove("alias");
                    copy.Remove("filters");
                    var format = copy["format"];
                    valueFormats.Add(format);
                    // a preset the engine knows stays with the engine; the catalog's own format is what `full` reads, and a mapping is applied to the cells afterwards
                    if (format is JsonValue fv && fv.TryGetValue<string>(out var preset)) copy["format"] = preset is "compact" or "percent" ? preset : "full";
                    else copy.Remove("format");
                    list.Add(copy);
                }
                spec[key] = list;
            }
            else spec[key] = value?.DeepClone();
        }
        foreach (var (axis, sort) in axisSorts)
        {
            if (encodings["sort"]?[axis] is not null)
                throw new WidgetProblem(ResolveCodes.AnswerMismatch, $"The {axis} are sorted twice: by an entry's `sort` and by `sort.{axis}`. Say it once.", $"widgets.{widget.Id}.encodings.sort.{axis}");
            var sorts = spec["sort"] as JsonObject ?? new JsonObject();
            sorts[axis] = sort;
            spec["sort"] = sorts;
        }
        var rendered = Render(widget, answer, spec, "matrix");
        FormatMatrix(rendered, answer, encodings, axisFormats, valueFormats);
        if (cellPlan is not null) FillCellAttributes(rendered, cellPlan, answer);
        return rendered;
    }

    /// <summary>The axis labels and the cells of a rendered matrix under the formats the widget gave them (the engine wrote the labels as the members are and the cells as its presets).</summary>
    private static void FormatMatrix(JsonObject rendered, AnswerRows answer, JsonObject encodings, Dictionary<string, List<JsonNode?>> axisFormats, List<JsonNode?> valueFormats)
    {
        JsonObject? CatalogOf(JsonNode? field) => field is JsonValue v && v.TryGetValue<string>(out var name) ? answer.Table.Resolve(name)?.Format : null;
        var axes = new[] { ("rows", rendered["rows"] as JsonArray), ("columns", rendered["columns"] as JsonArray) };
        foreach (var (name, items) in axes)
        {
            var fields = encodings[name] as JsonArray ?? [];
            if (items is null) continue;
            foreach (var item in items.OfType<JsonObject>())
            {
                if (item["total"]?.GetValue<bool>() == true || item["other"]?.GetValue<bool>() == true || item["labels"] is not JsonArray labels) continue;
                for (var level = 0; level < axisFormats[name].Count && level < labels.Count; level++)
                {
                    var field = fields[level] is JsonObject o ? o["field"] : fields[level];
                    // a member under no format of the widget reads the catalog's own date format when the attribute has one (DBR47)
                    var format = axisFormats[name][level] ?? (CatalogOf(field)?["date"] is not null ? JsonValue.Create("catalog") : null);
                    if (format is null || labels[level] is not JsonValue lv || !lv.TryGetValue<string>(out var label)) continue;
                    labels[level] = ViewFormat.Label(label, format, CatalogOf(field), answer.Locale);
                }
            }
        }
        // a locale other than English rewrites every number the engine wrote as its English text; a mapping always does
        var localized = answer.Locale is not (null or "en");
        if (!localized && !valueFormats.Any(f => f is JsonObject)) return;
        var count = valueFormats.Count;
        var onRows = encodings["values_on"]?.GetValue<string>() == "rows";
        var valueFields = (encodings["values"] as JsonArray ?? []).Select(v => Ref(v) is { } name ? (JsonNode)JsonValue.Create(name) : null).ToList();
        var rows = rendered["rows"] as JsonArray ?? [];
        for (var r = 0; r < rows.Count; r++)
        {
            if (rows[r]?["cells"] is not JsonArray cells) continue;
            for (var c = 0; c < cells.Count; c++)
            {
                // the values come in turn: one per column under each member, or one per row under each member
                var index = (onRows ? r : c) % count;
                if ((valueFormats[index] is not JsonObject && !localized) || cells[c] is not JsonObject cell || ResultTable.Number(cell["v"]) is not { } number) continue;
                cell["text"] = ViewFormat.Number(number, valueFormats[index], CatalogOf(valueFields[index]), answer.Locale);
            }
        }
    }

    public static JsonObject Chart(ViewWidget widget, AnswerRows answer)
    {
        var encodings = widget.Raw["encodings"] as JsonObject ?? [];
        var encoding = new JsonObject();
        // a channel's `format` is not the engine's (its subset is closed): it is taken off here and drawn after the engine has resolved the channel
        var formats = new Dictionary<string, JsonNode>();
        // a list of one level is the plain channel
        if (encodings["x"] is JsonArray { Count: 1 } oneLevel) { encodings = (JsonObject)encodings.DeepClone(); encodings["x"] = oneLevel[0]?.DeepClone(); }
        // a list of one measure on `y` is the plain channel; several are one series each (DBR50): the answer is folded into a `series` and a `value` column before the engine draws it
        if (encodings["y"] is JsonArray { Count: 1 } oneSeries) { encodings = (JsonObject)encodings.DeepClone(); encodings["y"] = oneSeries[0]?.DeepClone(); }
        if (encodings["y"] is JsonArray { Count: > 1 } series)
        {
            if (encodings["color"] is not null)
                throw new WidgetProblem(ViewsCodes.InvalidValue, "A chart with several measures on `y` has no `color`: the series are the colors.", $"widgets.{widget.Id}.encodings.color",
                    ["Move the field into `column` or `row`, or keep one measure on `y`."]);
            (answer, encodings) = FoldSeries(widget, answer, encodings, series);
        }
        // a nested `x` (DBR42): the innermost level is the engine's `x`, the outermost its `column`; the levels are drawn after the engine has resolved them
        var nested = ReadNestedLevels(encodings);
        foreach (var (channel, value) in encodings)
        {
            if (nested is not null && channel == "x")
            {
                encoding["x"] = nested.EngineChannel(nested.Inner, answer.Table);
                encoding["column"] = nested.EngineChannel(nested.Outer, answer.Table);
                continue;
            }
            if (value is JsonValue v && v.TryGetValue<string>(out var field))
            {
                encoding[channel] = new JsonObject { ["field"] = field, ["type"] = ScaleType(answer.Table, field) };
                if (CatalogDate(answer.Table, field)) formats[channel] = JsonValue.Create("catalog");
            }
            else if (value is JsonObject o)
            {
                var channelSpec = (JsonObject)o.DeepClone();
                // a filtered or repeated measure is the column its alias names (DBR50); the engine's subset knows neither key
                if (Ref(channelSpec) is { } outputName) channelSpec["field"] = outputName;
                channelSpec.Remove("alias");
                channelSpec.Remove("filters");
                if (channelSpec["type"] is null && channelSpec["field"] is JsonValue fv && fv.TryGetValue<string>(out var f)) channelSpec["type"] = ScaleType(answer.Table, f);
                if (channelSpec["format"] is { } format) formats[channel] = format.DeepClone();
                else if (channelSpec["field"] is JsonValue cf && cf.TryGetValue<string>(out var cfield) && CatalogDate(answer.Table, cfield)) formats[channel] = JsonValue.Create("catalog");
                channelSpec.Remove("format");
                encoding[channel] = channelSpec;
            }
            // a tooltip's list is resolved after the renderer (it reads one channel per object)
        }
        var vega = new JsonObject { ["mark"] = widget.Raw["mark"]?.DeepClone(), ["encoding"] = encoding };
        var rendered = Render(widget, answer, new JsonObject { ["type"] = "chart", ["id"] = "v", ["vega_lite"] = vega }, "chart");
        var tipFormats = new List<JsonNode?>();
        if (encodings["tooltip"] is JsonArray tips && rendered["vega_lite"]?["encoding"] is JsonObject drawn)
        {
            var list = new JsonArray();
            foreach (var tip in tips)
            {
                var name = Ref(tip);
                if (name is null) continue;
                var column = answer.Table.Resolve(name) ?? throw Missing(widget, answer, name, "encodings.tooltip");
                list.Add(new JsonObject { ["field"] = column.Name, ["type"] = ScaleType(answer.Table, name) });
                tipFormats.Add(tip is JsonObject to ? to["format"]?.DeepClone() : null);
            }
            drawn["tooltip"] = list;
        }
        else if (encodings["tooltip"] is JsonObject single && single["format"] is { } tf) tipFormats.Add(tf.DeepClone());
        FormatChart(rendered, answer, formats, tipFormats);
        if (nested is not null) DrawNestedAxis(widget, rendered, answer, nested);
        return rendered;
    }

    /// <summary>
    /// Several measures on a chart's <c>y</c> (plan 32 DBR50) are one series each: every row of the answer becomes one row per measure, in a <c>series</c> column (the entry's <c>title</c>, else its alias or field) and a
    /// <c>value</c> column, and the chart is drawn with <c>y: value</c>, <c>color: series</c> (the series keep the order of the list). The totals' level column is kept, so a total row stays a total. The axis takes
    /// the entries' format when they all have the same one. Returns the folded answer and the encodings to draw.
    /// </summary>
    private static (AnswerRows Answer, JsonObject Encodings) FoldSeries(ViewWidget widget, AnswerRows answer, JsonObject encodings, JsonArray series)
    {
        var entries = new List<(string Output, string Label, ResultField Field, JsonNode? Format, string? Title)>();
        for (var i = 0; i < series.Count; i++)
        {
            var name = Ref(series[i]) ?? throw new WidgetProblem(ViewsCodes.InvalidValue, "A `y` list names measures.", $"widgets.{widget.Id}.encodings.y[{i}]");
            var field = answer.Table.Resolve(name) ?? throw Missing(widget, answer, name, $"encodings.y[{i}].field");
            var title = series[i] is JsonObject o ? o["title"]?.GetValue<string>() : null;
            entries.Add((field.Name, title ?? name, field, series[i] is JsonObject so ? so["format"]?.DeepClone() : null, title));
        }
        var drop = entries.Select(e => e.Output).ToHashSet();
        var result = new JsonObject();
        var descriptor = (JsonObject)answer.Result["descriptor"]!.DeepClone();
        var fields = descriptor["schema"]!["fields"]!.AsArray();
        for (var i = fields.Count - 1; i >= 0; i--) if (drop.Contains(fields[i]!["name"]!.GetValue<string>())) fields.RemoveAt(i);
        fields.Add(new JsonObject { ["name"] = "series", ["type"] = "string", ["x_kind"] = "attribute", ["x_binding"] = "series" });
        fields.Add(new JsonObject { ["name"] = "value", ["type"] = "number", ["x_kind"] = "measure", ["x_binding"] = "value", ["x_additivity"] = "additive" });
        result["descriptor"] = descriptor;
        var rows = new JsonArray();
        foreach (var row in answer.Table.Rows)
            foreach (var entry in entries)
            {
                var copy = new JsonObject();
                foreach (var (key, value) in row) if (!drop.Contains(key)) copy[key] = value?.DeepClone();
                copy["series"] = entry.Label;
                copy["value"] = row[entry.Output]?.DeepClone();
                rows.Add(copy);
            }
        result["rows"] = rows;
        var folded = new AnswerRows(result, new ResultTable(result)).WithLocale(answer.Locale);

        var next = (JsonObject)encodings.DeepClone();
        var same = entries.Select(e => e.Field.Binding ?? e.Field.Name).Distinct().ToList();
        var y = new JsonObject { ["field"] = "value", ["type"] = "quantitative", ["title"] = same.Count == 1 ? entries[0].Title ?? same[0] : "Value" };
        if (entries.Select(e => e.Format?.ToJsonString()).Distinct().Count() == 1 && entries[0].Format is { } format) y["format"] = format.DeepClone();
        next["y"] = y;
        next["color"] = new JsonObject { ["field"] = "series", ["type"] = "nominal", ["title"] = "Series", ["sort"] = new JsonArray(entries.Select(e => (JsonNode)e.Label).ToArray()) };
        return (folded, next);
    }

    /// <summary>
    /// Draws a chart's channel formats (plan 32 DBR41). The engine's subset is closed and a spec with a date format must still sort in calendar order, so: on a **categorical** channel (nominal, ordinal, or a date that
    /// reads as its parts) the members are written as the format reads them into a column of their own, in the order the engine, or the author's <c>sort</c>, gave the raw members, so <c>MMM</c> labels January to December
    /// and never alphabetically; on a **quantitative** channel the labels are the ticks of the axis, so the format goes to the chart as a d3-format string (<c>formats</c>, made by <see cref="ViewFormat.D3Number"/> from the format's
    /// closed vocabulary) that the client gives to Vega-Lite after checking it against a fixed pattern. The author never writes an expression.
    /// </summary>
    private static void FormatChart(JsonObject rendered, AnswerRows answer, Dictionary<string, JsonNode> formats, List<JsonNode?> tipFormats)
    {
        if (rendered["vega_lite"] is not JsonObject vega || vega["encoding"] is not JsonObject encoding) return;
        var d3 = new JsonObject();
        var values = (vega["data"]?["values"] as JsonArray ?? []).OfType<JsonObject>().ToList();

        // returns the d3 string of a number format, or bakes the labels of a categorical one: true when the channel's field was replaced by a label column
        string? Apply(JsonObject def, string channel, JsonNode format, out bool baked)
        {
            baked = false;
            if (def["field"] is not JsonValue fv || !fv.TryGetValue<string>(out var name)) return null;
            var catalog = answer.Table.Resolve(name)?.Format;
            var dated = ViewFormat.DatePattern(format, catalog) is not null;
            // a number that a date format reads (a year held as an integer, DBR48) is a category, its labels written like any date's
            if (def["type"]?.GetValue<string>() == "quantitative" && !dated) return ViewFormat.D3Number(format, catalog);
            var column = $"{name}__{channel}";
            foreach (var row in values) row[column] = row[name] is { } raw ? Label(raw, format, catalog, answer.Locale) : null;
            def["field"] = column;
            def["title"] ??= answer.Table.Resolve(name)?.Binding ?? name;
            if (def["type"]?.GetValue<string>() is "temporal" or "quantitative") def["type"] = "ordinal";
            def["sort"] = LabelOrder(def["sort"], values, name, column);
            baked = true;
            return null;
        }

        foreach (var (channel, format) in formats)
        {
            if (channel == "tooltip" || encoding[channel] is not JsonObject def) continue;
            var text = Apply(def, channel, format, out _);
            if (text is not null) d3[channel] = text;
        }
        if (encoding["tooltip"] is JsonArray tips)
        {
            var list = new JsonArray();
            for (var i = 0; i < tips.Count; i++)
            {
                list.Add(null);
                if (i >= tipFormats.Count || tipFormats[i] is not { } format || tips[i] is not JsonObject tip) continue;
                var text = Apply(tip, $"tooltip{i}", format, out _);
                if (text is not null) list[i] = text;
            }
            if (list.Any(n => n is not null)) d3["tooltip"] = list;
        }
        else if (encoding["tooltip"] is JsonObject one && tipFormats.Count == 1 && tipFormats[0] is { } tooltipFormat)
        {
            var text = Apply(one, "tooltip", tooltipFormat, out _);
            if (text is not null) d3["tooltip"] = new JsonArray(text);
        }
        if (d3.Count > 0) rendered["formats"] = d3;
    }

    private static string Label(JsonNode raw, JsonNode format, JsonObject? catalog, string? locale) =>
        raw is JsonValue v && v.TryGetValue<string>(out var text) ? ViewFormat.Label(text, format, catalog, locale) : ViewFormat.Text(raw, format, catalog, locale);

    /// <summary>Whether the catalog gives a field a date format of its own (<c>x_format</c> of an attribute, DBR47).</summary>
    private static bool CatalogDate(ResultTable table, string field) => table.Resolve(field)?.Format?["date"] is not null;

    /// <summary>
    /// The sort of a channel whose members are now labels: the members' order is the order of the raw values (the engine's <c>sort_key</c> order that the view wrote out, a list the author gave, or ascending or descending
    /// by value), written as the distinct labels in that order, so a label that is not alphabetical (a month name) keeps its place.
    /// </summary>
    private static JsonNode LabelOrder(JsonNode? sort, List<JsonObject> values, string name, string column)
    {
        var raws = new List<JsonNode?>();
        void Add(JsonNode? n) { if (!raws.Any(r => JsonNode.DeepEquals(r, n))) raws.Add(n?.DeepClone()); }
        if (sort is JsonArray given) foreach (var n in given) Add(n);
        else
        {
            foreach (var row in values) Add(row[name]);
            var descending = sort is JsonValue sv && sv.TryGetValue<string>(out var word) && word == "descending";
            var keep = sort is null ? false : !(sort is JsonValue kv && kv.TryGetValue<string>(out var w) && w is "ascending" or "descending");
            if (!keep)
            {
                raws.Sort((a, b) => Compare(a, b));
                if (descending) raws.Reverse();
            }
        }
        var labels = new JsonArray();
        foreach (var raw in raws)
        {
            var row = values.FirstOrDefault(r => JsonNode.DeepEquals(r[name], raw));
            var label = row?[column];
            if (label is not null && !labels.Any(l => JsonNode.DeepEquals(l, label))) labels.Add(label.DeepClone());
        }
        return labels;
    }

    private static int Compare(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null ? (b is null ? 0 : 1) : -1;
        return ResultTable.Number(a) is { } x && ResultTable.Number(b) is { } y ? x.CompareTo(y) : string.CompareOrdinal(ResultTable.Label(a), ResultTable.Label(b));
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
    private static string Text(double n, JsonNode? format, JsonObject? catalog, string? locale = null) =>
        ViewFormat.DatePattern(format, catalog) is not null ? ViewFormat.Text(JsonValue.Create(n), format, catalog, locale)
        : format is JsonObject || locale is not (null or "en") ? ViewFormat.Number(n, format, catalog, locale) : MatrixView.Format(n, Preset(format), catalog);

    /// <summary>The number preset a widget names (<c>catalog</c>, <c>full</c>, <c>compact</c>, <c>percent</c>); the catalog's own format is the default and is what <c>full</c> reads.</summary>
    private static string Preset(JsonNode? format) => format is JsonValue v && v.TryGetValue<string>(out var s) && s is "compact" or "percent" ? s : "full";
}
