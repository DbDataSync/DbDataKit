namespace DbDataKit.Delivery;

/// <summary>
/// The delivery channels as a process that has none of its own knows them (plan 32 DBR18, decision of 2026-11-12): a channel <b>name</b> and its <b>kind</b>, as the console stored them on the subscriptions when it saved them
/// (<see cref="DbDataKit.State.SubscriptionStore.ChannelKinds"/>). With these a <see cref="DeliveryService"/> can write a message to the <b>shared outbox</b> (<see cref="DeliveryService.SendAsync"/>), which is all the Gateway does when it draws a subscription as the
/// subscriber; it never holds a connection or a secret, and it does not run the sender. The process that has the real channels (the console's sender) takes the row from the same outbox and sends it through its own channel, in the release channel the row names.
/// A name nobody stored a kind for is not a channel here: the subscription fails with the usual "not configured" words until the console saves it again.
/// </summary>
public sealed class EnqueueOnlyChannels(Func<IReadOnlyDictionary<string, string>> kinds) : IDeliveryChannels
{
    public IReadOnlyList<string> Names => [.. kinds().Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public IDeliveryChannel? Find(string name) => kinds().TryGetValue(name, out var kind) ? new Placeholder(name, kind) : null;

    /// <summary>A name is known in every release channel: which connection it reaches there is the sending process's binding, not this process's.</summary>
    public ChannelResolution Resolve(string name, string? releaseChannel)
    {
        var release = releaseChannel ?? DbDataKit.State.DefinitionStore.Production;
        return Find(name) is { } channel
            ? new ChannelResolution(channel, null, release, null)
            : new ChannelResolution(null, null, release, $"The delivery channel '{name}' is not known to this process: no subscription stored its kind.");
    }

    /// <summary>A channel that can only be named: sending is the other process's work, so a try here is a temporary failure that leaves the message for it.</summary>
    private sealed class Placeholder(string name, string kind) : IDeliveryChannel
    {
        public string Name { get; } = name;

        public string Kind { get; } = kind;

        public int? LimitPerMinute => null;

        public Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"The channel '{Name}' only names a channel of the console: this process does not send.");
    }
}
