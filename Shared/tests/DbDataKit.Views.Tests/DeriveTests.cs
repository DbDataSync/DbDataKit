using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Semantic;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>
/// <c>DeriveRequest</c> and the catalog check (plan 32 section 2.3, DBR03): <c>conformance/views/derive</c> holds a widget, the catalog's roles and the request it must give (or the refusals), and the TypeScript
/// port in <c>@dbdatakit/visuals</c> runs the same files. The requests are also run through the real engine's validator over the conformance catalog.
/// </summary>
public class DeriveTests
{
    private static readonly Catalog Cat = Catalog.Load(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog", "catalog.yaml"));
    private static readonly Validator Engine = new(Cat);

    public static IEnumerable<object[]> Good => Conformance.Rows("good", "derive/cases.json");
    public static IEnumerable<object[]> Refused => Conformance.Rows("refused", "derive/cases.json");

    private static JsonNode Case(string file) => JsonNode.Parse(Conformance.Read("derive/" + file))!;

    private static CatalogRoles Roles() => CatalogRoles.FromJson(Conformance.Cases("derive/cases.json")["catalogs"]!["sales_catalog"]!);

    private static IEnumerable<string> Parameters(JsonNode c) => c["parameters"]!.AsArray().Select(p => p!.GetValue<string>());

    private static ViewWidget Widget(JsonNode c, string key = "widgets")
    {
        IReadOnlyDictionary<string, ViewGroup>? groups = null;
        if (c["groups"] is { } declared)
        {
            var read = ViewsLoader.LoadGroups(declared.ToJsonString(), "derive.json");
            Assert.True(read.Groups is not null, Conformance.Describe(read.Diagnostics));
            groups = read.Groups;
        }
        var result = ViewsLoader.LoadWidgets(c[key]!.ToJsonString(), "derive.json", Parameters(c), groups: groups);
        Assert.True(result.Ok, Conformance.Describe(result.Diagnostics));
        return result.Widgets!.Single();
    }

    // the validator is the production adapter (SemanticRequestValidator, which DBR10 moved into the library): it resolves a request's parameters before it validates

    public static CatalogRoles EngineRoles() => new("sales_catalog", name => Cat.IsMeasure(name) ? CatalogRole.Measure : Cat.IsAttribute(name) ? CatalogRole.Attribute : CatalogRole.Unknown);

    private static IReadOnlyDictionary<string, CatalogBinding> Catalogs() =>
        new Dictionary<string, CatalogBinding> { ["sales_catalog"] = new(EngineRoles(), new SemanticRequestValidator(Cat)) };

    [Theory, MemberData(nameof(Good))]
    public void A_widget_gives_exactly_the_golden_request_byte_for_byte(string file)
    {
        var c = Case(file);
        var derived = DeriveRequest.Derive(Widget(c), Roles(), file);
        Assert.True(derived.Ok, Conformance.Describe(derived.Diagnostics));
        Assert.Equal(c["request"]!.ToJsonString(), derived.Request!.ToJsonString());
        var expectedBindings = c["bindings"]?.AsArray().Select(b => (b!["request"]!.GetValue<string>(), b["document"]!.GetValue<string>())).ToList() ?? [];
        Assert.Equal(expectedBindings, derived.Bindings.Select(b => (b.RequestParameter, b.DocumentParameter)).ToList());
    }

    [Theory, MemberData(nameof(Good))]
    public void A_derived_request_validates_against_the_conformance_model(string file)
    {
        var request = (JsonObject)Case(file)["request"]!;
        var errors = new SemanticRequestValidator(Cat).Validate(request);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Code} {e.Path}: {e.Message}")));
    }

    [Theory, MemberData(nameof(Good))]
    public void The_check_of_a_derived_widget_through_the_engine_has_nothing_to_say(string file)
    {
        var widget = Widget(Case(file));
        var diagnostics = CatalogCheck.Check([widget], file, Catalogs());
        Assert.True(diagnostics.Count == 0, Conformance.Describe(diagnostics));
    }

