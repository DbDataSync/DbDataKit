using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Providers;

/// <summary>What a Test of an embedding provider found: it worked and how many dimensions the model gives, or why it did not (<see cref="ProviderTestResult.Dimensions"/>).</summary>
public static class EmbeddingClients
{
    /// <summary>The text a Test embeds.</summary>
    public const string Sample = "The quick brown fox jumps over the lazy dog.";

    /// <summary>
    /// The embedding generator for <paramref name="model"/> of <paramref name="provider"/> (a connection of kind <c>model-provider</c> with <c>purpose: embeddings</c>; plan 27a KN05): Ollama's own embed API for <c>ollama</c>, the embeddings API OpenAI, Microsoft Foundry and
    /// compatible servers share for the others. Anthropic has no embeddings API and is refused in words. <paramref name="apiKey"/> is the key the provider's secret reference resolved to (null for one that takes none).
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> Create(ModelProviderDefinition provider, ModelEntry model, string? apiKey, HttpClient http)
    {
        if (provider.Purpose != "embeddings")
            throw new ModelProviderException($"{provider.Name} is a provider for {provider.Purpose}, not for embeddings. Add a model-provider connection with `purpose: embeddings` for the embedding model.", false);
        if (provider.Wire == ModelWire.Anthropic)
            throw new ModelProviderException($"{provider.Name} is an Anthropic provider, and Anthropic has no embeddings API. Use Ollama, OpenAI, Microsoft Foundry or an OpenAI-compatible server for embeddings.", false);
        var options = new ProviderClientOptions(provider.Name, provider.Endpoint, model.Id, apiKey, provider.AuthStyle);
        return provider.Kind == "ollama" ? new OllamaEmbeddingGenerator(http, options) : new OpenAiCompatibleEmbeddingGenerator(http, options);
    }

    /// <summary>
    /// Embeds a sample sentence and reports how many dimensions the model gives, or why it could not (an unreachable server, a refused key, a model the server does not have), in words, with whether the failure is temporary. The key is in no part of the result.
    /// </summary>
    public static async Task<ProviderTestResult> TestAsync(ModelProviderDefinition provider, ModelEntry model, string? apiKey, HttpClient http, CancellationToken cancellation = default)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var generator = Create(provider, model, apiKey, http);
            var result = await generator.GenerateAsync([Sample], cancellationToken: cancellation).ConfigureAwait(false);
            var dimensions = result.Count == 1 ? result[0].Vector.Length : 0;
            if (dimensions == 0) return new ProviderTestResult(false, $"{provider.Name} answered, but gave no vector for the sample text. Check the model's id ({model.Id}): it must be an embedding model.", null, null, null, clock.ElapsedMilliseconds, false);
            return new ProviderTestResult(true, $"{provider.Name} embedded a sample with {model.Id}: {dimensions} dimensions.", $"{dimensions} dimensions", result.Usage?.InputTokenCount, null, clock.ElapsedMilliseconds, false, dimensions);
        }
        catch (ModelProviderException ex) { return new ProviderTestResult(false, ex.Message, null, null, null, clock.ElapsedMilliseconds, ex.Temporary); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new ProviderTestResult(false, $"{provider.Name} did not answer in time.", null, null, null, clock.ElapsedMilliseconds, true); }
    }

    internal static HttpRequestMessage Post(ProviderClientOptions options, string path, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint.TrimEnd('/') + path) { Content = new StringContent(body.ToJsonString(ProviderHttp.Compact), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            if (options.AuthStyle == "api-key") request.Headers.Add("api-key", options.ApiKey);
            else if (options.AuthStyle != "none") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        return request;
    }

    /// <summary>The vector of a JSON array of numbers, or null when it is not one.</summary>
    internal static float[]? Vector(JsonNode? node) =>
        node is JsonArray array && array.Count > 0 && array.All(n => n is JsonValue v && v.TryGetValue<double>(out _)) ? [.. array.Select(n => (float)n!.GetValue<double>())] : null;
}

