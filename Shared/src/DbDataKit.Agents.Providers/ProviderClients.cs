using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Providers;

/// <summary>What a Test of a provider found, in words a person reads: it worked and what the model answered, or why it did not.</summary>
public sealed record ProviderTestResult(bool Ok, string Message, string? Answer, long? InputTokens, long? OutputTokens, long Milliseconds, bool Temporary);

/// <summary>Makes the client for a provider's model (plan 27a AG04, AG05) and tests it.</summary>
public static class ProviderClients
{
    /// <summary>The client for <paramref name="model"/> of <paramref name="provider"/>; <paramref name="apiKey"/> is the key the provider's secret reference resolved to (null for a provider that takes none).</summary>
    public static IChatClient Create(ModelProviderDefinition provider, ModelEntry model, string? apiKey, HttpClient http)
    {
        var options = new ProviderClientOptions(provider.Name, provider.Endpoint, model.Id, apiKey, provider.AuthStyle, model.MaxOutputTokens, UsesMaxCompletionTokens: provider.Kind is "openai");
        return provider.Wire == ModelWire.Anthropic ? new AnthropicChatClient(http, options) : new OpenAiCompatibleChatClient(http, options);
    }

    /// <summary>
    /// Asks the model to say one word and reports what happened. A failure is told in words (an unreachable endpoint, a refused key, an unknown model) and says whether it is temporary; the key is in no part of the result.
    /// </summary>
    public static async Task<ProviderTestResult> TestAsync(ModelProviderDefinition provider, ModelEntry model, string? apiKey, HttpClient http, CancellationToken cancellation = default)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var client = Create(provider, model, apiKey, http);
            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with the single word: ok")], new ChatOptions { MaxOutputTokens = 32 }, cancellation).ConfigureAwait(false);
            var answer = response.Text.Trim();
            return new ProviderTestResult(true, $"{provider.Name} answered with {model.Id}.", answer.Length > 200 ? answer[..200] : answer, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, clock.ElapsedMilliseconds, false);
        }
        catch (ModelProviderException ex) { return new ProviderTestResult(false, ex.Message, null, null, null, clock.ElapsedMilliseconds, ex.Temporary); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new ProviderTestResult(false, $"{provider.Name} did not answer in time.", null, null, null, clock.ElapsedMilliseconds, true); }
    }
}
