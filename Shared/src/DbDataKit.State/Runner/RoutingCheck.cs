namespace DbDataKit.State;

/// <summary>A live instance of a runner group with what routing needs: its labels (the administrator's and the reported ones) and its capabilities.</summary>
public sealed record LiveInstance(RunnerInstance Instance, IReadOnlySet<string> Labels, IReadOnlyDictionary<string, string> Capabilities);

/// <summary>How a label is written: <c>name:value</c> with no spaces around the colon (a manifest's <c>network: outside</c> and <c>network:outside</c> are one label).</summary>
public static class RunnerLabels
{
    /// <summary>The capabilities that are also labels (plan 28a R12): the runner reports them about itself, an administrator cannot change them.</summary>
    private static readonly string[] CapabilityLabels = ["network", "os", "memory_limit", "cpu_limit"];

    /// <summary>Trims the label and removes the spaces around a colon.</summary>
    public static string Normalize(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var text = label.Trim();
        var colon = text.IndexOf(':');
        return colon < 0 ? text : text[..colon].Trim() + ":" + text[(colon + 1)..].Trim();
    }

    /// <summary>The labels a map of capabilities (<c>network</c> = <c>outside</c>, <c>os</c> = <c>linux</c>, <c>memory_limit</c> = <c>enforced</c> or <c>watched</c>) stands for: <c>network:outside</c>, <c>os:linux</c>, <c>memory_limit:watched</c>.</summary>
    public static IReadOnlyList<string> FromCapabilities(IReadOnlyDictionary<string, string> capabilities) =>
        [.. CapabilityLabels.Where(capabilities.ContainsKey).Where(n => !string.IsNullOrWhiteSpace(capabilities[n])).Select(n => $"{n}:{capabilities[n].Trim()}"),
            // an installed ODBC driver is the label odbc:<id> (plan 28c QE12): the capability's name is the label, its value the driver's own name
            .. capabilities.Where(c => c.Key.StartsWith(OdbcLabelPrefix, StringComparison.Ordinal) && c.Key.Length > OdbcLabelPrefix.Length && !string.IsNullOrWhiteSpace(c.Value)).Select(c => c.Key).Order(StringComparer.Ordinal)];

    /// <summary>The prefix of the capabilities (and labels) that name an installed ODBC driver (<c>odbc:sqlserver18</c>).</summary>
    public const string OdbcLabelPrefix = "odbc:";
}

/// <summary>
/// Routing by label (plan 28 question 7, plan 28a R12): a task requires labels, a runner serves it only when it has every one. <see cref="CanActivate"/> is what staging (R17) asks before a version is activated: a version whose required
/// label no live runner has does not activate, and the answer names the label.
/// </summary>
public static class RoutingCheck
{
    /// <summary>The labels of <paramref name="requires"/> that <paramref name="have"/> lacks, in the order required.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string> requires, IReadOnlySet<string> have) =>
        [.. requires.Select(RunnerLabels.Normalize).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).Where(l => !have.Contains(l))];

    /// <summary>Whether a runner with <paramref name="have"/> may take a task that requires <paramref name="requires"/>.</summary>
    public static bool Serves(IEnumerable<string> requires, IReadOnlySet<string> have) => Missing(requires, have).Count == 0;

    /// <summary>
    /// What no live runner can provide: empty when some live instance has every required label (the version may activate). Otherwise the labels the closest instance lacks (for a label no runner has at all, that label is always among them);
    /// with no live instance, every required label.
    /// </summary>
    public static IReadOnlyList<string> CanActivate(IEnumerable<string> requires, IReadOnlyCollection<LiveInstance> liveInstances)
    {
        var wanted = requires.Select(RunnerLabels.Normalize).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return [];
        IReadOnlyList<string>? best = null;
        foreach (var instance in liveInstances)
        {
            var missing = Missing(wanted, instance.Labels);
            if (missing.Count == 0) return [];
            if (best is null || missing.Count < best.Count) best = missing;
        }

        return best ?? wanted;
    }

    /// <summary>The sentence for a missing label list: which label no live runner has, and how to fix it.</summary>
    public static string Reason(IReadOnlyList<string> missing, int liveRunners) =>
        missing.Count == 0 ? ""
        : liveRunners == 0 ? $"No runner is live; the task requires {Quote(missing)}."
        : $"No live runner has the label {Quote(missing)}; add it to a runner (dbdatarunner label add) or start one that reports it.";

    /// <summary>The refusal message of an activation that <see cref="CanActivate"/> turned down.</summary>
    public static string ActivationRefusal(string package, string version, IReadOnlyList<string> missing) =>
        $"Version {version} of '{package}' is not activated: it requires {Quote(missing)} and no live runner has {(missing.Count == 1 ? "that label" : "those labels")}. Add the label to a runner (dbdatarunner label add) or start a runner that reports it, then activate again.";

    private static string Quote(IReadOnlyList<string> labels) => string.Join(", ", labels.Select(l => $"'{l}'"));
}
