using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Delivery;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using Microsoft.Extensions.Hosting;

namespace DbDataKit.Server;

/// <summary>The <c>delivery:</c> section of <c>settings.yaml</c> in the console's directory (plan 22a GP07), read strictly by <see cref="DeliverySettings"/>. A file with no section is no channels; a section that is wrong is a <see cref="Problem"/> the screen shows (the console keeps starting).</summary>
public static class ConsoleDeliverySettings
{
    public const string Section = "delivery";

    /// <summary>The settings, or the reason they could not be read (empty settings then).</summary>
    public static (DeliverySettings Settings, string? Problem) Load(string consoleDirectory, ChannelKinds kinds)
    {
        var path = ConsoleState.SettingsFileIn(consoleDirectory);
        if (!File.Exists(path)) return (DeliverySettings.Empty, null);
        try
        {
            var diagnostics = new List<ConfigDiagnostic>();
            var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics);
            if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error) return (DeliverySettings.Empty, $"{path}: the file is not valid YAML ({error.Message}, line {error.Line}).");
            if (root is not YamlMapping mapping || mapping.GetEntry(Section) is not { } entry) return (DeliverySettings.Empty, null);
            if (entry.Value is YamlScalar { Value.Length: 0 }) return (DeliverySettings.Empty, null);
            return (DeliverySettings.Read(ToJson(entry.Value) as JsonObject ?? throw new DeliverySettingsException($"{path}: delivery is a set of settings (retry, lease_seconds, channels)."), path, kinds), null);
        }
        catch (DeliverySettingsException e)
        {
            return (DeliverySettings.Empty, e.Message);
        }
    }

    /// <summary>A YAML node as JSON: an unquoted true or false is a boolean, an unquoted whole number a number, an empty unquoted value or <c>null</c> nothing.</summary>
    public static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMapping m => new JsonObject(m.Entries.Select(e => KeyValuePair.Create(e.Key.Value, ToJson(e.Value)))),
        YamlSequence s => new JsonArray([.. s.Items.Select(ToJson)]),
        YamlScalar { Quoted: false, Value: "true" } => JsonValue.Create(true),
        YamlScalar { Quoted: false, Value: "false" } => JsonValue.Create(false),
        YamlScalar { Quoted: false, Value: "" or "null" or "~" } => null,
        YamlScalar { Quoted: false } s when long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n is >= int.MinValue and <= int.MaxValue => JsonValue.Create((int)n),
        YamlScalar s => JsonValue.Create(s.Value),
        _ => null,
    };
}

/// <summary>A channel as the screen shows it: no secret, only whether every reference it holds can be read now.</summary>
public sealed record ChannelView(string Name, string Kind, int? LimitPerMinute, bool Ready, IReadOnlyList<string> Missing, IReadOnlyDictionary<string, string> Settings, IReadOnlyDictionary<string, int> Outbox);

/// <summary>Who the console's users are to the subscriptions: their address is the one the account has.</summary>
public sealed class ConsoleSubscriberDirectory(IUserAccounts users) : ISubscriberDirectory
{
    public Task<SubscriberContact?> FindAsync(string person, CancellationToken cancellationToken) =>
        Task.FromResult(users.Get(person) is { } u ? new SubscriberContact(u.Id, u.DisplayName, u.Email, u.Enabled) : null);
}

/// <summary>The console draws no document: subscriptions are rendered where the views module runs (the Gateway), so "send now" is not offered here and a run that reaches this renderer says so.</summary>
public sealed class ConsoleNoRenderer : ISubscriptionRenderer
{
    public const string Words = "This console does not draw documents: a subscription is rendered by the process that runs the views module (the Gateway) with the views-export task, so there is nothing here to render it.";

    public Task<RenderResult> RenderAsync(SubscriptionRender request, CancellationToken cancellationToken) => Task.FromResult<RenderResult>(new RenderResult.Failed("unavailable", Words));
}

