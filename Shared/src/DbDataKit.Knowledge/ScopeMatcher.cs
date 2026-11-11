namespace DbDataKit.Knowledge;

/// <summary>
/// Something in play: a subject the work is on or has touched. <paramref name="Whole"/> says everything inside the subject counts as in play (a workspace on a Build project puts the whole project in play; a tool call that names the measure
/// <c>revenue</c> puts that measure in play and nothing else of its model).
/// </summary>
public sealed record InPlay(KnowledgeSubject Subject, bool Whole = false);

/// <summary>Who asks: the audience of the agent (<c>back-end</c> or <c>front-end</c>), its name and the channel it runs on. A null part is not asked about.</summary>
public sealed record ScopeAudience(string? Audience = null, string? Agent = null, string? Channel = null);

/// <summary>The bound knowledge for one turn: instructions and guardrails each from the broadest to the narrowest scope. Guardrails are a section of their own and a narrower instruction never replaces one.</summary>
public sealed record BoundKnowledge(IReadOnlyList<KnowledgeItem> Instructions, IReadOnlyList<KnowledgeItem> Guardrails)
{
    public static readonly BoundKnowledge Empty = new([], []);

    /// <summary>Every item given, instructions then guardrails.</summary>
    public IEnumerable<KnowledgeItem> All => [.. Instructions, .. Guardrails];
}

/// <summary>
/// Scope matching (plan 27a KN03, plan 27 sections 3.5 and 3.5a): which instructions and guardrails apply to what is in play. An item applies when something in play is one of its subjects or is inside it, and the audience matches; references are never
/// given by scope (they are found by search). Items are ordered install, product, project, folder, model, member, and within a level by their path, so the order is the same every time. Orphaned items reach nobody.
/// </summary>
public static class ScopeMatcher
{
    /// <param name="items">Every item of the knowledge the agent can draw on.</param>
    /// <param name="inPlay">What is in play now.</param>
    /// <param name="audience">Who asks.</param>
    /// <param name="catalog">When given, orphaned items (their subjects all gone) are left out.</param>
    public static BoundKnowledge Match(IEnumerable<KnowledgeItem> items, IReadOnlyList<InPlay> inPlay, ScopeAudience audience, ISubjectCatalog? catalog = null)
    {
        var matched = new List<(KnowledgeItem Item, int Rank)>();
        foreach (var item in items)
        {
            if (item.Role == KnowledgeRole.Reference) continue;
            if (!item.For.Includes(audience.Audience, audience.Agent, audience.Channel)) continue;
            if (catalog is not null && SubjectResolver.IsOrphaned(item, catalog)) continue;
            if (Rank(item, inPlay) is { } rank) matched.Add((item, rank));
        }
        var ordered = matched.OrderBy(m => m.Rank).ThenBy(m => m.Item.Id, StringComparer.Ordinal).Select(m => m.Item).ToList();
        return new BoundKnowledge([.. ordered.Where(i => i.Role == KnowledgeRole.Instruction)], [.. ordered.Where(i => i.Role == KnowledgeRole.Guardrail)]);
    }

    /// <summary>The level of an item for what is in play (install 0 to member 5), or null when it does not apply by its subjects.</summary>
    public static int? RankOf(KnowledgeItem item, IReadOnlyList<InPlay> inPlay) => Rank(item, inPlay);

    /// <summary>The level of an item that applies: the narrowest of the subjects that matched; null when none does. An item with no subject is about nothing in particular and applies everywhere its audience does, at the broadest level.</summary>
    internal static int? Rank(KnowledgeItem item, IReadOnlyList<InPlay> inPlay)
    {
        if (item.About.Count == 0) return 0;
        int? best = null;
        foreach (var subject in item.About)
            if (Covers(subject, inPlay)) best = Math.Max(best ?? 0, LevelOf(subject));
        return best;
    }

    /// <summary>Install 0, product 1, a tag or property or a whole project 2, a folder 3, a model or other whole object 4, a member 5.</summary>
    internal static int LevelOf(KnowledgeSubject subject) => subject.Type switch
    {
        "install" => 0,
        "product" => 1,
        "tag" or "property" => 2,
        "build-project" => subject.NarrowingKind switch { "" => 2, "folder" => 3, _ => 4 },
        _ => subject.NarrowingKind.Length > 0 ? 5 : 4,
    };

    /// <summary>Whether something in play is the subject or inside it.</summary>
    internal static bool Covers(KnowledgeSubject subject, IReadOnlyList<InPlay> inPlay)
    {
        if (subject.Type == "install") return true;
        foreach (var play in inPlay)
        {
            var other = play.Subject;
            if (other.Type != subject.Type) continue;
            if (subject.Type == "product")
            {
                if (subject.Names.Intersect(other.Names, StringComparer.Ordinal).Any()) return true;
                continue;
            }
            if (!string.Equals(subject.Name, other.Name, StringComparison.Ordinal)) continue;
            if (subject.NarrowingKind.Length == 0) return true;                       // the whole object: anything in it is inside it
            if (play.Whole) return true;                                              // the object and all it holds is in play
            if (subject.NarrowingKind == other.NarrowingKind && Inside(subject.NarrowingKind, subject.NarrowingName, other.NarrowingName)) return true;
        }
        return false;
    }

    // a folder holds the folders beneath it; every other narrowing is a name that must be the same
    private static bool Inside(string kind, string wanted, string actual) => kind == "folder"
        ? string.Equals(wanted.Trim('/'), actual.Trim('/'), StringComparison.Ordinal) || actual.Trim('/').StartsWith(wanted.Trim('/') + "/", StringComparison.Ordinal)
        : string.Equals(wanted, actual, StringComparison.Ordinal);
}
