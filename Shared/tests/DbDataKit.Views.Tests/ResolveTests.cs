using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Apache.Arrow.Types;
using Json.Schema;
using DbDataKit.Serving;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>
/// The resolution of a layout as the viewer (plan 32 DBR04): <see cref="WidgetResolver"/> over a fake serving core with a small retail sample (<see cref="RetailServing"/>). The recorded widget results of the sample documents
/// (<c>conformance/views/widget-results</c>, which <c>@dbdatakit/visuals</c> reads too) are what the resolver gives, and they satisfy the <c>rendered-widgets</c> schema. Set <c>DBDATAKIT_TEST_REGENERATE_WIDGET_RESULTS=1</c> to write them again.
/// </summary>
public class ResolveTests
{
    private static readonly DateTimeOffset Fixed = DateTimeOffset.Parse("2026-11-08T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static CatalogRoles? Roles(string model) => model == "retail_sales"
        ? new CatalogRoles("retail_sales", ["sales.net_amount", "sales.net_amount_prev", "sales.margin_rate", "sales.margin_amount", "sales.units"], ["customers.region", "products.category", "products.product", "date.year_month"])
        : null;

    private static ServingContext Viewer(string id = "ana", params (string Key, string Value)[] claims) =>
        new(new ServingCaller(id, id, new HashSet<string> { "analyst" }, claims.ToDictionary(c => c.Key, c => c.Value)), TraceId: "t-" + id);

    private static WidgetResolver Resolver(IServingQueries serving, ResolverOptions? options = null, IServingEntities? entities = null) =>
        new(serving, Roles, options ?? new ResolverOptions { Time = new ManualTimeProvider(Fixed), OnUnexpected = (id, e) => Unexpected.Add($"{id}: {e}") }, entities);

    private static readonly System.Collections.Concurrent.ConcurrentBag<string> Unexpected = [];

    private static ViewDocument Sample(string file)
    {
        var loaded = ViewsLoader.Load(Conformance.Read(file), file);
        Assert.True(loaded.Ok, Conformance.Describe(loaded.Diagnostics));
        return loaded.Document!;
    }

    private static ViewDocument Doc(string widgets, string parameters = "[]", string kind = "dashboard")
    {
        var ids = Regex.Matches(widgets, @"^  ([a-z][a-z0-9_-]*):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var tiles = string.Join("\n", ids.Select((id, i) => $"      - {{widget: {id}, x: 0, y: {i * 2}, w: 12, h: 2}}"));
        var yaml = $"schemaVersion: 1\nkind: {kind}\ntitle: Test\nparameters: {parameters}\nwidgets:\n{widgets}\nlayouts:\n  - name: desk\n    type: reflow\n    default: true\n    grid: {{columns: 12, rows: {ids.Count * 2}}}\n    tiles:\n{tiles}\n";
        var loaded = ViewsLoader.Load(yaml, "test.yaml");
        Assert.True(loaded.Ok, Conformance.Describe(loaded.Diagnostics) + "\n" + yaml);
        return loaded.Document!;
    }

    private static string Type(RenderedWidgets r, string id) => r.Widgets[id]["type"]!.GetValue<string>();

    private static JsonObject Ok(RenderedWidgets r, string id)
    {
        Assert.True(Type(r, id) != "error", r.Widgets[id].ToJsonString() + string.Join("\n", Unexpected));
        return r.Widgets[id];
    }

    private static JsonObject Params(params (string Name, JsonNode? Value)[] values) => new(values.Select(v => KeyValuePair.Create(v.Name, v.Value)));

    private static IReadOnlyDictionary<string, JsonNode?> Values(JsonObject o) => o.ToDictionary(p => p.Key, p => p.Value);

    // ---- the recorded results ----

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string SourceResultsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataKit.Views.Tests.csproj"))) dir = dir.Parent;
        return Path.GetFullPath(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("The test project folder was not found above the output."), "..", "..", "conformance", "views", "widget-results"));
    }

