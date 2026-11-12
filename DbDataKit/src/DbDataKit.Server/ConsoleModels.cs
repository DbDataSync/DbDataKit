using DbDataKit.Agents;
using DbDataKit.Agents.Providers;
using DbDataKit.Connections;
using DbDataKit.Connections.Bindings;
using DbDataKit.Knowledge;
using DbDataKit.Secrets;
using Microsoft.Extensions.AI;

namespace DbDataKit.Server;

/// <summary>The HTTP handler the model providers are called through; null is the platform's own. A test gives one that answers from a recording.</summary>
public sealed class ModelHttp(HttpMessageHandler? handler = null)
{
    public HttpMessageHandler? Handler { get; } = handler;
}

/// <summary>The names of the slots the suite itself binds (an agent's own slot is named in its definition).</summary>
public static class ModelSlotNames
{
    /// <summary>The slot a channel's knowledge index is embedded with (<see cref="BindingsLoader.EmbeddingsSlot"/>).</summary>
    public const string Embeddings = BindingsLoader.EmbeddingsSlot;
}

public sealed record ModelView(string Id, int Window, int MaxOutput, decimal? PriceInput, decimal? PriceOutput);

/// <summary>A provider as the screen lists it: its file, what the reader says, and whether its key can be read (never the key).</summary>
public sealed record ProviderView(string Name, string File, string Yaml, string? Kind, string? Endpoint, bool? Cloud, string? Purpose, string? KeyReference, bool KeyAvailable, string? KeyProblem,
    IReadOnlyList<ModelView> Models, IReadOnlyList<DiagnosticView> Diagnostics);

/// <summary>The models screen's picture: the providers and the slots the agents ask for with whether each is bound. The bindings themselves are the Bindings screen's (<see cref="ConsoleBindings"/>).</summary>
public sealed record ModelsView(IReadOnlyList<ProviderView> Providers, IReadOnlyList<AgentSlotView> AgentSlots, string SecretPrefix);

public sealed record AgentSlotView(string Slot, IReadOnlyList<string> Agents, bool Bound);

/// <summary>
/// The model providers and slots of the console (plan 27a AG05): a provider is a <b>connection of kind <c>model-provider</c></b> (<c>connections/&lt;name&gt;.yml</c>, kept by <see cref="ConsoleConnections"/>, plan 27a IX01) and a slot is a
/// <b>binding</b> (<c>bindings.yml</c>, section <c>models:</c>, kept by <see cref="ConsoleBindings"/>, plan 27a IX23: one reader, one rule for every slot of the suite); this builds the <see cref="IAgentModels"/> the agents use from them. A provider's key is a secret reference; the key itself is kept in the console's secret store (<see cref="ConsoleConnections.SecretNameFor"/>) when a person types it, and is never in a file, a view, an audit row or a message. <b>Fallback:</b> a slot bound to several
/// models is tried in order with back-off (<see cref="FallbackChatClient"/>); the install policy's rule (<c>data-rules: cloud-providers: fallback: refuse</c>, a tenant-only install, plan 27a AG21) keeps a cloud provider out of every position after the first; the file is part of the cache stamp, so a change is seen by the next call.
/// </summary>
public sealed class ConsoleModels : IAgentModels
{
    private readonly string _directory;
    private readonly ConsoleConnections _connections;
    private readonly ConsoleBindings _bindings;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private Dictionary<(string Slot, string? Channel), BoundModel?> _cache = [];
    private string _stamp = "";

    public ConsoleModels(ConsoleSettings settings, ConsoleConnections connections, ConsoleBindings bindings, ModelHttp? http = null)
    {
        _directory = settings.Directory;
        _connections = connections;
        _bindings = bindings;
        // a change to a connection, or a secret typed for one, or to the bindings is seen by the next call
        connections.Changed += () => { lock (_lock) { _cache = []; } };
        bindings.Changed += () => { lock (_lock) { _cache = []; } };
        _http = new HttpClient(http?.Handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(120) };
    }

