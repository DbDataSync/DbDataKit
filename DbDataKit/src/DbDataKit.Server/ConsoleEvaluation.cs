using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Evaluation;
using DbDataKit.Knowledge;
using DbDataKit.State;
using Microsoft.Extensions.AI;

namespace DbDataKit.Server;

/// <summary>A case of an agent's set as the screen lists it.</summary>
public sealed record EvaluationCaseListing(string Name, string File, string Task, IReadOnlyList<string> Checks, bool HasRubric, double PassAt);

/// <summary>A run that is going on: when it started, how many cases there are and how many are done.</summary>
public sealed record RunningEvaluation(DateTimeOffset StartedAt, int Total, int Done, string Trigger);

/// <summary>
/// The evaluation sets of the console's back-end agents (plan 27a AG23, AG24): the cases are the files <c>agents/&lt;name&gt;/tests/evaluation/*.yml</c>, a run puts each case to the agent as a conversation of its own (the agent's own tools, which only
/// read; the model its slot is bound to) and checks it with <see cref="EvaluationRunner"/>, and the run is kept in the <c>agents</c> module with the agent version, the knowledge versions it was given, the model, the score per case and the cost.
/// <b>It is a record, never a gate of a pull request.</b> A judge model is the slot <c>judge</c> (its own, not the agent's): with none bound a rubric is not judged and the run says so. The console has no semantic session of its own, so a case that
/// compares rows with a verified query is not decided here (it says there is nothing to compare with); a serving host that has one decides it.
/// </summary>
public sealed class ConsoleEvaluation(ConsoleAgents agents, TimeProvider? time = null)
{
    /// <summary>The console's one channel.</summary>
    public const string Channel = "main";