/// <summary>
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> over the embeddings API (<c>POST {endpoint}/embeddings</c>) that OpenAI, Microsoft Foundry and compatible servers speak (plan 27a KN05): the request has <c>model</c> and <c>input</c> (the texts),
/// the answer <c>data</c> with an <c>embedding</c> and an <c>index</c> for each and <c>usage</c>. Written against the documented shapes; a server's error is classified as <see cref="ModelProviderException"/> does (temporary or not).
/// </summary>
public sealed class OpenAiCompatibleEmbeddingGenerator(HttpClient http, ProviderClientOptions options) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? generation = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        if (inputs.Count == 0) return [];
        var body = new JsonObject { ["model"] = generation?.ModelId ?? options.Model, ["input"] = new JsonArray([.. inputs.Select(i => (JsonNode)JsonValue.Create(i)!)]), ["encoding_format"] = "float" };
        if (generation?.Dimensions is { } dimensions) body["dimensions"] = dimensions;
        using var request = EmbeddingClients.Post(options, "/embeddings", body);
        using var response = await ProviderHttp.SendAsync(http, options.Provider, request, stream: false, cancellationToken, options.ApiKey).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException ex) { throw new ModelProviderException($"{options.Provider} answered with something that is not JSON. Check the endpoint: it should be the address of the embeddings API (ending in /v1).", false, response.StatusCode, ex); }
        if (root?["data"] is not JsonArray { Count: > 0 } data)
            throw new ModelProviderException($"{options.Provider} answered with no embeddings. Check the endpoint and that {options.Model} is an embedding model.", false, response.StatusCode);
        var ordered = data.OfType<JsonObject>().OrderBy(d => (int?)ProviderHttp.Long(d["index"]) ?? 0).ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var item in ordered)
        {
            var vector = EmbeddingClients.Vector(item["embedding"]) ?? throw new ModelProviderException($"{options.Provider} answered with an embedding that is not a list of numbers.", false, response.StatusCode);
            result.Add(new Embedding<float>(vector) { ModelId = (string?)root["model"] ?? options.Model });
        }
        if (result.Count != inputs.Count) throw new ModelProviderException($"{options.Provider} gave {result.Count} embeddings for {inputs.Count} texts.", false, response.StatusCode);
        if (root["usage"] is JsonObject u) result.Usage = ProviderHttp.Usage(ProviderHttp.Long(u["prompt_tokens"]), null);
        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata(options.Provider, new Uri(options.Endpoint), options.Model)
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> over Ollama's own embed API (<c>POST {address}/api/embed</c>, plan 27a KN05): the request has <c>model</c> and <c>input</c> (the texts), the answer <c>embeddings</c>, a list of vectors, and
/// <c>prompt_eval_count</c>. The address is the server's, with the <c>/v1</c> of its OpenAI-compatible chat endpoint taken off when the connection names that. A model the server has not pulled is a 404, told in words with the fix.
/// </summary>
public sealed class OllamaEmbeddingGenerator(HttpClient http, ProviderClientOptions options) : IEmbeddingGenerator<string, Embedding<float>>
{
    private ProviderClientOptions Native => options with { Endpoint = options.Endpoint.TrimEnd('/') is var e && e.EndsWith("/v1", StringComparison.Ordinal) ? e[..^3] : e };

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? generation = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        if (inputs.Count == 0) return [];
        var model = generation?.ModelId ?? options.Model;
        var body = new JsonObject { ["model"] = model, ["input"] = new JsonArray([.. inputs.Select(i => (JsonNode)JsonValue.Create(i)!)]) };
        if (generation?.Dimensions is { } dimensions) body["dimensions"] = dimensions;
        using var request = EmbeddingClients.Post(Native, "/api/embed", body);
        HttpResponseMessage response;
        try { response = await ProviderHttp.SendAsync(http, options.Provider, request, stream: false, cancellationToken, options.ApiKey).ConfigureAwait(false); }
        catch (ModelProviderException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            var said = ex.Message.IndexOf(" It said:", StringComparison.Ordinal);
            throw new ModelProviderException($"{options.Provider} has no embedding model '{model}' (or no /api/embed at this address). Pull the model on the server (`ollama pull {model}`) and check the address.{(said >= 0 ? ex.Message[said..] : "")}", false, ex.Status);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (JsonException ex) { throw new ModelProviderException($"{options.Provider} answered with something that is not JSON. Check the address: it should be the Ollama server's.", false, response.StatusCode, ex); }
            if (root?["embeddings"] is not JsonArray { Count: > 0 } list)
                throw new ModelProviderException($"{options.Provider} answered with no embeddings. Check that '{model}' is an embedding model.", false, response.StatusCode);
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var item in list)
                result.Add(new Embedding<float>(EmbeddingClients.Vector(item) ?? throw new ModelProviderException($"{options.Provider} answered with an embedding that is not a list of numbers.", false, response.StatusCode)) { ModelId = (string?)root["model"] ?? model });
            if (result.Count != inputs.Count) throw new ModelProviderException($"{options.Provider} gave {result.Count} embeddings for {inputs.Count} texts.", false, response.StatusCode);
            if (ProviderHttp.Long(root["prompt_eval_count"]) is { } tokens) result.Usage = ProviderHttp.Usage(tokens, null);
            return result;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata(options.Provider, new Uri(Native.Endpoint), options.Model)
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
