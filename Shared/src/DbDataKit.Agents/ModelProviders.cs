using DbDataKit.Config.Yaml;
using DbDataKit.Connections;

namespace DbDataKit.Agents;

/// <summary>Which wire a provider speaks: Anthropic's Messages API, or the chat completions API that OpenAI, Microsoft Foundry, Ollama and most local servers share.</summary>
public enum ModelWire
{
    Anthropic,
    OpenAi,
}

/// <summary>One model a provider allows: its id as the provider names it, its window and largest answer, and its price per million tokens (null when not priced, such as a local model).</summary>
public sealed record ModelEntry(string Id, int WindowTokens, int MaxOutputTokens, ModelPrice? Price);

/// <summary>
/// A model provider as an agent's runtime uses it (plan 27a AG05, plan 27 decision 15): a <b>connection of kind <c>model-provider</c></b> (plan 27a IX01) read into what the adapters need: the provider's wire (<see cref="Kind"/>: anthropic, openai, foundry, ollama or
/// openai-compatible), the endpoint, the reference of the key (never the key), the models it allows with their prices, whether it is a cloud or a local one and what it is for. <see cref="Name"/> is the connection's name.
/// </summary>
public sealed record ModelProviderDefinition(string Name, string Kind, string Endpoint, bool Cloud, string Purpose, string AuthStyle, string? ApiKeySecret, IReadOnlyList<ModelEntry> Models)
{
    public ModelWire Wire => Kind == "anthropic" ? ModelWire.Anthropic : ModelWire.OpenAi;

    public ModelEntry? Model(string id) => Models.FirstOrDefault(m => m.Id == id);
}

public sealed class ModelProviderResult(ModelProviderDefinition? definition, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public ModelProviderDefinition? Definition { get; } = definition;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Definition is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Reads a connection of kind <c>model-provider</c> into a <see cref="ModelProviderDefinition"/>. The shared <see cref="ConnectionDefinitionLoader"/> does the strict reading and every rule (the address, the key as a secret reference that is never echoed, the models); this adds
/// only what the runtime derives: the cloud or local locality, the authentication style and the defaults of a model's window and largest answer. The earlier stopgap, <c>model-provider/1</c> files in <c>models/</c>, is gone: it was never released.
/// </summary>
public static class ModelProviderConnection
{
    public static readonly string[] Kinds = ["anthropic", "openai", "foundry", "ollama", "openai-compatible"];

    public static readonly string[] Purposes = ["chat", "embeddings"];

    /// <summary>The provider of a connection file, or the loader's diagnostics; a file of another group (not a chat or embedding model provider) is refused naming the connection, its kind and the group accepted.</summary>
    public static ModelProviderResult Load(string yaml, string file)
    {
        var result = ConnectionDefinitionLoader.Load(yaml.TrimStart('\uFEFF'), file, ConnectionKindGroup.Model | ConnectionKindGroup.Embedding, addressRequired: false);
        if (result.Definition is not { } d || result.Diagnostics.HasErrors()) return new(null, result.Diagnostics);
        return new(From(d), result.Diagnostics);
    }

    /// <summary>The provider of a definition that loaded as kind <c>model-provider</c>.</summary>
    public static ModelProviderDefinition From(ConnectionDefinition d)
    {
        var m = d.ModelProvider ?? throw new ArgumentException("The connection has no model_provider section.", nameof(d));
        var endpoint = (d.Endpoint ?? ModelProviderSpec.DefaultEndpoint(m.Provider) ?? "").TrimEnd('/');
        var cloud = m.Locality is { } locality ? locality == "cloud" : m.Provider != "ollama";
        var key = d.SignIn?.Method == "api_key" ? d.SignIn.KeySecret : null;
        // a provider with no key takes none; one with a key sends it the way its provider does unless the file says how
        var auth = key is null ? "none" : m.AuthStyle ?? (m.Provider is "anthropic" or "foundry" ? "api-key" : "bearer");
        var models = m.Models.Select(x =>
        {
            var window = x.Window ?? 32_000;
            return new ModelEntry(x.Id, window, x.MaxOutput ?? Math.Min(4096, window), x.Price is { } p ? new ModelPrice(p.Input, p.Output) : null);
        }).ToList();
        return new ModelProviderDefinition(d.Name, m.Provider, endpoint, cloud, m.Purpose, auth, key, models);
    }
}
