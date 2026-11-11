using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Providers;

/// <summary>How a client reaches one model of one provider. The key is a value held in memory for the call and is never part of a message, a log line or <c>ToString</c>.</summary>
/// <param name="Provider">The provider's name as the install knows it, used in messages for a person.</param>
/// <param name="Endpoint">The base address: <c>https://api.openai.com/v1</c> for the chat completions wire, <c>https://api.anthropic.com</c> for Anthropic's.</param>
/// <param name="Model">The model's id as the provider names it.</param>
/// <param name="ApiKey">The key, or null for a local server that takes none.</param>
/// <param name="AuthStyle"><c>bearer</c> (an <c>Authorization: Bearer</c> header), <c>api-key</c> (an <c>api-key</c> header, as Microsoft Foundry and Azure OpenAI take it; Anthropic's <c>x-api-key</c>) or <c>none</c>.</param>
/// <param name="MaxOutputTokens">The most the model may answer with; Anthropic requires one on every call.</param>
/// <param name="UsesMaxCompletionTokens">The limit is sent as <c>max_completion_tokens</c> (OpenAI's own and Foundry's newer models) rather than <c>max_tokens</c> (everything else).</param>
public sealed record ProviderClientOptions(string Provider, string Endpoint, string Model, string? ApiKey, string AuthStyle = "bearer", int MaxOutputTokens = 4096, bool UsesMaxCompletionTokens = false)
{
    public override string ToString() => $"{Provider} {Model}";
}

