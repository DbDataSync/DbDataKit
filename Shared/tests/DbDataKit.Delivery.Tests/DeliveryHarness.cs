using System.Collections.Concurrent;
using DbDataKit.Connections;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.State.Tests;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Delivery.Tests;

/// <summary>A channel a test scripts: what it answers and what it was given.</summary>
public sealed class ScriptedChannel(string name, Func<DeliveryPayload, DeliveryResult> answer, int? limitPerMinute = null) : IDeliveryChannel
{
    public string Name { get; } = name;

    public string Kind => "scripted";

    public int? LimitPerMinute { get; } = limitPerMinute;

    public ConcurrentQueue<DeliveryPayload> Received { get; } = new();

    public Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken)
    {
        Received.Enqueue(payload);
        return Task.FromResult(answer(payload));
    }
}

public sealed class ChannelSet(params IDeliveryChannel[] channels) : IDeliveryChannels
{
    private readonly List<IDeliveryChannel> _channels = [.. channels];

    public IReadOnlyList<string> Names => [.. _channels.Select(c => c.Name)];

    public IDeliveryChannel? Find(string name) => _channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Remove(string name) => _channels.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class MapResolver(Dictionary<string, string> values) : ISecretResolver
{
    public ValueTask<SecretValue> ResolveAsync(SecretRef reference, CancellationToken ct = default) =>
        values.TryGetValue(reference.ToString(), out var v) ? ValueTask.FromResult(SecretValue.From(v)) : throw new SecretNotFoundException(reference.ToString());
}

/// <summary>Collects what the service logs, so a test can look for what must never be there.</summary>
public sealed class CollectingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
}

/// <summary>A state database with the delivery module on an engine, a clock, and services over it.</summary>
public sealed class DeliveryHarness : IDisposable
{
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-11T08:00:00Z");

    private readonly StateEngines _engines = new();

    public StateDatabase Database { get; }

    public StateTestClock Clock { get; } = new(Start);

    public DeliveryStore Store { get; }

    public PayloadStore Payloads { get; }

    public StateEngines.Location Location { get; }

    public DeliveryHarness(string engine)
    {
        Location = _engines.Provision(engine);
        Database = Location.Open(DeliverySetup.Modules, time: Clock);
        Store = new DeliveryStore(Database);
        Payloads = new PayloadStore(Database);
    }

    public DeliveryService Service(IDeliveryChannels channels, string sender, DeliveryPolicy? policy = null, SecretScrubber? scrubber = null, ILogger<DeliveryService>? log = null) =>
        new(Store, Payloads, channels, policy ?? DeliveryPolicy.Default, scrubber ?? new SecretScrubber(), Clock, log, sender);

    public long Count(string table)
    {
        using var connection = Database.OpenConnection();
        using var count = Database.Command(DeliveryModule.Name, connection, $"SELECT COUNT(*) FROM {{table:{table}}};");
        return Convert.ToInt64(count.ExecuteScalar());
    }

    public void Dispose() => _engines.Dispose();
}

/// <summary>The modules a host opens for delivery.</summary>
public static class DeliverySetup
{
    public static IReadOnlyList<StateModule> Modules { get; } = [DeliveryModule.Module, RunsModule.Module];
}

/// <summary>Connections and the channels made from them, for tests (plan 22a GP18 fold): the way a host binds delivery channels to connections.</summary>
public static class TestConnections
{
    /// <summary>A connection read with the shared strict loader; the test fails with the loader's words when it does not read.</summary>
    public static ConnectionDefinition Load(string yaml, string file = "connections/test.yml")
    {
        var r = ConnectionDefinitionLoader.Load(yaml, file, addressRequired: false, outsideKinds: true);
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        return r.Definition!;
    }

    public static DeliverySettings Settings(string channelsJson) => Section("{\"channels\": " + channelsJson + "}");

    /// <summary>The whole <c>delivery:</c> section as JSON (channels, limits, retry).</summary>
    public static DeliverySettings Section(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        DbDataKit.Connections.Bindings.BindingSet? bindings = null;
        if (node["channels"] is { } channels)
        {
            // the channels are bindings (bindings.yml, section delivery:); JSON is YAML
            node.Remove("channels");
            var read = DbDataKit.Connections.Bindings.BindingsLoader.Load("bindings: 1\ndelivery: " + channels.ToJsonString(), "bindings.yml");
            Assert.True(read.Ok, string.Join("\n", read.Diagnostics));
            bindings = read.Bindings;
        }

        return DeliverySettings.Read(node, "settings.yaml", bindings);
    }

    public static DeliveryChannels Channels(DeliverySettings settings, IDeliveryConnections connections, ISecretResolver resolver, SecretScrubber? scrubber = null, TimeProvider? time = null) =>
        new(settings, connections, DeliveryKinds.Default(), resolver, scrubber ?? new SecretScrubber(), time ?? TimeProvider.System);

    /// <summary>The channel <paramref name="name"/> in production, bound to <paramref name="definition"/>.</summary>
    public static IDeliveryChannel Single(string name, ConnectionDefinition definition, ISecretResolver resolver, SecretScrubber? scrubber = null, TimeProvider? time = null) =>
        Channels(Settings($"{{\"{name}\": \"{definition.Name}\"}}"), new FixedDeliveryConnections([definition]), resolver, scrubber, time).Find(name)!;
}
