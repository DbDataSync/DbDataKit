namespace DbDataBuild.Core;

/// <summary>
/// Which of the tool's rewrites are switched off for one model (DESIGN.md 7.6.2). A rewrite is a step that makes an engine give DuckDB's answer where it would otherwise give its own (a trailing
/// space counted, an integer cut instead of rounded, a week counted another way); a project, or a model, can ask for the engine's own behavior to get a shorter query and a faster plan.
/// The set holds the names from <c>RewriteCatalog</c>; a rewrite an engine cannot do without is never in it.
/// </summary>
public sealed class RewritePolicy
{
    public static readonly RewritePolicy Exact = new(new HashSet<string>(StringComparer.Ordinal));

    private readonly HashSet<string> disabled;

    public RewritePolicy(IEnumerable<string> disabledRewrites) => disabled = new HashSet<string>(disabledRewrites, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Disabled => disabled;

    public bool IsDefault => disabled.Count == 0;

    /// <summary>True when the rewrite is to be applied.</summary>
    public bool Allows(string rewrite) => !disabled.Contains(rewrite);

    public string Describe() => IsDefault ? "exact" : string.Join(", ", disabled.Order(StringComparer.Ordinal));
}
