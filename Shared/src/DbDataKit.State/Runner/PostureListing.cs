namespace DbDataKit.State;

/// <summary>A recorded choice and the table that holds it: <c>console</c> for <c>bindings.posture_choice</c>, a server's name for its <c>runner.posture_choice</c>.</summary>
public sealed record PostureHolding(string Holder, PostureChoice Choice);

/// <summary>One line of the combined list of posture choices: the choice once, whichever tables hold it, with every holder named.</summary>
public sealed record PostureEntry(string Setting, string Scope, string ChosenBy, DateTimeOffset ChosenAt, string? Note, IReadOnlyList<string> HeldBy, string? Default, string? Effect);

/// <summary>
/// The combined, read-only list of the posture choices of the console's table (<c>bindings.posture_choice</c>) and of the Runners' (<c>runner.posture_choice</c>) (plan 27a IX25, <c>docs/channel-bindings.md</c>). Enforcement stays local
/// to the server that holds the binding (two tables, one view): this only reads. A choice appears <b>once</b> for a setting and scope, whichever table holds it, and names the holders; when several hold it the earliest is the one shown
/// (who and when are the first record), the others are only named.
/// </summary>
public static class PostureListing
{
    /// <summary>The choices, one entry per setting and scope, ordered by scope and setting.</summary>
    public static IReadOnlyList<PostureEntry> Merge(IEnumerable<PostureHolding> holdings) =>
        [.. holdings.GroupBy(h => (h.Choice.Setting, h.Choice.Scope))
            .Select(g =>
            {
                var first = g.OrderBy(h => h.Choice.ChosenAt).ThenBy(h => h.Holder, StringComparer.Ordinal).First().Choice;
                var setting = PostureSetting.Find(first.Setting);
                return new PostureEntry(first.Setting, first.Scope, first.ChosenBy, first.ChosenAt, first.Note, [.. g.Select(h => h.Holder).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], setting?.Default, setting?.Effect);
            })
            .OrderBy(e => e.Scope, StringComparer.Ordinal).ThenBy(e => e.Setting, StringComparer.Ordinal)];
}