    /// <summary>What a case may take before it is called stuck (a step that waits for a person nobody is there to ask).</summary>
    public static readonly TimeSpan CaseTimeout = TimeSpan.FromMinutes(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (RunningEvaluation Info, Task Task)> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _done = new(StringComparer.Ordinal);

    public string CasesFolder(string agent) => Path.Combine(agents.AgentsFolder, agent, "tests", "evaluation");

    public bool JudgeBound => agents.Models.For("judge", null) is not null;

    /// <summary>The cases of an agent, read strictly, and what is wrong with the files that do not read.</summary>
    public (IReadOnlyList<EvaluationCase> Cases, IReadOnlyList<DiagnosticView> Diagnostics) Cases(string agent)
    {
        var folder = CasesFolder(agent);
        var cases = new List<EvaluationCase>();
        var diagnostics = new List<DiagnosticView>();
        if (!Directory.Exists(folder)) return (cases, diagnostics);
        foreach (var path in Directory.EnumerateFiles(folder, "*.yml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = $"agents/{agent}/tests/evaluation/{Path.GetFileName(path)}";
            var result = EvaluationCaseLoader.Load(File.ReadAllText(path), relative);
            diagnostics.AddRange(result.Diagnostics.Select(DiagnosticView.Of));
            if (result.Ok) cases.Add(result.Case!);
        }
        return (cases, diagnostics);
    }

    public static IReadOnlyList<EvaluationCaseListing> Listing(IEnumerable<EvaluationCase> cases) =>
        [.. cases.Select(c => new EvaluationCaseListing(c.Name, c.File, c.Task, [.. c.Expect.Select(e => e.Describe())], c.Expect.Any(e => e.IsJudged), c.PassAt))];

    /// <summary>The version of the agent the next run would evaluate: the hash of its definition, as a conversation records it.</summary>
    public string? VersionOf(string agent) => agents.Read(agent, withReports: false) is { } detail ? KnowledgeItemLoader.HashOf(detail.Yaml)[..12] : null;

    public RunningEvaluation? Running(string agent) => _running.TryGetValue(agent, out var r) && !r.Task.IsCompleted ? r.Info with { Done = _done.GetValueOrDefault(agent) } : null;

    public Task WhenIdle(string agent) => _running.TryGetValue(agent, out var r) ? r.Task : Task.CompletedTask;

    /// <summary>Starts a run of the agent's set in the background; false and why when it cannot (no such agent, no case, no model bound, one already running).</summary>
    public (bool Started, string? Problem) Start(string agent, string trigger = "manual")
    {
        if (agents.Read(agent, withReports: false) is null) return (false, $"There is no agent named '{agent}'.");
        var (cases, diagnostics) = Cases(agent);
        if (diagnostics.Any(d => d.Severity == "error")) return (false, $"A case does not read ({diagnostics.First(d => d.Severity == "error").Message}). Fix it first.");
        if (cases.Count == 0) return (false, $"{agent} has no evaluation case: put files in agents/{agent}/tests/evaluation/.");
        var version = VersionOf(agent)!;
        if (Running(agent) is not null) return (false, $"{agent} is being evaluated now.");
        var info = new RunningEvaluation(_time.GetUtcNow(), cases.Count, 0, trigger);
        _done[agent] = 0;
        var task = Task.Run(async () =>
        {
            var host = new Host(agents, agent, () => _done.AddOrUpdate(agent, 1, (_, n) => n + 1));
            var result = await EvaluationRunner.RunAsync(cases, host, agent, version, Channel, trigger, _time).ConfigureAwait(false);
            agents.Evaluations.Save(Row(result));
        });
        _running[agent] = (info, task);
        return (true, null);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static EvaluationRunRow Row(EvaluationRunResult run) =>
        new(run.Id, run.Agent, run.AgentVersion, run.Channel, run.Model, JsonSerializer.Serialize(run.KnowledgeVersions, Web), run.Trigger, (decimal)run.Score, run.Passed, run.Total, run.Cost, run.StartedAt, run.FinishedAt,
            [.. run.Cases.Select(c => new EvaluationCaseRow(run.Id, c.Case, c.Task, (decimal)c.Score, c.Passed, c.Answer, JsonSerializer.Serialize(c.Checks.Select(k => new { k.Kind, k.Detail, k.Method, k.Score, k.Message, k.Judgement }), Web), c.InputTokens, c.OutputTokens, c.Cost, c.Problem))]);

    /// <summary>The refusal of a promotion of this version of the agent to the channel, or null when it may go on (the minimum is a recorded choice; with none, nothing is refused).</summary>
    public string? PromotionRefusal(string agent, string version, string channel = Channel)
    {
        var latest = agents.Evaluations.Latest(agent, version, channel);
        return EvaluationGate.Refusal(agent, version, channel, agents.Evaluations.Minimum(agent, channel)?.Minimum, latest?.Score, latest?.Passed, latest?.Total);
    }

    /// <summary>Runs a case as a conversation of the agent and gives what happened.</summary>
    private sealed class Host(ConsoleAgents agents, string agent, Action caseDone) : IEvaluationHost
    {
        public async Task<EvaluationAgentRun> RunAsync(EvaluationCase evaluation, CancellationToken cancellation)
        {
            try { return await RunOne(evaluation, cancellation).ConfigureAwait(false); }
            finally { caseDone(); }
        }

        private async Task<EvaluationAgentRun> RunOne(EvaluationCase evaluation, CancellationToken cancellation)
        {
            var calls = new List<RecordedToolCall>();
            var (row, problem) = await agents.StartAsync(agent, "evaluation", "Evaluation", $"Evaluation: {evaluation.Name}", null, cancellation, (call, outcome) => { lock (calls) calls.Add(new RecordedToolCall(call.Tool, call.Arguments.ToJsonString(), outcome.Text, outcome.IsError)); }).ConfigureAwait(false);
            if (row is null) throw new InvalidOperationException(problem);
            var text = evaluation.Task + string.Concat(evaluation.Fixtures.Select(f => $"\n\n[{f.Key}]\n{f.Value}"));
            var (accepted, why) = agents.Send(row.Id, "evaluation", text);
            if (!accepted) throw new InvalidOperationException(why);
            string? stuck = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(CaseTimeout);
                try { await agents.WhenIdle(row.Id).WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    stuck = $"The agent did not finish in {CaseTimeout.TotalMinutes:0} minutes (a step may be waiting for a person's approval, which an evaluation has nobody to ask).";
                    agents.Cancel(row.Id, "evaluation");
                }
            }
            var view = agents.View(row.Id)!;
            var answer = view.Text.LastOrDefault(t => t.Role == "assistant")?.Content ?? "";
            var knowledge = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var turn in view.Turns)
                try
                {
                    foreach (var part in JsonNode.Parse(turn.ContextRecord ?? "[]") as JsonArray ?? [])
                        if (part?["layer"]?.GetValue<string>() is { } layer && layer != "platform" && part["id"]?.GetValue<string>() is { } id) knowledge.Add($"{id}@{part["version"]?.GetValue<string>()}");
                }
                catch (JsonException) { }
            var failed = stuck ?? (view.Conversation.Status == "failed" ? $"The run failed: {view.Conversation.StopReason}" : null);
            return new EvaluationAgentRun(answer, calls, view.Turns.Sum(t => t.InputTokens), view.Turns.Sum(t => t.OutputTokens), view.Turns.Sum(t => t.Cost), view.Turns.FirstOrDefault(t => t.Model is not null)?.Model, [.. knowledge], failed);
        }

        public async Task<JudgeVerdict?> JudgeAsync(string rubric, EvaluationCase evaluation, string answer, CancellationToken cancellation)
        {
            if (agents.Models.For("judge", null) is not { } judge) return null;
            var prompt = $"Task given to an assistant:\n{evaluation.Task}\n\nThe assistant's answer:\n{answer}\n\nWhat a good answer is (the rubric):\n{rubric}\n\nScore the answer against the rubric from 0 (not at all) to 1 (entirely). Reply with only JSON: {{\"score\": <number>, \"reasoning\": \"<one or two sentences>\"}}";
            var response = await judge.Client.GetResponseAsync([new ChatMessage(ChatRole.System, "You are a strict, fair judge of an assistant's answer. You judge only against the written rubric, and you answer only in the JSON asked for."), new ChatMessage(ChatRole.User, prompt)], cancellationToken: cancellation).ConfigureAwait(false);
            var textOut = response.Text;
            var start = textOut.IndexOf('{', StringComparison.Ordinal);
            var end = textOut.LastIndexOf('}');
            try
            {
                var json = JsonNode.Parse(textOut[start..(end + 1)]);
                return new JudgeVerdict(json!["score"]!.GetValue<double>(), json["reasoning"]?.GetValue<string>() ?? "");
            }
            catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException or NullReferenceException or FormatException)
            {
                return new JudgeVerdict(0, $"The judge model did not answer in the form asked for, so the answer scores 0 on this rubric: {textOut.Trim()[..Math.Min(200, textOut.Trim().Length)]}");
            }
        }
    }
}
