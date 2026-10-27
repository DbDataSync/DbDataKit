namespace DbDataKit.Semantic;

/// <summary>
/// The grouping sets of a totals request, as the positions in the request's <c>rollup</c> list that each set has rolled up, and the
/// level number each gets. A chain (ROLLUP) has one set for each count of innermost attributes rolled up, so level k is "k rolled up". A cube (CUBE)
/// has every subset, numbered by how many are rolled up and then by position: with two attributes, 0 is detail, 1 rolls up the first, 2 rolls up the
/// second, 3 both. The numbering is shared by the engine's own sets and by the SQL and DAX the sources are asked for, so a level means the same
/// everywhere; the descriptor's level map says what each one is.
/// </summary>
public static class RollupSets
{
    /// <summary>Sets in level order: the index is the level, the array holds the rolled-up positions.</summary>
    public static List<int[]> Subsets(int n, bool cube)
    {
        if (!cube) return Enumerable.Range(0, n + 1).Select(k => Enumerable.Range(n - k, k).ToArray()).ToList();
        return Enumerable.Range(0, 1 << n)
            .Select(mask => Enumerable.Range(0, n).Where(p => (mask >> p & 1) == 1).ToArray())
            .OrderBy(p => p.Length).ThenBy(p => string.Join(",", p.Select(x => x.ToString("D2")))).ToList();
    }

    /// <summary>The value of the GROUPING bits of a set: position 0 is the highest bit.</summary>
    public static int Mask(int[] rolledUp, int n) => rolledUp.Sum(p => 1 << (n - 1 - p));

    /// <summary>The widest cube the engine will ask for: it has 2 to the power n sets.</summary>
    public const int MaxCubeWidth = 5;
}
