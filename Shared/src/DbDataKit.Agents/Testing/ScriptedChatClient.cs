using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Testing;

/// <summary>One answer of the scripted model: text, tool calls, a refusal; with the usage it reports.</summary>
public sealed class ScriptStep
{
    internal List<AIContent> Contents { get; } = [];

    internal ChatFinishReason Finish { get; set; } = ChatFinishReason.Stop;

    internal UsageDetails? Usage { get; set; }

    /// <summary>The model answers with text and no tool call.</summary>
    public static ScriptStep Say(string text) => new ScriptStep().Add(new TextContent(text));

    /// <summary>The model calls one tool; <paramref name="arguments"/> is a JSON object (or an anonymous object, written as JSON).</summary>
    public static ScriptStep Call(string tool, object? arguments = null, string? callId = null) => new ScriptStep().AndCall(tool, arguments, callId);

    /// <summary>The model calls a tool with arguments that are not a JSON object: the call arrives carrying the parse error, as a provider's malformed call does.</summary>
    public static ScriptStep Malformed(string tool, string error, string? callId = null) =>
        new ScriptStep().Add(new FunctionCallContent(callId ?? $"call-{Guid.NewGuid():N}"[..13], tool, null) { Exception = new JsonException(error) });

    /// <summary>The model refuses (a provider's content filter).</summary>
    public static ScriptStep Refuse(string text = "I can't help with that.") => new ScriptStep { Finish = ChatFinishReason.ContentFilter }.Add(new TextContent(text));

    /// <summary>Adds text before the tool calls of the step.</summary>
    public ScriptStep Saying(string text) { Contents.Insert(0, new TextContent(text)); return this; }

    /// <summary>Adds another tool call to the step (a model may call several at once).</summary>
    public ScriptStep AndCall(string tool, object? arguments = null, string? callId = null)
    {
        var node = arguments switch
        {
            null => new JsonObject(),
            JsonObject o => o,
            _ => JsonSerializer.SerializeToNode(arguments) as JsonObject ?? new JsonObject(),
        };
        var dictionary = node.ToDictionary(p => p.Key, p => p.Value is null ? (object?)null : JsonSerializer.Deserialize<JsonElement>(p.Value.ToJsonString()));
        Finish = ChatFinishReason.ToolCalls;
        return Add(new FunctionCallContent(callId ?? $"call-{Guid.NewGuid():N}"[..13], tool, dictionary));
    }

    /// <summary>The usage the provider reports for the step (otherwise a count from the characters, four to a token).</summary>
    public ScriptStep WithUsage(long input, long output) { Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output }; return this; }

    private ScriptStep Add(AIContent content) { Contents.Add(content); return this; }
}

/// <summary>
/// The scripted fake of the model (plan 27a AG03, plan 25 section 2): an <see cref="IChatClient"/> that answers from a list of steps, in order, and remembers every request it was given so a test can say exactly what the model saw. It makes no network call.
/// A script that runs out fails the test with a message, never silently.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ScriptStep> _steps;
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
    private readonly List<ChatOptions?> _options = [];

    public ScriptedChatClient(params ScriptStep[] steps) => _steps = new Queue<ScriptStep>(steps);

    /// <summary>The messages of each request, as they were when the request was made.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

    public IReadOnlyList<ChatOptions?> Options => _options;

    public int StepsLeft => _steps.Count;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var seen = messages.Select(m => m.Clone()).ToList();
        _requests.Add(seen);
        _options.Add(options);
        if (!_steps.TryDequeue(out var step))
            throw new InvalidOperationException($"The scripted model was asked a {_requests.Count}th time and its script has no step left.");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, step.Contents))
        {
            FinishReason = step.Finish,
            ModelId = "scripted",
            Usage = step.Usage ?? new UsageDetails
            {
                InputTokenCount = seen.Sum(m => (m.Text?.Length ?? 0) + 3) / 4,
                OutputTokenCount = (step.Contents.OfType<TextContent>().Sum(t => t.Text.Length) + 3) / 4,
            },
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
