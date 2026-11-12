using DbDataKit.Knowledge;
using DbDataKit.Views;

namespace DbDataKit.ReportCatalogs;

/// <summary>
/// Whether a person may read a report or dashboard of a provider that answers per person and asynchronously (ours: <c>ViewsCatalog</c>, plan 32 RC04, plan 27a's open item): the subject <c>report-catalog: &lt;provider&gt;</c> narrowed to <c>item: &lt;id&gt;</c> is readable when the
/// provider's own <c>FindAsync</c> gives the item to this viewer, so a person finds exactly the documents the catalog lists for them, an app's narrowing included, and a viewer the provider knows nothing of finds none. Other subjects are not handled (the synchronous caller
/// decides them). A subject of the whole catalog, with no item, is not readable here: no document is meant by it and asking the provider to browse would write an audit row for nothing. A provider's refusal or failure is "no" (fail closed).
/// </summary>
public sealed class ReportCatalogKnowledgeAccess(IReportCatalogProvider provider, ReportCatalogViewer? viewer) : IAsyncKnowledgeAccess
{
    public bool Handles(KnowledgeSubject subject) =>
        subject.Type == ReportCatalogKnowledge.SubjectType && string.Equals(subject.Name, provider.Provider, StringComparison.Ordinal);

    public async ValueTask<bool> MayReadAsync(KnowledgeSubject subject, CancellationToken cancellationToken = default)
    {
        if (viewer is null || !Handles(subject) || subject.NarrowingKind != "item" || subject.NarrowingName.Length == 0) return false;
        try
        {
            return await provider.FindAsync(viewer, subject.NarrowingName, cancellationToken).ConfigureAwait(false) is { IsFolder: false };
        }
        catch (ReportCatalogException)
        {
            return false;
        }
    }
}
