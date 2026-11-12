using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.Knowledge;

/// <summary>The owner of a verified query that no longer holds is told (the host delivers it: a notification, a work item).</summary>
/// <param name="Channel">The index channel the query is in.</param>
/// <param name="ItemId">The item.</param>
/// <param name="Title">Its title.</param>
/// <param name="Owner">The group that owns it, or null when it has none (the notice then goes to the administrators).</param>
/// <param name="Model">The model that changed.</param>
/// <param name="Reason">What no longer holds.</param>
public sealed record VerifiedNotice(string Channel, string ItemId, string Title, string? Owner, string Model, string Reason);

/// <summary>What a re-check of a model's verified queries in one index did.</summary>
/// <param name="Checked">Verified queries about the model.</param>
/// <param name="Marked">Marked <c>needs-reverification</c> now (a query that was already marked and still fails is not counted again, nor is its owner told twice).</param>
/// <param name="Restored">Marked before by a re-check and holding again (the model moved back).</param>
public sealed record RecheckResult(int Checked, int Marked, int Restored);

/// <summary>
/// What happens at staging to the verified queries about a model (plan 27a KN22): each is checked against the new version (<see cref="VerifiedRecheck"/>), a query that fails is marked <b>needs re-verification</b> (it is
/// still found, no longer given as verified) and its owner is told, and a query that was marked by an earlier re-check and holds again is restored. <b>A model change is never blocked:</b> nothing here throws, and a check that
/// cannot be made is a reason on the query, not a refusal of the model. A query a person verified again (the item written again) starts as verified.
/// </summary>
public static class VerifiedRechecker
{
    /// <summary>Starts every note this writes, so a later re-check knows the mark is its own and may lift it.</summary>
    public const string NotePrefix = "Re-checked when the model changed: ";

    public static RecheckResult Run(StateKnowledgeIndex index, string model, JsonNode? catalog, JsonNode? previous = null, RequestValidator? validate = null, Action<VerifiedNotice>? notify = null)
    {
        int count = 0, marked = 0, restored = 0;
        IReadOnlyList<(string ItemId, StoredVerified Verified)> queries;
        try { queries = index.Store.VerifiedQueries(index.Channel, model); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new RecheckResult(0, 0, 0); }
        foreach (var (itemId, verified) in queries)
        {
            count++;
            try
            {
                var problems = catalog is null
                    ? [$"{model} is no longer served"]
                    : VerifiedRecheck.Problems(verified.Request, verified.SavedQuery, catalog, previous, validate);
                var failing = problems.Count > 0;
                var ours = verified.Status == VerifiedStatus.NeedsReverification && verified.StatusNote?.StartsWith(NotePrefix, StringComparison.Ordinal) == true;
                if (failing)
                {
                    var note = NotePrefix + string.Join("; ", problems) + ".";
                    if (verified.Status == VerifiedStatus.NeedsReverification && verified.StatusNote == note) continue;
                    index.Store.SetVerifiedStatus(index.Channel, itemId, VerifiedStatus.NeedsReverification, note);
                    marked++;
                    var stored = index.Store.Item(index.Channel, itemId);
                    notify?.Invoke(new VerifiedNotice(index.Channel, itemId, stored?.Title ?? itemId, stored?.Owner, model, note));
                }
                else if (ours)
                {
                    index.Store.SetVerifiedStatus(index.Channel, itemId, VerifiedStatus.Verified, null);
                    restored++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the model is not held up by a query it could not check: the query says why it could not be
                index.Store.SetVerifiedStatus(index.Channel, itemId, VerifiedStatus.NeedsReverification, NotePrefix + $"the check could not be made ({ex.Message}).");
                marked++;
            }
        }
        return new RecheckResult(count, marked, restored);
    }
}