    [Theory, MemberData(nameof(Refused))]
    public void A_refused_widget_gives_the_golden_codes_in_the_golden_places(string file)
    {
        var c = Case(file);
        var derived = DeriveRequest.Derive(Widget(c), Roles(), file);
        Assert.Null(derived.Request);
        Assert.False(derived.Ok);
        var expected = c["refusal"]!.AsArray().Select(r => (r!["code"]!.GetValue<string>(), r["path"]!.GetValue<string>())).ToList();
        Assert.Equal(expected, derived.Diagnostics.Select(d => (d.Code, d.KeyPath!)).ToList());
        var messages = c["refusal"]!.AsArray().Select(r => r!["message"]?.GetValue<string>()).ToList();
        for (var i = 0; i < messages.Count; i++)
            if (messages[i] is { } m) Assert.Equal(m, derived.Diagnostics[i].Message);
        Assert.All(derived.Diagnostics, d => Assert.True(d.Line > 0 && d.Column > 0 && d.Path == file));
    }

    [Theory, MemberData(nameof(Good))]
    public void A_derived_request_and_the_hand_written_equivalent_have_the_same_request_hash(string file)
    {
        var c = Case(file);
        if (c["equivalent"] is null) return;
        var written = ViewsLoader.LoadWidgets(c["equivalent"]!.ToJsonString(), "equivalent.json", Parameters(c)).Widgets!.Single();
        var envelope = new JsonObject { ["spec_version"] = "0.3", ["kind"] = "query", ["query"] = ((JsonObject)written.Query!.Raw["semantic"]!["request"]!).DeepClone() };
        var derived = DeriveRequest.Derive(Widget(c), Roles(), file).Request!;
        Assert.Equal(QisJson.RequestHash(envelope), QisJson.RequestHash(derived));
        Assert.NotEmpty(QisJson.RequestHash(derived));
    }

    [Fact]
    public void Some_goldens_have_a_hand_written_equivalent()
    {
        Assert.True(Conformance.Cases("derive/cases.json")["good"]!.AsArray().Count(g => Case(g!["file"]!.GetValue<string>())["equivalent"] is not null) >= 1);
    }

    [Fact]
    public void The_goldens_cover_every_widget_type_with_a_request_and_every_encoding()
    {
        var widgets = Conformance.Cases("derive/cases.json")["good"]!.AsArray().Select(g => Case(g!["file"]!.GetValue<string>())["widgets"]!["w"]!).ToList();
        Assert.Equal(["chart", "matrix", "metric", "table"], widgets.Select(w => w["type"]!.GetValue<string>()).Distinct().Order());
        foreach (var channel in new[] { "x", "y", "color", "column", "tooltip" }) Assert.Contains(widgets, w => w["encodings"]?[channel] is not null);
        foreach (var key in new[] { "compare", "totals", "column_limit", "sort" }) Assert.Contains(widgets, w => w["encodings"]?[key] is not null);
        Assert.Equal(["alias_required", "DBD-106", "DBD-VIEW-040", "DBD-VIEW-041", "DBD-VIEW-042", "DBD-VIEW-048", "duplicate_measure", "unknown_member", "wrong_member_kind"],
            Conformance.Cases("derive/cases.json")["refused"]!.AsArray().SelectMany(r => Case(r!["file"]!.GetValue<string>())["refusal"]!.AsArray().Select(d => d!["code"]!.GetValue<string>())).Distinct().Order());
    }

    [Fact]
    public void The_golden_catalog_is_the_conformance_catalog_s_own_roles()
    {
        var node = Conformance.Cases("derive/cases.json")["catalogs"]!["sales_catalog"]!;
        Assert.Equal(Cat.Name, node["model"]!.GetValue<string>());
        foreach (var m in node["measures"]!.AsArray()) Assert.True(Cat.IsMeasure(m!.GetValue<string>()), m.ToString());
        foreach (var a in node["attributes"]!.AsArray()) Assert.True(Cat.IsAttribute(a!.GetValue<string>()), a.ToString());
        Assert.All(Cat.Doc.Measures.Keys, m => Assert.Contains(node["measures"]!.AsArray(), n => n!.GetValue<string>() == m));
    }

