using System.Collections.Concurrent;
using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents;

/// <summary>Writes the approvals of a host where an inbox can read them (the <c>agents</c> module; a test keeps them in memory).</summary>
public interface IApprovalLedger
{
    /// <summary>A step now waits for a person: returns the id the inbox shows it by.</summary>
    Guid Requested(string? conversationId, ApprovalPrompt prompt);

    /// <summary>The answer was given; false when it does not count (the step was answered or has expired, or it is not this person's: <paramref name="anyPerson"/> says an administrator may answer for another).</summary>
    bool Answered(Guid id, bool approved, string by, string? note, bool anyPerson);

    /// <summary>Nobody answered in time: the step is expired, which is a no.</summary>
    void Expired(Guid id);
}

/// <summary>A step that waits, as the inbox lists it.</summary>
public sealed record PendingApproval(Guid Id, string? ConversationId, ApprovalPrompt Prompt, DateTimeOffset Since);

/// <summary>
/// The approval inbox of a host (plan 27a AG15, AG29): the steps that wait for a person. <see cref="ApproverFor"/> is the <see cref="IApprover"/> of one conversation; <see cref="Answer"/> is the person's own act, from the chat screen or the inbox. Only the person a
/// step is for may answer it (an administrator may answer for another when the host says so, and the record then shows who did); a step nobody answers within the timeout is expired, which is a no. Nothing a model says reaches this class.
/// </summary>
public sealed class ApprovalDesk(IApprovalLedger ledger, TimeSpan? timeout = null, TimeProvider? time = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(15);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, (PendingApproval Item, TaskCompletionSource<ApprovalDecision> Answer)> _waiting = new();

    /// <summary>The steps that wait now, oldest first; of one person, or everyone's.</summary>
    public IReadOnlyList<PendingApproval> Pending(string? person = null) =>
        [.. _waiting.Values.Select(w => w.Item).Where(p => person is null || p.Prompt.Person == person).OrderBy(p => p.Since)];

    public IApprover ApproverFor(string? conversationId) => new ConversationApprover(this, conversationId);

    /// <summary>Gives the person's answer to a waiting step. False when the step is not waiting or the answer does not count.</summary>
    public bool Answer(Guid id, bool approved, string by, string? note = null, bool anyPerson = false)
    {
        if (!_waiting.TryGetValue(id, out var waiting)) return false;
        if (!ledger.Answered(id, approved, by, note, anyPerson)) return false;
        if (!_waiting.TryRemove(id, out _)) return false;
        waiting.Answer.TrySetResult(new ApprovalDecision(approved, approved ? note : string.IsNullOrWhiteSpace(note) ? "the person said no" : note));
        return true;
    }

    private async Task<ApprovalDecision> AskAsync(string? conversationId, ApprovalPrompt prompt, CancellationToken cancellation)
    {
        var id = ledger.Requested(conversationId, prompt);
        var answer = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = (new PendingApproval(id, conversationId, prompt, _time.GetUtcNow()), answer);
        using var expiry = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, expiry.Token);
        using var registration = linked.Token.Register(() => answer.TrySetCanceled(linked.Token));
        try { return await answer.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            _waiting.TryRemove(id, out _);
            ledger.Expired(id);
            return new ApprovalDecision(false, "nobody answered in time");
        }
        catch (OperationCanceledException)
        {
            _waiting.TryRemove(id, out _);
            ledger.Expired(id);
            throw;
        }
    }

    private sealed class ConversationApprover(ApprovalDesk desk, string? conversationId) : IApprover
    {
        public Task<ApprovalDecision> AskAsync(ApprovalPrompt prompt, CancellationToken cancellation) => desk.AskAsync(conversationId, prompt, cancellation);
    }
}

/// <summary>Where a conversation writes what happens (the <c>agents</c> module in a host; memory in a test). The events of the run are its audit.</summary>
public interface IConversationLog : IAgentRecorder
{
    void Text(string conversationId, string role, string content);

    /// <summary>One model turn: the tokens the provider reported, the cost, and what the turn was given.</summary>
    void Turn(AgentRunContext context, int turn, string? model, long inputTokens, long outputTokens, decimal cost, string? contextPrint, string? contextRecord);

    void Status(string conversationId, string status, string? reason);
}

/// <summary>A log in memory.</summary>
public sealed class InMemoryConversationLog : IConversationLog
{
    public InMemoryRecorder Recorder { get; } = new();

    public List<(string Conversation, string Role, string Content)> Texts { get; } = [];

    public List<(int Turn, string? Model, long In, long Out, decimal Cost, string? Print, string? Record)> Turns { get; } = [];

    public List<(string Conversation, string Status, string? Reason)> Statuses { get; } = [];

    public void Record(AgentEvent entry) => Recorder.Record(entry);

    public void Text(string conversationId, string role, string content) { lock (Texts) Texts.Add((conversationId, role, content)); }

    public void Turn(AgentRunContext context, int turn, string? model, long inputTokens, long outputTokens, decimal cost, string? contextPrint, string? contextRecord) { lock (Turns) Turns.Add((turn, model, inputTokens, outputTokens, cost, contextPrint, contextRecord)); }

    public void Status(string conversationId, string status, string? reason) { lock (Statuses) Statuses.Add((conversationId, status, reason)); }
}

/// <summary>What a session needs besides the model, the tools and the definition.</summary>
public sealed record AgentSessionOptions
{
    public AgentPolicy Policy { get; init; } = AgentPolicy.None;

    public IConversationLog? Log { get; init; }

