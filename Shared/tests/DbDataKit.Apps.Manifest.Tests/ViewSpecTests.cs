using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps.Manifest.Tests;

/// <summary>The declarative view files of an app, <c>view-spec/1</c> (plan 22a GP06): each kind loads, each bad part is refused with file, line and code, the checks against the manifest and the models, and the schema file.</summary>
public sealed class ViewSpecTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "view-spec-" + Guid.NewGuid().ToString("N")[..8]);

    public ViewSpecTests() => Directory.CreateDirectory(Path.Combine(folder, "views"));

    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private void Write(string relative, string text) { var p = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }

    private const string Grid = """
        schemaVersion: 1
        kind: grid
        title: Territories
        model: territories
        entity: territory
        params:
          - {name: region, type: string}
        columns:
          - name
          - {attribute: region, label: Sales region, sortable: false}
        filters: [region]
        sort:
          - {attribute: name, direction: desc}
        pageSize: 25
        links:
          - {label: Open, to: views/detail.yaml, row: true, with: {id: $id}}
          - {label: Overview, to: views/dashboard.yaml}
        """;

    private const string Form = """
        schemaVersion: 1
        kind: form
        title: Edit territory
        model: territories
        entity: territory
        mode: edit
        key: id
        params:
          - {name: id, type: int, required: true}
        fields:
          - name
          - {attribute: region, required: true, help: Where it sells}
          - {attribute: code, readonly: true}
        submit: Save
        links:
          - {label: Back, to: views/detail.yaml, with: {id: $params.id}}
        """;

    private const string Detail = """
        schemaVersion: 1
        kind: detail
        title: Territory
        model: territories
        entity: territory
        key: id
        params:
          - {name: id, type: int, required: true}
        sections:
          - title: Basics
            attributes: [name, {attribute: region, label: Sales region}]
          - title: Codes
            attributes: [code]
        links:
          - {label: Edit, to: views/form.yaml, with: {id: $params.id}}
        """;

    private const string Dashboard = """
        schemaVersion: 1
        kind: dashboard
        title: Overview
        model: territories
        widgets:
          - {kind: metric, title: Territories, entity: territory, measure: count}
          - {kind: bar, title: By region, entity: territory, measure: {sum: quota}, by: region, limit: 5}
          - {kind: table, title: Biggest, entity: territory, columns: [name, quota]}
        links:
          - {label: All, to: views/grid.yaml}
        """;

    private const string Manifest = "schemaVersion: 1\nname: sales\nversion: 1.0.0\nviews: [views/grid.yaml, views/form.yaml, views/detail.yaml, views/dashboard.yaml]\nmodels:\n  - model: territories\n    entities: {territory: [read, write]}\n";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>> Attributes =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>
        {
            ["territories"] = new Dictionary<string, IReadOnlySet<string>> { ["territory"] = new HashSet<string> { "id", "name", "region", "code", "quota" } },
        };

    private void WriteApp(string? manifest = null)
    {
        Write("app.yaml", manifest ?? Manifest);
        Write("views/grid.yaml", Grid); Write("views/form.yaml", Form); Write("views/detail.yaml", Detail); Write("views/dashboard.yaml", Dashboard);
    }

    [Fact]
    public void A_grid_loads_with_its_columns_filters_sort_and_links()
    {
        var load = ViewSpecLoader.Load(Grid, "views/grid.yaml");
        Assert.True(load.Ok, string.Join('\n', load.Diagnostics));
        var grid = Assert.IsType<GridView>(load.Spec);
        Assert.Equal(("Territories", "territories", "territory", 25), (grid.Title, grid.Model, grid.Entity, grid.PageSize));
        Assert.Equal(["name", "region"], grid.Columns.Select(c => c.Attribute));
        Assert.Equal(("Sales region", false), (grid.Columns[1].Label, grid.Columns[1].Sortable));
        Assert.True(grid.Columns[0].Sortable);
        Assert.Equal(["region"], grid.Filters.Select(f => f.Name));
        Assert.Equal(ViewSortDirection.Desc, Assert.Single(grid.Sort).Direction);
        Assert.Equal(ViewParamType.String, Assert.Single(grid.Params).Type);
        Assert.True(grid.Links[0].PerRow);
        Assert.Equal((ViewValueKind.Attribute, "id"), (grid.Links[0].With["id"].Kind, grid.Links[0].With["id"].Text));
        Assert.False(grid.Links[1].PerRow);
    }

    [Fact]
    public void A_form_loads_with_its_mode_key_and_fields()
    {
        var form = Assert.IsType<FormView>(Load(Form));
        Assert.Equal((ViewFormMode.Edit, "id", "Save"), (form.Mode, form.Key, form.Submit));
        Assert.Equal(["name", "region", "code"], form.Fields.Select(f => f.Attribute));
        Assert.Equal((true, "Where it sells"), (form.Fields[1].Required, form.Fields[1].Help));
        Assert.True(form.Fields[2].ReadOnly);
        Assert.Equal(ViewValueKind.Param, form.Links[0].With["id"].Kind);
        Assert.Equal(ViewParamType.Int, form.Params[0].Type);

        var create = Assert.IsType<FormView>(Load("schemaVersion: 1\nkind: form\ntitle: New\nmodel: territories\nentity: territory\nfields: [name]\n"));
        Assert.Equal((ViewFormMode.Create, null), (create.Mode, create.Key));
    }

    [Fact]
    public void A_detail_page_loads_with_its_key_and_sections()
    {
        var detail = Assert.IsType<DetailView>(Load(Detail));
        Assert.Equal("id", detail.Key);
        Assert.Equal(["Basics", "Codes"], detail.Sections.Select(s => s.Title));
        Assert.Equal(["name", "region"], detail.Sections[0].Attributes.Select(a => a.Name));
        Assert.Equal("Sales region", detail.Sections[0].Attributes[1].Label);
    }

    [Fact]
    public void A_dashboard_loads_with_metric_bar_and_table_widgets()
    {
        var board = Assert.IsType<DashboardView>(Load(Dashboard));
        Assert.Equal([ViewWidgetKind.Metric, ViewWidgetKind.Bar, ViewWidgetKind.Table], board.Widgets.Select(w => w.Kind));
        Assert.Equal(ViewAggregate.Count, board.Widgets[0].Aggregate);
        Assert.Equal((ViewAggregate.Sum, "quota", "region", 5), (board.Widgets[1].Aggregate, board.Widgets[1].Measure!.Name, board.Widgets[1].By!.Name, board.Widgets[1].Limit));
        Assert.Equal(["name", "quota"], board.Widgets[2].Columns.Select(c => c.Name));
        Assert.Equal(ViewSpecLoader.DefaultWidgetLimit, board.Widgets[0].Limit);
    }

    private static ViewSpec Load(string yaml)
    {
        var load = ViewSpecLoader.Load(yaml, "views/x.yaml");
        Assert.True(load.Ok, string.Join('\n', load.Diagnostics));
        return load.Spec!;
    }

    private const string Head = "schemaVersion: 1\nkind: grid\ntitle: T\nmodel: territories\nentity: territory\n";

    [Theory]
    // the file itself
    [InlineData("schemaVersion: 1\ntitle: T\n", SharedCodes.MissingKey, 1, "`kind`")]
    [InlineData("schemaVersion: 1\nkind: carousel\ntitle: T\n", ViewSpecLoader.BadViewSpec, 2, "not a kind of view")]
    [InlineData(Head + "columns: [name]\ncolour: red\n", SharedCodes.UnknownKey, 7, "Unknown key `colour`")]
    [InlineData("schemaVersion: 1\nkind: grid\nmodel: territories\nentity: territory\ncolumns: [name]\n", SharedCodes.MissingKey, 1, "`title`")]
    [InlineData("schemaVersion: 1\nkind: grid\ntitle: T\nmodel: Territories!\nentity: territory\ncolumns: [name]\n", ViewSpecLoader.BadViewSpec, 4, "not a model name")]
    [InlineData("schemaVersion: 1\nkind: grid\ntitle: T\nmodel: territories\nentity: terri-tory\ncolumns: [name]\n", ViewSpecLoader.BadViewSpec, 5, "not an entity name")]
    [InlineData("- a\n- b\n", ViewSpecLoader.BadViewSpec, 1, "is a mapping")]
    // parameters
    [InlineData(Head + "columns: [name]\nparams:\n  - {name: id, type: guid}\n", ViewSpecLoader.BadViewSpec, 8, "not a value of `type`")]
    [InlineData(Head + "columns: [name]\nparams:\n  - {name: id, type: int, default: abc}\n", ViewSpecLoader.BadViewSpec, 8, "not a int")]
    [InlineData(Head + "columns: [name]\nparams:\n  - {name: id, required: true, default: x}\n", ViewSpecLoader.BadViewSpec, 8, "no `default`")]
    [InlineData(Head + "columns: [name]\nparams:\n  - {name: id}\n  - {name: id}\n", ViewSpecLoader.BadViewSpec, 9, "declared twice")]
    // grid
    [InlineData(Head, ViewSpecLoader.BadViewSpec, 1, "`columns`")]
    [InlineData(Head + "columns: [name, name]\n", ViewSpecLoader.BadViewSpec, 6, "a column twice")]
    [InlineData(Head + "columns: [\"na me\"]\n", ViewSpecLoader.BadViewSpec, 6, "name of an attribute")]
    [InlineData(Head + "columns: [name]\npageSize: 5000\n", ViewSpecLoader.BadViewSpec, 7, "from 1 to 500")]
    [InlineData(Head + "columns: [name]\nsort:\n  - {attribute: name, direction: up}\n", ViewSpecLoader.BadViewSpec, 8, "not a value of `direction`")]
    [InlineData(Head + "columns:\n  - {attribute: name, sortable: maybe}\n", ViewSpecLoader.BadViewSpec, 7, "`true` or `false`")]
    // form
    [InlineData("schemaVersion: 1\nkind: form\ntitle: T\nmodel: m\nentity: e\nmode: edit\nfields: [a]\n", SharedCodes.MissingKey, 1, "An edit form names")]
    [InlineData("schemaVersion: 1\nkind: form\ntitle: T\nmodel: m\nentity: e\nmode: edit\nkey: id\nfields: [a]\n", ViewSpecLoader.BadViewSpec, 7, "not a parameter")]
    [InlineData("schemaVersion: 1\nkind: form\ntitle: T\nmodel: m\nentity: e\nmode: edit\nkey: id\nparams: [{name: id}]\nfields: [a]\n", ViewSpecLoader.BadViewSpec, 7, "required: true")]
    [InlineData("schemaVersion: 1\nkind: form\ntitle: T\nmodel: m\nentity: e\nkey: id\nfields: [a]\n", ViewSpecLoader.BadViewSpec, 6, "only an edit form")]
    [InlineData("schemaVersion: 1\nkind: form\ntitle: T\nmodel: m\nentity: e\nfields: []\n", ViewSpecLoader.BadViewSpec, 6, "`fields`")]
    // detail
    [InlineData("schemaVersion: 1\nkind: detail\ntitle: T\nmodel: m\nentity: e\nkey: id\nparams: [{name: id, required: true}]\nsections:\n  - title: A\n    attributes: []\n", ViewSpecLoader.BadViewSpec, 10, "`attributes`")]
    [InlineData("schemaVersion: 1\nkind: detail\ntitle: T\nmodel: m\nentity: e\nkey: id\nparams: [{name: id, required: true}]\nsections:\n  - attributes: [a]\n", SharedCodes.MissingKey, 9, "`title`")]
    // dashboard
    [InlineData("schemaVersion: 1\nkind: dashboard\ntitle: T\nmodel: m\nwidgets:\n  - {kind: pie, title: P, entity: e, measure: count}\n", ViewSpecLoader.BadViewSpec, 6, "not a value of `kind`")]
    [InlineData("schemaVersion: 1\nkind: dashboard\ntitle: T\nmodel: m\nwidgets:\n  - {kind: metric, title: P, entity: e}\n", SharedCodes.MissingKey, 6, "`measure`")]
    [InlineData("schemaVersion: 1\nkind: dashboard\ntitle: T\nmodel: m\nwidgets:\n  - {kind: bar, title: P, entity: e, measure: count}\n", SharedCodes.MissingKey, 6, "`by`")]
    [InlineData("schemaVersion: 1\nkind: dashboard\ntitle: T\nmodel: m\nwidgets:\n  - {kind: metric, title: P, entity: e, measure: {median: x}}\n", ViewSpecLoader.BadViewSpec, 6, "aggregate")]
    [InlineData("schemaVersion: 1\nkind: dashboard\ntitle: T\nmodel: m\nwidgets:\n  - {kind: table, title: P, entity: e, columns: [a], by: a}\n", ViewSpecLoader.BadViewSpec, 6, "`by` is for a bar")]
    // links
    [InlineData(Head + "columns: [name]\nlinks:\n  - {label: x, to: ../secret.yaml}\n", ViewSpecLoader.BadViewSpec, 8, "not a view file inside the app")]
    [InlineData(Head + "columns: [name]\nlinks:\n  - {label: x, to: views/a.json}\n", ViewSpecLoader.BadViewSpec, 8, "not a view file inside the app")]
    [InlineData(Head + "columns: [name]\nlinks:\n  - {label: x, to: views/a.yaml, with: {id: $id}}\n", ViewSpecLoader.BadViewSpec, 8, "needs a row")]
    [InlineData(Head + "columns: [name]\nlinks:\n  - {label: x, to: views/a.yaml, with: {id: $params.id}}\n", ViewSpecLoader.BadViewSpec, 8, "not a parameter of this view")]
    [InlineData(Head + "columns: [name]\nlinks:\n  - {label: x, to: views/a.yaml, row: maybe}\n", ViewSpecLoader.BadViewSpec, 8, "`true` or `false`")]
    [InlineData("schemaVersion: 1\nkind: detail\ntitle: T\nmodel: m\nentity: e\nkey: id\nparams: [{name: id, required: true}]\nsections: [{title: A, attributes: [a]}]\nlinks:\n  - {label: x, to: views/a.yaml, row: true}\n", ViewSpecLoader.BadViewSpec, 10, "only a grid has rows")]
    public void Each_bad_part_is_a_diagnostic_with_file_line_and_code(string yaml, string code, int line, string says)
    {
        var load = ViewSpecLoader.Load(yaml, "views/x.yaml");
        Assert.False(load.Ok);
        Assert.Null(load.Spec);
        var found = load.Diagnostics.Where(d => d.Code == code && d.Message.Contains(says)).ToList();
        var one = Assert.Single(found, d => d.Line == line);
        Assert.Equal("views/x.yaml", one.Path);
    }

    [Fact]
    public void A_newer_format_is_refused_naming_the_kind_the_file_and_both_numbers()
    {
        var load = ViewSpecLoader.Load("schemaVersion: 2\nkind: grid\ntitle: T\n", "views/grid.yaml", "sales");
        var found = Assert.Single(load.Diagnostics);
        Assert.Equal((ViewSpecLoader.ViewSpecFormat, "views/grid.yaml", 1), (found.Code, found.Path, found.Line));
        Assert.Contains("the app 'sales' holds the view-spec 'views/grid.yaml', which is in format 2, and this program reads format 1 of view-spec", found.Message);
        Assert.Contains("Upgrade this program", found.Message);

        Assert.Contains("the view-spec 'views/grid.yaml' is in format 2", ViewSpecLoader.Load("schemaVersion: 2\n", "views/grid.yaml").Diagnostics.Single().Message);
        Assert.Contains("`schemaVersion` is missing", ViewSpecLoader.Load("kind: grid\n", "views/grid.yaml").Diagnostics.Single().Message);
        Assert.Equal(ViewSpecLoader.ViewSpecFormat, ViewSpecLoader.Load("schemaVersion: one\n", "views/grid.yaml").Diagnostics.Single().Code);
        Assert.Equal(ViewSpecLoader.ViewSpecFormat, ViewSpecLoader.Load("schemaVersion: 0\n", "views/grid.yaml").Diagnostics.Single().Code);
    }

    [Fact]
    public void An_empty_or_damaged_file_is_refused_and_never_throws()
    {
        Assert.Contains(ViewSpecLoader.Load("", "views/x.yaml").Diagnostics, d => d.Message.Contains("empty"));
        foreach (var damaged in new[] { "[", "{", "schemaVersion: [", "- - -", "kind: {a: b}\nschemaVersion: 1\n", "schemaVersion: 1\nkind: grid\ntitle: [x]\n" })
            Assert.False(ViewSpecLoader.Load(damaged, "views/x.yaml").Ok, damaged);
    }

    [Fact]
    public void App_validate_over_a_folder_of_the_four_kinds_passes_with_the_models()
    {
        WriteApp();
        var result = AppValidator.Validate(folder, modelEntities: new Dictionary<string, IReadOnlySet<string>> { ["territories"] = new HashSet<string> { "territory" } }, modelAttributes: Attributes);
        Assert.True(result.Ok, result.Render());
        Assert.Equal("The app is valid.\n", result.Render().Replace("\r\n", "\n"));
    }

    [Fact]
    public void App_validate_refuses_a_spec_that_names_an_attribute_the_model_lacks()
    {
        WriteApp();
        Write("views/grid.yaml", Grid.Replace("filters: [region]", "filters: [regoin]"));
        var result = AppValidator.Validate(folder, modelAttributes: Attributes);
        Assert.False(result.Ok);
        var found = Assert.Single(result.Diagnostics, d => d.Code == ViewSpecLoader.UnknownViewAttribute);
        Assert.Equal(("views/grid.yaml", 11), (found.Path, found.Line));
        Assert.Contains("`territory.regoin`, which the model `territories` does not have", found.Message);
        Assert.Contains("views/grid.yaml:11:", result.Render());

        // without the Store's schema the attributes cannot be known; the rest is still checked
        Assert.True(AppValidator.Validate(folder).Ok);
    }

    [Theory]
    [InlineData("views/form.yaml", "- {attribute: code, readonly: true}", "- {attribute: nope}", 13)]
    [InlineData("views/detail.yaml", "attributes: [code]", "attributes: [colour]", 13)]
    [InlineData("views/dashboard.yaml", "columns: [name, quota]", "columns: [name, size]", 8)]
    [InlineData("views/dashboard.yaml", "by: region", "by: area", 7)]
    [InlineData("views/dashboard.yaml", "measure: {sum: quota}", "measure: {sum: amount}", 7)]
    [InlineData("views/grid.yaml", "sort:\n  - {attribute: name,", "sort:\n  - {attribute: label,", 13)]
    [InlineData("views/grid.yaml", "with: {id: $id}", "with: {id: $key}", 16)]
    public void An_unknown_attribute_is_found_in_every_part_that_names_one(string file, string from, string to, int line)
    {
        WriteApp();
        Write(file, File.ReadAllText(Path.Combine(folder, file)).Replace(from, to));
        var result = AppValidator.Validate(folder, modelAttributes: Attributes);
        var found = Assert.Single(result.Diagnostics, d => d.Code == ViewSpecLoader.UnknownViewAttribute);
        Assert.Equal(file, found.Path);
        Assert.Equal(line, found.Line);
    }

    [Fact]
    public void A_view_must_use_a_model_and_entity_the_manifest_lists_and_may_write_only_what_it_asks_to_write()
    {
        WriteApp();
        Write("views/dashboard.yaml", Dashboard.Replace("model: territories", "model: elsewhere"));
        var other = Assert.Single(AppValidator.Validate(folder).Diagnostics, d => d.Code == ViewSpecLoader.UnknownViewModel);
        Assert.Equal(("views/dashboard.yaml", 4), (other.Path, other.Line));
        Assert.Contains("`elsewhere`, which the manifest does not list under `models`", other.Message);

        WriteApp();
        Write("views/grid.yaml", Grid.Replace("entity: territory", "entity: region"));
        var entity = Assert.Single(AppValidator.Validate(folder).Diagnostics, d => d.Code == ViewSpecLoader.UnknownViewModel);
        Assert.Equal(("views/grid.yaml", 5), (entity.Path, entity.Line));
        Assert.Contains("`territories.region`", entity.Message);

        WriteApp(Manifest.Replace("territory: [read, write]", "territory: [read]"));
        var write = Assert.Single(AppValidator.Validate(folder).Diagnostics, d => d.Code == ViewSpecLoader.UnknownViewModel);
        Assert.Equal("views/form.yaml", write.Path);
        Assert.Contains("writes `territories.territory`, but the manifest asks only to read it", write.Message);
    }

    [Fact]
    public void A_link_must_lead_to_a_view_of_the_app_and_give_its_required_parameters()
    {
        WriteApp();
        Write("views/grid.yaml", Grid.Replace("to: views/detail.yaml, row: true, with: {id: $id}", "to: views/ghost.yaml, row: true"));
        var ghost = Assert.Single(AppValidator.Validate(folder).Diagnostics, d => d.Code == ViewSpecLoader.BadViewLink);
        Assert.Equal(("views/grid.yaml", 16), (ghost.Path, ghost.Line));
        Assert.Contains("`views/ghost.yaml`, which is not one of the app's `views`", ghost.Message);

        WriteApp();
        Write("views/grid.yaml", Grid.Replace("to: views/detail.yaml, row: true, with: {id: $id}", "to: views/detail.yaml, row: true"));
        var missing = Assert.Single(AppValidator.Validate(folder).Diagnostics, d => d.Code == ViewSpecLoader.BadViewLink);
        Assert.Contains("does not give the required parameter `id`", missing.Message);

        WriteApp();
        Write("views/grid.yaml", Grid.Replace("with: {id: $id}", "with: {id: $id, colour: red}"));
        var extra = Assert.Single(AppValidator.Validate(folder).Diagnostics, d => d.Code == ViewSpecLoader.BadViewLink);
        Assert.Contains("gives `colour`, which `views/detail.yaml` has no parameter for", extra.Message);

        WriteApp();
        Write("views/grid.yaml", Grid.Replace("with: {id: $id}", "with: {id: abc}"));
        var type = Assert.Single(AppValidator.Validate(folder).Diagnostics, d => d.Code == ViewSpecLoader.BadViewLink);
        Assert.Contains("`abc` is not a int", type.Message);
    }

    [Fact]
    public void A_damaged_spec_is_reported_under_its_own_file_and_a_missing_one_only_once()
    {
        WriteApp();
        Write("views/form.yaml", "schemaVersion: 1\nkind: form\ntitle: T\nmodel: territories\nentity: territory\nfields: [name]\nbogus: 1\n");
        File.Delete(Path.Combine(folder, "views", "dashboard.yaml"));
        var text = AppValidator.Validate(folder).Render();
        Assert.Contains("views/form.yaml:7:1", text);
        Assert.Contains("Unknown key `bogus`", text);
        Assert.Single(AppValidator.Validate(folder).Diagnostics, d => d.Message.Contains("`views/dashboard.yaml`, which is not in the app's folder"));
    }

    [Fact]
    public void The_schema_file_agrees_with_the_loader()
    {
        var schema = JsonNode.Parse(ViewSpecLoader.Schema())!.AsObject();
        static string[] KeysOf(JsonNode node) => [.. node["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];
        static string[] Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];
        static string[] Enum(JsonNode node) => [.. node["enum"]!.AsArray().Select(v => v!.GetValue<string>())];

        var kinds = schema["oneOf"]!.AsArray();
        Assert.Equal(ViewSpecLoader.Choices["kind"].Length, kinds.Count);
        foreach (var kind in ViewSpecLoader.Choices["kind"])
        {
            var one = kinds.Single(k => k!["properties"]!["kind"]!["const"]!.GetValue<string>() == kind)!;
            Assert.Equal(Sorted(ViewSpecLoader.Keys[kind]), KeysOf(one));
            Assert.False(one["additionalProperties"]!.GetValue<bool>());
            foreach (var required in one["required"]!.AsArray()) Assert.Contains(required!.GetValue<string>(), ViewSpecLoader.Keys[kind]);
        }
        var defs = schema["$defs"]!.AsObject();
        foreach (var part in new[] { "param", "link", "column", "field", "sort", "section", "attribute", "widget" })
        {
            Assert.Equal(Sorted(ViewSpecLoader.Keys[part]), KeysOf(defs[part]!));
            Assert.False(defs[part]!["additionalProperties"]!.GetValue<bool>(), part);
        }
        Assert.Equal(ViewSpecLoader.Choices["paramType"], Enum(defs["param"]!["properties"]!["type"]!));
        Assert.Equal(ViewSpecLoader.Choices["direction"], Enum(defs["sort"]!["properties"]!["direction"]!));
        Assert.Equal(ViewSpecLoader.Choices["widgetKind"], Enum(defs["widget"]!["properties"]!["kind"]!));
        Assert.Equal(ViewSpecLoader.Choices["aggregate"], Enum(defs["widget"]!["properties"]!["measure"]!["oneOf"]![1]!["propertyNames"]!));
        var form = kinds.Single(k => k!["properties"]!["kind"]!["const"]!.GetValue<string>() == "form")!;
        Assert.Equal(ViewSpecLoader.Choices["mode"], Enum(form["properties"]!["mode"]!));
        Assert.Equal(ViewSpecLoader.Version, schema["oneOf"]![0]!["properties"]!["schemaVersion"]!["const"]!.GetValue<int>());

        // the limits the loader enforces are the schema's
        var grid = kinds.Single(k => k!["properties"]!["kind"]!["const"]!.GetValue<string>() == "grid")!["properties"]!;
        Assert.Equal(ViewSpecLoader.MaximumColumns, grid["columns"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(ViewSpecLoader.MaximumPageSize, grid["pageSize"]!["maximum"]!.GetValue<int>());
        Assert.Equal(ViewSpecLoader.DefaultPageSize, grid["pageSize"]!["default"]!.GetValue<int>());
        Assert.Equal(ViewSpecLoader.MaximumLinks, grid["links"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(ViewSpecLoader.MaximumParams, grid["params"]!["maxItems"]!.GetValue<int>());
    }
}