    private string PolicyFile => Path.Combine(_directory, "agent-policy.yml");

    /// <summary>The install policy as far as the models need it (the rule on falling to a cloud provider); a policy that does not read binds nothing extra.</summary>
    private AgentPolicy Policy() => File.Exists(PolicyFile) && AgentPolicyLoader.Load(File.ReadAllText(PolicyFile), "agent-policy.yml").Policy is { } p ? p : AgentPolicy.None;

    private string Stamp() =>
        (File.Exists(PolicyFile) ? File.GetLastWriteTimeUtc(PolicyFile).Ticks + ":" + new FileInfo(PolicyFile).Length : "") + "#" + _connections.Stamp() + "#" + _bindings.Stamp();

    // ---- reading ----

    /// <summary>Every connection file of kind <c>model-provider</c>, read; a file that does not read is listed with its diagnostics and has no definition. Other kinds are not providers and are left out.</summary>
    public IReadOnlyList<(string Name, string Yaml, ModelProviderResult Result)> ReadProviders()
    {
        var list = new List<(string, string, ModelProviderResult)>();
        foreach (var (name, yaml, result) in _connections.Read())
        {
            // a file that reads and is of another kind is not a provider; one that does not read is shown, with its faults, if it says it is one
            if (result.Definition is { } d) { if (d.KindName == ConnectionKinds.ModelProvider) list.Add((name, yaml, new ModelProviderResult(ModelProviderConnection.From(d), result.Diagnostics))); }
            else if (yaml.Contains("kind: model-provider", StringComparison.Ordinal)) list.Add((name, yaml, new ModelProviderResult(null, result.Diagnostics)));
        }
        return list;
    }

    private IReadOnlyDictionary<string, ModelProviderDefinition> Definitions() =>
        ReadProviders().Where(p => p.Result.Ok).ToDictionary(p => p.Name, p => p.Result.Definition!, StringComparer.Ordinal);

    /// <summary>The key a provider's reference resolves to; null for a provider that takes none; a problem in words when it cannot be read.</summary>
    private (string? Key, string? Problem) Key(ModelProviderDefinition provider)
    {
        if (provider.ApiKeySecret is null) return (null, provider.AuthStyle == "none" ? null : $"{provider.Name} has no key (`sign_in.key_secret`).");
        try
        {
            using var value = _connections.Secrets.ResolveAsync(SecretRef.Parse(provider.ApiKeySecret)).AsTask().GetAwaiter().GetResult();
            return (value.RevealString(), null);
        }
        catch (SecretNotFoundException) { return (null, $"The key `{provider.ApiKeySecret}` is not set. Type the key on the Models screen (it is kept in the console's secret store), or set the secret or variable that names."); }
    }

    // ---- IAgentModels ----

    /// <summary>The model a slot is bound to in a channel (null channel: the install's own), with the fallback list behind it; null when nothing usable is bound.</summary>
    public BoundModel? For(string slot, string? channel)
    {
        lock (_lock)
        {
            var stamp = Stamp();
            if (stamp != _stamp) { _cache = []; _stamp = stamp; }
            if (_cache.TryGetValue((slot, channel), out var cached)) return cached;
            return _cache[(slot, channel)] = Build(slot, channel);
        }
    }

