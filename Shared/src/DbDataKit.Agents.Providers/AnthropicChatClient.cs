using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Providers;

/// <summary>
/// <see cref="IChatClient"/> over Anthropic's Messages API (<c>POST {endpoint}/v1/messages</c>, plan 27a AG04), written against the documented shapes: headers <c>x-api-key</c> and <c>anthropic-version</c>; the request has <c>model</c>,
/// <c>max_tokens</c> (always required), <c>system</c>, <c>messages</c> whose content is blocks (<c>text</c>, <c>tool_use</c> with an <c>input</c> object, <c>tool_result</c> with <c>tool_use_id</c>) and <c>tools</c> with <c>input_schema</c>;
/// the answer has <c>content</c> blocks, a <c>stop_reason</c> (<c>end_turn</c>, <c>tool_use</c>, <c>max_tokens</c>, <c>refusal</c>) and <c>usage</c>; a stream is the events <c>message_start</c>, <c>content_block_start</c>,
/// <c>content_block_delta</c> (<c>text_delta</c>, <c>input_json_delta</c>), <c>content_block_stop</c>, <c>message_delta</c> and <c>message_stop</c>. This is a small adapter of our own over HTTP, not a package: the official and community
/// packages were not pinned for it (the row of plan 27a AG04 says why), and the same recorded fixtures prove it.
/// </summary>
public sealed class AnthropicChatClient(HttpClient http, ProviderClientOptions options) : IChatClient
{
    /// <summary>The API version header the shapes above are those of.</summary>
    public const string ApiVersion = "2023-06-01";

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
    {
        using var request = Request(messages, chatOptions, stream: false);
        using var response = await ProviderHttp.SendAsync(http, options.Provider, request, stream: false, cancellationToken, options.ApiKey).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException ex) { throw new ModelProviderException($"{options.Provider} answered with something that is not JSON. Check the endpoint: it should be https://api.anthropic.com.", false, response.StatusCode, ex); }
        if (root?["content"] is not JsonArray blocks)
            throw new ModelProviderException($"{options.Provider} answered with no content. Check the endpoint and the model.", false, response.StatusCode);

