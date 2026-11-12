using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Connections.Bindings;
using DbDataKit.Delivery;
using DbDataKit.Secrets;
using DbDataKit.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Server;

/// <summary>The <c>delivery:</c> section of <c>settings.yaml</c> in the console's directory (plan 22a GP07: the retry policy, the lease and the limits) with the delivery bindings of <c>bindings.yml</c> (plan 27a IX23: which connection each channel name uses in each release channel), read strictly by <see cref="DeliverySettings"/>. A file with no section is the default policy; a section that is wrong is a <see cref="Problem"/> the screen shows (the console keeps starting).</summary>
public static class ConsoleDeliverySettings
{
    public const string Section = "delivery";

    /// <summary>The settings, or the reason they could not be read (empty settings then).</summary>
    public static (DeliverySettings Settings, string? Problem) Load(string consoleDirectory, BindingSet? bindings = null)
    {
        var path = ConsoleState.SettingsFileIn(consoleDirectory);
        var bound = DeliverySettings.Read(null, path, bindings);
        if (!File.Exists(path)) return (bound, null);
        try
        {
            var diagnostics = new List<ConfigDiagnostic>();
            var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics);
            if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error) return (bound, $"{path}: the file is not valid YAML ({error.Message}, line {error.Line}).");
            if (root is not YamlMapping mapping || mapping.GetEntry(Section) is not { } entry) return (bound, null);
            if (entry.Value is YamlScalar { Value.Length: 0 }) return (bound, null);
            return (DeliverySettings.Read(ToJson(entry.Value) as JsonObject ?? throw new DeliverySettingsException($"{path}: delivery is a set of settings (retry, lease_seconds, limits)."), path, bindings), null);
        }
        catch (DeliverySettingsException e)
        {
            return (bound, e.Message);
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

/// <summary>
/// What one release channel of a delivery channel is bound to, as the screen shows it: the connection (never an address: the template of its endpoint, or "address hidden"), whether it was bound for this release channel or by <c>*</c>, whether every secret reference it holds can be read
/// now, and the problem when it cannot be used as a channel.
/// </summary>
public sealed record ChannelBindingView(string Release, string Connection, bool Own, string? Kind, string? Target, bool Ready, IReadOnlyList<string> Missing, string? Problem);

/// <summary>A delivery channel as the screen shows it: its bindings per release channel, the limit, and what the outbox holds for it. No secret and no address.</summary>
public sealed record ChannelView(string Name, string Kind, int? LimitPerMinute, bool Ready, IReadOnlyList<string> Missing, IReadOnlyList<ChannelBindingView> Bindings, IReadOnlyDictionary<string, int> Outbox);

/// <summary>The connections of the console as the delivery channels find them (plan 22a GP18 fold): the files of <see cref="ConsoleConnections"/>, a version that changes when a file does.</summary>
public sealed class ConsoleDeliveryConnections(ConsoleConnections connections) : IDeliveryConnections
{
    public ConnectionLookup Find(string name)
    {
        if (!ConsoleConnections.IsName(name) || !File.Exists(connections.FileOf(name))) return ConnectionLookup.Missing(name);
        var file = new FileInfo(connections.FileOf(name));
        var read = ConsoleConnections.Check(name, File.ReadAllText(file.FullName));
        var version = $"{file.LastWriteTimeUtc.Ticks}:{file.Length}";
        if (read.Definition is { } d) return new ConnectionLookup(d, version);
        var first = read.Diagnostics.FirstOrDefault(x => x.Severity == ConfigSeverity.Error);
        return new ConnectionLookup(null, version, $"its file has errors ({first?.Message ?? "it does not read"})");
    }
}

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
    private readonly string _directory;
    private DeliveryChannels _channels = null!;
    private DeliverySettings _settings = DeliverySettings.Empty;
    private string? _problem;

    private readonly ConsoleBindings _bindings;

    public ConsoleDelivery(ConsoleSettings settings, StateDatabase state, ConsoleConnections connections, ConsoleBindings bindings, IUserAccounts users, ISubscriptionRenderer? renderer = null, TimeProvider? time = null, DbDataKit.Monitoring.INotificationFeed? notifications = null)
    {
        _directory = settings.Directory;
        _bindings = bindings;
        bindings.Changed += Reload;
        Kinds = DeliveryKinds.Default();
        Time = time ?? TimeProvider.System;
        Connections = connections;
        Resolver = connections.Secrets;
        Scrubber = new SecretScrubber();
        Reload();

        Outbox = new DeliveryStore(state);
        Subscriptions = new SubscriptionStore(state);
        Service = new DeliveryService(Outbox, new PayloadStore(state), new CurrentChannels(this), Settings.Policy, Scrubber, Time, null, "console");
        CanRender = renderer is not null;
        Runner = new SubscriptionService(Subscriptions, Service, renderer ?? new ConsoleNoRenderer(), new ConsoleSubscriberDirectory(users), Time, null, notifications);
        Users = users;
        Alerts = new AlertStore(state);
        AlertRunner = new AlertService(Alerts, Service, [new RunHistoryAlertSource(new RunStore(state, "console"), Time)], notifications, Time);
    }

    public ChannelKinds Kinds { get; }

    public DeliverySettings Settings => _settings;

    /// <summary>Why the <c>delivery:</c> section could not be used, or null.</summary>
    public string? Problem => _problem;

    /// <summary>Reads <c>settings.yaml</c> and the bindings again and makes the channels from them: what a saved binding does, so the sender uses the new bindings at once.</summary>
    public void Reload()
    {
        (_settings, _problem) = ConsoleDeliverySettings.Load(_directory, _bindings.Current);
        _channels = new DeliveryChannels(_settings, new ConsoleDeliveryConnections(Connections), Kinds, Resolver, Scrubber, Time);
    }

    /// <summary>The connections a message can be sent through, for the screen's choice: name, the channel kind it makes and where it reaches (its template, never the address).</summary>
    public IReadOnlyList<(string Name, string Kind, string? Target)> Usable() =>
        [.. Connections.Read().Where(r => r.Result.Definition is not null).Select(r => (r.Name, Kind: DeliveryConnectionRules.KindOf(r.Result.Definition!, out _), Target: ConsoleConnections.TargetOf(r.Result.Definition!))).Where(x => x.Kind is not null).Select(x => (x.Name, x.Kind!, x.Target))];

    public TimeProvider Time { get; }

    public ISecretResolver Resolver { get; }

    public SecretScrubber Scrubber { get; }

    public DeliveryChannels Channels => _channels;

    public ConsoleConnections Connections { get; }

    public DeliveryStore Outbox { get; }

    public SubscriptionStore Subscriptions { get; }

    public DeliveryService Service { get; }

    public SubscriptionService Runner { get; }

    /// <summary>The alert rules and what was last sent (plan 22a GP16).</summary>
    public AlertStore Alerts { get; }

    /// <summary>Turns the install's failed runs into messages by the rules; <see cref="ConsoleAlertSender"/> runs it every minute.</summary>
    public AlertService AlertRunner { get; }

    public IUserAccounts Users { get; }

    /// <summary>Whether this process can draw a subscription's document (it has a renderer of the views module).</summary>
    public bool CanRender { get; }

    /// <summary>The release channels that any channel is bound in, in order of first use (<c>prod</c> first).</summary>
    public IReadOnlyList<string> ReleaseChannels => [.. Settings.Channels.SelectMany(c => c.Connections.Keys).Where(k => k != ChannelBinding.AnyReleaseChannel).Prepend(State.DefinitionStore.Production).Distinct(StringComparer.Ordinal)];

    /// <summary>The delivery channels with, per release channel, the connection each uses, whether every secret reference it holds can be read now, and what the outbox holds for each name.</summary>
    public async Task<IReadOnlyList<ChannelView>> ChannelViewsAsync(CancellationToken cancellationToken)
    {
        var counts = Outbox.List(limit: 1000).GroupBy(m => m.Channel, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.GroupBy(m => m.Status).ToDictionary(s => s.Key, s => s.Count()), StringComparer.OrdinalIgnoreCase);
        var views = new List<ChannelView>();
        foreach (var binding in Settings.Channels)
        {
            var bindings = new List<ChannelBindingView>();
            foreach (var (release, connection) in binding.Connections.OrderBy(c => c.Key == State.DefinitionStore.Production ? "" : c.Key, StringComparer.Ordinal))
            {
                var missing = new List<string>();
                string? kind = null, target = null, problem = null;
                var definition = Connections.Find(connection);
                if (definition is null)
                {
                    problem = File.Exists(Connections.FileOf(connection)) ? $"The file of the connection '{connection}' has errors; open it on the Connections page." : $"There is no connection named '{connection}'.";
                }
                else
                {
                    kind = DeliveryConnectionRules.KindOf(definition, out var why);
                    problem = why;
                    target = ConsoleConnections.TargetOf(definition);
                    foreach (var (field, path, reference) in ConsoleConnections.ReferencesOf(definition))
                    {
                        try { using var value = await Resolver.ResolveAsync(SecretRef.Parse(reference), cancellationToken); }
                        catch (Exception e) when (e is not OperationCanceledException) { missing.Add($"{path}: {SecretRef.Parse(reference).ToAuditString()} cannot be read"); }
                    }
                }

                bindings.Add(new ChannelBindingView(release, connection, release == ChannelBinding.AnyReleaseChannel || binding.IsOwn(release), kind, target, problem is null && missing.Count == 0, missing, problem));
            }

            var prod = bindings.FirstOrDefault(b => b.Release == State.DefinitionStore.Production) ?? bindings.FirstOrDefault(b => b.Release == ChannelBinding.AnyReleaseChannel);
            views.Add(new ChannelView(binding.Name, prod?.Kind ?? "", binding.LimitPerMinute, prod is { Ready: true }, prod?.Missing ?? [], bindings, counts.GetValueOrDefault(binding.Name) ?? []));
        }

        return views;
    }
}

