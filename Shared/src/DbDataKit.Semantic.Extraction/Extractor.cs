using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Extraction;

/// <summary>What the caller knows about the file and the last time a spec was run on its predecessor.</summary>
public sealed record ExtractionContext(string? FileName = null, string? FileVersion = null, int? PreviousRowCount = null);

/// <summary>
/// The extraction spec: a spreadsheet to clean tables. The spec says what to extract (where the header is, where the data is,
/// which rows to skip, how to unpivot, what the types are, what must hold), never how. This executor reads the workbook with
/// ClosedXML, flattens it, types and checks the result, and emits each table as a <c>table</c> document with a descriptor,
/// ready to register for ad hoc mapping. A fingerprint that no longer holds, a conversion that fails or a failed check stops
/// the table: the output is never a guess.
/// </summary>
public sealed class Extractor
{
    private static QisError Err(string code, string path, string message, List<string>? suggestions = null) =>
        new("extraction", code, path, message, suggestions is { Count: > 0 } ? suggestions : null);

    // ---------- A1 references ----------

    private static int ColumnNumber(string letters) => letters.Aggregate(0, (n, c) => n * 26 + (c - 'A' + 1));
    private static string ColumnLetters(int number)
    {
        var s = "";
        for (; number > 0; number = (number - 1) / 26) s = (char)('A' + (number - 1) % 26) + s;
        return s;
    }
    private static (int Column, int Row) Ref(string cell)
    {
        var m = Regex.Match(cell, "^([A-Z]+)([0-9]+)$");
        return (ColumnNumber(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }
    private static List<int> Span(string span)
    {
        var parts = span.Split(':');
        var (a, b) = (ColumnNumber(parts[0]), ColumnNumber(parts[^1]));
        return Enumerable.Range(a, b - a + 1).ToList();
    }

    // ---------- reading cells as text ----------

    /// <summary>A cell as text, or null if blank. Every cell is read as text first and converted afterwards.</summary>
    private static string? Text(IXLWorksheet sheet, int row, int column, bool spreadMerged = false)
    {
        var cell = sheet.Cell(row, column);
        if (spreadMerged && cell.IsMerged()) cell = cell.MergedRange().FirstCell();
        var v = cell.Value;
        if (v.IsBlank) return null;
        if (v.IsText) return v.GetText();
        if (v.IsNumber) return v.GetNumber().ToString("R", CultureInfo.InvariantCulture);
        if (v.IsBoolean) return v.GetBoolean() ? "true" : "false";
        if (v.IsDateTime)
        {
            var d = v.GetDateTime();
            return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }
        return v.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    // ---------- matching ----------

    /// <summary>Whether a saved spec applies to a file: its library and a name pattern (* and ?).</summary>
    public static bool Matches(JsonObject spec, string library, string fileName)
    {
        if (spec["match"] is not JsonObject match) return false;
        if (match["library"]?.GetValue<string>() is { } lib && !string.Equals(lib.Trim('/'), library.Trim('/'), StringComparison.OrdinalIgnoreCase)) return false;
        if (match["name_pattern"]?.GetValue<string>() is { } pattern)
            return Regex.IsMatch(fileName, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
        return true;
    }

    // ---------- inspect ----------

    /// <summary>
    /// A compact map of each sheet for the agent that writes a spec: non-empty cells with coordinates, values, number formats
    /// and bold or filled cells, and the merged regions. A big sheet is sampled: its top 40 rows, bottom 10 and every row a merged region touches.
    /// </summary>
    public JsonObject Inspect(Stream workbook)
    {
        using var book = new XLWorkbook(workbook);
        var sheets = new JsonArray();
        foreach (var sheet in book.Worksheets)
        {
            var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
            var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            var merged = sheet.MergedRanges.Select(r => r.RangeAddress.ToStringRelative()).ToList();
            var keep = new HashSet<int>();
            if (last <= 50) keep.UnionWith(Enumerable.Range(1, last));
            else
            {
                keep.UnionWith(Enumerable.Range(1, 40));
                keep.UnionWith(Enumerable.Range(last - 9, 10));
                foreach (var r in sheet.MergedRanges) keep.UnionWith(Enumerable.Range(r.RangeAddress.FirstAddress.RowNumber, r.RangeAddress.RowSpan));
            }
            var cells = new JsonArray();
            foreach (var row in keep.Where(r => r <= last).Order())
                for (int col = 1; col <= lastColumn; col++)
                {
                    var text = Text(sheet, row, col);
                    if (text is null) continue;
                    var cell = sheet.Cell(row, col);
                    var entry = new JsonObject { ["ref"] = $"{ColumnLetters(col)}{row}", ["value"] = text };
                    var format = cell.Style.NumberFormat.Format;
                    if (!string.IsNullOrEmpty(format) && format != "General") entry["format"] = format;
                    if (cell.Style.Font.Bold) entry["bold"] = true;
                    if (cell.Style.Fill.BackgroundColor.ColorType != XLColorType.Color || cell.Style.Fill.PatternType != XLFillPatternValues.None)
                        if (cell.Style.Fill.PatternType != XLFillPatternValues.None) entry["filled"] = true;
                    cells.Add(entry);
                }
            sheets.Add(new JsonObject
            {
                ["name"] = sheet.Name, ["rows"] = last, ["columns"] = ColumnLetters(Math.Max(lastColumn, 1)),
                ["sampled"] = last > 50, ["merged"] = new JsonArray(merged.Select(m => (JsonNode)m).ToArray()), ["cells"] = cells,
            });
        }
        return new JsonObject { ["kind"] = "sheet_map", ["sheets"] = sheets };
    }

    // ---------- extract ----------

    public JsonObject Extract(JsonObject spec, Stream workbook, ExtractionContext? context = null)
    {
        context ??= new ExtractionContext();
        var errors = SchemaStore.Errors(spec, SchemaStore.Get("extraction_spec"), "schema_invalid", "extraction");
        var tables = new JsonArray();
        if (errors.Count == 0)
        {
            using var book = new XLWorkbook(workbook);
            errors.AddRange(CheckFingerprint(spec["fingerprint"] as JsonObject, book));
            if (errors.Count == 0)
            {
                var tableSpecs = spec["tables"]!.AsArray();
                for (int i = 0; i < tableSpecs.Count; i++)
                {
                    var (table, tableErrors) = ExtractTable(spec, tableSpecs[i]!.AsObject(), book, $"$.tables[{i}]", context);
                    errors.AddRange(tableErrors);
                    if (table is not null) tables.Add(table);
                }
            }
        }
        return new JsonObject
        {
            ["kind"] = "extraction_result", ["status"] = errors.Count == 0 ? "complete" : "failed",
            ["tables"] = errors.Count == 0 ? tables : null,
            ["errors"] = errors.Count == 0 ? null : System.Text.Json.JsonSerializer.SerializeToNode(errors, QisJson.Options),
        };
    }

    private static List<QisError> CheckFingerprint(JsonObject? fingerprint, XLWorkbook book)
    {
        var errors = new List<QisError>();
        if (fingerprint is null) return errors;
        var have = book.Worksheets.Select(s => s.Name).ToList();
        foreach (var name in fingerprint["sheets"]?.AsArray().Select(s => s!.GetValue<string>()) ?? [])
            if (!have.Contains(name))
                errors.Add(Err("fingerprint_failed", "$.fingerprint.sheets", $"The file has no sheet called {name}; it has {string.Join(", ", have)}. The layout has changed.", have));
        var cells = fingerprint["cells"]?.AsArray() ?? [];
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i]!.AsObject();
            if (!book.TryGetWorksheet(c["sheet"]!.GetValue<string>(), out var sheet)) continue;   // reported with the sheets
            var (col, row) = Ref(c["ref"]!.GetValue<string>());
            var text = Text(sheet, row, col)?.Trim();
            var ok = c["equals"] is { } eq ? text == eq.GetValue<string>() : text is not null && Regex.IsMatch(text, c["matches"]!.GetValue<string>());
            if (ok) continue;
            var expected = c["equals"]?.GetValue<string>();
            var message = $"{c["sheet"]}!{c["ref"]} is {(text is null ? "empty" : $"\"{text}\"")}, which doesn't {(expected is not null ? $"equal \"{expected}\"" : $"match {c["matches"]}")}.";
            // Say where the anchor went if it only moved along its row: "a column was inserted".
            if (expected is not null)
            {
                var last = sheet.LastColumnUsed()?.ColumnNumber() ?? col;
                for (int found = 1; found <= last; found++)
                    if (found != col && Text(sheet, row, found)?.Trim() == expected)
                    {
                        var shift = found - col;
                        message += $" \"{expected}\" is now at {ColumnLetters(found)}{row}: {(shift > 0 ? $"{shift} column(s) were inserted before it" : $"{-shift} column(s) were removed before it")}.";
                        break;
                    }
            }
            errors.Add(Err("fingerprint_failed", $"$.fingerprint.cells[{i}]", message));
        }
        return errors;
    }

