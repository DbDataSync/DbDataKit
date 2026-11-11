using System.Collections.Concurrent;
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
    public static IReadOnlyList<StateModule> Modules { get; } = [DeliveryModule.Module];
}