/// <summary>
/// <see cref="IChatClient"/> over the chat completions API (<c>POST {endpoint}/chat/completions</c>) that OpenAI, Microsoft Foundry, Ollama (at <c>/v1</c>) and most local servers speak (plan 27a AG04). Written against the documented shapes:
/// the request has <c>model</c>, <c>messages</c> (system, user, assistant with <c>tool_calls</c>, tool with <c>tool_call_id</c>) and <c>tools</c> of type <c>function</c>; the answer has <c>choices[0].message</c> with <c>content</c>, <c>refusal</c>
/// or <c>tool_calls</c>, a <c>finish_reason</c> and <c>usage</c>. A call's arguments are a JSON <i>string</i>: a string that is not an object arrives as a call carrying its parse error. Nothing is sent that the caller did not give.
/// </summary>
public sealed class OpenAiCompatibleChatClient(HttpClient http, ProviderClientOptions options) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
    {
        using var request = Request(messages, chatOptions, stream: false);
        using var response = await ProviderHttp.SendAsync(http, options.Provider, request, stream: false, cancellationToken, options.ApiKey).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException ex) { throw new ModelProviderException($"{options.Provider} answered with something that is not JSON. Check the endpoint: it should be the address of the chat completions API (ending in /v1).", false, response.StatusCode, ex); }
        if (root?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
            throw new ModelProviderException($"{options.Provider} answered with no choices. Check the endpoint and the model.", false, response.StatusCode);

        var message = choice["message"] as JsonObject;
        var contents = new List<AIContent>();
        var refusal = (string?)message?["refusal"];
        if (!string.IsNullOrEmpty((string?)message?["content"])) contents.Add(new TextContent((string?)message!["content"]));
        if (!string.IsNullOrEmpty(refusal)) contents.Add(new TextContent(refusal));
        if (message?["tool_calls"] is JsonArray calls)
            foreach (var call in calls.OfType<JsonObject>())
                contents.Add(ProviderHttp.Call((string?)call["id"] ?? $"call-{Guid.NewGuid():N}"[..13], (string?)call["function"]?["name"] ?? "", (string?)call["function"]?["arguments"]));
        var usage = root["usage"] is JsonObject u ? ProviderHttp.Usage((long?)u["prompt_tokens"], (long?)u["completion_tokens"]) : null;
        return ProviderHttp.Response((string?)root["id"], (string?)root["model"], Finish((string?)choice["finish_reason"], !string.IsNullOrEmpty(refusal), contents), contents, usage);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = Request(messages, chatOptions, stream: true);
        using var response = await ProviderHttp.SendAsync(http, options.Provider, request, stream: true, cancellationToken, options.ApiKey).ConfigureAwait(false);
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        string? id = null, model = null;
        ChatFinishReason? finish = null;
        UsageDetails? usage = null;
        await foreach (var (_, data) in ProviderHttp.Events(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
        {
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch (JsonException ex) { throw new ModelProviderException($"{options.Provider} sent a stream event that is not JSON.", true, null, ex); }
            if (chunk?["error"] is not null) throw new ModelProviderException($"{options.Provider} stopped the answer: {ProviderHttp.ErrorText(data)}", true);
            if (string.IsNullOrEmpty(id)) id = (string?)chunk?["id"];
            if (string.IsNullOrEmpty(model)) model = (string?)chunk?["model"];
            if (chunk?["usage"] is JsonObject u) usage = ProviderHttp.Usage((long?)u["prompt_tokens"], (long?)u["completion_tokens"]);
            if (chunk?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
            if (choice["delta"] is JsonObject delta)
            {
                if (!string.IsNullOrEmpty((string?)delta["content"])) yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)delta["content"]) { ResponseId = id, ModelId = model };
                if (!string.IsNullOrEmpty((string?)delta["refusal"])) { finish = ChatFinishReason.ContentFilter; yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)delta["refusal"]) { ResponseId = id, ModelId = model }; }
                if (delta["tool_calls"] is JsonArray parts)
                    foreach (var part in parts.OfType<JsonObject>())
                    {
                        int index = ProviderHttp.Index(part);
                        var (cid, name, arguments) = calls.TryGetValue(index, out var have) ? have : ("", "", new StringBuilder());
                        calls[index] = ((string?)part["id"] ?? cid, (string?)part["function"]?["name"] is { Length: > 0 } n ? n : name, arguments.Append((string?)part["function"]?["arguments"]));
                    }
            }
            if ((string?)choice["finish_reason"] is { } reason) finish = finish == ChatFinishReason.ContentFilter ? finish : Finish(reason, false, []);
        }
        var rest = new List<AIContent>();
        foreach (var (_, call) in calls) rest.Add(ProviderHttp.Call(call.Id.Length > 0 ? call.Id : $"call-{Guid.NewGuid():N}"[..13], call.Name, call.Arguments.ToString()));
        if (usage is not null) rest.Add(new UsageContent(usage));
        yield return new ChatResponseUpdate(ChatRole.Assistant, rest) { ResponseId = id, ModelId = model, FinishReason = finish ?? (calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop) };
    }

    private static ChatFinishReason Finish(string? reason, bool refused, IList<AIContent> contents) => refused ? ChatFinishReason.ContentFilter : reason switch
    {
        "length" => ChatFinishReason.Length,
        "content_filter" => ChatFinishReason.ContentFilter,
        "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
        _ => contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
    };

    private HttpRequestMessage Request(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions, bool stream)
    {
        var body = new JsonObject { ["model"] = chatOptions?.ModelId ?? options.Model, ["messages"] = Messages(messages) };
        var tools = ProviderHttp.Tools(chatOptions).Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema.DeepClone() },
        }).ToList();
        if (tools.Count > 0) { body["tools"] = new JsonArray([.. tools]); body["tool_choice"] = "auto"; }
        body[options.UsesMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = chatOptions?.MaxOutputTokens ?? options.MaxOutputTokens;
        if (chatOptions?.Temperature is { } t0) body["temperature"] = t0;
        if (stream) { body["stream"] = true; body["stream_options"] = new JsonObject { ["include_usage"] = true }; }

        var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint.TrimEnd('/') + "/chat/completions") { Content = new StringContent(body.ToJsonString(ProviderHttp.Compact), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            if (options.AuthStyle == "api-key") request.Headers.Add("api-key", options.ApiKey);
            else if (options.AuthStyle != "none") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        return request;
    }

    private static JsonArray Messages(IEnumerable<ChatMessage> messages)
    {
        var list = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                foreach (var result in message.Contents.OfType<FunctionResultContent>())
                    list.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.CallId, ["content"] = ProviderHttp.ResultText(result.Result) });
                continue;
            }
            var text = string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));
            if (message.Role == ChatRole.Assistant)
            {
                var entry = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text : null };
                var calls = message.Contents.OfType<FunctionCallContent>().Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.CallId, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = ProviderHttp.ArgumentsNode(c).ToJsonString(ProviderHttp.Compact) },
                }).ToList();
                if (calls.Count > 0) entry["tool_calls"] = new JsonArray([.. calls]);
                if (text.Length > 0 || calls.Count > 0) list.Add(entry);
            }
            else list.Add(new JsonObject { ["role"] = message.Role == ChatRole.System ? "system" : "user", ["content"] = text });
        }
        return list;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
