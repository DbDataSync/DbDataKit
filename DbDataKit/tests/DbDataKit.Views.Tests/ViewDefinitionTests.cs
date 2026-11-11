using System.Security.Cryptography;
using System.Text;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Views.Tests;

/// <summary>
/// The kinds <c>dashboard</c> and <c>report</c> in plan 30's registry (plan 32 DBR10). The validator, the format rule, the dependency check, the listing, promotion and rollback run on the state database of
/// every engine (SQLite, and SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> / <c>DBDATAKIT_TEST_POSTGRES</c> name a server); the validator's refusals need no database and run once.
/// </summary>
public sealed class ViewDefinitionTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-08T12:00:00Z");

    private const string Model = "sales_catalog";
    private const string Saved = "\nqueries:\n  revenue_by_region:\n    sql: SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY ALL LIMIT 100\n";

    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4];
    private static readonly string PngSha = Convert.ToHexStringLower(SHA256.HashData(Png));

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private static string CatalogText(bool withSaved = true) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog", "catalog.yaml")) + (withSaved ? Saved : "");

    /// <summary>The content of a published model version, as the Proxy stores it (<c>{"catalog": "..."}</c>).</summary>
    private static string ModelContent(bool withSaved = true) => new System.Text.Json.Nodes.JsonObject { ["catalog"] = CatalogText(withSaved) }.ToJsonString();

    private static string Dashboard(string measure = "orders.net_revenue", string model = Model, bool image = false, string? saved = null, string? control = "customers.region", int schema = 1)
    {
        var widgets = new StringBuilder();
        var tiles = new StringBuilder();
        var row = 0;
        void Add(string id, string body)
        {
            widgets.Append($"  {id}:\n{body}\n");
            tiles.Append($"      - {{widget: {id}, x: 0, y: {row}, w: 12, h: 2}}\n");
            row += 2;
        }
        Add("revenue", $"    type: metric\n    label: Revenue\n    encodings: {{value: {measure}}}\n    query: {{model: {model}}}");
        if (control is not null) Add("regions", $"    type: control\n    parameter: region\n    control: multiselect\n    values: {{attribute: {control}}}");
        if (image) Add("logo", $"    type: image\n    hash: sha256:{PngSha}\n    media_type: image/png\n    alt: Logo");
        if (saved is not null) Add("by_region", $"    type: table\n    encodings: {{columns: [{{field: region}}]}}\n    query: {{mode: explicit, saved: {saved}, outputs: [region]}}");
        return $"schemaVersion: {schema}\nkind: dashboard\ntitle: Sales\nparameters:\n  - {{name: region, type: string, for: customers.region, many: true, default: []}}\nwidgets:\n{widgets}layouts:\n  - name: desk\n    type: reflow\n    default: true\n    grid: {{columns: 12, rows: {row}}}\n    tiles:\n{tiles}";
    }

    private static string Report(string measure = "orders.net_revenue", string model = Model) =>
        $"schemaVersion: 1\nkind: report\ntitle: Summary\nwidgets:\n  revenue:\n    type: metric\n    label: Revenue\n    encodings: {{value: {measure}}}\n    query: {{model: {model}}}\nlayouts:\n  - name: print\n    type: flow\n    default: true\n    sections:\n      - title: Summary\n        blocks:\n          - {{widget: revenue}}\n";

    private static string[] Pin(int version = 1) => [$"semantic-model:{Model}@{version}"];

    // ---- no database: the validator and its helpers

    private static ViewPublishingSources Sources(bool withSaved = true, byte[]? file = null) =>
        new((name, version) => name == Model && version is 1 or 2 ? ModelContent(withSaved) : null, file is null ? null : _ => file);

    private static DefinitionCandidate Candidate(string kind, string text, string[]? depends = null, params ArtifactRef[] files) =>
        new(kind, "sales", text, files, depends ?? Pin(), 1);

    private static string? Check(string kind, string text, string[]? depends = null, ViewPublishingSources? sources = null, params ArtifactRef[] files) =>
        ViewDefinitions.Validate(Candidate(kind, text, depends, files), sources ?? Sources());

    [Fact]
    public void A_good_dashboard_and_a_good_report_pass_the_validator()
    {
        var said = Check("dashboard", Dashboard(saved: "revenue_by_region"));
        Assert.True(said is null, said);
        said = Check("report", Report());
        Assert.True(said is null, said);
    }

    [Fact]
    public void The_registry_lists_both_kinds_with_the_one_format_and_serving_as_target()
    {
        var kinds = ViewDefinitions.Kind(Sources());
        Assert.Equal(["dashboard", "report"], kinds.All.Select(k => k.Name));
        Assert.All(kinds.All, k =>
        {
            Assert.Equal(new DefinitionFormats(ViewsFormat.Previous, ViewsFormat.Current), k.Formats);
            Assert.Equal(["serving"], k.TargetTypes);
            Assert.Equal(PublishMode.Serve, k.DefaultMode);
        });
        Assert.Equal("dashboard=1-1;report=1-1", kinds.FormatText);
    }

    [Fact]
    public void The_format_is_the_documents_schema_version_in_yaml_and_in_json()
    {
        Assert.Equal(1, ViewDefinitions.FormatOf(Dashboard()));
        Assert.Equal(2, ViewDefinitions.FormatOf(Dashboard(schema: 2)));
        Assert.Equal(7, ViewDefinitions.FormatOf("""{"schemaVersion": 7, "kind": "report"}"""));
        Assert.Null(ViewDefinitions.FormatOf("kind: report"));
        Assert.Null(ViewDefinitions.FormatOf("schemaVersion: \"2\""));
        Assert.Equal("dashboard/sales.json", ViewDefinitions.FileName("dashboard", "sales", " {\"a\":1}"));
        Assert.Equal("report/sales.yaml", ViewDefinitions.FileName("report", "sales", "a: 1"));
    }

    [Fact]
    public void The_dependencies_are_the_models_the_widgets_query_pinned_to_a_version()
    {
        Assert.Equal(["semantic-model:sales_catalog@3"], ViewDefinitions.DependsOn("dashboard", "sales", Dashboard(), m => m == Model ? 3 : null));
        Assert.Equal(["semantic-model:sales_catalog"], ViewDefinitions.DependsOn("report", "sales", Report(), _ => null));
        Assert.Empty(ViewDefinitions.DependsOn("report", "sales", "not a document: [", _ => 1));
        Assert.Equal(1, ViewDefinitions.PinnedModels(["semantic-model:Sales_Catalog@1", "data-source:x@2", "semantic-model:other"])["sales_catalog"]);
        Assert.Equal(0, ViewDefinitions.PinnedModels(["semantic-model:other"])["other"]);
    }

    [Fact]
    public void A_document_that_does_not_load_is_refused_with_file_line_and_code()
    {
        var said = Check("dashboard", Dashboard().Replace("kind: dashboard", "kind: report"))!;
        Assert.Contains("dashboard/sales.yaml", said);
        Assert.Contains("DBD-VIEW-001", said);
        Assert.Contains("DBD-VIEW-", Check("report", Dashboard())!);
    }

    [Fact]
    public void A_model_the_document_queries_and_does_not_depend_on_is_refused_naming_it()
    {
        var said = Check("dashboard", Dashboard(), [])!;
        Assert.Contains("DBD-VIEW-060", said);
        Assert.Contains("`sales_catalog`", said);
        Assert.Contains("semantic-model:sales_catalog@<version>", said);
        Assert.Contains("`other`", Check("dashboard", Dashboard(), ["semantic-model:other@1"])!);
    }

    [Fact]
    public void A_model_with_no_version_or_a_version_that_does_not_exist_is_refused_naming_it()
    {
        Assert.Contains("`sales_catalog` with no version", Check("dashboard", Dashboard(), ["semantic-model:sales_catalog"])!);
        var said = Check("report", Report(), Pin(9))!;
        Assert.Contains("DBD-VIEW-060", said);
        Assert.Contains("`sales_catalog` has no version 9", said);
    }

    [Fact]
    public void A_model_whose_catalog_does_not_load_is_refused_naming_it()
    {
        var said = Check("report", Report(), Pin(), new ViewPublishingSources((_, _) => """{"catalog": "name: broken"}"""))!;
        Assert.Contains("`sales_catalog` version 1 does not load", said);
    }

    [Fact]
    public void An_encoded_field_the_model_version_lacks_is_refused_with_the_engines_code_and_the_fields_place()
    {
        foreach (var (kind, text) in new[] { ("dashboard", Dashboard("orders.nope")), ("report", Report("orders.nope")) })
        {
            var said = Check(kind, text)!;
            Assert.Contains("unknown_member", said);
            Assert.Contains("orders.nope", said);
            Assert.Contains($"{kind}/sales.yaml:", said);
            Assert.Contains("widgets.revenue.encodings", said);
        }
        // a dimension in a measure well is the engine's other role error
        Assert.Contains("wrong_member_kind", Check("report", Report("customers.region"))!);
    }

    [Fact]
    public void A_saved_query_no_model_has_and_a_control_attribute_no_model_has_are_refused()
    {
        var said = Check("dashboard", Dashboard(saved: "revenue_by_region"), sources: Sources(withSaved: false))!;
        Assert.Contains("DBD-VIEW-061", said);
        Assert.Contains("`revenue_by_region`", said);
        Assert.Contains("widgets.by_region.query.saved", said);

        var control = Check("dashboard", Dashboard(control: "customers.nope"))!;
        Assert.Contains("DBD-VIEW-062", control);
        Assert.Contains("`customers.nope`", control);
    }

    [Fact]
    public void An_image_must_be_a_file_of_the_version_a_raster_by_its_first_bytes_of_at_most_a_mebibyte()
    {
        var file = new ArtifactRef("logo.png", PngSha, Png.Length, "image/png");
        Assert.Null(Check("dashboard", Dashboard(image: true), null, Sources(file: Png), file));

        Assert.Contains("DBD-VIEW-063", Check("dashboard", Dashboard(image: true))!);
        // the first bytes decide, never the name or the type the file was stored as
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var svgFile = new ArtifactRef("logo.png", PngSha, svg.Length, "image/png");
        var refused = Check("dashboard", Dashboard(image: true), null, Sources(file: svg), svgFile)!;
        Assert.Contains("DBD-VIEW-003", refused);
        Assert.Contains("The bytes are not a image/png.", refused);
        Assert.Contains("stored as `image/svg+xml`", Check("dashboard", Dashboard(image: true), null, Sources(file: Png), file with { MediaType = "image/svg+xml" })!);
        var big = Check("dashboard", Dashboard(image: true), null, Sources(file: Png), file with { Size = ImageCheck.MaxBytes + 1 })!;
        Assert.Contains($"The image is {ImageCheck.MaxBytes + 1} bytes; a widget's image is at most {ImageCheck.MaxBytes}.", big);
    }

    [Fact]
    public void The_image_check_is_the_one_of_the_renderers()
    {
        // the cases of widgets.test.tsx (checkImageBytes)
        Assert.Equal(1_048_576, ImageCheck.MaxBytes);
        Assert.Null(ImageCheck.Refusal(Png, "image/png"));
        Assert.Null(ImageCheck.Refusal([0xff, 0xd8, 0xff, 0xe0], "image/jpeg"));
        Assert.Null(ImageCheck.Refusal("RIFF\0\0\0\0WEBPVP8 "u8, "image/webp"));
        Assert.NotNull(ImageCheck.Refusal("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8, "image/png"));
        Assert.Contains("never SVG", ImageCheck.Refusal("<svg/>"u8, "image/svg+xml")!);
        Assert.NotNull(ImageCheck.Refusal("RIFF\0\0\0\0WAVEfmt "u8, "image/webp"));
        Assert.NotNull(ImageCheck.Refusal([0x89, 0x50], "image/png"));
        var big = new byte[ImageCheck.MaxBytes + 1];
        Array.Copy(Png, big, Png.Length);
        Assert.Contains(big.Length.ToString(), ImageCheck.Refusal(big, "image/png")!);
        Assert.Null(ImageCheck.Refusal(big.AsSpan(0, ImageCheck.MaxBytes), "image/png"));
    }

    // ---- on every engine: the life cycle

    private sealed record Setup(DefinitionPublisher Publisher, DefinitionStore Store, PayloadStore Payloads);

    private Setup Open(string engine)
    {
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"), time: _clock);
        var store = new DefinitionStore(database);
        var payloads = new PayloadStore(database);
        var kinds = new DefinitionKinds().Register(new DefinitionKind("semantic-model", ["serving"], PublishMode.Serve) { Formats = DefinitionFormats.Of(2) });
        ViewDefinitions.Register(kinds, ViewPublishingSources.From(store, payloads));
        return new Setup(new DefinitionPublisher(database, kinds, payloads, "serving"), store, payloads);
    }

    private static PublishRequest Publish(string kind, string name, string text, string[]? depends, string channel = "prod", ArtifactRef[]? files = null) =>
        new(kind, name, text, "ada", channel, PublishMode.Serve, depends, Artifacts: files);

    private static PublishRequest Model1(string channel = "prod", string content = "") =>
        new("semantic-model", Model, content.Length == 0 ? ModelContent() : content, "ada", channel, PublishMode.Serve);

    [Fact]
    public void The_definitions_module_is_the_one_the_views_kinds_use() => Assert.Empty(StateConventions.Violations(DefinitionsModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_fixture_dashboard_and_a_fixture_report_are_published_against_a_model_version_and_followed(string engine)
    {
        var s = Open(engine);
        var logo = new PublishArtifacts(s.Payloads, s.Publisher, "ada").Add("logo.png", Png, "image/png");
        Assert.Equal(1, s.Publisher.Publish(Model1()).Version.Version);

        var dashboard = s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(image: true, saved: "revenue_by_region"), Pin(), files: [logo]));
        var report = s.Publisher.Publish(Publish("report", "summary", Report(), Pin()));

        Assert.Equal((1, 1), (dashboard.Version.Version, dashboard.Version.Format));
        Assert.Equal(Pin(), dashboard.Version.DependsOn);
        Assert.Equal(Pin(), report.Version.DependsOn);
        Assert.Equal(1, s.Publisher.Head("dashboard", "sales")!.Active);
        Assert.Equal("logo.png", s.Publisher.Artifacts("dashboard", "sales", 1).Single().Path);

        // an instance reads them through the definition follower
        var dashboards = new PublishedViews("dashboard", s.Store, s.Publisher, s.Payloads);
        var reports = new PublishedViews("report", s.Store, s.Publisher, s.Payloads);
        var problems = new List<string>();
        var follower = new DefinitionFollower(s.Store, [dashboards, reports], (kind, name, version, ex) => problems.Add($"{kind}/{name}@{version}: {ex.Message}"));
        follower.Start();
        Assert.Empty(problems);
        Assert.Equal("dashboard=1-1;report=1-1", follower.FormatText);

        var view = dashboards.Find("sales")!;
        Assert.Equal((1, "Sales"), (view.Version, view.Document.Title));
        Assert.Equal([Model], view.Models.Select(m => m.Name));
        Assert.Equal(CatalogRole.Measure, view.Model(Model)!.Roles.Of("orders.net_revenue"));
        Assert.Equal(CatalogRole.Attribute, view.Model(Model)!.Roles.Of("customers.region"));
        Assert.Equal(Model, view.SavedQueryModel("revenue_by_region"));
        Assert.Null(view.SavedQueryModel("nope"));
        Assert.Equal(Png, view.ImageBytes($"sha256:{PngSha}"));
        Assert.Null(view.ImageBytes("sha256:" + new string('0', 64)));
        Assert.Null(reports.Find("sales"));
        Assert.Equal("Summary", reports.Find("summary")!.Document.Title);

        // the resolver it gives has the picture, so a widget of the document resolves with no host code
        var resolved = await view.Resolver(new RetailServing()).ResolveAsync(new DbDataKit.Serving.ServingContext(new DbDataKit.Serving.ServingCaller("ana", "ana", new HashSet<string>(), new Dictionary<string, string>())), view.Document, "desk", only: ["logo"]);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), resolved.Widgets["logo"]["src"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_publish_the_validator_refuses_stores_nothing_and_says_why(string engine)
    {
        var s = Open(engine);
        s.Publisher.Publish(Model1());

        var noModel = Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(), [])));
        Assert.Equal(PublishRefusal.Invalid, noModel.Reason);
        Assert.Contains("`sales_catalog`", noModel.Message);
        Assert.Contains("`sales_catalog` has no version 5", Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(), Pin(5)))).Message);
        var lacks = Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Publish("report", "summary", Report("orders.nope"), Pin())));
        Assert.Contains("unknown_member", lacks.Message);
        Assert.Contains("orders.nope", lacks.Message);
        Assert.Null(s.Publisher.Head("dashboard", "sales"));
        Assert.Empty(s.Store.Versions("dashboard", "sales"));
        Assert.Empty(s.Store.Versions("report", "summary"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_newer_format_is_refused_naming_kind_name_and_both_numbers(string engine)
    {
        var s = Open(engine);
        s.Publisher.Publish(Model1());
        foreach (var (kind, name, text) in new[] { ("dashboard", "sales", Dashboard(schema: 2)), ("report", "summary", Report().Replace("schemaVersion: 1", "schemaVersion: 2")) })
        {
            var refused = Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Publish(kind, name, text, Pin())));
            Assert.Equal(PublishRefusal.Format, refused.Reason);
            Assert.Contains($"the {kind} '{name}' is in format 2", refused.Message);
            Assert.Contains("reads format 1 of " + kind, refused.Message);
        }
        // the follower refuses a version stored newer than it reads the same way, and what it served stays
        s.Publisher.Publish(Publish("report", "summary", Report(), Pin()));
        var reports = new PublishedViews("report", s.Store, s.Publisher, s.Payloads);
        Assert.Equal(DefinitionFormats.One, reports.Formats);
        Assert.Contains("format 2", DefinitionFormat.Refusal("report", "summary", 2, 2, reports.Formats)!);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_registry_lists_and_filters_by_kind(string engine)
    {
        var s = Open(engine);
        s.Publisher.Publish(Model1());
        s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(), Pin()));
        s.Publisher.Publish(Publish("dashboard", "ops", Dashboard(), Pin()));
        s.Publisher.Publish(Publish("report", "summary", Report(), Pin()));

        Assert.Equal(["ops", "sales"], ViewDefinitions.List(s.Publisher, "dashboard").Select(h => h.Name));
        Assert.Equal(["summary"], ViewDefinitions.List(s.Publisher, "report").Select(h => h.Name));
        Assert.Equal([("dashboard", "ops"), ("dashboard", "sales"), ("report", "summary")], ViewDefinitions.List(s.Publisher).Select(h => (h.Kind, h.Name)));
        Assert.Empty(ViewDefinitions.List(s.Publisher, "dashboard", "test"));
        Assert.DoesNotContain(ViewDefinitions.List(s.Publisher), h => h.Kind == "semantic-model");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Promotion_moves_the_dashboard_and_the_model_together_and_rollback_restores(string engine)
    {
        var s = Open(engine);
        // the model has two versions: the first serves in prod, the second was tried in test with the dashboard that needs it
        s.Publisher.Publish(Model1());
        s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(), Pin(1)));
        s.Publisher.Publish(Model1("test", ModelContent(withSaved: false)));
        var two = s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(), Pin(2), "test"));
        Assert.Equal(2, two.Version.Version);
        Assert.Equal(2, s.Publisher.Head("semantic-model", Model, "test")!.Active);
        Assert.Equal(1, s.Publisher.Head("semantic-model", Model)!.Active);

        // the dashboard alone cannot go: the model version it was published against is not what prod serves
        var alone = Assert.Throws<PublishRefusedException>(() => s.Publisher.Switch("dashboard", "sales", 2, "ada"));
        Assert.Equal(PublishRefusal.DependencyNotActive, alone.Reason);
        Assert.Contains("semantic-model:sales_catalog@2", alone.Message);
        Assert.Equal(1, s.Publisher.Head("dashboard", "sales")!.Active);

        // together they go, the model first, in one step
        var moved = s.Publisher.PromoteWithDependencies("dashboard", "sales", 2, "ada");
        Assert.Equal([("semantic-model", 2), ("dashboard", 2)], moved.Select(v => (v.Kind, v.Version)));
        Assert.Equal(2, s.Publisher.Head("semantic-model", Model)!.Active);
        Assert.Equal(2, s.Publisher.Head("dashboard", "sales")!.Active);
        Assert.Empty(s.Publisher.PromoteWithDependencies("dashboard", "sales", 2, "ada"));

        // rollback restores both
        var back = s.Publisher.RollbackWithDependencies("dashboard", "sales", "ada");
        Assert.Equal([("dashboard", 1), ("semantic-model", 1)], back.Select(v => (v.Kind, v.Version)));
        Assert.Equal(1, s.Publisher.Head("dashboard", "sales")!.Active);
        Assert.Equal(1, s.Publisher.Head("semantic-model", Model)!.Active);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_rollback_does_not_take_a_model_from_under_another_document_that_needs_it(string engine)
    {
        var s = Open(engine);
        s.Publisher.Publish(Model1());
        s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(), Pin(1)));
        s.Publisher.Publish(Model1("test", ModelContent(withSaved: false)));
        s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(), Pin(2), "test"));
        s.Publisher.Publish(Publish("report", "summary", Report(), Pin(2), "test"));
        s.Publisher.PromoteWithDependencies("dashboard", "sales", 2, "ada");
        s.Publisher.PromoteWithDependencies("report", "summary", 1, "ada");
        Assert.Equal(2, s.Publisher.Head("semantic-model", Model)!.Active);

        var refused = Assert.Throws<PublishRefusedException>(() => s.Publisher.RollbackWithDependencies("dashboard", "sales", "ada"));
        Assert.Contains("the report 'summary' needs that version", refused.Message);
        // nothing moved
        Assert.Equal(2, s.Publisher.Head("dashboard", "sales")!.Active);
        Assert.Equal(2, s.Publisher.Head("semantic-model", Model)!.Active);

        // the registry's plain rollback still returns the dashboard alone, as for any kind
        Assert.Equal(1, s.Publisher.Rollback("dashboard", "sales", "ada").Version);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_image_that_is_not_a_raster_is_refused_when_the_version_is_published(string engine)
    {
        var s = Open(engine);
        s.Publisher.Publish(Model1());
        var files = new PublishArtifacts(s.Payloads, s.Publisher, "ada");
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var good = files.Add("logo.png", Png, "image/png");
        var bad = files.Add("logo.png", svg, "image/png");

        // the widget names the hash of the PNG; the svg stored as a png has another hash, so its widget names it
        Assert.True(s.Publisher.Publish(Publish("dashboard", "sales", Dashboard(image: true), Pin(), files: [good])).Changed);
        var text = Dashboard(image: true).Replace(PngSha, bad.Sha256);
        var refused = Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Publish("dashboard", "other", text, Pin(), files: [bad])));
        Assert.Contains("DBD-VIEW-003", refused.Message);
        Assert.Contains("The bytes are not a image/png.", refused.Message);
        Assert.Contains("DBD-VIEW-063", Assert.Throws<PublishRefusedException>(() => s.Publisher.Publish(Publish("dashboard", "third", Dashboard(image: true), Pin()))).Message);
    }
}
