using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents;

/// <summary>One model of a slot's ordered list: the connection and model it names, the client that talks to it, and whether it is a cloud provider (the install policy may refuse to fall back to one).</summary>
public sealed record ModelBinding(string Name, IChatClient Client, bool Cloud = false);

/// <summary>How a failing model is retried before the next one is tried (plan 27a AG21, plan 27 section 3.15).</summary>
public sealed record RetryOptions(int Retries = 2, Func<int, TimeSpan>? Backoff = null, Func<TimeSpan, CancellationToken, Task>? Delay = null)
{
    public TimeSpan WaitBefore(int attempt) => (Backoff ?? (n => TimeSpan.FromSeconds(Math.Pow(2, n - 1))))(attempt);

    public Task Wait(TimeSpan time, CancellationToken cancellation) => (Delay ?? Task.Delay)(time, cancellation);
}

/// <summary>
/// Where a request says which model answered it (plan 27a AG21): the caller puts one in <see cref="FallbackChatClient.Holder"/> before the request and reads <see cref="Name"/> after it. A client shared by many conversations (the one a slot is bound to is) cannot say it itself
/// without telling one conversation the answer of another; each request's own holder can.
/// </summary>
public sealed class AnswerHolder
{
    /// <summary>The name of the model that answered, or null until one has.</summary>
    public string? Name { get; set; }

    /// <summary>Whether it was not the first model of the slot.</summary>
    public bool Fallback { get; set; }
}

/// <summary>
/// A slot bound to an ordered list of models (plan 27a AG21): the first is tried, retried with back-off when it fails with an error that is worth retrying, and then the next one the install policy allows for this agent. The turn records which model answered.
/// A tenant-only install never falls back to a cloud provider: <paramref name="allowedAsFallback"/> says which bindings may be used after the first.
/// </summary>
public sealed class FallbackChatClient(IReadOnlyList<ModelBinding> bindings, AgentRunContext context, IAgentRecorder? recorder = null, Func<ModelBinding, bool>? allowedAsFallback = null, Func<Exception, bool>? worthRetrying = null, RetryOptions? retry = null) : IChatClient
{
    private readonly RetryOptions _retry = retry ?? new RetryOptions();

    /// <summary>The name of the model that answered the last request (of whichever conversation: the answer of a request is in its <see cref="Holder"/>).</summary>
    public string? LastAnswered { get; private set; }

    private static readonly AsyncLocal<AnswerHolder?> CurrentHolder = new();

    /// <summary>The holder the request being made now reports to; set around a request by its caller, in the flow of that request only.</summary>
    public static AnswerHolder? Holder { get => CurrentHolder.Value; set => CurrentHolder.Value = value; }

    /// <summary>Whether a failure is worth retrying and then falling through: by default anything but a cancellation or a refusal of the call itself (an argument exception).</summary>
    private bool Transient(Exception ex) => (worthRetrying ?? (e => e is not (OperationCanceledException or ArgumentException)))(ex);

    private IEnumerable<ModelBinding> Candidates() => bindings.Where((b, i) => i == 0 || (allowedAsFallback?.Invoke(b) ?? true));

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var failures = new List<string>();
        foreach (var binding in Candidates())
        {
            for (var attempt = 1; attempt <= _retry.Retries + 1; attempt++)
            {
                try
                {
                    var response = await binding.Client.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
                    Answered(binding, failures.Count > 0);
                    return response;
                }
                catch (Exception ex) when (Transient(ex) && !cancellationToken.IsCancellationRequested)
                {
                    failures.Add($"{binding.Name}: {ex.Message}");
                    recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, "model-failed", context, 0, Detail: $"{binding.Name}, attempt {attempt}: {ex.Message}"));
                    if (attempt <= _retry.Retries) await _retry.Wait(_retry.WaitBefore(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        throw new InvalidOperationException($"No model of the slot answered. {string.Join("; ", failures)}");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // a stream falls through only before its first update: once something was shown to the person it is not started again on another model
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    private void Answered(ModelBinding binding, bool afterFailure)
    {
        LastAnswered = binding.Name;
        if (Holder is { } holder) { holder.Name = binding.Name; holder.Fallback = afterFailure || !ReferenceEquals(binding, bindings[0]); }
        recorder?.Record(new AgentEvent(DateTimeOffset.UtcNow, "model-answered-by", context, 0, Detail: binding.Name + (afterFailure ? " (fallback)" : "")));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>What a conversation does when a new version of its agent is activated or an old one is rolled back (plan 27a AG21, plan 27 section 3.14).</summary>
/// <param name="Version">The version this turn runs.</param>
/// <param name="OfferedVersion">A newer version the agent offers to move to, or null.</param>
/// <param name="Ended">The conversation is ended because its version was rolled back; the message says so.</param>
/// <param name="Message">What the person is told when it ended, or null.</param>
public sealed record ConversationVersion(string Version, string? OfferedVersion, bool Ended, string? Message);

public static class ConversationVersions
{
    /// <summary>
    /// An open conversation stays on the agent version it started with and is offered the active one; each turn records the version it ran. When an administrator has ended conversations on a rolled-back version, one that started on it ends with a message and
    /// is offered the active version.
    /// </summary>
    public static ConversationVersion For(string startedOn, string active, IReadOnlySet<string>? rolledBack = null)
    {
        if (rolledBack is not null && rolledBack.Contains(startedOn))
            return new ConversationVersion(startedOn, active, true, $"This conversation started on version {startedOn} of the agent, which was rolled back. Start a new conversation to use version {active}.");
        return new ConversationVersion(startedOn, string.Equals(startedOn, active, StringComparison.Ordinal) ? null : active, false, null);
    }
}