    private BoundModel? Build(string slot, string? channel)
    {
        var providers = Definitions();
        var policy = Policy();
        var usable = new List<(ModelBinding Binding, ModelEntry Model, ModelProviderDefinition Provider)>();
        foreach (var choice in _bindings.Current.Resolve(SlotKind.ModelChat, slot, channel).Entries)
        {
            if (!providers.TryGetValue(choice.Connection, out var provider) || provider.Purpose != "chat" || choice.Model is null || provider.Model(choice.Model) is not { } model) continue;
            var (key, problem) = Key(provider);
            if (problem is not null) continue;
            usable.Add((new ModelBinding($"{provider.Name}/{model.Id}", ProviderClients.Create(provider, model, key, _http), provider.Cloud), model, provider));
        }
        if (usable.Count == 0) return null;
        var first = usable[0];
        IChatClient client = usable.Count == 1
            ? first.Binding.Client
            : new FallbackChatClient([.. usable.Select(u => u.Binding)], new AgentRunContext("console", slot, "1"), null, b => policy.AllowsFallbackTo(b.Cloud), ModelProviderException.WorthRetrying);
        return new BoundModel(client, $"{first.Provider.Name}/{first.Model.Id}", first.Model.Price, first.Model.WindowTokens, usable.ToDictionary(u => u.Binding.Name, u => u.Model.Price, StringComparer.Ordinal));
    }

    // ---- the screen ----

    public ModelsView View(IEnumerable<(string Slot, string Agent)> agentSlots)
    {
        var providers = new List<ProviderView>();
        foreach (var (name, yaml, result) in ReadProviders())
        {
            var d = result.Definition;
            var (keyOk, keyProblem) = d is null ? (false, null) : KeyState(d);
            providers.Add(new ProviderView(name, $"connections/{name}.yml", yaml, d?.Kind, d?.Endpoint, d?.Cloud, d?.Purpose, d?.ApiKeySecret, keyOk, keyProblem,
                [.. (d?.Models ?? []).Select(m => new ModelView(m.Id, m.WindowTokens, m.MaxOutputTokens, m.Price?.InputPerMillion, m.Price?.OutputPerMillion))], [.. result.Diagnostics.Select(DiagnosticView.Of)]));
        }
        var agentView = agentSlots.GroupBy(a => a.Slot, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AgentSlotView(g.Key, [.. g.Select(a => a.Agent).Distinct().Order(StringComparer.Ordinal)], For(g.Key, null) is not null)).ToList();
        return new ModelsView(providers, agentView, "secret:dbdatakit:server:connection:<name>:key");
    }

    private (bool Ok, string? Problem) KeyState(ModelProviderDefinition provider)
    {
        var (_, problem) = Key(provider);
        return (problem is null, problem);
    }

    /// <summary>Whether a binding's connection and model can be used now: the connection is a provider that reads, it allows the model, and its key can be read; the problem in words when not (the Bindings screen shows it).</summary>
    public (bool Usable, string? Problem) EntryState(string connection, string? model)
    {
        if (!Definitions().TryGetValue(connection, out var provider)) return (false, $"There is no provider `{connection}` that reads.");
        if (model is null || provider.Model(model) is null) return (false, $"`{connection}` does not allow `{model}`.");
        var (_, problem) = Key(provider);
        return (problem is null, problem);
    }

    // ---- embeddings (plan 27a KN05, KN08) ----

    /// <summary>The embedding model a channel's knowledge index is bound to, for the screen: what the <c>embeddings</c> slot names, whether it can be used now and why not.</summary>
    public sealed record EmbeddingBindingView(string? Provider, string? Model, string? ModelId, bool Usable, string? Problem);

    /// <summary>What the <c>embeddings</c> slot is bound to in a channel (the first choice; there is no fallback for an index, one model per index) and whether it can be used now.</summary>
    public EmbeddingBindingView EmbeddingBinding(string? channel)
    {
        var (_, read) = _bindings.Read();
        if (read.Bindings is not { } bindings) return new(null, null, null, false, "The bindings file does not read; fix it on the Bindings screen.");
        if (bindings.Resolve(SlotKind.ModelEmbeddings, ModelSlotNames.Embeddings, channel).Primary is not { } choice) return new(null, null, null, false, null);
        var id = $"{choice.Connection}/{choice.Model}";
        if (!Definitions().TryGetValue(choice.Connection, out var provider)) return new(choice.Connection, choice.Model, id, false, $"There is no provider `{choice.Connection}` that reads.");
        if (provider.Purpose != "embeddings") return new(choice.Connection, choice.Model, id, false, $"`{choice.Connection}` is for {provider.Purpose}, not embeddings.");
        if (choice.Model is null || provider.Model(choice.Model) is null) return new(choice.Connection, choice.Model, id, false, $"`{choice.Connection}` does not allow `{choice.Model}`.");
        var (_, problem) = Key(provider);
        return new(choice.Connection, choice.Model, id, problem is null, problem);
    }

