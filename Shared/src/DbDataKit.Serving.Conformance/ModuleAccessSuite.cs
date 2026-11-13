using System.Globalization;
using System.Text;

namespace DbDataKit.Serving.Conformance;

/// <summary>
/// One thing a person can ask of a module (plan 22a GP14): a name and what it is, for the report. A question is answered with rows, so what a module gives a person is comparable with what the serving core gives them.
/// </summary>
public sealed record ConformanceQuestion(string Id, string Description);

/// <summary>What a module answered a person: the rows as text (every cell in invariant form, a NULL as <c>NULL</c>), or a refusal.</summary>
/// <param name="Rows">The rows, each one text of its cells joined by <c>|</c>; compared as a multiset, so a module that sorts differently is not a violation.</param>
/// <param name="Refused">The module refused the request (a denied, an unknown column, a rules table that is not served): refusals compare equal to each other whatever their words, because a module may put them in its own.</param>
public sealed record ModuleAnswer(IReadOnlyList<string> Rows, bool Refused = false, string? Reason = null)
{
    public static ModuleAnswer Of(IEnumerable<string> rows) => new([.. rows.Order(StringComparer.Ordinal)]);

    public static ModuleAnswer Refusal(string reason) => new([], true, reason);

    public bool SameAs(ModuleAnswer other) => Refused == other.Refused && Rows.SequenceEqual(other.Rows, StringComparer.Ordinal);

    public override string ToString() => Refused ? $"refused ({Reason})" : Rows.Count == 0 ? "no rows" : "[" + string.Join("; ", Rows) + "]";

    /// <summary>One cell as text in the form every module is compared in: invariant, no trailing zeros of a number, <c>NULL</c> for a missing value.</summary>
    public static string Cell(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        float f => f.ToString("0.####", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "NULL",
    };

    /// <summary>A row from its cells.</summary>
    public static string Row(params object?[] cells) => string.Join("|", cells.Select(Cell));
}

/// <summary>
/// A way to data that a person uses (Flight SQL, the semantic REST, the feeds, an app's data call, a dashboard, a subscription, an agent tool): the module answers a question for a named person the way its own caller would see it. The suite
/// holds each module to the answer the serving core gives the same person. A module says which questions it can ask; one it cannot ask is reported as not covered, never as passed.
/// </summary>
public interface IConformanceModule
{
    string Name { get; }

    /// <summary>Whether this module can ask the question at all (a dashboard has no members listing, a feed no explain).</summary>
    bool Asks(ConformanceQuestion question);

    Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default);
}

/// <summary>One answer that differs from the core's.</summary>
public sealed record ConformanceViolation(string Phase, string Module, string Person, string Question, ModuleAnswer Expected, ModuleAnswer Actual)
{
    public override string ToString() => $"[{Phase}] {Module}: {Person} asked '{Question}' and got {Actual}, the serving core gives {Expected}";
}

/// <summary>What a run found: the violations, how many answers were compared, and what no module asked.</summary>
public sealed class ConformanceReport
{
    private readonly List<ConformanceViolation> _violations = [];
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _contrast = new(StringComparer.Ordinal);

    public IReadOnlyList<ConformanceViolation> Violations => _violations;

    /// <summary>Answers compared with the core's.</summary>
    public int Checks { get; internal set; }

    /// <summary>The (module, question) pairs that were asked, as <c>module/question</c>.</summary>
    public IReadOnlyCollection<string> Asked => _asked;

    /// <summary>The questions for which every person got the same answer from the core: a run on those cannot tell a module that ignores the rules from one that applies them, and says so.</summary>
    public IReadOnlyList<string> WithoutContrast => [.. _contrast.Where(c => c.Value.Count < 2).Select(c => c.Key).Order(StringComparer.Ordinal)];

    public bool Passed => Violations.Count == 0 && WithoutContrast.Count == 0 && Checks > 0;

    internal void Violation(ConformanceViolation violation) => _violations.Add(violation);

    internal void Ask(string module, string question) => _asked.Add(module + "/" + question);

    internal void Reference(string question, ModuleAnswer answer)
    {
        if (!_contrast.TryGetValue(question, out var seen)) _contrast[question] = seen = new HashSet<string>(StringComparer.Ordinal);
        seen.Add(answer.ToString());
    }

    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine($"{Checks} answers compared with the serving core's, {Violations.Count} differ.");
        foreach (var violation in Violations) text.AppendLine("  " + violation);
        foreach (var question in WithoutContrast) text.AppendLine($"  question '{question}': every person gets the same answer from the core, so it cannot tell a module that applies the rules from one that does not");
        if (Checks == 0) text.AppendLine("  nothing was compared");
        return text.ToString();
    }
}

/// <summary>
/// The conformance suite of the access rules (plan 22a GP14): the same people and the same questions are put to the serving core and to every module that reaches data, and an answer that differs from the core's is a violation. The rules are the host's
/// (a model with row and column rules and a rules table); the suite is what holds every module to them. A phase is one state of the rules (before and after the rules table changed, a column rule added): the host changes the rules between runs and
/// calls <see cref="RunAsync"/> again. Each question is asked of each person twice, in opposite orders of the people, so an answer one person's request left in a cache shows when the next asks.
/// </summary>
public sealed class ModuleAccessSuite(IConformanceModule core, IReadOnlyList<IConformanceModule> modules, IReadOnlyList<string> people, IReadOnlyList<ConformanceQuestion> questions)
{
    public async Task<ConformanceReport> RunAsync(string phase, ConformanceReport? into = null, CancellationToken cancellationToken = default)
    {
        var report = into ?? new ConformanceReport();
        foreach (var question in questions)
        {
            foreach (var round in new[] { people, [.. people.Reverse()] })
            {
                foreach (var person in round)
                {
                    var expected = await Ask(core, person, question, cancellationToken).ConfigureAwait(false);
                    report.Reference(question.Id, expected);
                    foreach (var module in modules.Where(m => m.Asks(question)))
                    {
                        report.Ask(module.Name, question.Id);
                        var actual = await Ask(module, person, question, cancellationToken).ConfigureAwait(false);
                        report.Checks++;
                        if (!actual.SameAs(expected)) report.Violation(new ConformanceViolation(phase, module.Name, person, question.Id, expected, actual));
                    }
                }
            }
        }

        return report;
    }

    /// <summary>The (module, question) pairs no module of the suite asks, for the report of what is not covered.</summary>
    public IEnumerable<string> NotAsked() => modules.SelectMany(m => questions.Where(q => !m.Asks(q)).Select(q => m.Name + "/" + q.Id));

    private static async Task<ModuleAnswer> Ask(IConformanceModule module, string person, ConformanceQuestion question, CancellationToken cancellationToken)
    {
        try { return await module.AskAsync(person, question, cancellationToken).ConfigureAwait(false); }
        catch (ServingException e) when (e is not ServingBackendException) { return ModuleAnswer.Refusal(e.Message); }
    }
}
