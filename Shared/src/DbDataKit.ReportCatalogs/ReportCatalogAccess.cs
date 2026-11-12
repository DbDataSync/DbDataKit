namespace DbDataKit.ReportCatalogs;

/// <summary>
/// Who may see what of a snapshot (plan 32 RC03). <see cref="Names"/> are the names the person goes by on the report server, as it writes them (<c>CORP\ana</c> and the groups the identity home knows the person to hold, <c>CORP\Analysts</c>): the
/// host takes them from the sign-in's recorded external groups and the home's mappings. <see cref="AdminGroups"/> are the groups an administrator named for a catalog that states no policies.
/// </summary>
public sealed record ReportCatalogPrincipals(IReadOnlyCollection<string> Names, IReadOnlyCollection<string>? AdminGroups = null);

/// <summary>
/// The filter of a snapshot for one person. A report is listed when a policy names one of the person's names; a report whose policies the catalog does not state is listed only to a person in a group the administrator named (a recorded choice);
/// a folder is listed when it holds a listed item (a title of a folder is all a wrong mapping can show, and the report server still decides at open time). Nothing is listed to a person with no name that matches.
/// </summary>
public static class ReportCatalogAccess
{
    public static ReportCatalogSnapshot Filter(ReportCatalogSnapshot snapshot, ReportCatalogPrincipals person)
    {
        var names = new HashSet<string>(person.Names, StringComparer.OrdinalIgnoreCase);
        var admin = new HashSet<string>(person.AdminGroups ?? [], StringComparer.OrdinalIgnoreCase);
        var named = admin.Overlaps(names);
        var listed = snapshot.Items.Where(i => !i.Type.Equals("folder", StringComparison.Ordinal) && Sees(i, names, named)).ToList();
        var kept = new List<ReportCatalogSnapshotItem>(listed);
        foreach (var folder in snapshot.Items.Where(i => i.Type == "folder"))
        {
            var prefix = folder.Path.TrimEnd('/') + "/";
            if (listed.Any(i => i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) kept.Add(folder);
        }
        return snapshot with { Items = [.. kept.OrderBy(i => i.Path, StringComparer.Ordinal)] };
    }

    private static bool Sees(ReportCatalogSnapshotItem item, HashSet<string> names, bool namedByAdministrator) =>
        item.Permissions.Known ? item.Permissions.Principals.Any(names.Contains) : namedByAdministrator;
}
