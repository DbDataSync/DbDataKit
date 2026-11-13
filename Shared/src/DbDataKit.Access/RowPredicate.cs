namespace DbDataKit.Access;

/// <summary>
/// The row filter of a dataset as a boolean tree over its rules (plan 22a GP11, QG2, QR1): a host renders it in its own query language (a semi-join to the rules table per rule). It holds no value of a caller: only the names of the caller that appear in
/// a rules table, which the host binds as parameters.
/// </summary>
public abstract record RowPredicate
{
    /// <summary>Every row passes (a dataset with no rule for the caller's side of it).</summary>
    public sealed record Always : RowPredicate;

    /// <summary>No row passes: a rule the rules table has no row for the caller in, or a person with no matching rule (D2).</summary>
    public sealed record Never : RowPredicate;

    /// <summary>The row matches the rule: the rules table has a row for one of <see cref="Resolution"/>'s names whose mapped columns equal the row's values (<c>IS NOT DISTINCT FROM</c>).</summary>
    public sealed record Rule(RuleResolution Resolution) : RowPredicate;

    public sealed record And(IReadOnlyList<RowPredicate> Items) : RowPredicate;

    public sealed record Or(IReadOnlyList<RowPredicate> Items) : RowPredicate;

    public sealed record Not(RowPredicate Item) : RowPredicate;

    public static RowPredicate True { get; } = new Always();

    public static RowPredicate False { get; } = new Never();

    /// <summary>
    /// The predicate of a dataset: its groups combine by the dataset's <c>match</c>; in a group the include rules combine by the group's <c>match</c> and the exclude rules always subtract (a row is in the group when the includes say so and no exclude
    /// matches it, QR1). A rule the caller has no names in is <see cref="False"/>, so an include rule of a person with no row never holds and an exclude rule of one removes nothing; a caller with no matching rule sees no row (D2).
    /// </summary>
    public static RowPredicate Of(DatasetResolution dataset)
    {
        var groups = dataset.Groups.Select(Of).ToList();
        return dataset.Rules.Match == AccessMatch.All ? Conjunction(groups) : Disjunction(groups);
    }

    /// <summary>The predicate of one group (its includes combined by its <c>match</c>, its excludes subtracted): the explanation asks it for a group's verdict.</summary>
    public static RowPredicate Of(GroupResolution group)
    {
        RowPredicate Holds(RuleResolution rule) => rule.Names.Count == 0 ? False : new Rule(rule);
        var includes = group.Rules.Where(r => r.Rule.Effect == AccessEffect.Include).Select(Holds).ToList();
        var excludes = group.Rules.Where(r => r.Rule.Effect == AccessEffect.Exclude).Select(Holds).ToList();
        var included = group.Group.Match == AccessMatch.All ? Conjunction(includes) : Disjunction(includes);
        if (included is Never) return False;
        var removed = Disjunction(excludes);
        return removed is Never ? included : Conjunction([included, Negation(removed)]);
    }

    private static RowPredicate Negation(RowPredicate item) => item switch { Always => False, Never => True, Not not => not.Item, _ => new Not(item) };

    private static RowPredicate Conjunction(IReadOnlyList<RowPredicate> items)
    {
        var kept = new List<RowPredicate>();
        foreach (var item in items)
        {
            if (item is Never) return False;
            if (item is Always) continue;
            if (item is And inner) kept.AddRange(inner.Items); else kept.Add(item);
        }

        return kept.Count == 0 ? True : kept.Count == 1 ? kept[0] : new And(kept);
    }

    private static RowPredicate Disjunction(IReadOnlyList<RowPredicate> items)
    {
        var kept = new List<RowPredicate>();
        foreach (var item in items)
        {
            if (item is Always) return True;
            if (item is Never) continue;
            if (item is Or inner) kept.AddRange(inner.Items); else kept.Add(item);
        }

        return kept.Count == 0 ? False : kept.Count == 1 ? kept[0] : new Or(kept);
    }

    /// <summary>Whether a row passes, given which rules hold for it (<paramref name="holds"/>): the same tree a host renders as SQL, read in memory by <c>access explain</c>, so the explanation can never disagree with the enforcement about how rules combine.</summary>
    public bool Evaluate(Func<RuleResolution, bool> holds) => this switch
    {
        Always => true,
        Never => false,
        Rule rule => holds(rule.Resolution),
        And and => and.Items.All(i => i.Evaluate(holds)),
        Or or => or.Items.Any(i => i.Evaluate(holds)),
        Not not => !not.Item.Evaluate(holds),
        _ => false,
    };

    /// <summary>The rules the predicate still reads (the ones that can hold for the caller), for the cache and the explanation.</summary>
    public IEnumerable<RuleResolution> Rules() => this switch
    {
        Rule rule => [rule.Resolution],
        And and => and.Items.SelectMany(i => i.Rules()),
        Or or => or.Items.SelectMany(i => i.Rules()),
        Not not => not.Item.Rules(),
        _ => [],
    };
}
