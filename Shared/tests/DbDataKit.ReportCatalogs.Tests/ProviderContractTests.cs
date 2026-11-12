using System.Net;
using DbDataKit.ReportCatalogs;
using DbDataKit.Serving;
using DbDataKit.Views;
using DbDataKit.Views.Tests;

namespace DbDataKit.ReportCatalogs.Tests;

/// <summary>What every provider of the catalog interface answers the same way (plan 32 RC01): the fake of the tests and the report server's provider over its recorded-shape answers run the same facts.</summary>
public abstract class ProviderContractTests
{
    protected static readonly ReportCatalogViewer Viewer = new(new ServingCaller("ana", "Ana", new HashSet<string>(), new Dictionary<string, string>()));

    protected abstract IReportCatalogProvider Create();

    /// <summary>The id of a report the provider holds.</summary>
    protected abstract string ReportId { get; }

    protected const string Missing = "99999999-9999-9999-9999-999999999999";

    [Fact]
    public async Task Every_item_of_a_search_carries_the_providers_name_and_a_key_made_of_it()
    {
        var provider = Create();
        var items = await provider.BrowseAsync(Viewer, new ReportCatalogQuery(Search: "e"));
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal(provider.Provider, i.Provider));
        Assert.All(items, i => Assert.StartsWith(provider.Provider + ":", i.Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_found_is_the_item_listed_and_one_that_is_not_there_is_null()
    {
        var provider = Create();
        var listed = (await provider.BrowseAsync(Viewer, new ReportCatalogQuery(Search: "e"))).First(i => i.Id == ReportId);
        Assert.Equal(listed.Name, (await provider.FindAsync(Viewer, ReportId))!.Name);
        Assert.Null(await provider.FindAsync(Viewer, Missing));
    }

    [Fact]
    public async Task A_report_has_exports_and_permissions_that_say_whether_they_are_known()
    {
        var provider = Create();
        Assert.NotEmpty(await provider.ExportsAsync(Viewer, ReportId));
        var permissions = await provider.PermissionsAsync(Viewer, ReportId);
        Assert.True(permissions.Known ? permissions.Principals.Count > 0 : permissions.Note is not null || permissions.Principals.Count == 0);
    }

    [Fact]
    public async Task Opening_a_report_gives_an_address_to_frame_or_follow()
    {
        var opening = await Create().OpenAsync(Viewer, ReportId);
        Assert.Contains(opening.Mode, new[] { ReportOpenModes.Frame, ReportOpenModes.Link });
        Assert.False(string.IsNullOrEmpty(opening.Url));
    }
}

public sealed class FakeProviderContractTests : ProviderContractTests
{
    protected override string ReportId => "22222222-2222-2222-2222-222222222222";

    protected override IReportCatalogProvider Create() => new FakeCatalogProvider("fake",
        new ReportCatalogItem(ReportId, "/Sales/Quarterly revenue", "Quarterly revenue", ReportItemTypes.External, "fake"))
    {
        Opens = id => new ReportCatalogOpening($"fake:{id}", ReportOpenModes.Frame, "Quarterly revenue", Url: "https://reports.example.com/ReportServer?/Sales"),
    };
}

public sealed class ReportServerContractTests : ProviderContractTests
{
    protected override string ReportId => "22222222-2222-2222-2222-222222222222";

    protected override IReportCatalogProvider Create()
    {
        var stub = new ReportServerStub
        {
            Answer = uri => uri.AbsolutePath switch
            {
                "/Reports/api/v2.0/CatalogItems" => (HttpStatusCode.OK, ReportServerStub.File("catalogitems-2.json").Replace("""{ "Id": "44444444""", """{ "Id": "22222222-2222-2222-2222-222222222222", "Name": "Quarterly revenue", "Path": "/Sales/Quarterly revenue", "Type": "Report" }, { "Id": "44444444""", StringComparison.Ordinal)),
                var p when p.EndsWith($"CatalogItems({ReportId})", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"Id":"22222222-2222-2222-2222-222222222222","Name":"Quarterly revenue","Path":"/Sales/Quarterly revenue","Type":"Report"}"""),
                var p when p.EndsWith($"CatalogItems({ReportId})/Policies", StringComparison.Ordinal) => (HttpStatusCode.OK, ReportServerStub.File("policies.json")),
                var p when p.EndsWith($"Reports({ReportId})/ParameterDefinitions", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"value":[]}"""),
                _ => (HttpStatusCode.NotFound, "{}"),
            },
        };
        return new ReportServerCatalogProvider(new HttpClient(stub), new ReportServerOptions(new Uri("https://reports.example.com/Reports")));
    }
}