/// <summary>
/// The delivery service as the console hosts it (plan 22a GP07 to GP09, GP17, GP18): the install's <c>delivery:</c> channels from <c>settings.yaml</c> with their secret references resolved from the console's secret store (then the environment), the outbox and
/// the subscriptions in the console's state (the module <c>delivery</c>), and a sender that works through the outbox. Subscriptions are kept here; they are <b>rendered</b> by whatever process has the views module and runs the <c>views-export</c> task over the same state, and
/// the console says so (<see cref="CanRender"/>).
/// </summary>
public sealed class ConsoleDelivery
{
    public ConsoleDelivery(ConsoleSettings settings, StateDatabase state, EncryptedFileSecretStore secrets, IUserAccounts users, ISubscriptionRenderer? renderer = null, TimeProvider? time = null)
    {
        Kinds = DeliveryKinds.Default();
        (Settings, Problem) = ConsoleDeliverySettings.Load(settings.Directory, Kinds);
        Time = time ?? TimeProvider.System;
        Resolver = new SecretResolverChain([secrets, new EnvSecretProvider()]);
        Scrubber = new SecretScrubber();
        try
        {
            Channels = new DeliveryChannels(Settings, Kinds, Resolver, Scrubber, Time);
        }
        catch (DeliverySettingsException e)
        {
            Problem = e.Message;
            Settings = DeliverySettings.Empty;
            Channels = new DeliveryChannels(Settings, Kinds, Resolver, Scrubber, Time);
        }

        Outbox = new DeliveryStore(state);
        Subscriptions = new SubscriptionStore(state);
        Service = new DeliveryService(Outbox, new PayloadStore(state), Channels, Settings.Policy, Scrubber, Time, null, "console");
        CanRender = renderer is not null;
        Runner = new SubscriptionService(Subscriptions, Service, renderer ?? new ConsoleNoRenderer(), new ConsoleSubscriberDirectory(users), Time);
        Users = users;
    }

    public ChannelKinds Kinds { get; }

    public DeliverySettings Settings { get; }

    /// <summary>Why the <c>delivery:</c> section could not be used, or null.</summary>
    public string? Problem { get; }

    public TimeProvider Time { get; }

    public ISecretResolver Resolver { get; }

    public SecretScrubber Scrubber { get; }

    public IDeliveryChannels Channels { get; }

    public DeliveryStore Outbox { get; }

    public SubscriptionStore Subscriptions { get; }

    public DeliveryService Service { get; }

    public SubscriptionService Runner { get; }

    public IUserAccounts Users { get; }

    /// <summary>Whether this process can draw a subscription's document (it has a renderer of the views module).</summary>
    public bool CanRender { get; }

    /// <summary>The channels with their readiness (every secret reference resolves now) and what the outbox holds for each.</summary>
    public async Task<IReadOnlyList<ChannelView>> ChannelViewsAsync(CancellationToken cancellationToken)
    {
        var counts = Outbox.List(limit: 1000).GroupBy(m => m.Channel, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.GroupBy(m => m.Status).ToDictionary(s => s.Key, s => s.Count()), StringComparer.OrdinalIgnoreCase);
        var views = new List<ChannelView>();
        foreach (var channel in Settings.Channels)
        {
            var missing = new List<string>();
            foreach (var (key, reference) in channel.Secrets)
            {
                try { using var value = await Resolver.ResolveAsync(reference, cancellationToken); }
                catch (Exception e) when (e is not OperationCanceledException) { missing.Add($"{key}: {reference.ToAuditString()} cannot be read"); }
            }

            var shown = channel.Values.ToDictionary(p => p.Key, p => p.Value is JsonArray a ? string.Join(", ", a.Select(x => x?.ToString())) : p.Value?.ToString() ?? "");
            foreach (var key in channel.Secrets.Keys) shown[key] = channel.Secrets[key].ToAuditString();
            views.Add(new ChannelView(channel.Name, channel.Kind, channel.LimitPerMinute, missing.Count == 0, missing, shown, counts.GetValueOrDefault(channel.Name) ?? []));
        }

        return views;
    }
}

/// <summary>Runs the console's sender: works through the outbox until the console stops. It starts only when channels are configured (with none a message could not be written).</summary>
public sealed class ConsoleDeliverySender(ConsoleDelivery delivery) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (delivery.Settings.Channels.Count == 0) return;
        await delivery.Service.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
