using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>A custom page on the apps host (plan 32 DBR33): found by its manifest entry, its parameters read from the address against the declaration, listed, and drawn as the shell's page around the app's own document.</summary>
public sealed class CustomPageTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "custom-page-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore store;

    private const string Manifest = """
        schemaVersion: 1
        name: sales-portal
        version: 1.0.0
        entry: index.html
        origin: sandbox
        roles: [viewer]
        semantic: [sales]
        views:
          - name: orders-grid
            kind: widget
            export: OrdersGrid
            queries:
              orders:
                semantic: {model: sales, request: {measures: [orders.net_revenue]}}
          - name: regional-detail
            kind: page
            export: RegionalDetail
            title: Regional detail
            formats: [pdf, png]
            params:
              - {name: region, type: string, required: true}
              - {name: top, type: integer}
              - {name: tags, type: string, many: true}
              - {name: closed, type: boolean}
              - {name: share, type: number}
            queries:
              orders:
                semantic: {model: sales, request: {measures: [orders.net_revenue]}}
          - name: plain
            kind: page
            export: Plain
        """;

    public CustomPageTests()
    {
        Directory.CreateDirectory(folder);
        store = new AppInstanceStore(new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module])));
        store.PublishApp(new AppPackage(Manifest, new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<!doctype html><html><head></head><body></body></html>") }), "ada");
        store.Save(new AppInstance("acme", "sales-portal"), "ada");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private ResolvedInstance Instance => store.Resolve("acme")!;

    private static IEnumerable<KeyValuePair<string, string[]>> Query(params (string Name, string Value)[] pairs) => pairs.Select(p => KeyValuePair.Create(p.Name, new[] { p.Value }));

    [Fact]
    public void A_page_is_found_by_its_name_and_a_widget_has_no_address()
    {
        Assert.Equal("RegionalDetail", CustomPages.Find(Instance, "regional-detail")!.Export);
        Assert.Null(CustomPages.Find(Instance, "orders-grid"));   // a widget is placed by a dashboard
        Assert.Null(CustomPages.Find(Instance, "nothing"));
        Assert.Equal("/apps/acme/_views/regional-detail", CustomPages.Address("acme", "regional-detail"));
        Assert.Equal("/apps/acme~preview/_views/x/_rows", CustomPages.RowsAddress("acme~preview", "x").Replace("%7E", "~"));
    }

    [Fact]
    public void The_pages_of_an_instance_are_listed_by_their_manifest_entries_with_their_formats_and_parameters()
    {
        var pages = CustomPages.Pages(Instance, "acme");
        Assert.Equal(["regional-detail", "plain"], pages.Select(p => p.Name));
        var detail = pages[0];
        Assert.Equal("Regional detail", detail.Title);
        Assert.Equal("/apps/acme/_views/regional-detail", detail.Address);
        Assert.Equal(["pdf", "png"], detail.Formats);
        Assert.Equal(["region", "top", "tags", "closed", "share"], detail.Parameters);
        Assert.Equal(["pdf"], pages[1].Formats);   // a page that names no format offers the PDF
    }

    [Fact]
    public void The_addresss_parameters_are_read_by_their_declared_types_and_a_name_nobody_declared_reaches_nothing()
    {
        var view = CustomPages.Find(Instance, "regional-detail")!;
        var (values, problem) = CustomPages.Parameters(view, [
            KeyValuePair.Create("region", new[] { "West" }), KeyValuePair.Create("top", new[] { "10" }), KeyValuePair.Create("tags", new[] { "a", "b" }),
            KeyValuePair.Create("closed", new[] { "false" }), KeyValuePair.Create("share", new[] { "0.25" }), KeyValuePair.Create("evil", new[] { "1" })]);
        Assert.Null(problem);
        Assert.Equal("""{"region":"West","top":10,"tags":["a","b"],"closed":false,"share":0.25}""", new JsonObject(values!).ToJsonString());
    }

    [Theory]
    [InlineData("region", "", "needs the parameter `region`")]
    [InlineData("top", "ten", "`top` of the page 'regional-detail' is an integer, not 'ten'")]
    [InlineData("closed", "yes", "`closed` of the page 'regional-detail' is true or false, not 'yes'")]
    [InlineData("share", "NaN", "`share` of the page 'regional-detail' is a number, not 'NaN'")]
    public void A_wrong_or_missing_parameter_is_a_problem_in_words_and_no_values(string name, string value, string words)
    {
        var view = CustomPages.Find(Instance, "regional-detail")!;
        var given = name == "region" ? Query(("top", "1")) : Query(("region", "West"), (name, value));
        var (values, problem) = CustomPages.Parameters(view, given);
        Assert.Null(values);
        Assert.Contains(words, problem);
    }

    [Fact]
    public void A_single_parameter_takes_one_value()
    {
        var view = CustomPages.Find(Instance, "regional-detail")!;
        var (values, problem) = CustomPages.Parameters(view, [KeyValuePair.Create("region", new[] { "West", "East" })]);
        Assert.Null(values);
        Assert.Contains("takes one value, not 2", problem);
    }

    [Fact]
    public void The_page_frames_the_apps_own_document_on_the_view_and_names_where_its_rows_come_from()
    {
        var view = CustomPages.Find(Instance, "regional-detail")!;
        var response = AppShell.ViewPage("acme", "Regional detail", view, new AppShell.Header("Ada", []));
        var html = Encoding.UTF8.GetString(response.Body);
        Assert.Contains("sandbox=\"allow-scripts allow-forms allow-popups\"", html);
        Assert.DoesNotContain("allow-same-origin", html);
        Assert.Contains("src=\"/apps/acme/_app/#view=RegionalDetail&amp;mode=screen\"", html);
        Assert.Contains("data-view-page=\"regional-detail\"", html);
        Assert.Contains("data-rows=\"/apps/acme/_views/regional-detail/_rows\"", html);
        Assert.Contains("data-instance=\"acme\"", html);   // the shell's broker still answers the frame
        var declared = Regex.Match(html, "data-params=\"([^\"]*)\"").Groups[1].Value.Replace("&quot;", "\"");
        Assert.Equal("""{"region":{"type":"string","many":false},"top":{"type":"integer","many":false},"tags":{"type":"string","many":true},"closed":{"type":"boolean","many":false},"share":{"type":"number","many":false}}""", declared);
        var policy = response.Headers["Content-Security-Policy"];
        Assert.Contains("frame-src 'self'", policy);   // the shell may frame the app's own document
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("connect-src 'self'", policy);   // the script fetches the rows
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
    }

    [Fact]
    public void A_name_in_the_page_is_encoded_and_the_fragment_cannot_be_forged()
    {
        var m = AppManifestLoader.Load(Manifest.Replace("export: RegionalDetail", "export: RegionalDetail").Replace("title: Regional detail", "title: \"<b>x</b>\"")).Manifest!;
        var view = m.CustomViews.First(v => v.Name == "regional-detail");
        var html = Encoding.UTF8.GetString(AppShell.ViewPage("acme", view.Title!, view).Body);
        Assert.Contains("title=\"&lt;b&gt;x&lt;/b&gt;\"", html);
        Assert.DoesNotContain("<b>x</b>", html);
    }

    [Fact]
    public void The_shell_script_plays_the_hosts_side_of_the_frames_conversation_and_reads_only_the_sdks_shapes_from_that_frame()
    {
        var script = AppShell.Script;
        Assert.Contains("data-view-page", script);
        Assert.Contains("views:hello", script);
        Assert.Contains("views:init", script);
        Assert.Contains("views:props", script);
        Assert.Contains("views:setParam", script);
        Assert.Contains("X-DbData-Shell", script);
        Assert.DoesNotContain("__TOKENS__", script);
        // the frame is found by its window, and its messages are handled only when it is a page's frame
        Assert.Contains("f.contentWindow === event.source", script);
        Assert.Contains("hasAttribute(\"data-view-page\")", script);
    }

    [Fact]
    public void The_theme_tokens_are_the_chart_palette_of_the_visuals()
    {
        var text = File.ReadAllText(Locate("Shared/web/visuals/src/theme.ts"));
        foreach (var scheme in new[] { "light", "dark" })
        {
            var line = Regex.Match(text, $@"^\s*{scheme}: \{{ text: '(?<text>[^']+)', muted: '(?<muted>[^']+)', grid: '(?<grid>[^']+)', axis: '(?<axis>[^']+)', categories: \['(?<accent>[^']+)'", RegexOptions.Multiline);
            Assert.True(line.Success, $"theme.ts has no {scheme} palette in the expected shape");
            var tokens = CustomPages.Tokens[scheme];
            Assert.Equal(line.Groups["text"].Value, tokens["sui-text"], ignoreCase: true);
            Assert.Equal(line.Groups["muted"].Value, tokens["sui-muted"], ignoreCase: true);
            Assert.Equal(line.Groups["grid"].Value, tokens["sui-grid"], ignoreCase: true);
            Assert.Equal(line.Groups["axis"].Value, tokens["sui-axis"], ignoreCase: true);
            Assert.Equal(line.Groups["accent"].Value, tokens["sui-accent"], ignoreCase: true);
        }
        Assert.Equal("#ffffff", CustomPages.Tokens["light"]["sui-bg"]);
        Assert.Equal("#0d1117", CustomPages.Tokens["dark"]["sui-bg"]);
    }

    private static string Locate(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException($"{relative} was not found above {AppContext.BaseDirectory}.");
    }
}
