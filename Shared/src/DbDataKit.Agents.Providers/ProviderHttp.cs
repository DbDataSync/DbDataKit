using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Providers;

/// <summary>
/// A call to a model provider that failed, in words for a person. <see cref="Temporary"/> says whether trying again (or the next model of a slot) can help: a busy or unreachable provider is temporary, a key the provider refuses or a
/// request it will not accept is not. The message never holds the key, a header or the request body.
/// </summary>
public sealed class ModelProviderException(string message, bool temporary, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public bool Temporary { get; } = temporary;

    public HttpStatusCode? Status { get; } = status;

    /// <summary>Whether a failure is worth a retry and then the next model: a temporary provider failure or anything unforeseen, but never a cancellation, a bad argument or a refusal the provider will repeat.</summary>
    public static bool WorthRetrying(Exception ex) => ex switch
    {
        ModelProviderException p => p.Temporary,
        OperationCanceledException or ArgumentException => false,
        _ => true,
    };
}

/// <summary>What the two adapters share: sending a request, classifying a failure, reading a server-sent stream, and the choice of the model and limits for a call.</summary>
internal static class ProviderHttp
{
    public static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The failure for a response that was not a success, worded for a person and classified; <paramref name="provider"/> is the provider's name as the install knows it.</summary>
    public static ModelProviderException Failure(string provider, HttpStatusCode status, string body, string? key = null)
    {
        string detail = ErrorText(body);
        if (!string.IsNullOrEmpty(key)) detail = detail.Replace(key, "***", StringComparison.Ordinal);   // a provider that echoes the key back must not put it in a message
        string suffix = detail.Length > 0 ? $" It said: {detail}" : "";
        return (int)status switch
        {
            401 or 403 => new ModelProviderException($"{provider} did not accept the key ({(int)status}). Check the key's secret and that it is allowed to use this model.{suffix}", false, status),
            404 => new ModelProviderException($"{provider} has no such address or model ({(int)status}). Check the endpoint and the model's id.{suffix}", false, status),
            408 or 409 or 425 or 429 => new ModelProviderException($"{provider} is busy or limiting requests ({(int)status}); try again shortly.{suffix}", true, status),
            >= 500 => new ModelProviderException($"{provider} failed on its side ({(int)status}); try again shortly.{suffix}", true, status),
            _ => new ModelProviderException($"{provider} refused the request ({(int)status}).{suffix}", false, status),
        };
    }

    /// <summary>The message of the provider's own error body (OpenAI's <c>error.message</c>, Anthropic's <c>error.message</c>), cut short; empty when there is none.</summary>
    public static string ErrorText(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"] is JsonObject e ? (string?)e["message"] : node?["error"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            return message is null ? "" : message.Length > 300 ? message[..300] + "..." : message;
        }
        catch (JsonException) { return ""; }
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, string provider, HttpRequestMessage request, bool stream, CancellationToken cancellation, string? key = null)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellation).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new ModelProviderException($"{provider} could not be reached: {Short(ex)}. Check that the endpoint is right and the provider is running.", true, null, ex); }
        catch (TaskCanceledException ex) when (!cancellation.IsCancellationRequested) { throw new ModelProviderException($"{provider} did not answer in time.", true, null, ex); }
        if (response.IsSuccessStatusCode) return response;
        using (response) throw Failure(provider, response.StatusCode, await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), key);
    }

    private static string Short(HttpRequestException ex) => ex.InnerException is { } inner ? inner.Message : ex.Message;

    /// <summary>The data lines of a server-sent event stream, one JSON text per event (with the event name when there is one), ending at the end of the stream or a <c>[DONE]</c>.</summary>
    public static async IAsyncEnumerable<(string? Event, string Data)> Events(Stream stream, [EnumeratorCancellation] CancellationToken cancellation)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? name = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var text = data.ToString();
                    data.Clear();
                    if (text == "[DONE]") yield break;
                    yield return (name, text);
                }
                name = null;
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); }
        }
        if (data.Length > 0 && data.ToString() is var last && last != "[DONE]") yield return (name, last);
    }

    /// <summary>The declared tools of a request as (name, description, JSON schema) triples.</summary>
    public static IEnumerable<(string Name, string Description, JsonNode Schema)> Tools(ChatOptions? options)
    {
        foreach (var tool in options?.Tools ?? [])
            if (tool is AIFunctionDeclaration f)
                yield return (f.Name, f.Description ?? "", JsonNode.Parse(f.JsonSchema.GetRawText()) ?? new JsonObject { ["type"] = "object" });
    }

    /// <summary>A call's arguments as the dictionary a <see cref="FunctionCallContent"/> carries, or the failure to read them (a model's malformed call arrives as a call that carries its error, so the runner can tell the model).</summary>
    public static FunctionCallContent Call(string id, string name, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new FunctionCallContent(id, name, new Dictionary<string, object?>());
        try
        {
            if (JsonNode.Parse(argumentsJson) is not JsonObject o) return new FunctionCallContent(id, name, null) { Exception = new JsonException("the arguments are not a JSON object") };
            return new FunctionCallContent(id, name, o.ToDictionary(p => p.Key, p => p.Value is null ? (object?)null : JsonSerializer.Deserialize<JsonElement>(p.Value.ToJsonString())));
        }
        catch (JsonException ex) { return new FunctionCallContent(id, name, null) { Exception = ex }; }
    }

    /// <summary>The text of a tool result as the model is given it: a string stays as it is, anything else is written as JSON.</summary>
    public static string ResultText(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString() ?? "",
        _ => JsonSerializer.Serialize(result),
    };

    public static JsonNode ArgumentsNode(FunctionCallContent call) =>
        call.Arguments is null ? new JsonObject() : JsonSerializer.SerializeToNode(call.Arguments) ?? new JsonObject();

    /// <summary>A string value of a JSON node, or null when it is missing, null or not a string.</summary>
    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A whole number of a JSON node, or null.</summary>
    public static long? Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    /// <summary>The <c>index</c> of a stream event or a tool call part (0 when it has none).</summary>
    public static int Index(JsonNode? node) => node?["index"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    public static ChatResponse Response(string? id, string? model, ChatFinishReason? finish, IList<AIContent> contents, UsageDetails? usage) =>
        new(new ChatMessage(ChatRole.Assistant, contents)) { ResponseId = id, ModelId = model, FinishReason = finish, Usage = usage };

    public static UsageDetails Usage(long? input, long? output) =>
        new() { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input is null && output is null ? null : (input ?? 0) + (output ?? 0) };
}