    [Fact]
    public void The_roles_of_the_goldens_refuse_in_the_engines_own_words()
    {
        string Say(string measure, string attribute) => Engine.ValidateRequest(JsonNode.Parse(
            "{\"spec_version\":\"0.3\",\"kind\":\"query\",\"query\":{\"measures\":[\"" + measure + "\"],\"attributes\":[\"" + attribute + "\"],\"limit\":1}}")!.AsObject())[0].Message;
        string Derived(string type, string encodings)
        {
            var yaml = $"w:\n  type: {type}\n  encodings: {encodings}\n  query: {{model: sales_catalog}}\n";
            return DeriveRequest.Derive(ViewsLoader.LoadWidgets(yaml, "w.yaml").Widgets!.Single(), EngineRoles(), "w.yaml").Diagnostics[0].Message;
        }
        Assert.Equal(Say("orders.net_revenue", "customers.regon"), Derived("matrix", "{rows: [customers.regon], values: [{field: orders.net_revenue}]}"));
        Assert.Equal(Say("customers.region", "customers.region"), Derived("metric", "{value: customers.region}"));
        Assert.Equal(Say("orders.net_revenue", "orders.net_revenue"), Derived("matrix", "{rows: [orders.net_revenue], values: [{field: orders.order_count}]}"));
    }

    // ---- the catalog check ----

    private const string Document = """
        schemaVersion: 1
        kind: dashboard
        title: Check
        parameters:
          - {name: region, type: string, for: customers.region, many: true, default: []}
        widgets:
          net:
            type: metric
            encodings: {value: orders.net_revenue}
            query:
              model: sales_catalog
              filters: [{attribute: customers.nowhere, operator: equals, values: [West]}]
          bars:
            type: chart
            mark: bar
            encodings:
              x: customers.region
              y: orders.net_revnue
            query: {model: sales_catalog}
          ordered:
            type: table
            encodings:
              columns: [{field: customers.region}, {field: orders.net_revenue}]
            query: {model: sales_catalog, order: [[orders.order_count, desc]]}
          written:
            type: metric
            encodings: {value: orders.net_revenue}
            query:
              mode: explicit
              semantic: {model: sales_catalog, request: {measures: [orders.net_revenue, orders.nothing], limit: 1}}
          elsewhere:
            type: metric
            encodings: {value: stock.on_hand}
            query: {model: warehouse}
        layouts:
          - name: desk
            type: reflow
            default: true
            grid: {columns: 12, rows: 8}
            tiles:
              - {widget: net, x: 0, y: 0, w: 3, h: 2}
              - {widget: bars, x: 3, y: 0, w: 3, h: 2}
              - {widget: ordered, x: 6, y: 0, w: 3, h: 2}
              - {widget: written, x: 9, y: 0, w: 3, h: 2}
              - {widget: elsewhere, x: 0, y: 2, w: 3, h: 2}
        """;

    [Fact]
    public void The_check_gives_the_engines_codes_at_the_documents_files_lines_and_paths()
    {
        var loaded = ViewsLoader.Load(Document, "views/check.yaml");
        Assert.True(loaded.Ok, Conformance.Describe(loaded.Diagnostics));
        var found = CatalogCheck.Check(loaded.Document!.Widgets, "views/check.yaml", Catalogs());
        var lines = Document.Split('\n');
        int LineOf(string text, int after = 0) => Array.FindIndex(lines, after, l => l.Contains(text)) + 1;

        // a filter on an attribute the model lacks: the filter, as the engine reports it
        var filter = Assert.Single(found, d => d.KeyPath == "widgets.net.query.filters[0].attribute");
        Assert.Equal("unknown_member", filter.Code);
        Assert.Equal("views/check.yaml", filter.Path);
        // an encoded field the model lacks: the field itself (its own line and column)
        var field = Assert.Single(found, d => d.KeyPath == "widgets.bars.encodings.y");
        Assert.Equal(("unknown_member", LineOf("y: orders.net_revnue")), (field.Code, field.Line));
        Assert.Equal("orders.net_revnue is not a catalog member.", field.Message);
        Assert.True(field.Column > 1);
        // an order on a member the query does not return: the order entry the person wrote
        Assert.Equal("order_member_missing", Assert.Single(found, d => d.KeyPath == "widgets.ordered.query.order[0]").Code);
        // a written request is checked as it is
        Assert.Equal("unknown_member", Assert.Single(found, d => d.KeyPath == "widgets.written.query.semantic.request.measures[1]").Code);
        // a model the document does not depend on
        var model = Assert.Single(found, d => d.Code == ViewsCodes.ModelNotBound);
        Assert.Equal("widgets.elsewhere.query.model", model.KeyPath);
        Assert.Equal(5, found.Count);
    }