    /// <summary>Asks the person; null is an unattended run (only <i>do not ask</i> runs).</summary>
    public IApprover? Approver { get; init; }

    /// <summary>What is given to the model apart from the conversation, for a turn (plan 27a AG10); the instructions alone when null.</summary>
    public Func<int, ContextInputs>? Inputs { get; init; }

    public ModelPrice? Price { get; init; }

    public string? Model { get; init; }

    public IUsageLedger? Ledger { get; init; }

    public ILimitNotifier? Notifier { get; init; }

    public ITokenCounter? Counter { get; init; }

    public Action<ToolCall, ToolOutcome>? AfterCall { get; init; }
}

/// <summary>
/// One conversation of an agent with a person (plan 27a AG14's core, hosted by the console): it keeps the messages, runs each message the person sends through <see cref="AgentRunner"/> with the prompting gate, the limits and the context of each turn, and
/// writes what happened to the log: the words, one record per model turn (tokens, cost, what the turn was given) and, through the recorder, every tool call, ask, approval and refusal naming the person, the agent and its version. A run that ends on a limit, a denial or a refusal
/// says so in the conversation's status; the next message may go on.
/// </summary>
public sealed class AgentSession(AgentRunContext context, AgentDefinition definition, IChatClient client, IReadOnlyList<AgentTool> tools, AgentSessionOptions? options = null)
{
    private readonly AgentSessionOptions _options = options ?? new AgentSessionOptions();
    private readonly List<ChatMessage> _history = [];
    private readonly IUsageLedger _ledger = options?.Ledger ?? new InMemoryUsageLedger();

    public AgentRunContext Context { get; } = context;

    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>Takes up an earlier conversation from the words that were kept (the person's and the agent's; what the tools returned is not kept in memory across a restart).</summary>
    public void Resume(IEnumerable<(string Role, string Content)> kept)
    {
        _history.Clear();
        foreach (var (role, content) in kept)
            if (role is "user" or "assistant") _history.Add(new ChatMessage(role == "user" ? ChatRole.User : ChatRole.Assistant, content));
    }

    public async Task<AgentRunResult> SendAsync(string message, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var log = _options.Log;
        log?.Text(Context.ConversationId, "user", message);
        var before = _history.Count;
        _history.Add(new ChatMessage(ChatRole.User, message));

        var tap = new UsageTap(client);
        var turnContext = new TurnContext(_options.Inputs ?? (_ => ContextInputs.Empty), Context, log, _options.Counter);
        var agentSettings = definition.Tools.Where(t => t.Prompting is not null).ToDictionary(t => t.ToolName, t => t.Prompting!.Value, StringComparer.Ordinal);
        var gate = new ApprovalGate(_options.Approver, _options.Policy.Prompting, agentSettings, log) { EndRunOnDenial = false };
        var limits = new RunLimits(Merge(definition.Limits, _options.Policy.Limits.Conversation), _options.Policy.Limits.Person, _options.Policy.Limits.Install, _options.Price, _ledger, _options.Notifier, _options.Policy.Limits.NotifyAtPercent);
        var run = new AgentRunOptions
        {
            InstructionsForTurn = turnContext.ForTurn,
            Gate = gate,
            Limits = limits,
            Recorder = log,
            AfterCall = _options.AfterCall,
        };

        AgentRunResult result;
        try
        {
            result = await new AgentRunner(tap, tools).RunAsync(_history, Context, run, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _history.RemoveRange(before, _history.Count - before);
            log?.Status(Context.ConversationId, "failed", $"The model could not be reached or failed: {ex.Message}");
            throw;
        }

        // the runner worked on a copy with the system message first: keep what came after it, so the next message goes on from here
        _history.Clear();
        _history.AddRange(result.Messages.Where(m => m.Role != ChatRole.System));
        for (var i = 0; i < tap.Usages.Count; i++)
        {
            var usage = tap.Usages[i];
            var given = i < turnContext.Given.Count ? turnContext.Given[i] : null;
            log?.Turn(Context, i + 1, _options.Model, usage.In, usage.Out, _options.Price?.CostOf(usage.In, usage.Out) ?? 0m, given is null ? null : ContextAssembler.Fingerprint(given), given?.RecordLine);
        }
        limits.Commit(Context, result.Usage);

        if (result.Stop == AgentStop.Completed && result.Text.Length > 0) log?.Text(Context.ConversationId, "assistant", result.Text);
        else if (result.StopMessage is { } stopped) log?.Text(Context.ConversationId, "assistant", stopped);
        log?.Status(Context.ConversationId, result.Stop switch
        {
            AgentStop.Completed => "open",
            AgentStop.LimitReached => "limit-reached",
            AgentStop.Denied => "denied",
            _ => "refused",
        }, result.StopMessage);
        return result;
    }

    /// <summary>The agent's own limits narrow the install's for a conversation: the smaller of each.</summary>
    private static LimitSet Merge(LimitSet agent, LimitSet install) => new(Min(agent.Turns, install.Turns), Min(agent.ToolCalls, install.ToolCalls), Min(agent.Tokens, install.Tokens), Min(agent.Cost, install.Cost));

    private static T? Min<T>(T? a, T? b) where T : struct, IComparable<T> => a is null ? b : b is null ? a : a.Value.CompareTo(b.Value) <= 0 ? a : b;

    /// <summary>Notes the usage the provider reported for each model turn.</summary>
    private sealed class UsageTap(IChatClient inner) : DelegatingChatClient(inner)
    {
        public List<(long In, long Out)> Usages { get; } = [];

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            Usages.Add((response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0));
            return response;
        }
    }
}
