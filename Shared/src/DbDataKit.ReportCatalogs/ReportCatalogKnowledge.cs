using System.Text.Json.Nodes;
using DbDataKit.Knowledge;

namespace DbDataKit.ReportCatalogs;

/// <summary>
/// Searchable like everything else (plan 32 RC04): the glue between a snapshot and the knowledge module. <see cref="Render"/> makes the generated items of a snapshot (<see cref="GeneratedCatalogKnowledge"/>); <see cref="MayRead"/> answers the access question of the subject
/// <c>report-catalog</c> with RC03's filter (<see cref="ReportCatalogAccess"/>), so a person's search finds only the reports the report server's policies, as the home maps them, let them see; <see cref="SubjectCatalog"/> says which of those subjects exist.
/// </summary>
public static class ReportCatalogKnowledge
{
    public const string SubjectType = "report-catalog";

    /// <summary>The items of a snapshot, through the renderer: the snapshot's own JSON is what the renderer reads.</summary>
    public static IReadOnlyList<KnowledgeItem> Render(ReportCatalogSnapshot snapshot, string version, string channel) =>
        GeneratedCatalogKnowledge.Render(JsonNode.Parse(snapshot.ToJson())!, GeneratedCatalogKnowledge.SourceOf(snapshot.Provider, version, channel));

    /// <summary>Whether a person with these names may read the subject: a subject of another type is not this catalog's to answer (false: the caller decides it elsewhere); an item the filter does not list for them, or one the snapshot does not hold, is not readable.</summary>
    public static bool MayRead(KnowledgeSubject subject, ReportCatalogSnapshot? snapshot, ReportCatalogPrincipals person)
    {
        if (subject.Type != SubjectType || snapshot is null || !string.Equals(subject.Name, snapshot.Provider, StringComparison.Ordinal)) return false;
        var visible = ReportCatalogAccess.Filter(snapshot, person).Items;
        return subject.NarrowingKind.Length == 0
            ? visible.Count > 0
            : visible.Any(i => string.Equals(i.Id, subject.NarrowingName, StringComparison.OrdinalIgnoreCase) && i.Type != "folder");
    }

    /// <summary>The subjects of a snapshot as the subject catalog says it: each report that is there exists; the catalog itself exists.</summary>
    public static ISubjectCatalog Subjects(ReportCatalogSnapshot snapshot)
    {
        var catalog = new DbDataKit.Knowledge.SubjectCatalog();
        foreach (var item in snapshot.Items.Where(i => i.Type != "folder")) catalog.Add(SubjectType, snapshot.Provider, "item", item.Id);
        return catalog;
    }
}