    [Fact]
    public void A_derived_widget_with_a_wrong_field_is_refused_by_the_derivation_and_not_sent_twice()
    {
        var yaml = "m:\n  type: metric\n  encodings: {value: customers.region}\n  query: {model: sales_catalog}\n";
        var widgets = ViewsLoader.LoadWidgets(yaml, "w.yaml").Widgets!;
        var found = CatalogCheck.Check(widgets, "w.yaml", Catalogs());
        var one = Assert.Single(found);
        Assert.Equal(("wrong_member_kind", "widgets.m.encodings.value", 3), (one.Code, one.KeyPath, one.Line));
    }

    [Fact]
    public void A_text_widget_and_the_other_explicit_forms_are_left_to_the_engine_when_they_run()
    {
        var widgets = ViewsLoader.LoadWidgets(Conformance.Read("widgets/good/explicit-queries.json"), "e.json", ["quarter", "top"]).Widgets!;
        var found = CatalogCheck.Check(widgets, "e.json", Catalogs().ToDictionary(e => "sales", e => e.Value));
        // the semantic request of the first widget is checked (it names measures that exist in no model here, so it has diagnostics); SQL, saved, store and ref are not looked into
        Assert.All(found, d => Assert.StartsWith("widgets.semantic.", d.KeyPath));
    }

    [Fact]
    public void An_operator_the_engine_does_not_have_is_found_by_the_check_at_the_filter_the_person_wrote()
    {
        var yaml = "m:\n  type: metric\n  encodings: {value: orders.net_revenue}\n  query:\n    model: sales_catalog\n    filters: [{attribute: customers.region, operator: in, values: [West]}]\n";
        var found = CatalogCheck.Check(ViewsLoader.LoadWidgets(yaml, "w.yaml").Widgets!, "w.yaml", Catalogs());
        Assert.Equal("widgets.m.query.filters[0]", Assert.Single(found).KeyPath);
        Assert.Equal("schema_invalid", found[0].Code);
    }

    // ---- the sample documents ----

    private static CatalogRoles RetailRoles() => new("retail_sales", ["sales.net_amount", "sales.margin_rate", "sales.units"], ["customers.region", "products.category", "products.product", "date.year_month"]);

    [Theory]
    [InlineData("samples/dashboards/retail-sales-today.yaml", 4)]
    [InlineData("samples/reports/retail-sales-monthly.yaml", 5)]
    public void The_sample_documents_derive_a_request_for_each_widget_that_asks_for_rows(string file, int derivable)
    {
        var loaded = ViewsLoader.Load(Conformance.Read(file), file);
        Assert.True(loaded.Ok, Conformance.Describe(loaded.Diagnostics));
        var asking = loaded.Document!.Widgets.Where(w => w.Query is not null).ToList();
        Assert.Equal(derivable, asking.Count);
        foreach (var w in asking)
        {
            var derived = DeriveRequest.Derive(w, RetailRoles(), file);
            Assert.True(derived.Ok, Conformance.Describe(derived.Diagnostics));
            Assert.Equal("query", derived.Request!["kind"]!.GetValue<string>());
        }
        var first = DeriveRequest.Derive(asking[0], RetailRoles(), file).Request!["query"]!;
        Assert.Equal("""{"measures":["sales.net_amount"],"filters":[{"attribute":"customers.region","operator":"equals","values":{"param":"region"}}],"limit":1}""", first.ToJsonString());
    }
}
