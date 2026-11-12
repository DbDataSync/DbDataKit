using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DbDataKit.Agents.Evaluation;

/// <summary>A tool the agent called in a case, with what it was given and what came back (the rows of a query are in the result).</summary>
public sealed record RecordedToolCall(string Tool, string Arguments, string Result, bool IsError);

/// <summary>What running one case gave: the answer, the tools called, what it cost and what the agent was given (the model, the knowledge items and their versions).</summary>
public sealed record EvaluationAgentRun(string Answer, IReadOnlyList<RecordedToolCall> ToolCalls, long InputTokens, long OutputTokens, decimal Cost, string? Model, IReadOnlyList<string> KnowledgeVersions, string? Problem = null);

/// <summary>What the judge model thought of an answer against a written rubric: a score from 0 to 1 and the reasons, which are shown as a judgement and never as a measurement.</summary>
public sealed record JudgeVerdict(double Score, string Reasoning);

/// <summary>Rows of a query, as the suite compares them.</summary>
public sealed record RowSet(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>What the evaluation asks of its host (the console, a runner task): run the agent on a case against the channel's bindings, and the three things only the host can do. A piece the host does not have is a default that says so.</summary>
public interface IEvaluationHost
{
    Task<EvaluationAgentRun> RunAsync(EvaluationCase evaluation, CancellationToken cancellation);

    /// <summary>The judge model's verdict on an answer against a rubric; null when no judge model is bound (the rubric is then not judged, and the run says so).</summary>
    Task<JudgeVerdict?> JudgeAsync(string rubric, EvaluationCase evaluation, string answer, CancellationToken cancellation) => Task.FromResult<JudgeVerdict?>(null);

    /// <summary>A check of the host's by name (the workspace compiles and its tests pass); null when the host does not know it.</summary>
    Task<bool?> CheckAsync(string name, EvaluationCase evaluation, EvaluationAgentRun run, CancellationToken cancellation) => Task.FromResult<bool?>(null);

    /// <summary>The rows of a verified query run as the evaluation's own account (the agent's query is run as that account too), or null when there is no such query.</summary>
    Task<RowSet?> VerifiedRowsAsync(string verifiedQueryId, CancellationToken cancellation) => Task.FromResult<RowSet?>(null);
}

/// <summary>The result of one check: how it was decided (<see cref="Method"/> is <c>suite</c> or <c>judge</c>), its score (null when it could not be decided), and the words.</summary>
public sealed record CheckResult(string Kind, string Detail, string Method, double? Score, string Message, string? Judgement = null)
{
    public bool? Passed => Score is null ? null : Score >= 1.0 - 1e-9;
}

public sealed record CaseResult(string Case, string Task, double Score, bool Passed, IReadOnlyList<CheckResult> Checks, string Answer, long InputTokens, long OutputTokens, decimal Cost, string? Problem);

/// <summary>A run of an agent's set: which agent at which version on which channel, with which model and which knowledge, the score of every case and the cost.</summary>
public sealed record EvaluationRunResult(string Id, string Agent, string AgentVersion, string Channel, string? Model, IReadOnlyList<string> KnowledgeVersions, string Trigger, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    double Score, int Passed, int Total, decimal Cost, IReadOnlyList<CaseResult> Cases, int NotJudged);

/// <summary>
/// Evaluation sets (plan 27a AG23): each case is run on the agent, then checked. <b>The suite decides what it can</b>: the tools called, a text in the answer, the rows of the agent's query against the rows of a verified query, a check the host
/// knows by name. <b>A judge model reads only what cannot be decided that way</b>, a rubric of free text, and what it says is kept as a judgement (its score and its reasons) apart from the measurements; with no judge model bound the rubric is not
/// judged, left out of the score, and the run counts how many were left out. A case that cannot be run scores 0 with the reason, and the set goes on. The run is a record, never a gate of a pull request.
/// </summary>
public static class EvaluationRunner
{
    public static async Task<EvaluationRunResult> RunAsync(IReadOnlyList<EvaluationCase> cases, IEvaluationHost host, string agent, string agentVersion, string channel, string trigger = "manual", TimeProvider? time = null, CancellationToken cancellation = default)
    {
        time ??= TimeProvider.System;
        var started = time.GetUtcNow();
        var results = new List<CaseResult>();
        string? model = null;
        var knowledge = new SortedSet<string>(StringComparer.Ordinal);
        var notJudged = 0;
        foreach (var evaluation in cases)
        {
            cancellation.ThrowIfCancellationRequested();
            EvaluationAgentRun run;
            try { run = await host.RunAsync(evaluation, cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new CaseResult(evaluation.Name, evaluation.Task, 0, false, [], "", 0, 0, 0, $"The agent could not be run on this case: {ex.Message}"));
                continue;
            }
            model ??= run.Model;
            foreach (var k in run.KnowledgeVersions) knowledge.Add(k);
            var checks = new List<CheckResult>();
            foreach (var check in evaluation.Expect) checks.Add(await CheckOne(check, evaluation, run, host, cancellation).ConfigureAwait(false));
            notJudged += checks.Count(c => c.Method == "judge" && c.Score is null);
            var counted = checks.Where(c => c.Score is not null).ToList();
            var score = counted.Count == 0 ? 0 : counted.Average(c => c.Score!.Value);
            var problem = run.Problem ?? (counted.Count == 0 ? "Nothing could be checked: every check needed something this host does not have." : null);
            results.Add(new CaseResult(evaluation.Name, evaluation.Task, Math.Round(score, 4), counted.Count > 0 && run.Problem is null && score >= evaluation.PassAt - 1e-9, checks, run.Answer, run.InputTokens, run.OutputTokens, run.Cost, problem));
        }
        var total = results.Count;
        var average = total == 0 ? 0 : results.Average(r => r.Score);
        return new EvaluationRunResult(Guid.NewGuid().ToString("N"), agent, agentVersion, channel, model, [.. knowledge], trigger, started, time.GetUtcNow(), Math.Round(average, 4), results.Count(r => r.Passed), total, results.Sum(r => r.Cost), results, notJudged);
    }

    private static async Task<CheckResult> CheckOne(EvaluationCheck check, EvaluationCase evaluation, EvaluationAgentRun run, IEvaluationHost host, CancellationToken cancellation)
    {
        CheckResult Suite(bool ok, string message) => new(check.Kind, check.Describe(), "suite", ok ? 1 : 0, message);
        var called = run.ToolCalls.Where(c => !c.IsError).Select(c => c.Tool).ToHashSet(StringComparer.Ordinal);
        switch (check.Kind)
        {
            case "tools-called":
                var missing = check.Values.Where(v => !called.Contains(v)).ToList();
                return Suite(missing.Count == 0, missing.Count == 0 ? "All were called." : $"Not called: {string.Join(", ", missing)}.");
            case "tools-not-called":
                var wrong = check.Values.Where(v => run.ToolCalls.Any(c => c.Tool == v)).ToList();
                return Suite(wrong.Count == 0, wrong.Count == 0 ? "None was called." : $"Called, and should not have been: {string.Join(", ", wrong)}.");
            case "answer-contains":
                var has = run.Answer.Contains(check.Text!, StringComparison.OrdinalIgnoreCase);
                return Suite(has, has ? "The answer says it." : "The answer does not say it.");
            case "answer-matches":
                var matches = Regex.IsMatch(run.Answer, check.Text!, RegexOptions.None, TimeSpan.FromSeconds(1));
                return Suite(matches, matches ? "The answer matches." : "The answer does not match.");
            case "rows-match-verified-query":
                return await RowsAsync(check, run, host, cancellation).ConfigureAwait(false);
            case "check":
                return await host.CheckAsync(check.Text!, evaluation, run, cancellation).ConfigureAwait(false) is { } ok
                    ? Suite(ok, ok ? "The check holds." : "The check does not hold.")
                    : new CheckResult(check.Kind, check.Describe(), "suite", null, $"This host has no check named '{check.Text}'.");
            default:
                var verdict = await host.JudgeAsync(check.Text!, evaluation, run.Answer, cancellation).ConfigureAwait(false);
                return verdict is null
                    ? new CheckResult(check.Kind, check.Describe(), "judge", null, "No judge model is bound, so the rubric was not judged.")
                    : new CheckResult(check.Kind, check.Describe(), "judge", Math.Clamp(verdict.Score, 0, 1), $"The judge model scored it {Math.Clamp(verdict.Score, 0, 1):0.##}.", verdict.Reasoning);
        }
    }

    private static async Task<CheckResult> RowsAsync(EvaluationCheck check, EvaluationAgentRun run, IEvaluationHost host, CancellationToken cancellation)
    {
        CheckResult Result(double? score, string message) => new(check.Kind, check.Describe(), "suite", score, message);
        var expected = await host.VerifiedRowsAsync(check.Text!, cancellation).ConfigureAwait(false);
        if (expected is null) return Result(null, $"There is no verified query '{check.Text}' to compare with.");
        var theirs = run.ToolCalls.Where(c => !c.IsError).Select(c => RowSets.FromToolResult(c.Result)).LastOrDefault(r => r is not null);
        if (theirs is null) return Result(0, "The agent ran no query whose rows can be compared.");
        var difference = RowSets.Difference(expected, theirs);
        return Result(difference is null ? 1 : 0, difference ?? "The agent's rows are the verified query's rows.");
    }
}

/// <summary>Comparing the rows two queries returned: the same columns by name and the same rows, whatever the order and the way a number is written.</summary>
public static class RowSets
{
    /// <summary>The rows in the JSON document a command answered (<c>data.columns</c> and <c>data.rows</c>, or those at the top), or null when the text is not one.</summary>
    public static RowSet? FromToolResult(string text)
    {
        try
        {
            var start = text.IndexOf('{', StringComparison.Ordinal);
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            var root = JsonNode.Parse(text[start..(end + 1)]);
            var data = root?["data"] is JsonObject d ? d : root as JsonObject;
            if (data?["columns"] is not JsonArray columns || data["rows"] is not JsonArray rows) return null;
            var names = columns.Select(c => c is JsonValue v && v.TryGetValue<string>(out var s) ? s : c?["name"]?.GetValue<string>() ?? "").ToList();
            var list = new List<IReadOnlyList<object?>>();
            foreach (var row in rows)
                if (row is JsonArray cells) list.Add([.. cells.Select(Cell)]);
            return new RowSet(names, list);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    private static object? Cell(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => node.ToJsonString(),
    };

    private static string Normal(object? value) => value switch
    {
        null => "\u0000null",
        double d => "n:" + Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture),
        float f => "n:" + Math.Round(f, 6).ToString("R", CultureInfo.InvariantCulture),
        decimal m => "n:" + Math.Round((double)m, 6).ToString("R", CultureInfo.InvariantCulture),
        int or long or short or byte => "n:" + Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        bool b => "b:" + b,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && s.Trim().Length > 0 && !s.Any(char.IsLetter) => "n:" + Math.Round(number, 6).ToString("R", CultureInfo.InvariantCulture),
        string s => "s:" + s.Trim(),
        _ => "s:" + Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>What is different between two row sets, in words, or null when they are the same: the columns by name (in any order) and the rows as a multiset.</summary>
    public static string? Difference(RowSet expected, RowSet actual)
    {
        var want = expected.Columns.Select(c => c.Trim().ToLowerInvariant()).ToList();
        var have = actual.Columns.Select(c => c.Trim().ToLowerInvariant()).ToList();
        var missing = want.Where(c => !have.Contains(c)).ToList();
        var extra = have.Where(c => !want.Contains(c)).ToList();
        if (missing.Count > 0 || extra.Count > 0)
            return $"The columns differ{(missing.Count > 0 ? $"; missing: {string.Join(", ", missing)}" : "")}{(extra.Count > 0 ? $"; extra: {string.Join(", ", extra)}" : "")}.";
        // the agent's columns are put in the verified query's order, then the rows are compared as a multiset (a query without an order says nothing about it)
        var order = want.Select(c => have.IndexOf(c)).ToList();
        string Key(IReadOnlyList<object?> row, IReadOnlyList<int>? permutation) => string.Join("\u001f", (permutation ?? Enumerable.Range(0, row.Count).ToList()).Select(i => Normal(i < row.Count ? row[i] : null)));
        var wanted = expected.Rows.Select(r => Key(r, null)).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var got = actual.Rows.Select(r => Key(r, order)).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var wrong = wanted.Sum(w => Math.Max(0, w.Value - got.GetValueOrDefault(w.Key))) + got.Sum(g => Math.Max(0, g.Value - wanted.GetValueOrDefault(g.Key)));
        if (expected.Rows.Count != actual.Rows.Count) return $"The agent's query returned {actual.Rows.Count} rows and the verified one {expected.Rows.Count}.";
        return wrong == 0 ? null : $"{wrong / 2} of the {expected.Rows.Count} rows differ.";
    }
}

/// <summary>The minimum score before a promotion, as a recorded choice (plan 27a AG23): a promotion of an agent version whose last run on the channel scored below it, or that has no run, is refused in words that name the score.</summary>
public static class EvaluationGate
{
    /// <summary>The refusal, or null when the promotion may go on (no minimum is set, or the version's latest run on the channel reached it).</summary>
    public static string? Refusal(string agent, string agentVersion, string channel, decimal? minimum, decimal? latestScore, int? latestPassed = null, int? latestTotal = null)
    {
        if (minimum is not { } min) return null;
        if (latestScore is not { } score)
            return $"{agent} {agentVersion} cannot be promoted to {channel}: a minimum evaluation score of {min:0.00} is set for it and this version has not been evaluated there. Run its evaluation set first.";
        return score + 0.00005m >= min ? null
            : $"{agent} {agentVersion} cannot be promoted to {channel}: its last evaluation scored {score:0.00}{(latestTotal is { } t && latestPassed is { } p ? $" ({p} of {t} cases passed)" : "")}, below the minimum of {min:0.00} set for {channel}.";
    }
}
