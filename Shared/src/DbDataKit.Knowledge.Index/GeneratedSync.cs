namespace DbDataKit.Knowledge;

/// <summary>What bringing a definition's generated items up to date in an index did.</summary>
/// <param name="Added">Items that were not there.</param>
/// <param name="Changed">Items whose text changed (the ones that were chunked and indexed again).</param>
/// <param name="Unchanged">Items whose text is the same (not indexed again; only the version they came from was brought up to date).</param>
/// <param name="Retired">Items of the definition that it no longer produces (a measure that was dropped), removed.</param>
public sealed record GeneratedSyncResult(int Added, int Changed, int Unchanged, int Retired);

/// <summary>
/// Keeps a definition's generated knowledge in a channel's index (plan 27a KN12): the items the renderer made for the version being staged are put in (an item whose text did not change is not indexed again, so a version that changed one measure changes one
/// item) and the items of the same definition that it no longer renders are retired. Items of other definitions and items typed by people are never touched: an item is the definition's when its source is the definition's key and its id is in the
/// <c>generated:</c> namespace. Retiring a whole definition is <see cref="Retire"/>.
/// </summary>
public static class GeneratedSync
{
    public static GeneratedSyncResult Apply(StateKnowledgeIndex index, GeneratedSource source, IReadOnlyList<KnowledgeItem> items, TimeProvider? time = null)
    {
        var before = OwnedBy(index, source).ToHashSet(StringComparer.Ordinal);
        int added = 0, changed = 0, unchanged = 0;
        foreach (var item in items)
        {
            if (!item.Id.StartsWith(GeneratedKnowledge.IdPrefix, StringComparison.Ordinal)) throw new ArgumentException($"'{item.Id}' is not a generated item (its id starts with '{GeneratedKnowledge.IdPrefix}').", nameof(items));
            var existed = before.Remove(item.Id);
            if (index.Put(item, Chunker.Split(item), source.Key, source.Version, time)) { if (existed) changed++; else added++; }
            else unchanged++;
        }
        foreach (var gone in before) index.Remove(gone);
        return new GeneratedSyncResult(added, changed, unchanged, before.Count);
    }

    /// <summary>The definition is retired (it was deleted, or its channel no longer has it): every item generated from it goes. Returns how many.</summary>
    public static int Retire(StateKnowledgeIndex index, GeneratedSource source)
    {
        var owned = OwnedBy(index, source);
        foreach (var id in owned) index.Remove(id);
        return owned.Count;
    }

    private static List<string> OwnedBy(StateKnowledgeIndex index, GeneratedSource source) =>
        [.. index.Store.Items(index.Channel).Where(i => i.Source == source.Key && i.Id.StartsWith(GeneratedKnowledge.IdPrefix, StringComparison.Ordinal)).Select(i => i.Id)];
}
