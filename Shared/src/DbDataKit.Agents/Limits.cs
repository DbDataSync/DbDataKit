using System.Globalization;

namespace DbDataKit.Agents;

/// <summary>The most one scope may use: model turns, tool calls, tokens and cost. A null part is not limited (plan 27a AG08).</summary>
public sealed record LimitSet(int? Turns = null, int? ToolCalls = null, long? Tokens = null, decimal? Cost = null)
{
    public static readonly LimitSet None = new();

    public bool IsNone => Turns is null && ToolCalls is null && Tokens is null && Cost is null;
}

/// <summary>The price of a model on its connection (plan 27 section 3.7), per million tokens in each direction. Cost is the provider's reported usage times this.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    public decimal CostOf(long inputTokens, long outputTokens) => (inputTokens * InputPerMillion + outputTokens * OutputPerMillion) / 1_000_000m;
}

/// <summary>What a person or the install has used: the sum of finished runs and, while one runs, that run's own so far.</summary>
public sealed record UsageTotals(int Turns, int ToolCalls, long Tokens, decimal Cost)
{
    public static readonly UsageTotals Zero = new(0, 0, 0, 0m);

    public UsageTotals Plus(RunUsage usage, ModelPrice? price) =>
        new(Turns + usage.ModelTurns, ToolCalls + usage.ToolCalls, Tokens + usage.Tokens, Cost + (price?.CostOf(usage.InputTokens, usage.OutputTokens) ?? 0m));
}

/// <summary>What finished runs used, by person and for the install (the <c>agents</c> module keeps it, AG06; memory here).</summary>
public interface IUsageLedger
{
    UsageTotals Person(string person);

    UsageTotals Install();

    /// <summary>Adds the usage of a finished run.</summary>
    void Add(string person, UsageTotals usage);
}

public sealed class InMemoryUsageLedger : IUsageLedger
{
    private readonly Dictionary<string, UsageTotals> _people = new(StringComparer.Ordinal);
    private UsageTotals _install = UsageTotals.Zero;

    public UsageTotals Person(string person) { lock (_people) return _people.GetValueOrDefault(person, UsageTotals.Zero); }

    public UsageTotals Install() { lock (_people) return _install; }

    public void Add(string person, UsageTotals usage)
    {
        lock (_people)
        {
            var before = _people.GetValueOrDefault(person, UsageTotals.Zero);
            _people[person] = new UsageTotals(before.Turns + usage.Turns, before.ToolCalls + usage.ToolCalls, before.Tokens + usage.Tokens, before.Cost + usage.Cost);
            _install = new UsageTotals(_install.Turns + usage.Turns, _install.ToolCalls + usage.ToolCalls, _install.Tokens + usage.Tokens, _install.Cost + usage.Cost);
        }
    }
}

/// <summary>Where administrators are told that a limit is nearly reached (the notification feed, plan 29; a test collects them).</summary>
public interface ILimitNotifier
{
    void Notify(string message);
}

public sealed class CollectingNotifier : ILimitNotifier
{
    public List<string> Messages { get; } = [];

    public void Notify(string message) => Messages.Add(message);
}

/// <summary>
/// Limits per conversation, per person and per install (plan 27a AG08, plan 27 section 3.9) on turns, tool calls, tokens and cost. <see cref="Reached"/> is asked before each model turn and each tool call; a limit that is reached stops the run with a message
/// that says which, and administrators are told once when a scope passes the threshold. The person's and the install's totals are what finished runs used (the ledger) plus the run in hand; <see cref="Commit"/> adds a run to the ledger when it ends.
/// </summary>
public sealed class RunLimits(LimitSet conversation, LimitSet person, LimitSet install, ModelPrice? price, IUsageLedger ledger, ILimitNotifier? notifier = null, int notifyAtPercent = 80) : IRunLimits
{
    private readonly HashSet<string> _told = new(StringComparer.Ordinal);

    public string? Reached(RunUsage usage, AgentRunContext context)
    {
        var mine = UsageTotals.Zero.Plus(usage, price);
        string? reached = null;
        foreach (var (scope, phrase, limits, used) in new[]
        {
            ("This conversation", "its limit", conversation, mine),
            (context.Person, "the per-person limit", person, ledger.Person(context.Person).Plus(usage, price)),
            ("This install", "its limit", install, ledger.Install().Plus(usage, price)),
        })
        {
            foreach (var (name, limit, now, unit) in Metrics(limits, used))
            {
                if (limit is null) continue;
                if (now >= limit.Value) reached ??= $"{scope} reached {phrase} of {Format(limit.Value)} {unit} (used {Format(now)}). The run stopped.";
                else if (notifier is not null && now * 100 >= limit.Value * notifyAtPercent && _told.Add($"{scope}/{name}/{context.ConversationId}"))
                    notifier.Notify($"{scope.ToLowerInvariant()} has used {Format(now)} of its {Format(limit.Value)} {unit} ({context.Agent}, {context.Person}).");
            }
        }
        return reached;
    }

    /// <summary>Adds a finished run to the person's and the install's totals.</summary>
    public void Commit(AgentRunContext context, RunUsage usage) => ledger.Add(context.Person, UsageTotals.Zero.Plus(usage, price));

    private static IEnumerable<(string Name, decimal? Limit, decimal Now, string Unit)> Metrics(LimitSet limits, UsageTotals used)
    {
        yield return ("turns", limits.Turns, used.Turns, "model turns");
        yield return ("tool-calls", limits.ToolCalls, used.ToolCalls, "tool calls");
        yield return ("tokens", limits.Tokens, used.Tokens, "tokens");
        yield return ("cost", limits.Cost, used.Cost, "in cost");
    }

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