    private sealed record Cell(string? Text, int Row, int Column);

    private (JsonObject? Table, List<QisError> Errors) ExtractTable(JsonObject spec, JsonObject ts, XLWorkbook book, string path, ExtractionContext context)
    {
        var errors = new List<QisError>();
        var sheetName = ts["sheet"]!.GetValue<string>();
        if (!book.TryGetWorksheet(sheetName, out var sheet))
            return (null, [Err("sheet_not_found", path + ".sheet", $"The file has no sheet called {sheetName}.", book.Worksheets.Select(s => s.Name).ToList())]);

        var data = ts["data"]!.AsObject();
        var columns = Span(data["columns"]!.GetValue<string>());
        var (startColumn, startRow) = Ref(data["start"]!.GetValue<string>());
        _ = startColumn;

        // The header: one name per column, joined from the header rows, with merged cells spread across their span.
        var join = ts["header"]!["join"]?.GetValue<string>() ?? " ";
        var headerRows = ts["header"]!["rows"]!.AsArray().Select(r => r!.GetValue<int>()).ToList();
        var headers = columns.ToDictionary(c => c, c => string.Join(join, headerRows.Select(r => Text(sheet, r, c, spreadMerged: true)?.Trim()).Where(t => !string.IsNullOrEmpty(t))));

        // The data rows: from the start, to a fixed end or the first blank in a column.
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var endRow = lastRow;
        if (data["end"] is JsonValue fixedEnd) endRow = Ref(fixedEnd.GetValue<string>()).Row;
        else if (data["end"]?["until_blank_in"] is { } until)
        {
            var column = ColumnNumber(until.GetValue<string>());
            endRow = startRow - 1;
            while (endRow < lastRow && !IsBlank(Text(sheet, endRow + 1, column))) endRow++;
        }

        var skips = ts["skip_rows"]?.AsArray().Select(s => s!.AsObject()).ToList() ?? [];
        var kept = new List<Dictionary<int, Cell>>();
        for (int row = startRow; row <= endRow; row++)
        {
            var values = columns.ToDictionary(c => c, c => new Cell(Text(sheet, row, c), row, c));
            var skip = skips.Any(rule => rule["blank_in"] is { } blank
                ? Span(blank.GetValue<string>()).All(c => IsBlank(Text(sheet, row, c)))
                : Text(sheet, row, ColumnNumber(rule["column"]!.GetValue<string>())) is { } t && Regex.IsMatch(t, rule["matches"]!.GetValue<string>()));
            if (!skip) kept.Add(values);
        }

        // Fill down: a value applies to the rows below it until the next one.
        foreach (var letters in ts["fill_down"]?.AsArray().Select(c => c!.GetValue<string>()) ?? [])
        {
            var column = ColumnNumber(letters);
            Cell? previous = null;
            foreach (var row in kept.Where(r => r.ContainsKey(column)))
                if (IsBlank(row[column].Text) && previous is not null) row[column] = row[column] with { Text = previous.Text };
                else if (!IsBlank(row[column].Text)) previous = row[column];
        }

        // Field names: from the header, as snake_case and unique. The unpivoted columns become two fields.
        var unpivot = ts["unpivot"] as JsonObject;
        var unpivoted = unpivot is null ? [] : Span(unpivot["columns"]!.GetValue<string>());
        var names = new Dictionary<int, string>();
        var used = new HashSet<string>();
        foreach (var column in columns.Where(c => !unpivoted.Contains(c)))
        {
            var name = FieldName(headers[column], column);
            for (int n = 2; !used.Add(name); n++) name = $"{FieldName(headers[column], column)}_{n}";
            names[column] = name;
        }
        var fieldOrder = names.Values.ToList();
        if (unpivot is not null)
        {
            fieldOrder.Add(unpivot["name_as"]!.GetValue<string>());
            fieldOrder.Add(unpivot["value_as"]!.GetValue<string>());
        }

        var rows = new List<(Dictionary<string, string?> Values, Dictionary<string, (int Row, int Column)> Origin)>();
        foreach (var row in kept)
        {
            Dictionary<string, string?> Base() => names.ToDictionary(p => p.Value, p => row[p.Key].Text);
            Dictionary<string, (int Row, int Column)> Origins() => names.ToDictionary(p => p.Value, p => (row[p.Key].Row, p.Key));
            if (unpivot is null) rows.Add((Base(), Origins()));
            else
                foreach (var column in unpivoted)
                {
                    var values = Base();
                    values[unpivot["name_as"]!.GetValue<string>()] = headers[column];
                    values[unpivot["value_as"]!.GetValue<string>()] = row[column].Text;
                    var origin = Origins();
                    origin[unpivot["value_as"]!.GetValue<string>()] = (row[column].Row, column);
                    origin[unpivot["name_as"]!.GetValue<string>()] = (headerRows[^1], column);
                    rows.Add((values, origin));
                }
        }

        // Types: every cell was read as text; now convert, and report what doesn't convert.
        var types = ts["types"]?.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()) ?? [];
        foreach (var unknown in types.Keys.Where(t => !fieldOrder.Contains(t)))
            errors.Add(Err("type_for_unknown_field", $"{path}.types.{unknown}", $"{unknown} isn't a field of this table.", fieldOrder));
        var typed = new List<Dictionary<string, JsonNode?>>();
        var failures = new Dictionary<string, List<string>>();
        foreach (var (values, origin) in rows)
        {
            var o = new Dictionary<string, JsonNode?>();
            foreach (var field in fieldOrder)
            {
                var (node, ok) = Convert(values[field], types.GetValueOrDefault(field, "string"));
                o[field] = node;
                if (!ok)
                {
                    if (!failures.TryGetValue(field, out var list)) failures[field] = list = [];
                    list.Add($"\"{values[field]}\" at {ColumnLetters(origin[field].Column)}{origin[field].Row}");
                }
            }
            typed.Add(o);
        }
        foreach (var (field, list) in failures)
            errors.Add(Err("type_conversion_failed", $"{path}.types.{field}",
                $"{list.Count} value(s) in {field} aren't {types[field]}: {string.Join(", ", list.Take(3))}{(list.Count > 3 ? ", ..." : "")}."));
        if (errors.Count > 0) return (null, errors);

