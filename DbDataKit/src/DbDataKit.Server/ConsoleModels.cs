using DbDataKit.Agents;
using DbDataKit.Agents.Providers;
using DbDataKit.Knowledge;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using Microsoft.Extensions.AI;

namespace DbDataKit.Server;

/// <summary>The HTTP handler the model providers are called through; null is the platform's own. A test gives one that answers from a recording.</summary>
public sealed class ModelHttp(HttpMessageHandler? handler = null)
{
    public HttpMessageHandler? Handler { get; } = handler;
}

public sealed record ModelView(string Id, int Window, int MaxOutput, decimal? PriceInput, decimal? PriceOutput);

/// <summary>A provider as the screen lists it: its file, what the reader says, and whether its key can be read (never the key).</summary>
public sealed record ProviderView(string Name, string File, string Yaml, string? Title, string? Kind, string? Endpoint, bool? Cloud, string? Purpose, string? KeyReference, bool KeyAvailable, string? KeyProblem,
    IReadOnlyList<ModelView> Models, IReadOnlyList<DiagnosticView> Diagnostics);

/// <summary>What a slot is bound to in one channel, in order, and whether each choice can be used now.</summary>
public sealed record SlotChoiceView(string Provider, string Model, bool Usable, string? Problem);

public sealed record SlotView(string Slot, string Channel, IReadOnlyList<SlotChoiceView> Choices);

/// <summary>The models screen's whole picture: providers, the bindings with their diagnostics, and the slots the agents ask for with whether each is bound.</summary>
public sealed record ModelsView(IReadOnlyList<ProviderView> Providers, string SlotsYaml, IReadOnlyList<DiagnosticView> SlotDiagnostics, IReadOnlyList<SlotView> Bindings, IReadOnlyList<AgentSlotView> AgentSlots, string SecretPrefix);

public sealed record AgentSlotView(string Slot, IReadOnlyList<string> Agents, bool Bound);

/// <summary>
/// The model providers and slots of the console (plan 27a AG05): <c>models/&lt;name&gt;.yml</c> (<c>model-provider/1</c>) and <c>models/_slots.yml</c> (<c>model-slots/1</c>) in the console's directory, and the <see cref="IAgentModels"/> the agents use, built from them. A provider's
/// key is a secret reference; the key itself is kept in the console's secret store (<see cref="SecretNameFor"/>) when a person types it here, and is never in a file, a view, an audit row or a message. <b>Fallback:</b> a slot bound to several
/// models is tried in order with back-off (<see cref="FallbackChatClient"/>); the install policy's say on falling to a cloud provider (plan 27a AG21) is not read yet, so every choice may be used.
/// </summary>
public sealed class ConsoleModels : IAgentModels
{
    private readonly string _directory;
    private readonly EncryptedFileSecretStore _store;
    private readonly ISecretResolver _secrets;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private Dictionary<(string Slot, string? Channel), BoundModel?> _cache = [];
    private string _stamp = "";

