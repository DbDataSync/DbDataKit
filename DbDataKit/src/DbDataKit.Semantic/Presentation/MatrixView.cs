using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Presentation;

/// <summary>
/// A matrix arranges a result's rows into a grid: attributes on the rows and columns, measures in the cells. Every number in the
/// grid is a number from the result, found by its coordinates. Totals are the result's own total rows (rows that rolled the
/// axis' attributes up); a view that asks for a total the result doesn't have is refused. The one value a matrix computes is
/// the Other column of top_n_other, a sum, and only for measures the descriptor says add up.
/// </summary>
public static class MatrixView
{
    private sealed record Member(string Key, List<string> Labels, int Index);
    private sealed record ValueSpec(ResultField Field, string Empty, string Format, string Label);
    private sealed record Sort(string By, bool Descending);

    private const char Sep = '\u001f';
    private static string Join(IEnumerable<string> labels) => string.Join(Sep, labels);

    public static (JsonObject? View, List<QisError> Errors) Render(JsonObject spec, ResultTable table, string path)
    {
        var errors = new List<QisError>();
        QisError Err(string code, string where, string message, List<string>? suggestions = null) =>
            new("presentation", code, where, message, suggestions is { Count: > 0 } ? suggestions : null);

        ResultField? Field(string reference, string where, string role)
        {
            var field = table.Resolve(reference);
            if (field is null) { errors.Add(Err("unknown_field", where, $"{reference} isn't a field of the result.", table.Suggest(reference))); return null; }
            if (field.Kind != role) errors.Add(Err("wrong_field_kind", where, $"{reference} is a {field.Kind}; here a {role} is needed."));
            return field;
        }

        var rowFields = new List<ResultField>();
        var colFields = new List<ResultField>();
        var rowSpec = spec["rows"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        var colSpec = spec["columns"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        for (int i = 0; i < rowSpec.Count; i++) if (Field(rowSpec[i], $"{path}.rows[{i}]", "attribute") is { } f) rowFields.Add(f);
        for (int i = 0; i < colSpec.Count; i++) if (Field(colSpec[i], $"{path}.columns[{i}]", "attribute") is { } f) colFields.Add(f);

        var values = new List<ValueSpec>();
        var valueNodes = spec["values"]!.AsArray();
        for (int i = 0; i < valueNodes.Count; i++)
            if (Field(valueNodes[i]!["field"]!.GetValue<string>(), $"{path}.values[{i}].field", "measure") is { } f)
                values.Add(new ValueSpec(f, valueNodes[i]!["empty"]?.GetValue<string>() ?? "blank", valueNodes[i]!["format"]?.GetValue<string>() ?? "full",
                    valueNodes[i]!["label"]?.GetValue<string>() ?? f.Binding ?? f.Name));
        if (errors.Count > 0) return (null, errors);

        // Every attribute of the result is on an axis: one that isn't would put several rows in one cell.
        var placed = rowFields.Concat(colFields).Select(f => f.Name).ToHashSet();
        foreach (var d in table.Fields.Where(f => f.Kind == "attribute" && !placed.Contains(f.Name)))
            errors.Add(Err("attribute_not_placed", path, $"{d.Binding ?? d.Name} is a attribute of the result but is on neither axis, so a cell would hold several rows. Put it on rows or columns."));
        if (errors.Count > 0) return (null, errors);

        string LogicalName(ResultField f) => f.Binding ?? f.Name;
        var rowAttrs = rowFields.Select(LogicalName).ToHashSet();
        var colAttrs = colFields.Select(LogicalName).ToHashSet();

        // What each row of the result is for: a detail row, a total row or column, or the corner. Other subtotals aren't shown.
        string Kind(JsonObject row)
        {
            var rolled = table.RolledUp(row);
            if (rolled.Count == 0) return "detail";
            if (rowAttrs.Count > 0 && rolled.SetEquals(rowAttrs)) return "rowtotal";
            if (colAttrs.Count > 0 && rolled.SetEquals(colAttrs)) return "coltotal";
            if (rowAttrs.Count > 0 && colAttrs.Count > 0 && rolled.SetEquals(rowAttrs.Union(colAttrs))) return "corner";
            return "other";
        }
        List<string> Labels(JsonObject row, List<ResultField> fields) => fields.Select(f => ResultTable.Label(row[f.Name])).ToList();

        var cells = new Dictionary<(string Kind, string Row, string Col), JsonObject>();
        var rowMembers = new List<Member>();
        var colMembers = new List<Member>();
        var kinds = new HashSet<string>();
        foreach (var row in table.Rows)
        {
            var kind = Kind(row);
            kinds.Add(kind);
            if (kind == "other") continue;
            var rowLabels = kind is "detail" or "coltotal" ? Labels(row, rowFields) : [];
            var colLabels = kind is "detail" or "rowtotal" ? Labels(row, colFields) : [];
            cells.TryAdd((kind, Join(rowLabels), Join(colLabels)), row);
            if (kind is "detail" or "coltotal" && rowMembers.All(m => m.Key != Join(rowLabels))) rowMembers.Add(new Member(Join(rowLabels), rowLabels, rowMembers.Count));
            if (kind is "detail" or "rowtotal" && colMembers.All(m => m.Key != Join(colLabels))) colMembers.Add(new Member(Join(colLabels), colLabels, colMembers.Count));
        }
        if (rowFields.Count == 0) rowMembers = [new Member("", [], 0)];
        if (colFields.Count == 0) colMembers = [new Member("", [], 0)];

        var wantRowTotal = spec["totals"]?["rows"]?.GetValue<bool>() ?? false;
        var wantColTotal = spec["totals"]?["columns"]?.GetValue<bool>() ?? false;
        if (wantRowTotal && !kinds.Contains("rowtotal"))
            errors.Add(Err("totals_missing", $"{path}.totals.rows", rowFields.Count == 0
                ? "There are no row attributes, so there is no total row."
                : $"The result has no rows where {string.Join(", ", rowAttrs)} is rolled up. Request the query with totals."));
        if (wantColTotal && !kinds.Contains("coltotal"))
            errors.Add(Err("totals_missing", $"{path}.totals.columns", colFields.Count == 0
                ? "There are no column attributes, so there is no total column."
                : $"The result has no rows where {string.Join(", ", colAttrs)} is rolled up. Request the query with totals."));
        if (wantRowTotal && wantColTotal && rowFields.Count > 0 && colFields.Count > 0 && !kinds.Contains("corner"))
            errors.Add(Err("totals_missing", $"{path}.totals", "Both totals are asked for, and the result has no row where both axes are rolled up (the grand total)."));
        if (errors.Count > 0) return (null, errors);

        JsonNode? Value(string kind, string rowKey, string colKey, ResultField f) =>
            cells.TryGetValue((kind, rowKey, colKey), out var row) ? row[f.Name] : null;

        // Sorting: the order the result gives (sort_key), the labels, or a measure's value in the axis' total, which the result must hold.
        List<Member> Order(List<Member> members, JsonObject? sort, bool rowsAxis, string where)
        {
            if (sort is null || sort["by"]!.GetValue<string>() == "sort_key") return members;
            var by = sort["by"]!.GetValue<string>();
            var desc = sort["dir"]?.GetValue<string>() == "desc";
            Func<Member, IComparable?> key;
            if (by == "label") key = m => m.Key;
            else
            {
                var measure = table.Resolve(by);
                if (measure is null || measure.Kind != "measure")
                {
                    errors.Add(Err("unknown_field", $"{where}.by", $"{by} isn't a measure of the result.", table.Suggest(by)));
                    return members;
                }
                var totalKind = rowsAxis ? "coltotal" : "rowtotal";
                if (!kinds.Contains(totalKind))
                {
                    errors.Add(Err("sort_needs_totals", $"{where}.by",
                        $"Sorting by {by} uses each member's total across the other axis, which the result doesn't contain. Request the query with totals."));
                    return members;
                }
                key = m => ResultTable.Number(rowsAxis ? Value("coltotal", m.Key, "", measure) : Value("rowtotal", "", m.Key, measure));
            }
            return (desc ? members.OrderByDescending(key) : members.OrderBy(key)).ThenBy(m => m.Index).ToList();
        }
        rowMembers = Order(rowMembers, spec["sort"]?["rows"] as JsonObject, true, $"{path}.sort.rows");
        colMembers = Order(colMembers, spec["sort"]?["columns"] as JsonObject, false, $"{path}.sort.columns");
        if (errors.Count > 0) return (null, errors);

        // Column limit.
        string? notice = null;
        List<Member> otherMembers = [];
        if (spec["column_limit"] is JsonObject limit && colMembers.Count > limit["max"]!.GetValue<int>())
        {
            var max = limit["max"]!.GetValue<int>();
            switch (limit["overflow"]!.GetValue<string>())
            {
                case "error":
                    errors.Add(Err("column_limit_exceeded", $"{path}.column_limit", $"The result has {colMembers.Count} columns and the limit is {max}."));
                    return (null, errors);
                case "truncate":
                    notice = $"Showing the first {max} of {colMembers.Count} columns.";
                    colMembers = colMembers.Take(max).ToList();
                    break;
                default:
                    foreach (var v in values.Where(v => !v.Field.Additive))
                        errors.Add(Err("other_not_additive", $"{path}.column_limit", $"{v.Label} can't be added up, so the columns past the limit can't be combined into Other."));
                    if (errors.Count > 0) return (null, errors);
                    // Keep the columns with the most of the first measure, in the order they were already in.
                    double Weight(Member c) => rowMembers.Sum(r => ResultTable.Number(Value("detail", r.Key, c.Key, values[0].Field)) ?? 0);
                    var keep = colMembers.OrderByDescending(Weight).ThenBy(c => c.Index).Take(max - 1).Select(c => c.Key).ToHashSet();
                    otherMembers = colMembers.Where(c => !keep.Contains(c.Key)).ToList();
                    colMembers = colMembers.Where(c => keep.Contains(c.Key)).ToList();
                    notice = $"The {otherMembers.Count} smallest of {otherMembers.Count + colMembers.Count} columns are combined as Other.";
                    break;
            }
        }

        // A cell, as the value found for its coordinates and how it reads.
        JsonObject Cell(JsonNode? value, ValueSpec v)
        {
            var n = ResultTable.Number(value);
            if (n is null && v.Empty == "zero") n = 0;
            return new JsonObject { ["v"] = n is null ? null : (JsonNode?)(value is not null ? value.DeepClone() : JsonValue.Create(0)), ["text"] = n is null ? "" : Format(n.Value, v.Format, v.Field.Format) };
        }
        JsonObject At(string rk, bool rowTotal, string ck, bool colTotal, ValueSpec v)
        {
            var kind = rowTotal ? (colTotal ? "corner" : "rowtotal") : (colTotal ? "coltotal" : "detail");
            return Cell(Value(kind, rowTotal ? "" : rk, colTotal ? "" : ck, v.Field), v);
        }
        JsonObject OtherAt(string rk, bool rowTotal, ValueSpec v)
        {
            double? sum = null;
            foreach (var c in otherMembers)
                if (ResultTable.Number(Value(rowTotal ? "rowtotal" : "detail", rowTotal ? "" : rk, c.Key, v.Field)) is { } n) sum = (sum ?? 0) + n;
            return Cell(sum is null ? null : JsonValue.Create(sum.Value), v);
        }

        // The columns: each column member, then Other, then the total; with values_on columns, each measure inside each.
        var valuesOnRows = spec["values_on"]?.GetValue<string>() == "rows";
        var columns = new List<(List<string> Labels, string? Key, bool Total, bool Other, ValueSpec? Value)>();
        var members = colMembers.Select(c => (c.Labels, (string?)c.Key, false, false)).ToList();
        if (otherMembers.Count > 0) members.Add((["Other"], null, false, true));
        if (wantColTotal) members.Add((["Total"], null, true, false));
        foreach (var (labels, key, total, other) in members)
        {
            if (valuesOnRows) columns.Add((labels, key, total, other, null));
            else foreach (var v in values) columns.Add((labels.Append(v.Label).ToList(), key, total, other, v));
        }

        var gridRows = new JsonArray();
        void AddRows(List<string> labels, string rk, bool rowTotal)
        {
            foreach (var v in valuesOnRows ? values.Cast<ValueSpec?>().ToList() : new List<ValueSpec?> { null })
            {
                var cellsOut = new JsonArray();
                foreach (var c in columns)
                {
                    var spec_ = v ?? c.Value!;
                    cellsOut.Add(c.Other ? OtherAt(rk, rowTotal, spec_) : At(rk, rowTotal, c.Key ?? "", c.Total, spec_));
                }
                gridRows.Add(new JsonObject
                {
                    ["labels"] = new JsonArray((v is null ? labels : labels.Append(v.Label).ToList()).Select(l => (JsonNode)l).ToArray()),
                    ["total"] = rowTotal, ["cells"] = cellsOut,
                });
            }
        }
        foreach (var r in rowMembers) AddRows(r.Labels, r.Key, false);
        if (wantRowTotal) AddRows(["Total"], "", true);

        var view = new JsonObject
        {
            ["id"] = spec["id"]!.GetValue<string>(), ["type"] = "matrix",
            ["columns"] = new JsonArray(columns.Select(c => (JsonNode)new JsonObject
            {
                ["labels"] = new JsonArray(c.Labels.Select(l => (JsonNode)l).ToArray()), ["total"] = c.Total, ["other"] = c.Other,
            }).ToArray()),
            ["rows"] = gridRows,
            ["notice"] = notice,
        };
        return (view, errors);
    }

    /// <summary>
    /// A number as one of the named presets, shaped by the catalog's format for the measure when it has one: its currency symbol, and its decimals in the full form
    /// (compact always keeps one).
    /// A view can't write its own format. <c>percent</c> reads a fraction as a percentage whatever the catalog says; a measure whose catalog style
    /// is percent reads that way under any preset.
    /// </summary>
    internal static string Format(double v, string preset, JsonObject? catalog = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var style = catalog?["style"]?.GetValue<string>() ?? "number";
        int? decimals = catalog?["decimals"]?.GetValue<int>();
        if (preset == "percent" || style == "percent")
            return Math.Round(v * 100, decimals ?? 0, MidpointRounding.AwayFromZero).ToString("0" + (decimals > 0 ? "." + new string('0', decimals.Value) : ""), inv) + "%";

        string body;
        var a = Math.Abs(v);
        if (preset == "compact" && a >= 1e3)
        {
            var (scaled, suffix) = a >= 1e9 ? (a / 1e9, "B") : a >= 1e6 ? (a / 1e6, "M") : (a / 1e3, "K");
            body = scaled.ToString("0.#", inv) + suffix;
        }
        else if (preset == "compact") body = a.ToString("0.##", inv);
        else body = a.ToString(decimals is { } d ? "#,##0" + (d > 0 ? "." + new string('0', d) : "") : "#,##0.##", inv);

        var symbol = style == "currency" ? CurrencySymbol(catalog!["currency"]?.GetValue<string>()) : "";
        return (v < 0 && body.Any(c => c is >= '1' and <= '9') ? "-" : "") + symbol + body;
    }

    private static string CurrencySymbol(string? code) => code switch
    {
        "USD" => "$", "EUR" => "€", "GBP" => "£", "JPY" => "¥", "CNY" => "¥", "INR" => "₹", "KRW" => "₩", null => "", _ => code + " ",
    };
}