/// <summary>The channels the console has now: the service asks here each time, so a saved mapping is used by the next message.</summary>
internal sealed class CurrentChannels(ConsoleDelivery delivery) : IDeliveryChannels
{
    public IReadOnlyList<string> Names => delivery.Channels.Names;

    public IDeliveryChannel? Find(string name) => delivery.Channels.Find(name);

    public ChannelResolution Resolve(string name, string? releaseChannel) => delivery.Channels.Resolve(name, releaseChannel);
}

/// <summary>Runs the console's sender: works through the outbox until the console stops. It always runs: channels can be bound while the console is up (the mapping is saved on the Delivery page).</summary>
public sealed class ConsoleDeliverySender(ConsoleDelivery delivery) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await delivery.Service.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}

/// <summary>Runs the alert service every minute (plan 22a GP16): what is failing is told once per rule's cool-down, a recovery once. It does nothing for a kind whose rule is off, so an install that set no rule sends and records nothing.</summary>
public sealed class ConsoleAlertSender(ConsoleDelivery delivery, Microsoft.Extensions.Logging.ILogger<ConsoleAlertSender>? log = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    var result = await delivery.AlertRunner.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    foreach (var problem in result.Problems) log?.LogWarning("Alerts: {Problem}", problem);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) { log?.LogWarning("Alerts: the pass failed: {Message}", ex.Message); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }
}