    public ConsoleModels(ConsoleSettings settings, EncryptedFileSecretStore store, ModelHttp? http = null)
    {
        _directory = settings.Directory;
        _store = store;
        _secrets = new SecretResolverChain([store, new EnvSecretProvider()]);
        _http = new HttpClient(http?.Handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public string Folder => Path.Combine(_directory, "models");

    public string SlotsFile => Path.Combine(Folder, "_slots.yml");

    /// <summary>Where a person's pasted key for a provider is kept.</summary>
    public static SecretName SecretNameFor(string provider) => new("server", "model-provider", provider, "key");

    /// <summary>The reference to write in a provider's file for the key kept here.</summary>
    public static string ReferenceFor(string provider) => "secret:" + SecretNameFor(provider);

    private string FileOf(string name) => Path.Combine(Folder, name + ".yml");

    private string Stamp() =>
        !Directory.Exists(Folder) ? "" : string.Join('|', Directory.EnumerateFiles(Folder, "*.yml").Order(StringComparer.Ordinal).Select(f => $"{Path.GetFileName(f)}:{File.GetLastWriteTimeUtc(f).Ticks}:{new FileInfo(f).Length}"));

    // ---- reading ----

    /// <summary>Every provider file, read; a file that does not read is listed with its diagnostics and has no definition.</summary>
    public IReadOnlyList<(string Name, string Yaml, ModelProviderResult Result)> ReadProviders()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<(string, string, ModelProviderResult)>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.yml").Where(f => !Path.GetFileName(f).StartsWith('_')).Order(StringComparer.Ordinal))
        {
            var yaml = File.ReadAllText(file);
            var name = Path.GetFileNameWithoutExtension(file);
            var result = ModelProviderLoader.Load(yaml, $"models/{name}.yml");
            if (result.Definition is { } d && d.Name != name)
                result = new ModelProviderResult(null, [.. result.Diagnostics, DbDataKit.Config.Yaml.DefaultDiagnosticCatalog.Instance.Create(ModelProviderLoader.BadProvider, $"models/{name}.yml", 1, 1, $"The file is named {name}.yml but the provider is `{d.Name}`.", "A provider's file is named after it.")]);
            list.Add((name, yaml, result));
        }
        return list;
    }

    private IReadOnlyDictionary<string, ModelProviderDefinition> Definitions() =>
        ReadProviders().Where(p => p.Result.Ok).ToDictionary(p => p.Name, p => p.Result.Definition!, StringComparer.Ordinal);

    public (string Yaml, SlotBindingsResult Result) ReadSlots()
    {
        var yaml = File.Exists(SlotsFile) ? File.ReadAllText(SlotsFile) : "";
        return (yaml, SlotBindingsLoader.Load(yaml, "models/_slots.yml"));
    }

    /// <summary>The key a provider's reference resolves to; null for a provider that takes none; a problem in words when it cannot be read.</summary>
    private (string? Key, string? Problem) Key(ModelProviderDefinition provider)
    {
        if (provider.ApiKeySecret is null) return (null, provider.AuthStyle == "none" ? null : $"{provider.Name} has no `api-key-secret`.");
        try
        {
            using var value = _secrets.ResolveAsync(SecretRef.Parse(provider.ApiKeySecret)).AsTask().GetAwaiter().GetResult();
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
        var (_, slots) = ReadSlots();
        if (slots.Bindings is not { } bindings) return null;
        var providers = Definitions();
        var usable = new List<(ModelBinding Binding, ModelEntry Model, ModelProviderDefinition Provider)>();
        foreach (var choice in bindings.For(slot, channel))
        {
            if (!providers.TryGetValue(choice.Provider, out var provider) || provider.Purpose != "chat" || provider.Model(choice.Model) is not { } model) continue;
            var (key, problem) = Key(provider);
            if (problem is not null) continue;
            usable.Add((new ModelBinding($"{provider.Name}/{model.Id}", ProviderClients.Create(provider, model, key, _http), provider.Cloud), model, provider));
        }
        if (usable.Count == 0) return null;
        var first = usable[0];
        IChatClient client = usable.Count == 1
            ? first.Binding.Client
            : new FallbackChatClient([.. usable.Select(u => u.Binding)], new AgentRunContext("console", slot, "1"), null, null, ModelProviderException.WorthRetrying);
        return new BoundModel(client, $"{first.Provider.Name}/{first.Model.Id}", first.Model.Price, first.Model.WindowTokens);
    }

    // ---- the screen ----

    public ModelsView View(IEnumerable<(string Slot, string Agent)> agentSlots)
    {
        var providers = new List<ProviderView>();
        foreach (var (name, yaml, result) in ReadProviders())
        {
            var d = result.Definition;
            var (keyOk, keyProblem) = d is null ? (false, null) : KeyState(d);
            providers.Add(new ProviderView(name, $"models/{name}.yml", yaml, d?.Title, d?.Kind, d?.Endpoint, d?.Cloud, d?.Purpose, d?.ApiKeySecret, keyOk, keyProblem,
                [.. (d?.Models ?? []).Select(m => new ModelView(m.Id, m.WindowTokens, m.MaxOutputTokens, m.Price?.InputPerMillion, m.Price?.OutputPerMillion))], [.. result.Diagnostics.Select(DiagnosticView.Of)]));
        }
        var (slotsYaml, slots) = ReadSlots();
        var defined = Definitions();
        var slotDiagnostics = slots.Diagnostics.Select(DiagnosticView.Of).ToList();
        var views = new List<SlotView>();
        if (slots.Bindings is { } b)
        {
            slotDiagnostics.AddRange(SlotBindingsLoader.Check(b, defined, "models/_slots.yml").Select(DiagnosticView.Of));
            foreach (var (slot, channels) in b.Slots.OrderBy(s => s.Key, StringComparer.Ordinal))
                foreach (var (channel, choices) in channels.OrderBy(c => c.Key, StringComparer.Ordinal))
                    views.Add(new SlotView(slot, channel, [.. choices.Select(c => Usable(c, defined))]));
        }
        var agentView = agentSlots.GroupBy(a => a.Slot, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AgentSlotView(g.Key, [.. g.Select(a => a.Agent).Distinct().Order(StringComparer.Ordinal)], For(g.Key, null) is not null)).ToList();
        return new ModelsView(providers, slotsYaml, slotDiagnostics, views, agentView, "secret:dbdatakit:server:model-provider:<name>:key");
    }

    private (bool Ok, string? Problem) KeyState(ModelProviderDefinition provider)
    {
        var (_, problem) = Key(provider);
        return (problem is null, problem);
    }

    private SlotChoiceView Usable(SlotChoice choice, IReadOnlyDictionary<string, ModelProviderDefinition> providers)
    {
        if (!providers.TryGetValue(choice.Provider, out var provider)) return new SlotChoiceView(choice.Provider, choice.Model, false, $"There is no provider `{choice.Provider}` that reads.");
        if (provider.Model(choice.Model) is null) return new SlotChoiceView(choice.Provider, choice.Model, false, $"`{choice.Provider}` does not allow `{choice.Model}`.");
        var (_, problem) = Key(provider);
        return new SlotChoiceView(choice.Provider, choice.Model, problem is null, problem);
    }

    // ---- writing ----

    /// <summary>What a provider file says before it is saved: the diagnostics of the reader.</summary>
    public (ModelProviderResult Result, IReadOnlyList<DiagnosticView> Diagnostics) CheckProvider(string name, string yaml)
    {
        var result = ModelProviderLoader.Load(yaml, $"models/{name}.yml");
        var diagnostics = result.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (result.Definition is { } d && d.Name != name)
            diagnostics.Add(new DiagnosticView(ModelProviderLoader.BadProvider, "error", $"models/{name}.yml", 1, 1, $"The file is named {name}.yml but the provider is `{d.Name}`.", "A provider's file is named after it."));
        return (result, diagnostics);
    }

    public (bool Saved, IReadOnlyList<DiagnosticView> Diagnostics) SaveProvider(string name, string yaml)
    {
        var (_, diagnostics) = CheckProvider(name, yaml);
        if (diagnostics.Any(d => d.Severity == "error")) return (false, diagnostics);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileOf(name), yaml);
        return (true, diagnostics);
    }

    public bool DeleteProvider(string name)
    {
        var file = FileOf(name);
        if (!ModelProviderLoader.IsName(name) || !File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    public async Task StoreKeyAsync(string provider, string key, CancellationToken cancellation = default)
    {
        using var value = SecretValue.From(key);
        await _store.SetAsync(SecretNameFor(provider), value, cancellation).ConfigureAwait(false);
        lock (_lock) { _cache = []; }
    }

    public (SlotBindingsResult Result, IReadOnlyList<DiagnosticView> Diagnostics) CheckSlots(string yaml)
    {
        var result = SlotBindingsLoader.Load(yaml, "models/_slots.yml");
        var diagnostics = result.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (result.Bindings is { } b) diagnostics.AddRange(SlotBindingsLoader.Check(b, Definitions(), "models/_slots.yml").Select(DiagnosticView.Of));
        return (result, diagnostics);
    }

    public (bool Saved, IReadOnlyList<DiagnosticView> Diagnostics) SaveSlots(string yaml)
    {
        var (_, diagnostics) = CheckSlots(yaml);
        if (diagnostics.Any(d => d.Severity == "error")) return (false, diagnostics);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SlotsFile, yaml);
        return (true, diagnostics);
    }

    /// <summary>Calls the provider's model with a one-word request and says what came of it; the key never appears in the result.</summary>
    public async Task<ProviderTestResult> TestAsync(string name, string modelId, CancellationToken cancellation = default)
    {
        var read = ReadProviders().FirstOrDefault(p => p.Name == name);
        if (read.Result?.Definition is not { } provider) return new ProviderTestResult(false, read.Result is null ? $"There is no provider named '{name}'." : $"The provider '{name}' has errors in its file; fix them first.", null, null, null, 0, false);
        if (provider.Model(modelId) is not { } model) return new ProviderTestResult(false, $"'{name}' does not allow the model '{modelId}'. It allows: {string.Join(", ", provider.Models.Select(m => m.Id))}.", null, null, null, 0, false);
        var (key, problem) = Key(provider);
        if (problem is not null) return new ProviderTestResult(false, problem, null, null, null, 0, false);
        return await ProviderClients.TestAsync(provider, model, key, _http, cancellation).ConfigureAwait(false);
    }
}