    /// <summary>
    /// Binds the <c>embeddings</c> slot to a model of an embedding provider for every release channel (<c>*</c>; <paramref name="provider"/> null unbinds it), keeping any binding the slot has for one release channel only. The bindings file is written again from what it holds
    /// (its comments are not kept); one that does not read is not touched. The model of an index is one: there is no fallback list for embeddings.
    /// </summary>
    public (bool Saved, IReadOnlyList<DiagnosticView> Diagnostics) BindEmbedding(string? provider, string? model, string actor = "console")
    {
        var (_, read) = _bindings.Read();
        if (read.Bindings is not { } current) return (false, [.. read.Diagnostics.Select(DiagnosticView.Of)]);
        var channels = (current.Find(SlotKind.ModelEmbeddings, ModelSlotNames.Embeddings)?.PerChannel ?? new Dictionary<string, IReadOnlyList<BindingEntry>>())
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        if (provider is null || model is null) channels.Remove(BindingSet.AnyReleaseChannel);
        else channels[BindingSet.AnyReleaseChannel] = [new BindingEntry(provider, model)];
        var outcome = _bindings.SetSlot("models", ModelSlotNames.Embeddings, channels, null, actor, null);
        return (outcome.Saved, outcome.Diagnostics);
    }

    /// <summary>The generator of the embedding model a channel is bound to, with the id the index records it under (<c>connection/model</c>); null when nothing usable is bound.</summary>
    public (IEmbeddingGenerator<string, Embedding<float>> Generator, string ModelId)? EmbeddingFor(string? channel)
    {
        var view = EmbeddingBinding(channel);
        if (!view.Usable || view.ModelId is null) return null;
        return EmbeddingById(view.ModelId) is { } generator ? (generator, view.ModelId) : null;
    }

    /// <summary>The generator for an id the index recorded (<c>connection/model</c>), as long as that connection and model still exist and its key can be read: how a question is embedded for an index made with an earlier model than the one now bound.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingById(string modelId)
    {
        var slash = modelId.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !Definitions().TryGetValue(modelId[..slash], out var provider) || provider.Purpose != "embeddings" || provider.Model(modelId[(slash + 1)..]) is not { } model) return null;
        var (key, problem) = Key(provider);
        return problem is null ? EmbeddingClients.Create(provider, model, key, _http) : null;
    }

    /// <summary>Calls the provider's model with a one-word request and says what came of it (for an embedding provider: embeds a sample and says how many dimensions it gives); the key never appears in the result.</summary>
    public async Task<ProviderTestResult> TestAsync(string name, string modelId, CancellationToken cancellation = default)
    {
        var read = ReadProviders().FirstOrDefault(p => p.Name == name);
        if (read.Result?.Definition is not { } provider) return new ProviderTestResult(false, read.Result is null ? $"There is no provider named '{name}'." : $"The provider '{name}' has errors in its file; fix them first.", null, null, null, 0, false);
        if (provider.Model(modelId) is not { } model) return new ProviderTestResult(false, $"'{name}' does not allow the model '{modelId}'. It allows: {string.Join(", ", provider.Models.Select(m => m.Id))}.", null, null, null, 0, false);
        var (key, problem) = Key(provider);
        if (problem is not null) return new ProviderTestResult(false, problem, null, null, null, 0, false);
        return provider.Purpose == "embeddings"
            ? await EmbeddingClients.TestAsync(provider, model, key, _http, cancellation).ConfigureAwait(false)
            : await ProviderClients.TestAsync(provider, model, key, _http, cancellation).ConfigureAwait(false);
    }
}
