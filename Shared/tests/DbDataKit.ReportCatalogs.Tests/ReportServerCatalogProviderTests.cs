using System.Net;
using System.Text;
using DbDataKit.ReportCatalogs;
using DbDataKit.Serving;
using DbDataKit.Views;

namespace DbDataKit.ReportCatalogs.Tests;

/// <summary>The answers of a report server by address, and the addresses asked.</summary>
internal sealed class ReportServerStub : HttpMessageHandler
{
    public List<string> Asked { get; } = [];
    public Func<Uri, (HttpStatusCode, string)>? Answer { get; set; }

    public static string File(string name) => System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "answers", name));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Asked.Add(request.RequestUri!.ToString());
        var (status, body) = Answer!(request.RequestUri);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public sealed class ReportServerCatalogProviderTests
{
    private const string Portal = "https://reports.example.com/Reports";
    private const string Quarterly = "22222222-2222-2222-2222-222222222222";
    private const string Pipeline = "44444444-4444-4444-4444-444444444444";
    private static readonly ReportCatalogViewer Viewer = new(new ServingCaller("ana", "Ana", new HashSet<string>(), new Dictionary<string, string>()));

    private static ReportServerStub Server() => new()
    {
        Answer = uri => uri.AbsolutePath switch
        {
            "/Reports/api/v2.0/CatalogItems" when uri.Query.Contains("skip", StringComparison.Ordinal) => (HttpStatusCode.OK, ReportServerStub.File("catalogitems-2.json")),
            "/Reports/api/v2.0/CatalogItems" => (HttpStatusCode.OK, ReportServerStub.File("catalogitems.json")),
            var p when p.EndsWith($"CatalogItems({Quarterly})", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"Id":"22222222-2222-2222-2222-222222222222","Name":"Quarterly revenue","Path":"/Sales/Quarterly revenue","Type":"Report"}"""),
            var p when p.EndsWith($"CatalogItems({Pipeline})", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"Id":"44444444-4444-4444-4444-444444444444","Name":"Pipeline","Path":"/Pipeline","Type":"PowerBIReport"}"""),
            var p when p.EndsWith($"Reports({Quarterly})/ParameterDefinitions", StringComparison.Ordinal) => (HttpStatusCode.OK, ReportServerStub.File("parameterdefinitions.json")),
            var p when p.EndsWith($"CatalogItems({Quarterly})/Policies", StringComparison.Ordinal) => (HttpStatusCode.OK, ReportServerStub.File("policies.json")),
            _ => (HttpStatusCode.NotFound, "{}"),
        },
    };

    private static ReportServerCatalogProvider Provider(ReportServerStub stub, ReportServerKind kind = ReportServerKind.Ssrs) =>
        new(new HttpClient(stub), new ReportServerOptions(new Uri(Portal), kind));

    [Fact]
    public async Task Browse_follows_the_pages_and_lists_the_folders_and_reports_under_a_folder()
    {
        var provider = Provider(Server());
        var root = await provider.BrowseAsync(Viewer, new ReportCatalogQuery());
        Assert.Equal(["/Pipeline", "/Sales"], root.Select(i => i.Path).Order());
        var sales = await provider.BrowseAsync(Viewer, new ReportCatalogQuery("/Sales"));
        Assert.Equal(["/Sales/Archive", "/Sales/Quarterly revenue"], sales.Select(i => i.Path).Order());
        Assert.Equal(ReportItemTypes.External, sales.Single(i => i.Name == "Quarterly revenue").Type);
        Assert.True(sales.Single(i => i.Name == "Archive").IsFolder);
        Assert.Equal("ssrs", root[0].Provider);
    }

    [Fact]
    public async Task Browse_leaves_out_what_is_no_report_and_searches_by_name_path_and_description()
    {
        var provider = Provider(Server());
        var found = await provider.BrowseAsync(Viewer, new ReportCatalogQuery(Search: "revenue by"));
        Assert.Equal(["Quarterly revenue"], found.Select(i => i.Name));
        Assert.DoesNotContain(await provider.BrowseAsync(Viewer, new ReportCatalogQuery(Search: "source")), i => i.Name == "Data source");
        var ex = await Assert.ThrowsAsync<ReportCatalogException>(() => provider.BrowseAsync(Viewer, new ReportCatalogQuery("/Nothing")));
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public async Task Parameters_come_with_their_types_values_defaults_and_dependencies()
    {
        var parameters = await Provider(Server()).ParametersAsync(Viewer, Quarterly);
        var region = parameters.Single(p => p.Name == "Region");
        Assert.True(region.Many);
        Assert.True(region.Required);
        Assert.Equal(["Country"], region.DependsOn);
        Assert.Equal([("East", "Eastern"), ("West", "Western")], region.ValidValues!.Select(o => (o.Value, o.Label)));
        var year = parameters.Single(p => p.Name == "Year");
        Assert.Equal("2026", year.Default);
        Assert.False(year.Required);
    }

    [Fact]
    public async Task Open_fills_the_parameters_into_the_url_access_address_and_refuses_a_parameter_the_report_lacks()
    {
        var provider = Provider(Server());
        var opening = await provider.OpenAsync(Viewer, Quarterly, new Dictionary<string, string?> { ["Region"] = "East & West", ["Year"] = null });
        Assert.Equal(ReportOpenModes.Frame, opening.Mode);
        Assert.Equal("https://reports.example.com/ReportServer?/Sales/Quarterly%20revenue&rs:Command=Render&rc:Toolbar=false&Region=East%20%26%20West&Year=&Year:isnull=true", opening.Url);
        var ex = await Assert.ThrowsAsync<ReportCatalogException>(() => provider.OpenAsync(Viewer, Quarterly, new Dictionary<string, string?> { ["Nope"] = "x" }));
        Assert.Equal("invalid", ex.Code);
    }

    [Fact]
    public async Task A_power_bi_report_opens_in_the_portal_and_offers_no_export_through_the_server()
    {
        var provider = Provider(Server(), ReportServerKind.Pbirs);
        Assert.Equal("pbirs", provider.Provider);
        Assert.Equal("https://reports.example.com/Reports/powerbi/Pipeline", (await provider.OpenAsync(Viewer, Pipeline)).Url);
        Assert.Empty(await provider.ExportsAsync(Viewer, Pipeline));
        Assert.Empty(await provider.ParametersAsync(Viewer, Pipeline));
    }

    [Fact]
    public async Task Exports_and_permissions_say_what_the_server_says()
    {
        var provider = Provider(Server());
        Assert.Contains(await provider.ExportsAsync(Viewer, Quarterly), e => e.Format == "PDF");
        var permissions = await provider.PermissionsAsync(Viewer, Quarterly);
        Assert.True(permissions.Known);
        Assert.Equal(["CORP\\Analysts", "CORP\\admin"], permissions.Principals);
    }

    [Fact]
    public async Task A_server_that_states_no_policies_is_unknown_not_empty()
    {
        var stub = Server();
        var inner = stub.Answer!;
        stub.Answer = uri => uri.AbsolutePath.EndsWith("/Policies", StringComparison.Ordinal) ? (HttpStatusCode.NotFound, "{}") : inner(uri);
        var permissions = await Provider(stub).PermissionsAsync(Viewer, Quarterly);
        Assert.False(permissions.Known);
        Assert.Empty(permissions.Principals);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_service_account_the_server_refuses_is_reported_naming_the_address(HttpStatusCode status)
    {
        var stub = new ReportServerStub { Answer = _ => (status, "{}") };
        var ex = await Assert.ThrowsAsync<ReportCatalogException>(() => Provider(stub).BrowseAsync(Viewer, new ReportCatalogQuery()));
        Assert.Equal("unavailable", ex.Code);
        Assert.Contains("https://reports.example.com/Reports", ex.Message, StringComparison.Ordinal);
        Assert.Contains("service account", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_that_is_not_json_says_the_address_may_not_be_the_portals()
    {
        var stub = new ReportServerStub { Answer = _ => (HttpStatusCode.OK, "<html>sign in</html>") };
        var ex = await Assert.ThrowsAsync<ReportCatalogException>(() => Provider(stub).BrowseAsync(Viewer, new ReportCatalogQuery()));
        Assert.Contains("not JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_next_link_to_another_host_is_not_followed_and_only_the_api_is_asked()
    {
        var stub = Server();
        var inner = stub.Answer!;
        stub.Answer = uri => uri.AbsolutePath == "/Reports/api/v2.0/CatalogItems" && !uri.Query.Contains("skip", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"value":[],"@odata.nextLink":"https://evil.example.net/steal"}""")
            : inner(uri);
        await Provider(stub).BrowseAsync(Viewer, new ReportCatalogQuery());
        Assert.All(stub.Asked, a => Assert.StartsWith("https://reports.example.com/Reports/api/v2.0/", a, StringComparison.Ordinal));
    }

    [Fact]
    public void The_url_access_address_is_the_portals_with_report_server_for_reports_unless_given()
    {
        Assert.Equal("https://reports.example.com/ReportServer", new ReportServerOptions(new Uri("https://reports.example.com/Reports/")).UrlAccess.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("https://x.example.com/rs", new ReportServerOptions(new Uri("https://x.example.com/portal"), ReportServerAddress: new Uri("https://x.example.com/rs")).UrlAccess.AbsoluteUri.TrimEnd('/'));
    }
}