        var contents = new List<AIContent>();
        foreach (var block in blocks.OfType<JsonObject>())
        {
            switch ((string?)block["type"])
            {
                case "text" when !string.IsNullOrEmpty((string?)block["text"]): contents.Add(new TextContent((string?)block["text"])); break;
                case "tool_use": contents.Add(ProviderHttp.Call((string?)block["id"] ?? $"call-{Guid.NewGuid():N}"[..13], (string?)block["name"] ?? "", block["input"]?.ToJsonString())); break;
            }
        }
        var usage = root["usage"] is JsonObject u ? ProviderHttp.Usage((long?)u["input_tokens"], (long?)u["output_tokens"]) : null;
        return ProviderHttp.Response((string?)root["id"], (string?)root["model"], Finish((string?)root["stop_reason"]), contents, usage);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = Request(messages, chatOptions, stream: true);
        using var response = await ProviderHttp.SendAsync(http, options.Provider, request, stream: true, cancellationToken, options.ApiKey).ConfigureAwait(false);
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Input)>();
        string? id = null, model = null;
        long? input = null, output = null;
        ChatFinishReason? finish = null;
        await foreach (var (name, data) in ProviderHttp.Events(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
        {
            JsonNode? e;
            try { e = JsonNode.Parse(data); }
            catch (JsonException ex) { throw new ModelProviderException($"{options.Provider} sent a stream event that is not JSON.", true, null, ex); }
            string kind = name ?? ProviderHttp.Str(e?["type"]) ?? "";
            if (kind == "error")
            {
                string errorType = ProviderHttp.Str(e?["error"]?["type"]) ?? "";
                throw new ModelProviderException($"{options.Provider} stopped the answer: {ProviderHttp.ErrorText(data)}", errorType is "overloaded_error" or "api_error" or "rate_limit_error");
            }
            if (kind == "message_start")
            {
                id = ProviderHttp.Str(e?["message"]?["id"]);
                model = ProviderHttp.Str(e?["message"]?["model"]);
                input = ProviderHttp.Long(e?["message"]?["usage"]?["input_tokens"]);
                output = ProviderHttp.Long(e?["message"]?["usage"]?["output_tokens"]);
            }
            else if (kind == "content_block_start" && e?["content_block"] is JsonObject block && ProviderHttp.Str(block["type"]) == "tool_use")
                calls[ProviderHttp.Index(e)] = (ProviderHttp.Str(block["id"]) ?? "", ProviderHttp.Str(block["name"]) ?? "", new StringBuilder());
            else if (kind == "content_block_delta" && e?["delta"] is JsonObject delta)
            {
                string deltaType = ProviderHttp.Str(delta["type"]) ?? "";
                string? text = ProviderHttp.Str(delta["text"]);
                if (deltaType == "text_delta" && !string.IsNullOrEmpty(text)) yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ResponseId = id, ModelId = model };
                else if (deltaType == "input_json_delta" && calls.TryGetValue(ProviderHttp.Index(e), out var call)) call.Input.Append(ProviderHttp.Str(delta["partial_json"]));
            }
            else if (kind == "message_delta")
            {
                if (ProviderHttp.Str(e?["delta"]?["stop_reason"]) is { } reason) finish = Finish(reason);
                if (ProviderHttp.Long(e?["usage"]?["output_tokens"]) is { } o) output = o;
                if (ProviderHttp.Long(e?["usage"]?["input_tokens"]) is { } i) input = i;
            }
        }
        var rest = new List<AIContent>();
        foreach (var (_, call) in calls) rest.Add(ProviderHttp.Call(call.Id.Length > 0 ? call.Id : $"call-{Guid.NewGuid():N}"[..13], call.Name, call.Input.Length > 0 ? call.Input.ToString() : "{}"));
        if (input is not null || output is not null) rest.Add(new UsageContent(ProviderHttp.Usage(input, output)));
        yield return new ChatResponseUpdate(ChatRole.Assistant, rest) { ResponseId = id, ModelId = model, FinishReason = finish ?? (calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop) };
    }

    private static ChatFinishReason Finish(string? reason) => reason switch
    {
        "tool_use" => ChatFinishReason.ToolCalls,
        "max_tokens" => ChatFinishReason.Length,
        "refusal" => ChatFinishReason.ContentFilter,
        _ => ChatFinishReason.Stop,
    };

    private HttpRequestMessage Request(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions, bool stream)
    {
        var all = messages.ToList();
        var system = string.Join("\n\n", all.Where(m => m.Role == ChatRole.System).Select(m => string.Concat(m.Contents.OfType<TextContent>().Select(c => c.Text))).Where(t => t.Length > 0));
        var body = new JsonObject { ["model"] = chatOptions?.ModelId ?? options.Model, ["max_tokens"] = chatOptions?.MaxOutputTokens ?? options.MaxOutputTokens };
        if (system.Length > 0) body["system"] = system;
        body["messages"] = Messages(all.Where(m => m.Role != ChatRole.System));
        var tools = ProviderHttp.Tools(chatOptions).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Schema.DeepClone() }).ToList();
        if (tools.Count > 0) { body["tools"] = new JsonArray([.. tools]); body["tool_choice"] = new JsonObject { ["type"] = "auto" }; }
        if (chatOptions?.Temperature is { } temperature) body["temperature"] = temperature;
        if (stream) body["stream"] = true;

        var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint.TrimEnd('/') + "/v1/messages") { Content = new StringContent(body.ToJsonString(ProviderHttp.Compact), Encoding.UTF8, "application/json") };
        request.Headers.Add("anthropic-version", ApiVersion);
        if (!string.IsNullOrEmpty(options.ApiKey) && options.AuthStyle != "none")
        {
            if (options.AuthStyle == "bearer") request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            else request.Headers.Add("x-api-key", options.ApiKey);
        }
        return request;
    }

    /// <summary>The messages as Anthropic wants them: alternating user and assistant turns of blocks, the results of tools in a user turn of <c>tool_result</c> blocks (several results of one turn together).</summary>
    private static JsonArray Messages(IEnumerable<ChatMessage> messages)
    {
        var list = new JsonArray();
        void Add(string role, IEnumerable<JsonNode> blocks)
        {
            var array = blocks.ToList();
            if (array.Count == 0) return;
            if (list.Count > 0 && list[^1] is JsonObject last && (string?)last["role"] == role) { foreach (var b in array) ((JsonArray)last["content"]!).Add(b); return; }
            list.Add(new JsonObject { ["role"] = role, ["content"] = new JsonArray([.. array]) });
        }
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                var blocks = new List<JsonNode>();
                foreach (var c in message.Contents)
                {
                    if (c is TextContent { Text.Length: > 0 } t) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                    else if (c is FunctionCallContent f) blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = f.CallId, ["name"] = f.Name, ["input"] = ProviderHttp.ArgumentsNode(f) });
                }
                Add("assistant", blocks);
            }
            else if (message.Role == ChatRole.Tool)
                Add("user", message.Contents.OfType<FunctionResultContent>().Select(r => (JsonNode)new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = r.CallId, ["content"] = ProviderHttp.ResultText(r.Result) }));
            else
                Add("user", message.Contents.OfType<TextContent>().Where(t => t.Text.Length > 0).Select(t => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = t.Text }));
        }
        return list;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
