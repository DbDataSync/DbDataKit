using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Evaluation;

/// <summary>An agent ready to be asked one case: the model, the tools (an evaluation gives an agent tools that only read, or fakes), the options and the context it runs in, what it was given, and what a turn costs.</summary>
public sealed record EvaluationAgent(IChatClient Client, IReadOnlyList<AgentTool> Tools, AgentRunOptions Options, AgentRunContext Context, string? Model, IReadOnlyList<string> KnowledgeVersions, Func<RunUsage, decimal>? Price = null);

/// <summary>
/// The evaluation host over <see cref="AgentRunner"/> (plan 27a AG23): a case is put to the agent the way a person would (the task, then the fixtures as text), the tools it calls are recorded with what they returned (the rows of a query are in the
/// result, which is what the suite compares), and the run's usage becomes tokens and cost. What builds the agent for a case (the channel's bindings, its tools) and what judges, checks and reads verified queries are the host's, given as functions.
/// </summary>
public sealed class AgentEvaluationHost(Func<EvaluationCase, EvaluationAgent> agentFor, Func<string, EvaluationCase, string, CancellationToken, Task<JudgeVerdict?>>? judge = null,
    Func<string, EvaluationCase, EvaluationAgentRun, CancellationToken, Task<bool?>>? check = null, Func<string, CancellationToken, Task<RowSet?>>? verifiedRows = null) : IEvaluationHost
{
    public async Task<EvaluationAgentRun> RunAsync(EvaluationCase evaluation, CancellationToken cancellation)
    {
        var agent = agentFor(evaluation);
        var calls = new List<RecordedToolCall>();
        var options = agent.Options with
        {
            AfterCall = (call, outcome) =>
            {
                lock (calls) calls.Add(new RecordedToolCall(call.Tool, call.Arguments.ToJsonString(), outcome.Text, outcome.IsError));
                agent.Options.AfterCall?.Invoke(call, outcome);
            },
        };
        var text = evaluation.Task + string.Concat(evaluation.Fixtures.Select(f => $"\n\n[{f.Key}]\n{f.Value}"));
        var result = await new AgentRunner(agent.Client, agent.Tools).RunAsync([new ChatMessage(ChatRole.User, text)], agent.Context, options, cancellation).ConfigureAwait(false);
        string? problem = result.Stop == AgentStop.Completed ? null : $"The run stopped before an answer: {result.StopMessage ?? result.Stop.ToString()}";
        return new EvaluationAgentRun(result.Text, calls, result.Usage.InputTokens, result.Usage.OutputTokens, agent.Price?.Invoke(result.Usage) ?? 0m, agent.Model, agent.KnowledgeVersions, problem);
    }

    public Task<JudgeVerdict?> JudgeAsync(string rubric, EvaluationCase evaluation, string answer, CancellationToken cancellation) =>
        judge is null ? Task.FromResult<JudgeVerdict?>(null) : judge(rubric, evaluation, answer, cancellation);

    public Task<bool?> CheckAsync(string name, EvaluationCase evaluation, EvaluationAgentRun run, CancellationToken cancellation) =>
        check is null ? Task.FromResult<bool?>(null) : check(name, evaluation, run, cancellation);

    public Task<RowSet?> VerifiedRowsAsync(string verifiedQueryId, CancellationToken cancellation) =>
        verifiedRows is null ? Task.FromResult<RowSet?>(null) : verifiedRows(verifiedQueryId, cancellation);
}
