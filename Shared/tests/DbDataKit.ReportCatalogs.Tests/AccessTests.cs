using DbDataKit.ReportCatalogs;
using DbDataKit.Views;

namespace DbDataKit.ReportCatalogs.Tests;

public sealed class AccessTests
{
    private static ReportCatalogSnapshotItem Item(string path, string type, bool known, params string[] principals) =>
        new(path, path, path[(path.LastIndexOf('/') + 1)..], type, null, null, null, [], [], new ReportCatalogPermissions(known, principals));

    private static readonly ReportCatalogSnapshot Snapshot = new("ssrs",
    [
        Item("/Sales", ReportItemTypes.Folder, true),
        Item("/Sales/Quarterly", ReportItemTypes.External, true, "CORP\\Analysts"),
        Item("/Sales/Board", ReportItemTypes.External, true, "CORP\\Board"),
        Item("/Hr", ReportItemTypes.Folder, true),
        Item("/Hr/Payroll", ReportItemTypes.External, true, "CORP\\Hr"),
        Item("/Legacy", ReportItemTypes.Folder, true),
        Item("/Legacy/Old", ReportItemTypes.External, false),
    ]);

    private static string[] Paths(ReportCatalogSnapshot s) => [.. s.Items.Select(i => i.Path)];

    [Fact]
    public void A_person_sees_the_reports_a_policy_names_and_the_folders_that_hold_them()
    {
        var filtered = ReportCatalogAccess.Filter(Snapshot, new ReportCatalogPrincipals(["corp\\ana", "CORP\\analysts"]));
        Assert.Equal(["/Sales", "/Sales/Quarterly"], Paths(filtered));
    }

    [Fact]
    public void A_person_with_no_mapped_policy_sees_nothing_not_even_a_folder()
    {
        Assert.Empty(ReportCatalogAccess.Filter(Snapshot, new ReportCatalogPrincipals(["CORP\\ana"])).Items);
        Assert.Empty(ReportCatalogAccess.Filter(Snapshot, new ReportCatalogPrincipals([])).Items);
    }

    [Fact]
    public void A_catalog_that_states_no_policies_shows_only_to_the_groups_the_administrator_named()
    {
        var named = ReportCatalogAccess.Filter(Snapshot, new ReportCatalogPrincipals(["CORP\\Staff"], ["CORP\\Staff"]));
        Assert.Equal(["/Legacy", "/Legacy/Old"], Paths(named));
        Assert.Empty(ReportCatalogAccess.Filter(Snapshot, new ReportCatalogPrincipals(["CORP\\Other"], ["CORP\\Staff"])).Items);
    }
}
