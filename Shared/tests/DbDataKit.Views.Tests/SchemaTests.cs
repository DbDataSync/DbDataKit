using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Json.Schema;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>
/// The loader is the authority and the JSON Schemas (<c>core</c>, <c>dashboard</c>, <c>report</c>) are for editors and for the TypeScript validators; these tests keep the two equal, keep the two kinds on
/// one core and one number. The widget part of the core is checked by <see cref="WidgetSchemaTests"/>.
/// </summary>
public class SchemaTests
{
    private static readonly object Gate = new();
    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(() =>
    {
        Conformance.RegisterSchemas();
        return new[] { "dashboard", "report" }.ToDictionary(n => n, n => JsonSchema.FromText(ViewsLoader.Schema(n)));
    });

    private static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance.SchemaRoot, name.EndsWith(".lock", StringComparison.Ordinal) ? name + ".json" : name + ".schema.json")))!;

    private static JsonNode At(JsonNode root, string pointer)
    {
        var node = root;
        foreach (var part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            node = node is JsonArray a ? a[int.Parse(part)]! : node[part] ?? throw new InvalidOperationException($"{pointer}: no '{part}'");
        return node;
    }

    private static string[] Props(JsonNode node) => [.. (node["properties"] as JsonObject ?? throw new InvalidOperationException("no properties")).Select(p => p.Key).Order()];

    [Fact]
    public void The_embedded_kind_schemas_are_the_files_of_the_visuals_package()
    {
        foreach (var name in new[] { "dashboard", "report" })
            Assert.Equal(File.ReadAllText(Path.Combine(Conformance.SchemaRoot, name + ".schema.json")), ViewsLoader.Schema(name));
    }

    [Fact]
    public void The_keys_of_every_document_part_are_the_keys_of_the_schema()
    {
        var core = Load("core"); var dashboard = Load("dashboard"); var report = Load("report");
        var parts = new Dictionary<string, string[]>
        {
            ["dashboard"] = Props(dashboard), ["report"] = Props(report),
            ["theme"] = Props(At(core, "$defs/theme")),
            ["layout.reflow"] = Props(At(core, "$defs/layout_reflow")), ["layout.canvas"] = Props(At(core, "$defs/layout_canvas")), ["layout.flow"] = Props(At(core, "$defs/layout_flow")),
            ["grid"] = Props(At(core, "$defs/layout_reflow/properties/grid")), ["canvas"] = Props(At(core, "$defs/layout_canvas/properties/canvas")),
            ["tile.reflow"] = Props(At(core, "$defs/tile_reflow")), ["tile.canvas"] = Props(At(core, "$defs/tile_canvas")), ["min"] = Props(At(core, "$defs/min_size")),
            ["when"] = Props(At(core, "$defs/selector")), ["page"] = Props(At(core, "$defs/page")), ["margins"] = Props(At(core, "$defs/page/properties/margins")),
            ["running_text"] = Props(At(core, "$defs/running_text")), ["section"] = Props(At(core, "$defs/flow_section")),
            ["block.widget"] = Props(At(core, "$defs/flow_widget_block")), ["block.row"] = Props(At(core, "$defs/flow_row_block")), ["block.pagebreak"] = Props(At(core, "$defs/flow_break_block")),
            ["row_item"] = Props(At(core, "$defs/flow_row_block/properties/row/items")), ["widget_options"] = Props(At(core, "$defs/widget_options/additionalProperties")),
            ["refresh"] = Props(At(dashboard, "properties/refresh")), ["kiosk"] = Props(At(dashboard, "properties/kiosk")),
            ["limits"] = Props(At(report, "properties/limits")), ["cover"] = Props(At(report, "properties/cover")),
            ["link"] = Props(At(core, "$defs/link")),
        };
        // `range` is the same shape as min and max; `layout.common` is what the three layout types share
        Assert.Equal(["max", "min"], Props(new JsonObject { ["properties"] = At(core, "$defs/range/properties").DeepClone() }));
        Assert.Equal(ViewsLoader.Keys["range"].Order(), ["max", "min"]);
        var common = Props(At(core, "$defs/layout_reflow")).Intersect(Props(At(core, "$defs/layout_canvas"))).Intersect(Props(At(core, "$defs/layout_flow"))).Order().ToArray();
        Assert.Equal(ViewsLoader.Keys["layout.common"].Order(), common);
        foreach (var type in new[] { "reflow", "canvas", "flow" })
            Assert.Equal(parts["layout." + type], ViewsLoader.Keys["layout.common"].Union(ViewsLoader.Keys["layout." + type]).Order().ToArray());
        parts.Remove("layout.reflow"); parts.Remove("layout.canvas"); parts.Remove("layout.flow");
        foreach (var (part, keys) in parts) Assert.True(ViewsLoader.Keys[part].Order().SequenceEqual(keys), $"{part}: loader {string.Join(",", ViewsLoader.Keys[part].Order())} / schema {string.Join(",", keys)}");
        var known = parts.Keys.Concat(["range", "layout.common", "layout.reflow", "layout.canvas", "layout.flow"]).Order().ToArray();
        Assert.Equal(ViewsLoader.Keys.Keys.Where(k => !k.StartsWith("widget.") && !k.StartsWith("encodings.") && !k.StartsWith("query.") && k is not ("parameter" or "table_column" or "channel" or "matrix_axis_entry" or "x_level" or "mark" or "semantic" or "store" or "on_select" or "drill" or "drill.to" or "control.values")).Order(), known);
    }

    [Fact]
    public void The_choices_of_the_loader_are_the_enums_of_the_schema()
    {
        var core = Load("core"); var dashboard = Load("dashboard");
        string[] Enum(JsonNode n) => [.. n["enum"]!.AsArray().Select(v => v!.GetValue<string>())];
        var checks = new Dictionary<string, string[]>
        {
            ["font"] = Enum(At(core, "$defs/theme/properties/font")),
            ["locale"] = Enum(At(core, "$defs/locale")),
            ["orientation"] = Enum(At(core, "$defs/selector/properties/orientation")), ["medium"] = Enum(At(core, "$defs/medium")),
            ["overflow"] = Enum(At(core, "$defs/layout_canvas/properties/overflow")), ["unit"] = Enum(At(core, "$defs/layout_canvas/properties/canvas/properties/unit")),
            ["pageSize"] = Enum(At(core, "$defs/page/properties/size/oneOf/0")), ["break"] = Enum(At(core, "$defs/flow_section/properties/break")),
            ["density"] = Enum(At(core, "$defs/widget_options/additionalProperties/properties/density")), ["legend"] = Enum(At(core, "$defs/widget_options/additionalProperties/properties/legend")),
            ["layoutType"] = [.. new[] { "reflow", "canvas", "flow" }.Where(t => At(core, $"$defs/layout_{t}/properties/type")["const"]!.GetValue<string>() == t)],
        };
        foreach (var (name, values) in checks) Assert.True(ViewsLoader.Choices[name].Order().SequenceEqual(values.Order()), $"{name}: loader {string.Join(",", ViewsLoader.Choices[name])} / schema {string.Join(",", values)}");
        Assert.Equal(["dashboard", "report"], ViewsLoader.Choices["kind"].Order());
        Assert.Equal("dashboard", At(dashboard, "properties/kind")["const"]!.GetValue<string>());
        // the limits the loader states are in the schema
        Assert.Equal(48, At(core, "$defs/layout_reflow/properties/grid/properties/columns")["maximum"]!.GetValue<int>());
        Assert.Equal(ViewsLoader.MaximumLayouts, At(core, "$defs/layouts")["maxItems"]?.GetValue<int>() ?? ViewsLoader.MaximumLayouts);
    }

    [Fact]
    public void Both_kinds_reference_one_core_file_and_one_format_number()
    {
        var coreId = Load("core")["$id"]!.GetValue<string>();
        var refs = new Dictionary<string, List<string>>();
        foreach (var kind in new[] { "dashboard", "report" })
        {
            var list = new List<string>();
            void Walk(JsonNode? n)
            {
                switch (n)
                {
                    case JsonObject o: foreach (var e in o) { if (e.Key == "$ref") list.Add(e.Value!.GetValue<string>()); else Walk(e.Value); } break;
                    case JsonArray a: foreach (var i in a) Walk(i); break;
                }
            }
            Walk(Load(kind));
            refs[kind] = list;
            Assert.NotEmpty(list);
            Assert.All(list, r => Assert.StartsWith(coreId + "#/$defs/", r));
            Assert.Equal(ViewsFormat.Current, At(Load(kind), "properties/schemaVersion")["const"]!.GetValue<int>());
        }
        // what the kinds share is the same definition in both: the title, the parameters, the theme, the widgets and the layouts
        foreach (var shared in new[] { "title", "parameters", "theme", "locale", "links", "widgets", "layouts" })
            Assert.Equal(At(Load("dashboard"), $"properties/{shared}/$ref")!.GetValue<string>(), At(Load("report"), $"properties/{shared}/$ref")!.GetValue<string>());
        Assert.Equal(ViewsFormat.Current, ViewsFormat.Previous);
    }

    [Fact]
    public void A_change_to_a_schema_needs_the_lock_and_the_lock_names_the_one_format_number()
    {
        var lockFile = Load("views-format.lock");
        var number = lockFile["format"]!.GetValue<int>();
        var status = lockFile["status"]!.GetValue<string>();
        Assert.Equal(ViewsFormat.Current, number);
        Assert.Contains(status, new[] { "open", "frozen" });
        foreach (var name in new[] { "core", "dashboard", "report" })
            Assert.True(Hash(name) == lockFile["sha256"]![name]!.GetValue<string>(),
                $"{name}.schema.json changed (now {Hash(name)}). While the format is open (unreleased) edit the lock in the same commit; once it is frozen (the first release that holds it) a change is the next number of BOTH kinds: " +
                "raise ViewsFormat.Current, the two schemaVersion constants and the lock's format together (plan 32 section 3.1, plan 24a rule 3).");
    }

    private static string Hash(string name)
    {
        var text = File.ReadAllText(Path.Combine(Conformance.SchemaRoot, name + ".schema.json")).Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    [Theory]
    [InlineData("good")]
    public void Every_good_document_is_valid_under_its_kinds_schema(string section)
    {
        foreach (var c in Conformance.Cases()[section]!.AsArray().Where(c => c!["file"]!.GetValue<string>().EndsWith(".json")))
        {
            var file = c!["file"]!.GetValue<string>();
            var errors = Errors(c["kind"]!.GetValue<string>(), file);
            Assert.True(errors.Count == 0, $"{file}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void Every_bad_document_the_schema_can_express_is_invalid_under_it()
    {
        var checkedCount = 0;
        foreach (var c in Conformance.Cases()["bad"]!.AsArray().Where(c => c!["schema"]!.GetValue<bool>()))
        {
            var file = c!["file"]!.GetValue<string>();
            Assert.True(Errors(JsonNode.Parse(Conformance.Read(file))!["kind"]?.GetValue<string>() ?? "dashboard", file).Count > 0, $"{file} is valid under the schema but the case says it is not");
            checkedCount++;
        }
        Assert.True(checkedCount >= 10, $"only {checkedCount} cases are checked against the schema");
    }

    [Fact]
    public void Bad_documents_the_schema_cannot_express_are_valid_under_it_so_the_cases_say_so_honestly()
    {
        foreach (var c in Conformance.Cases()["bad"]!.AsArray().Where(c => !c!["schema"]!.GetValue<bool>()))
        {
            var file = c!["file"]!.GetValue<string>();
            var kind = JsonNode.Parse(Conformance.Read(file))!["kind"]?.GetValue<string>() ?? "dashboard";
            if (kind is not ("dashboard" or "report")) continue;
            Assert.True(Errors(kind, file).Count == 0, $"{file} is invalid under the schema but the case says the schema cannot tell: set \"schema\": true");
        }
    }

    [Fact]
    public void The_bundled_schemas_of_the_editors_judge_every_conformance_document_as_the_schemas_of_the_package_do()
    {
        var bundled = new[] { "dashboard", "report" }.ToDictionary(n => n, n => JsonSchema.FromText(File.ReadAllText(Path.Combine(Conformance.SchemaRoot, "bundled", n + ".schema.json"))));
        var judged = 0;
        foreach (var section in new[] { "good", "bad" })
            foreach (var c in Conformance.Cases()[section]!.AsArray())
            {
                var file = c!["file"]!.GetValue<string>();
                if (!file.EndsWith(".json", StringComparison.Ordinal)) continue;
                var doc = JsonNode.Parse(Conformance.Read(file))!;
                var kind = doc["kind"]?.GetValue<string>() ?? "dashboard";
                if (kind is not ("dashboard" or "report")) continue;
                EvaluationResults one;
                lock (Gate) one = bundled[kind].Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(one.IsValid == (Errors(kind, file).Count == 0), $"{file}: the bundle says {(one.IsValid ? "valid" : "invalid")}, the package's schema says the opposite");
                judged++;
            }
        Assert.True(judged > 30, $"only {judged} documents were judged");
    }

    private static List<string> Errors(string kind, string file)
    {
        var doc = JsonNode.Parse(Conformance.Read(file))!;
        EvaluationResults result;
        lock (Gate) result = Schemas.Value[kind].Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return [.. result.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).Take(5)];
    }
}