    [Theory]
    [InlineData("dashboard", "retail-sales-today", "samples/dashboards/retail-sales-today.yaml", "desk")]
    [InlineData("report", "retail-sales-monthly", "samples/reports/retail-sales-monthly.yaml", "paged")]
    public async Task The_recorded_widget_results_are_what_the_resolver_gives_over_the_retail_sample(string kind, string name, string file, string layout)
    {
        var rendered = await Resolver(new RetailServing()).ResolveAsync(Viewer(), Sample(file), layout);
        Assert.All(rendered.Widgets, w => Assert.NotEqual("error", w.Value["type"]!.GetValue<string>()));

        // a text widget is drawn from the document and has no file (the fixture client needs none)
        var results = rendered.Widgets.Where(w => w.Value["type"]!.GetValue<string>() != "text").ToList();
        var regenerate = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REGENERATE_WIDGET_RESULTS") == "1";
        var folder = Path.Combine(regenerate ? SourceResultsRoot() : Path.Combine(Conformance.Root, "widget-results"), kind, name);
        if (regenerate)
        {
            Directory.CreateDirectory(folder);
            foreach (var stale in Directory.GetFiles(folder, "*.json")) File.Delete(stale);
            foreach (var (id, widget) in results) File.WriteAllText(Path.Combine(folder, id + ".json"), Lf(widget.ToJsonString(Pretty)) + "\n");
        }
        Assert.Equal(results.Select(r => r.Key).Order(), Directory.GetFiles(folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order());
        foreach (var (id, widget) in results)
            Assert.Equal(File.ReadAllText(Path.Combine(folder, id + ".json")).Replace("\r\n", "\n").TrimEnd(), Lf(widget.ToJsonString(Pretty)).TrimEnd());
    }

    // indented JSON uses the platform newline (CRLF on Windows); the recorded files are LF
    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static readonly Lazy<JsonSchema> RenderedSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(Conformance.SchemaRoot, "rendered-widgets.schema.json"))));

    private static IEnumerable<string> Violations(JsonNode document)
    {
        var result = RenderedSchema.Value.Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });
        return result.IsValid ? [] : result.Details.Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"));
    }

    [Theory]
    [InlineData("samples/dashboards/retail-sales-today.yaml", "desk")]
    [InlineData("samples/dashboards/retail-sales-today.yaml", "phone")]
    [InlineData("samples/reports/retail-sales-monthly.yaml", "paged")]
    public async Task What_a_resolution_gives_satisfies_the_rendered_widgets_schema(string file, string layout)
    {
        var rendered = await Resolver(new RetailServing()).ResolveAsync(Viewer(), Sample(file), layout);
        Assert.Empty(Violations(rendered.ToJson()));
        Assert.Equal("2026-11-08T09:00:00Z", rendered.ToJson()["as_of"]!.GetValue<string>());
    }

    [Fact]
    public void The_schema_refuses_what_the_renderers_do_not_read()
    {
        Assert.NotEmpty(Violations(JsonNode.Parse("""{"kind":"rendered_widgets","layout":"x","as_of":"2026-11-08T09:00:00Z","widgets":{"a":{"type":"metric","value":{"text":3}}}}""")!));
        Assert.NotEmpty(Violations(JsonNode.Parse("""{"kind":"rendered_widgets","layout":"x","as_of":"2026-11-08T09:00:00Z","widgets":{"a":{"type":"image","src":"https://example.com/x.png"}}}""")!));
        Assert.NotEmpty(Violations(JsonNode.Parse("""{"kind":"rendered_widgets","layout":"x","as_of":"yesterday","widgets":{}}""")!));
        Assert.Empty(Violations(JsonNode.Parse("""{"kind":"rendered_widgets","layout":"x","as_of":"2026-11-08T09:00:00Z","widgets":{"a":{"type":"error","code":"denied","message":"no"}}}""")!));
    }

    // ---- what each widget type becomes ----

    [Fact]
    public async Task A_metric_has_server_text_and_a_comparison_with_its_change()
    {
        var doc = Doc("""
              m:
                type: metric
                format: compact
                encodings: {value: sales.net_amount, compare: sales.net_amount_prev}
                query: {model: retail_sales}
              pct:
                type: metric
                format: percent
                encodings: {value: sales.margin_rate}
                query: {model: retail_sales}
            """);
        var r = await Resolver(new RetailServing()).ResolveAsync(Viewer(), doc, "desk");
        var m = r.Widgets["m"];
        var total = RetailServing.Lines.Sum(l => l.Net);
        Assert.Equal(Math.Round(total, 4), m["value"]!["v"]!.GetValue<double>());
        Assert.Matches(@"^\d+(\.\d)?[KMB]$", m["value"]!["text"]!.GetValue<string>());
        Assert.Equal("+11.1%", m["compare"]!["change"]!["text"]!.GetValue<string>());      // 100 against 90
        Assert.Equal("up", m["compare"]!["change"]!["direction"]!.GetValue<string>());
        Assert.EndsWith("%", r.Widgets["pct"]["value"]!["text"]!.GetValue<string>());
        Assert.Null(r.Widgets["pct"]["compare"]);
    }

    [Fact]
    public async Task A_table_has_cell_text_alignment_and_a_total_row_that_says_so()
    {
        var doc = Doc("""
              t:
                type: table
                encodings:
                  columns:
                    - {field: products.category, label: Category}
                    - {field: sales.units, label: Units, format: full}
                    - {field: sales.net_amount, label: Net, format: compact, align: center}
                query:
                  model: retail_sales
                  order: [[sales.units, desc]]
                  totals: {grand_total: true}
            """);
        var r = await Resolver(new RetailServing()).ResolveAsync(Viewer(), doc, "desk");
        var t = Ok(r, "t");
        Assert.Equal("table", t["type"]!.GetValue<string>());
        Assert.Equal(["left", "right", "center"], t["columns"]!.AsArray().Select(c => c!["align"]!.GetValue<string>()));
        var rows = t["rows"]!.AsArray();
        Assert.Equal(5, rows.Count);                                          // four categories and the total
        var total = rows[^1]!;
        Assert.True(total["total"]!.GetValue<bool>());
        Assert.Equal("Total", total["cells"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(RetailServing.Lines.Sum(l => l.Units), total["cells"]![1]!["v"]!.GetValue<long>());
        Assert.Equal(RetailServing.Lines.Sum(l => l.Units).ToString("#,##0", System.Globalization.CultureInfo.InvariantCulture), total["cells"]![1]!["text"]!.GetValue<string>());
        Assert.Null(rows[0]!["total"]);
    }

    [Fact]
    public async Task A_matrix_and_a_chart_are_made_by_the_engines_renderer_from_the_answer()
    {
        var doc = Doc("""
              mx:
                type: matrix
                encodings:
                  rows: [customers.region]
                  columns: [products.category]
                  values: [{field: sales.net_amount, format: compact}]
                  totals: {rows: true, columns: true}
                query: {model: retail_sales}
              ch:
                type: chart
                mark: bar
                encodings: {x: customers.region, y: sales.net_amount, color: products.category, tooltip: [sales.units]}
                query: {model: retail_sales, limit: 100}
            """);
        var r = await Resolver(new RetailServing()).ResolveAsync(Viewer(), doc, "desk");
        var mx = Ok(r, "mx");
        Assert.Equal("matrix", mx["type"]!.GetValue<string>());
        Assert.Equal("mx", mx["id"]!.GetValue<string>());
        Assert.Equal(4 + 1, mx["rows"]!.AsArray().Count);                          // regions and the total row
        Assert.Equal(4 + 1, mx["columns"]!.AsArray().Count);                       // categories and the total column
        Assert.True(mx["rows"]![4]!["total"]!.GetValue<bool>());
        // the grand total is the result's own, not summed by the view
        Assert.Equal(Math.Round(RetailServing.Lines.Sum(l => l.Net), 4), mx["rows"]![4]!["cells"]![4]!["v"]!.GetValue<double>());

        var ch = Ok(r, "ch");
        Assert.Equal("chart", ch["type"]!.GetValue<string>());
        var spec = ch["vega_lite"]!;
        Assert.Equal(16, spec["data"]!["values"]!.AsArray().Count);
        Assert.Equal("customers_region", spec["encoding"]!["x"]!["field"]!.GetValue<string>());
        Assert.Equal("nominal", spec["encoding"]!["x"]!["type"]!.GetValue<string>());
        Assert.Equal("quantitative", spec["encoding"]!["y"]!["type"]!.GetValue<string>());
        Assert.Equal("sales_units", spec["encoding"]!["tooltip"]![0]!["field"]!.GetValue<string>());
        Assert.Empty(Violations(r.ToJson()));
    }

    [Fact]
    public async Task A_matrix_the_renderer_refuses_is_that_widgets_error_with_the_renderers_code_at_the_encoding()
    {
        // a stacked bar of a measure that does not add up (the margin rate) is refused by the engine's renderer: stack_not_additive
        var doc = Doc("""
              bad:
                type: chart
                mark: bar
                encodings: {x: customers.region, y: sales.margin_rate, color: products.category}
                query: {model: retail_sales}
              fine:
                type: metric
                encodings: {value: sales.units}
                query: {model: retail_sales}
            """);
        var r = await Resolver(new RetailServing()).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal("error", Type(r, "bad"));
        Assert.Equal("stack_not_additive", r.Widgets["bad"]["code"]!.GetValue<string>());
        Assert.Equal("widgets.bad.encodings.y", r.Widgets["bad"]["path"]!.GetValue<string>());
        Assert.Equal("metric", Type(r, "fine"));
    }

    [Fact]
    public async Task A_chart_channels_format_labels_a_categorical_axis_in_the_members_order_and_gives_a_quantitative_one_a_generated_d3_string()
    {
        var doc = Doc("""
              ch:
                type: chart
                mark: bar
                encodings:
                  x: {field: date.year_month, format: {style: number, grouping: true}}
                  y: {field: sales.net_amount, format: {style: currency, currency: USD, decimals: 0}}
                  tooltip: [{field: sales.units, format: compact}]
                query: {model: retail_sales, limit: 100}
            """);
        var r = await Resolver(new RetailServing()).ResolveAsync(Viewer(), doc, "desk");
        var ch = Ok(r, "ch");
        var spec = ch["vega_lite"]!;
        // the month members are written as the format reads them, in the order of the raw members (here the numbers 202601 to 202610), never alphabetically
        Assert.Equal("date_year_month__x", spec["encoding"]!["x"]!["field"]!.GetValue<string>());
        Assert.Equal(["202,601", "202,602", "202,603", "202,604", "202,605", "202,606", "202,607", "202,608", "202,609", "202,610"], spec["encoding"]!["x"]!["sort"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("202,601", spec["data"]!["values"]![0]!["date_year_month__x"]!.GetValue<string>());
        Assert.NotNull(spec["encoding"]!["x"]!["title"]);
        // a quantitative channel keeps its field and gets the d3 string the format maps to; the engine's own subset saw no `format`
        Assert.Equal("sales_net_amount", spec["encoding"]!["y"]!["field"]!.GetValue<string>());
        Assert.Equal("$,.0f", ch["formats"]!["y"]!.GetValue<string>());
        Assert.Equal(".2~s", ch["formats"]!["tooltip"]![0]!.GetValue<string>());
        Assert.Null(spec["encoding"]!["y"]!["format"]);
        Assert.Empty(Violations(r.ToJson()));
    }

    [Fact]
    public async Task A_chart_without_a_format_has_no_formats_and_a_bad_format_is_refused_by_the_loader_before_it_is_drawn()
    {
        var doc = Doc("""
              ch:
                type: chart
                mark: bar
                encodings: {x: customers.region, y: sales.net_amount}
                query: {model: retail_sales}
            """);
        var r = await Resolver(new RetailServing()).ResolveAsync(Viewer(), doc, "desk");
        Assert.Null(Ok(r, "ch")["formats"]);
        var bad = ViewsLoader.Load("schemaVersion: 1\nkind: dashboard\ntitle: T\nwidgets:\n  ch:\n    type: chart\n    mark: bar\n    encodings: {x: {field: customers.region, format: {date: yyy}}, y: sales.net_amount}\n    query: {model: retail_sales}\nlayouts:\n  - {name: desk, type: reflow, default: true, grid: {columns: 12}, tiles: [{widget: ch, x: 0, y: 0, w: 12, h: 6}]}\n", "t.yaml");
        Assert.False(bad.Ok);
        Assert.Contains(bad.Diagnostics, d => d.KeyPath == "widgets.ch.encodings.x.format.date");
    }

    [Fact]
    public async Task A_matrix_formats_its_axis_labels_and_its_values_by_mapping_and_takes_the_sort_of_an_entry()
    {
        var doc = Doc("""
              mx:
                type: matrix
                encodings:
                  rows: [{field: customers.region, sort: {by: label, dir: desc}}]
                  columns: [{field: date.year_month, format: {style: number, grouping: true}}]
                  values:
                    - {field: sales.net_amount, format: {style: currency, currency: EUR, decimals: 0}}
                    - {field: sales.units}
                query: {model: retail_sales}
            """);
        var r = await Resolver(new RetailServing()).ResolveAsync(Viewer(), doc, "desk");
        var mx = Ok(r, "mx");
        var columns = mx["columns"]!.AsArray();
        Assert.Equal(20, columns.Count);                                           // ten months, two values under each
        Assert.Equal("202,601", columns[0]!["labels"]![0]!.GetValue<string>());
        Assert.Equal("202,601", columns[1]!["labels"]![0]!.GetValue<string>());
        Assert.Equal("202,610", columns[19]!["labels"]![0]!.GetValue<string>());
        // the regions are sorted by label descending, as the entry says
        Assert.Equal(["West", "South", "East", "Central"], mx["rows"]!.AsArray().Select(row => row!["labels"]![0]!.GetValue<string>()));
        var first = mx["rows"]![0]!["cells"]!;
        var euros = first[0]!["v"]!.GetValue<double>();
        Assert.Equal(ViewFormat.Number(euros, new JsonObject { ["style"] = "currency", ["currency"] = "EUR", ["decimals"] = 0 }), first[0]!["text"]!.GetValue<string>());
        Assert.StartsWith("€", first[0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("€", first[1]!["text"]!.GetValue<string>());          // the second value has no format of its own: the engine's text
        Assert.Empty(Violations(r.ToJson()));
    }

    [Fact]
    public async Task A_matrix_sorted_by_an_entry_and_by_the_axis_sort_is_that_widgets_error()
    {
        var doc = Doc("""
              mx:
                type: matrix
                encodings:
                  rows: [{field: customers.region, sort: {by: label}}]
                  values: [{field: sales.net_amount}]
                  sort: {rows: {by: sort_key}}
                query: {model: retail_sales}
            """);
        var r = await Resolver(new RetailServing()).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal("error", Type(r, "mx"));
        Assert.Equal("widgets.mx.encodings.sort.rows", r.Widgets["mx"]["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Text_has_its_parameters_substituted_as_text_and_an_image_its_bytes_as_a_data_uri()
    {
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };
        var hash = "sha256:" + new string('a', 64);
        var doc = Doc($$$"""
              note:
                type: text
                text: "Region **{{params.region}}**, top {{params.top}}, none: [{{params.none}}]"
              logo:
                type: image
                hash: {{{hash}}}
                media_type: image/png
                alt: Logo
              lost:
                type: image
                hash: sha256:{{{new string('b', 64)}}}
                media_type: image/png
                alt: Missing
            """, "[{name: region, type: string, many: true, default: []}, {name: top, type: integer, default: 5}, {name: none, type: string}]");
        var options = new ResolverOptions { Time = new ManualTimeProvider(Fixed), ImageBytes = h => h == hash ? png : null };
        var r = await Resolver(new RetailServing(), options).ResolveAsync(Viewer(), doc, "desk", Values(Params(("region", new JsonArray("West", "East")))));
        Assert.Equal("Region **West, East**, top 5, none: []", r.Widgets["note"]["text"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), r.Widgets["logo"]["src"]!.GetValue<string>());
        Assert.Equal(ResolveCodes.ImageNotFound, r.Widgets["lost"]["code"]!.GetValue<string>());
        Assert.Empty(Violations(r.ToJson()));

        var big = await Resolver(new RetailServing(), new ResolverOptions { Time = new ManualTimeProvider(Fixed), ImageBytes = _ => png, MaxImageBytes = 4 }).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal(ResolveCodes.TooLarge, big.Widgets["logo"]["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_control_lists_the_values_the_viewer_may_see_and_one_without_a_list_has_nothing_to_resolve()
    {
        var doc = Doc("""
              region: {type: control, parameter: region, control: multiselect, values: {attribute: customers.region, limit: 20}}
              when: {type: control, parameter: region, control: text}
              m: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales}}
            """, "[{name: region, type: string, many: true, default: []}]");
        var serving = new RetailServing();
        var all = await Resolver(serving).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal(["West", "Central", "East", "South"], all.Widgets["region"]["values"]!.AsArray().Select(v => v!["value"]!.GetValue<string>()));
        Assert.Null(all.Widgets["when"]["values"]);
        var east = await Resolver(serving).ResolveAsync(Viewer("eve", ("region", "East")), doc, "desk");
        Assert.Equal(["East"], east.Widgets["region"]["values"]!.AsArray().Select(v => v!["value"]!.GetValue<string>()));
        Assert.Contains(serving.Received, r => r.Request is ServingRequest.Semantic s && s.Source == "semantic:retail_sales" && s.Json.Contains("\"members\"", StringComparison.Ordinal));
    }

    // ---- the viewer ----

    [Fact]
    public async Task Each_viewer_gets_their_own_rendering_under_their_own_identity_and_the_cache_key_differs()
    {
        var doc = Sample("samples/dashboards/retail-sales-today.yaml");
        var serving = new RetailServing();
        var resolver = Resolver(serving);
        var ana = await resolver.ResolveAsync(Viewer("ana"), doc, "desk");
        var eve = await resolver.ResolveAsync(Viewer("eve", ("region", "East")), doc, "desk");
        var eveAgain = await resolver.ResolveAsync(Viewer("eve", ("region", "East")), doc, "desk");

        // the same widget, different rows: eve's access reaches the East only
        Assert.NotEqual(ana.Widgets["net"]["value"]!["v"]!.GetValue<double>(), eve.Widgets["net"]["value"]!["v"]!.GetValue<double>());
        Assert.Equal(Math.Round(RetailServing.Lines.Where(l => l.Region == "East").Sum(l => l.Net), 4), eve.Widgets["net"]["value"]!["v"]!.GetValue<double>());
        Assert.Equal(Math.Round(RetailServing.Lines.Sum(l => l.Net), 4), ana.Widgets["net"]["value"]!["v"]!.GetValue<double>());
        Assert.All(eve.Widgets["by_region"]["vega_lite"]!["data"]!["values"]!.AsArray(), v => Assert.Equal("East", v!["customers_region"]!.GetValue<string>()));

        foreach (var id in new[] { "net", "margin", "by_region", "top_products", "regions" })
        {
            Assert.NotEqual(ana.CacheKeys[id], eve.CacheKeys[id]);
            Assert.Equal(eve.CacheKeys[id], eveAgain.CacheKeys[id]);
        }
        Assert.DoesNotContain("note", ana.CacheKeys.Keys);                           // text runs no request

        // every request carried the context of the viewer it was made for
        var byCaller = serving.Received.GroupBy(r => r.Context.Caller.Id).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(5, byCaller["ana"]);
        Assert.Equal(10, byCaller["eve"]);
        Assert.All(serving.Received, r => Assert.Equal("t-" + r.Context.Caller.Id, r.Context.TraceId));
    }

    [Fact]
    public async Task A_denied_widget_and_a_widget_the_engine_refuses_are_error_entries_and_the_others_render()
    {
        var doc = Doc("""
              ok: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales}}
              denied: {type: metric, encodings: {value: sales.net_amount}, query: {model: retail_sales}}
              refused: {type: metric, encodings: {value: sales.margin_amount}, query: {model: retail_sales}}
              broken: {type: metric, encodings: {value: sales.margin_rate}, query: {model: retail_sales}}
              nocatalog: {type: metric, encodings: {value: sales.units}, query: {model: other}}
              typo: {type: metric, encodings: {value: sales.nope}, query: {model: retail_sales}}
            """);
        var serving = new RetailServing
        {
            Before = (_, request) => request is ServingRequest.Semantic s ? s.Json switch
            {
                var j when j.Contains("sales.net_amount", StringComparison.Ordinal) => throw new ServingDeniedException("net_amount is not available to ana."),
                var j when j.Contains("sales.margin_amount", StringComparison.Ordinal) => throw new ServingInvalidException("unknown_member: sales.margin_amount is not a catalog member; schema_invalid: x"),
                var j when j.Contains("sales.margin_rate", StringComparison.Ordinal) => throw new InvalidOperationException("password=hunter2 for server db01"),
                _ => Task.CompletedTask,
            } : Task.CompletedTask,
        };
        var r = await Resolver(serving).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal("metric", Type(r, "ok"));
        Assert.Equal("denied", r.Widgets["denied"]["code"]!.GetValue<string>());
        Assert.Equal("net_amount is not available to ana.", r.Widgets["denied"]["message"]!.GetValue<string>());
        Assert.Equal("unknown_member", r.Widgets["refused"]["code"]!.GetValue<string>());
        Assert.Contains("not a catalog member", r.Widgets["refused"]["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("error", r.Widgets["broken"]["code"]!.GetValue<string>());
        Assert.DoesNotContain("hunter2", r.Widgets["broken"].ToJsonString(), StringComparison.Ordinal);       // nothing of an unexpected failure but its kind
        Assert.Equal(ResolveCodes.NoCatalog, r.Widgets["nocatalog"]["code"]!.GetValue<string>());
        Assert.Equal("unknown_member", r.Widgets["typo"]["code"]!.GetValue<string>());
        Assert.Equal("widgets.typo.encodings.value", r.Widgets["typo"]["path"]!.GetValue<string>());
        Assert.Empty(Violations(r.ToJson()));
    }

    [Fact]
    public async Task A_widget_that_runs_out_of_time_is_stopped_on_the_injected_clock_and_the_others_render()
    {
        var clock = new ManualTimeProvider(Fixed);
        var doc = Doc("""
              slow: {type: metric, encodings: {value: sales.net_amount}, query: {model: retail_sales}}
              quick: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales}}
            """);
        var started = new TaskCompletionSource();
        var serving = new RetailServing
        {
            Before = async (_, request) =>
            {
                if (request is ServingRequest.Semantic s && s.Json.Contains("sales.net_amount", StringComparison.Ordinal)) { started.SetResult(); await Task.Delay(Timeout.Infinite, CancellationToken.None); }
            },
        };
        // the fake ignores the token it is given while it waits, as a stuck source would; the resolver stops waiting
        var resolving = Resolver(serving, new ResolverOptions { Time = clock, WidgetTimeout = TimeSpan.FromSeconds(10) }).ResolveAsync(Viewer(), doc, "desk");
        await started.Task;
        Assert.False(resolving.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(11));
        var r = await resolving.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("timeout", r.Widgets["slow"]["code"]!.GetValue<string>());
        Assert.Contains("10 seconds", r.Widgets["slow"]["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("metric", Type(r, "quick"));
        // and the core was asked to hold the request to the same limits
        Assert.All(serving.Received, x => { Assert.Equal(TimeSpan.FromSeconds(10), x.Request.Asked!.Timeout); Assert.Equal(100_000L, x.Request.Asked.MaxRows); });
    }

    [Fact]
    public async Task Callers_cancellation_stops_the_whole_resolution()
    {
        using var cts = new CancellationTokenSource();
        var serving = new RetailServing { Before = (_, _) => { cts.Cancel(); return Task.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolver(serving).ResolveAsync(Viewer(), Sample("samples/dashboards/retail-sales-today.yaml"), "desk", cancellationToken: cts.Token));
    }

    // ---- limits ----

    [Fact]
    public async Task An_answer_over_the_row_limit_is_an_error_not_a_cut_table_and_widgets_past_the_cap_are_not_run()
    {
        var doc = Doc("""
              big:
                type: table
                encodings: {columns: [{field: products.product}, {field: sales.units}]}
                query: {model: retail_sales}
              small: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales}}
              late: {type: metric, encodings: {value: sales.net_amount}, query: {model: retail_sales}}
            """);
        var serving = new RetailServing();
        var r = await Resolver(serving, new ResolverOptions { Time = new ManualTimeProvider(Fixed), MaxRows = 5, MaxWidgets = 2 }).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal(ResolveCodes.TooLarge, r.Widgets["big"]["code"]!.GetValue<string>());
        Assert.Equal("metric", Type(r, "small"));
        Assert.Equal(ResolveCodes.TooManyWidgets, r.Widgets["late"]["code"]!.GetValue<string>());
        Assert.Equal(2, serving.Received.Count);                                    // `late` never reached the core
    }

    [Fact]
    public async Task Parallelism_is_bounded()
    {
        var doc = Doc(string.Join("\n", Enumerable.Range(0, 6).Select(i => $"  w{i}: {{type: metric, encodings: {{value: sales.units}}, query: {{model: retail_sales}}}}")));
        var running = 0; var peak = 0;
        var serving = new RetailServing
        {
            Before = async (_, _) =>
            {
                var now = Interlocked.Increment(ref running);
                lock (doc) peak = Math.Max(peak, now);
                await Task.Yield();
                await Task.Delay(1);
                Interlocked.Decrement(ref running);
            },
        };
        var r = await Resolver(serving, new ResolverOptions { MaxParallelism = 2, Time = TimeProvider.System }).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal(6, r.Widgets.Count(w => w.Value["type"]!.GetValue<string>() == "metric"));
        Assert.True(peak <= 2, $"{peak} widgets ran at once");
    }

    // ---- layouts and `only` ----

    [Fact]
    public async Task Only_the_widgets_a_layout_places_are_run_and_only_narrows_that_further()
    {
        var doc = Sample("samples/dashboards/retail-sales-today.yaml");
        var serving = new RetailServing();
        var resolver = Resolver(serving);
        var phone = await resolver.ResolveAsync(Viewer(), doc, "phone");
        Assert.Equal(["regions", "net", "margin", "by_region", "top_products"], phone.Widgets.Keys);       // `note` is not placed on the phone
        Assert.Equal(5, serving.Received.Count);

        serving.Received.Clear();
        var narrowed = await resolver.ResolveAsync(Viewer(), doc, "desk", only: ["net", "note", "not_a_widget"]);
        Assert.Equal(["net", "note"], narrowed.Widgets.Keys);
        Assert.Single(serving.Received);
        Assert.Equal("phone", phone.Layout);

        var ex = await Assert.ThrowsAsync<ViewResolutionException>(() => resolver.ResolveAsync(Viewer(), doc, "nope"));
        Assert.Equal(ResolveCodes.UnknownLayout, ex.Code);
        Assert.Contains("`desk`", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_widget_says_which_parameters_it_uses()
    {
        var doc = Doc("""
              c: {type: control, parameter: region, control: text}
              m: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales, filters: [{attribute: customers.region, operator: equals, values: {param: r}}], params: {r: region}}}
              n: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales}}
              t: {type: text, text: "Top {{ params.top }}"}
              s: {type: table, encodings: {columns: [{field: region}]}, query: {mode: explicit, semantic: {model: retail_sales, sql: "select customers.region as region from retail_sales where x = :q and y = ?::int"}, params: {q: top}}}
            """, "[{name: region, type: string, many: true, default: []}, {name: top, type: integer, default: 3}]");
        string[] Used(string id) => [.. WidgetResolver.UsedParameters(doc.Widget(id)!).Order()];
        Assert.Equal(["region"], Used("c"));
        Assert.Equal(["region"], Used("m"));
        Assert.Empty(Used("n"));
        Assert.Equal(["top"], Used("t"));
        Assert.Equal(["top"], Used("s"));
    }

    // ---- parameters ----

    [Fact]
    public async Task The_viewers_values_are_bound_beside_the_request_with_the_documents_definitions_and_an_unset_filter_drops_out()
    {
        var doc = Sample("samples/dashboards/retail-sales-today.yaml");
        var serving = new RetailServing();
        var resolver = Resolver(serving);
        var chosen = await resolver.ResolveAsync(Viewer(), doc, "desk", Values(Params(("region", new JsonArray("West", "East")))), only: ["net"]);
        var sent = JsonNode.Parse(((ServingRequest.Semantic)serving.Received[0].Request).Json)!;
        Assert.Equal("""["West","East"]""", sent["parameters"]!["region"]!.ToJsonString());
        Assert.Equal("""{"type":"string","for":"customers.region","many":true}""", sent["parameter_definitions"]!["region"]!.ToJsonString());
        Assert.Equal("""{"param":"region"}""", sent["query"]!["filters"]![0]!["values"]!.ToJsonString());      // the marker stays: the request is the literal form's twin
        Assert.Equal(Math.Round(RetailServing.Lines.Where(l => l.Region is "West" or "East").Sum(l => l.Net), 4), chosen.Widgets["net"]["value"]!["v"]!.GetValue<double>());

        serving.Received.Clear();
        var none = await resolver.ResolveAsync(Viewer(), doc, "desk", only: ["net"]);                             // the default is the empty list: all regions
        var sentNone = JsonNode.Parse(((ServingRequest.Semantic)serving.Received[0].Request).Json)!;
        Assert.Null(sentNone["query"]!["filters"]);
        Assert.Null(sentNone["parameters"]);
        Assert.Equal(Math.Round(RetailServing.Lines.Sum(l => l.Net), 4), none.Widgets["net"]["value"]!["v"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_url_value_is_read_as_the_parameters_type_and_a_value_that_is_not_one_refuses_the_resolution()
    {
        var doc = Doc("""
              m: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales, filters: [{attribute: date.year_month, operator: gte, values: [{param: from}]}]}}
            """, "[{name: from, type: integer, for: date.year_month, default: 202601}, {name: top, type: integer, default: 1}]");
        var serving = new RetailServing();
        var resolver = Resolver(serving);
        var r = await resolver.ResolveAsync(Viewer(), doc, "desk", Values(Params(("from", JsonValue.Create("202605")), ("top", JsonValue.Create(3.0)))));
        Assert.Null(JsonNode.Parse(((ServingRequest.Semantic)serving.Received[0].Request).Json)!["parameters"]!["top"]);       // a parameter no marker uses is not sent
        var sent = JsonNode.Parse(((ServingRequest.Semantic)serving.Received[0].Request).Json)!;
        Assert.Equal(202605, sent["parameters"]!["from"]!.GetValue<long>());
        Assert.Equal(RetailServing.Lines.Where(l => l.YearMonth >= 202605).Sum(l => l.Units), r.Widgets["m"]["value"]!["v"]!.GetValue<long>());

        foreach (var bad in new[] { Params(("from", JsonValue.Create("May"))), Params(("from", new JsonArray(1, 2))), Params(("nope", JsonValue.Create(1))) })
        {
            var ex = await Assert.ThrowsAsync<ViewResolutionException>(() => resolver.ResolveAsync(Viewer(), doc, "desk", Values(bad)));
            Assert.Equal(ResolveCodes.BadParameterValue, ex.Code);
        }
    }

    [Fact]
    public async Task A_required_parameter_with_no_value_is_the_error_of_the_widgets_that_use_it_and_its_control_still_draws()
    {
        var doc = Doc("""
              q: {type: control, parameter: quarter, control: text}
              uses: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales, filters: [{attribute: date.year_month, operator: equals, values: [{param: quarter}]}]}}
              free: {type: metric, encodings: {value: sales.units}, query: {model: retail_sales}}
            """, "[{name: quarter, type: integer, for: date.year_month, required: true}]");
        var serving = new RetailServing();
        var without = await Resolver(serving).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal("control", Type(without, "q"));
        Assert.Equal(ResolveCodes.RequiredParameter, without.Widgets["uses"]["code"]!.GetValue<string>());
        Assert.Equal("metric", Type(without, "free"));
        Assert.Single(serving.Received);                                              // `uses` never reached the core

        var given = await Resolver(serving).ResolveAsync(Viewer(), doc, "desk", Values(Params(("quarter", JsonValue.Create(202603)))));
        Assert.Equal(RetailServing.Lines.Where(l => l.YearMonth == 202603).Sum(l => l.Units), given.Widgets["uses"]["value"]!["v"]!.GetValue<long>());
    }

    // ---- the other query forms ----

    private sealed class Capturing(Func<ServingRequest, IServingAnswer> answer) : IServingQueries
    {
        public List<ServingRequest> Seen { get; } = [];

        public Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default) { Seen.Add(request); return Task.FromResult(answer(request)); }

        public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Rows(Schema schema, RecordBatch batch) : IServingAnswer
    {
        public Schema Schema => schema;
        public ServingProvenance Provenance { get; } = new("x", null, "prod");
        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { yield return batch; await Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public static Rows Of(params (string Name, string[] Values)[] columns)
        {
            var fields = columns.Select(c => new Field(c.Name, StringType.Default, true)).ToList();
            var arrays = columns.Select(c => { var b = new StringArray.Builder(); foreach (var v in c.Values) b.Append(v); return (IArrowArray)b.Build(); }).ToList();
            var schema = new Schema(fields, null);
            return new Rows(schema, new RecordBatch(schema, arrays, columns[0].Values.Length));
        }

        public static Rows OfNumbers(string name, params long[] values)
        {
            var b = new Int64Array.Builder();
            foreach (var v in values) b.Append(v);
            var schema = new Schema([new Field(name, Int64Type.Default, true)], null);
            return new Rows(schema, new RecordBatch(schema, [b.Build()], values.Length));
        }
    }

    [Fact]
    public async Task A_written_semantic_request_and_written_semantic_sql_run_with_the_values_bound_and_an_answer_with_no_descriptor_is_described_by_its_schema()
    {
        var doc = Doc("""
              req:
                type: table
                encodings: {columns: [{field: sales.units}]}
                query:
                  mode: explicit
                  semantic:
                    model: retail_sales
                    request: {measures: [sales.units], filters: [{attribute: customers.region, operator: equals, values: [{param: r}]}]}
                  params: {r: region}
              sql:
                type: table
                encodings: {columns: [{field: region}, {field: units, label: Units}]}
                query:
                  mode: explicit
                  semantic:
                    model: retail_sales
                    sql: "select customers.region as region, sum(sales.units) as units from retail_sales where date.year_month >= :from and customers.region in :r group by 1"
                  params: {r: region}
            """, "[{name: region, type: string, for: customers.region, many: true, default: []}, {name: from, type: integer, default: 202601}]");
        var seen = new Capturing(r => r is ServingRequest.Semantic ? Rows.OfNumbers("sales_units", 42) : Rows.Of(("region", ["West"]), ("units", ["7"])));
        var r = await Resolver(seen).ResolveAsync(Viewer(), doc, "desk", Values(Params(("region", new JsonArray("West")))));

        var request = Assert.IsType<ServingRequest.Semantic>(seen.Seen.First(s => s is ServingRequest.Semantic));
        Assert.Equal("semantic:retail_sales", request.Source);
        var json = JsonNode.Parse(request.Json)!;
        Assert.Equal("""["West"]""", json["parameters"]!["r"]!.ToJsonString());               // bound under the request's own name, not the document's
        Assert.Equal("""{"type":"string","for":"customers.region","many":true}""", json["parameter_definitions"]!["r"]!.ToJsonString());
        Assert.Equal(42, r.Widgets["req"]["rows"]![0]!["cells"]![0]!["v"]!.GetValue<long>());

        Assert.True(seen.Seen.Any(s => s is ServingRequest.SemanticSql), r.Widgets["sql"].ToJsonString() + string.Join("\n", Unexpected));
        var sql = Assert.IsType<ServingRequest.SemanticSql>(seen.Seen.Single(s => s is ServingRequest.SemanticSql));
        Assert.Equal("semantic:retail_sales", sql.Source);
        Assert.Equal(["from", "r"], sql.Parameters!.Select(p => p.Name).Order());
        Assert.Equal("""["West"]""", sql.Parameters!.Single(p => p.Name == "r").Text);
        Assert.Equal("list", sql.Parameters!.Single(p => p.Name == "r").Type);
        Assert.Equal(("int64", "202601"), (sql.Parameters!.Single(p => p.Name == "from").Type, sql.Parameters!.Single(p => p.Name == "from").Text));
        Assert.Equal("Units", r.Widgets["sql"]["columns"]![1]!["label"]!.GetValue<string>());
        Assert.Equal("West", r.Widgets["sql"]["rows"]![0]!["cells"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_saved_query_is_called_on_its_model_and_without_one_it_is_not_run_here()
    {
        var doc = Doc("""
              s:
                type: metric
                encodings: {value: total}
                query: {mode: explicit, saved: total_revenue, outputs: [total], params: {quarter: q}}
            """, "[{name: q, type: string, default: FY27-Q1}]");
        var seen = new Capturing(_ => Rows.OfNumbers("total", 9));
        var refused = await Resolver(seen).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal(ResolveCodes.NotRunHere, refused.Widgets["s"]["code"]!.GetValue<string>());
        Assert.Empty(seen.Seen);

        var options = new ResolverOptions { Time = new ManualTimeProvider(Fixed), SavedQueryModel = n => n == "total_revenue" ? "sales" : null };
        var r = await Resolver(seen, options).ResolveAsync(Viewer(), doc, "desk");
        var call = Assert.IsType<ServingRequest.SemanticSql>(Assert.Single(seen.Seen));
        Assert.Equal("semantic:sales", call.Source);
        Assert.Equal("SELECT * FROM total_revenue(quarter := :quarter)", call.Text);
        Assert.Equal("FY27-Q1", call.Parameters!.Single().Text);
        Assert.Equal(9, r.Widgets["s"]["value"]!["v"]!.GetValue<long>());
    }

    private sealed class Entities(Rows rows) : IServingEntities
    {
        public List<string> Read { get; } = [];

        public Task<IServingAnswer> ReadAsync(ServingContext context, string entity, DateTimeOffset? asOf, CancellationToken cancellationToken = default)
        {
            Read.Add(entity);
            return Task.FromResult<IServingAnswer>(rows);
        }

        public Task<ServingWriteResult> WriteAsync(ServingContext context, string entity, IAsyncEnumerable<RecordBatch> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_store_read_goes_through_the_entities_service_a_filter_is_not_run_and_a_ref_is_not_held_here()
    {
        var entities = new Entities(Rows.Of(("id", ["1", "2", "3"]), ("name", ["a", "b", "c"])));
        var doc = Doc("""
              st:
                type: table
                encodings: {columns: [{field: id}, {field: name}]}
                query: {mode: explicit, store: {model: crm, entity: accounts, columns: [id, name], limit: 2}}
              filtered:
                type: table
                encodings: {columns: [{field: id}]}
                query: {mode: explicit, store: {model: crm, entity: accounts, columns: [id], filter: {name: a}}}
              held:
                type: table
                encodings: {columns: [{field: region}]}
                query: {mode: explicit, ref: result.assistant_1, outputs: [region]}
            """);
        var serving = new RetailServing();
        var r = await Resolver(serving, entities: entities).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal(["accounts"], entities.Read);
        Assert.Equal(2, r.Widgets["st"]["rows"]!.AsArray().Count);                    // the read's own limit
        Assert.Equal("a", r.Widgets["st"]["rows"]![0]!["cells"]![1]!["text"]!.GetValue<string>());
        Assert.Equal(ResolveCodes.NotRunHere, r.Widgets["filtered"]["code"]!.GetValue<string>());
        Assert.Equal(ResolveCodes.NotRunHere, r.Widgets["held"]["code"]!.GetValue<string>());
        Assert.Empty(serving.Received);

        var without = await Resolver(serving).ResolveAsync(Viewer(), doc, "desk");
        Assert.Equal(ResolveCodes.NotRunHere, without.Widgets["st"]["code"]!.GetValue<string>());
    }
}