        errors.AddRange(RunChecks(ts, path, typed, book, context));
        if (errors.Count > 0) return (null, errors);

        return (TableDocument(spec, ts, fieldOrder, types, typed, context), errors);
    }

    private static string FieldName(string header, int column)
    {
        var name = Regex.Replace(header.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (name.Length == 0) return $"column_{ColumnLetters(column).ToLowerInvariant()}";
        return char.IsAsciiLetter(name[0]) ? name : "x_" + name;
    }

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy-MM", "yyyy MMM", "yyyy MMMM", "MMM yyyy", "MMMM yyyy", "MMM-yy", "MMM-yyyy", "MMMM-yyyy", "M/d/yyyy", "yyyy/MM/dd", "d-MMM-yyyy"];

    /// <summary>One value as the type asks for it, and whether it converted. A blank is null for every type.</summary>
    private static (JsonNode? Value, bool Ok) Convert(string? text, string type)
    {
        if (IsBlank(text)) return (null, true);
        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case "string": return (JsonValue.Create(text), true);
            case "integer" or "number":
            {
                var t = text!.Trim();
                var negative = t.StartsWith('(') && t.EndsWith(')');
                t = t.Trim('(', ')').Replace(",", "").Replace("$", "");
                if (!decimal.TryParse(t, NumberStyles.Float, inv, out var d)) return (null, false);
                if (negative) d = -d;
                if (type == "integer") return d == decimal.Truncate(d) && Math.Abs(d) < long.MaxValue ? (JsonValue.Create((long)d), true) : (null, false);
                return (JsonValue.Create(d), true);
            }
            case "boolean":
                return text!.Trim().ToLowerInvariant() switch
                {
                    "true" or "yes" or "y" or "1" => (JsonValue.Create(true), true),
                    "false" or "no" or "n" or "0" => (JsonValue.Create(false), true),
                    _ => (null, false),
                };
            case "date":
                return DateTime.TryParseExact(text!.Trim(), DateFormats, inv, DateTimeStyles.None, out var date)
                    ? (JsonValue.Create(date.ToString("yyyy-MM-dd", inv)), true) : (null, false);
            case "datetime":
                return DateTime.TryParse(text!.Trim(), inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                    ? (JsonValue.Create(when.ToString("yyyy-MM-ddTHH:mm:ss", inv) + "Z"), true) : (null, false);
            default: return (JsonValue.Create(text), true);
        }
    }

    // ---------- checks ----------

    private List<QisError> RunChecks(JsonObject ts, string path, List<Dictionary<string, JsonNode?>> rows, XLWorkbook book, ExtractionContext context)
    {
        var errors = new List<QisError>();
        var checks = ts["checks"]?.AsArray() ?? [];
        for (int i = 0; i < checks.Count; i++)
        {
            var check = checks[i]!.AsObject();
            var where = $"{path}.checks[{i}]";
            switch (check["type"]!.GetValue<string>())
            {
                case "row_count":
                    var min = check["min"]?.GetValue<int>();
                    var max = check["max"]?.GetValue<int>();
                    if (min is not null && rows.Count < min) errors.Add(Err("row_count_failed", where, $"The table has {rows.Count} rows; at least {min} are expected."));
                    if (max is not null && rows.Count > max) errors.Add(Err("row_count_failed", where, $"The table has {rows.Count} rows; at most {max} are expected."));
                    if (check["within_percent_of_previous"] is { } pct && context.PreviousRowCount is { } previous)
                    {
                        var allowed = previous * pct.GetValue<double>() / 100;
                        if (Math.Abs(rows.Count - previous) > allowed)
                            errors.Add(Err("row_count_failed", where, $"The table has {rows.Count} rows; the previous version had {previous}, and {pct}% of that is the most it may change by."));
                    }
                    break;
                case "not_null":
                    foreach (var column in check["columns"]!.AsArray().Select(c => c!.GetValue<string>()))
                    {
                        var nulls = rows.Count(r => r.GetValueOrDefault(column) is null);
                        if (nulls > 0) errors.Add(Err("not_null_failed", where, $"{column} is empty in {nulls} row(s)."));
                    }
                    break;
                case "unique":
                    var keys = check["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
                    var duplicates = rows.GroupBy(r => string.Join("\u001f", keys.Select(k => r.GetValueOrDefault(k)?.ToJsonString()))).Where(g => g.Count() > 1).ToList();
                    if (duplicates.Count > 0)
                        errors.Add(Err("unique_failed", where, $"{string.Join(", ", keys)} isn't unique: {duplicates.Count} value(s) repeat, such as {duplicates[0].Key.Replace("\u001f", " / ")}."));
                    break;
                case "total_matches":
                    var column2 = check["column"]!.GetValue<string>();
                    var against = check["against"]!.AsObject();
                    var tolerance = check["tolerance"]?.GetValue<decimal>() ?? 0m;
                    var extracted = rows.Sum(r => r.GetValueOrDefault(column2) is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? v.GetValue<decimal>() : 0m);
                    if (!book.TryGetWorksheet(against["sheet"]!.GetValue<string>(), out var other))
                    {
                        errors.Add(Err("total_row_not_found", where, $"The file has no sheet called {against["sheet"]}."));
                        break;
                    }
                    var find = against["find_row"]!.AsObject();
                    var findColumn = ColumnNumber(find["column"]!.GetValue<string>());
                    var found = Enumerable.Range(1, other.LastRowUsed()?.RowNumber() ?? 0).FirstOrDefault(r => Text(other, r, findColumn)?.Trim() == find["equals"]!.GetValue<string>());
                    if (found == 0)
                    {
                        errors.Add(Err("total_row_not_found", where, $"No row of {against["sheet"]} has \"{find["equals"]}\" in column {find["column"]}, so the total can't be checked."));
                        break;
                    }
                    var sheetTotal = Span(against["columns"]!.GetValue<string>())
                        .Sum(c => decimal.TryParse(Text(other, found, c), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m);
                    if (Math.Abs(extracted - sheetTotal) > tolerance)
                        errors.Add(Err("total_mismatch", where, $"The extracted {column2} adds up to {extracted}; the sheet's own total (row {found}) is {sheetTotal}. A range, a skipped row or a column is probably wrong."));
                    break;
            }
        }
        return errors;
    }

    // ---------- the table document ----------

    private static JsonObject TableDocument(JsonObject spec, JsonObject ts, List<string> fieldOrder, Dictionary<string, string> types,
        List<Dictionary<string, JsonNode?>> rows, ExtractionContext context)
    {
        var fields = new JsonArray();
        foreach (var name in fieldOrder)
        {
            var values = rows.Select(r => r[name]).ToList();
            var present = values.Where(v => v is not null).ToList();
            fields.Add(new JsonObject
            {
                ["name"] = name, ["type"] = types.GetValueOrDefault(name, "string"), ["x_kind"] = "column",
                ["x_profile"] = new JsonObject
                {
                    ["nulls"] = values.Count - present.Count,
                    ["distinct"] = present.Select(v => v!.ToJsonString()).Distinct().Count(),
                    ["samples"] = new JsonArray(present.Select(v => v!.ToJsonString()).Distinct().Take(5).Select(v => (JsonNode)JsonNode.Parse(v)!).ToArray()),
                },
            });
        }
        var provenance = new JsonObject
        {
            ["spec"] = "extraction", ["spec_version"] = "0.3", ["mode"] = "engine", ["extraction_spec"] = spec["id"]!.GetValue<string>(),
        };
        if (context.FileName is not null) provenance["file"] = context.FileName;
        if (context.FileVersion is not null) provenance["file_version"] = context.FileVersion;
        var name_ = ts["name"]!.GetValue<string>();
        return new JsonObject
        {
            ["spec_version"] = "0.3", ["kind"] = "table", ["name"] = "adhoc." + name_,
            ["descriptor"] = new JsonObject
            {
                ["descriptor_version"] = "1.0", ["name"] = name_,
                ["schema"] = new JsonObject { ["fields"] = fields },
                ["x_row_count"] = rows.Count, ["x_provenance"] = provenance,
            },
            ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject(r.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())))).ToArray()),
        };
    }
}
