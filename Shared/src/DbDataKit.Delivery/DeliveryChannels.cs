using DbDataKit.Secrets;

namespace DbDataKit.Delivery;

/// <summary>
/// Keeps every secret a channel resolved so that what is written down (a try's detail, a log line) can be scrubbed of it. A channel's words come from servers that sometimes echo what they were sent (a rejected login
/// quotes the password); the service scrubs before it stores. Values shorter than 4 characters are not tracked (they would scrub ordinary words).
/// </summary>
public sealed class SecretScrubber
{
    private readonly object _gate = new();
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    public void Track(string value)
    {
        if (value.Length < 4) return;
        lock (_gate) _values.Add(value);
    }

    /// <summary>The text with every tracked secret replaced by <c>***</c>.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string[] values;
        lock (_gate) values = [.. _values.OrderByDescending(v => v.Length)];
        foreach (var value in values) text = text.Replace(value, SecretValue.Mask, StringComparison.Ordinal);
        return text;
    }
}

/// <summary>What a kind of channel gets to build itself: its configuration, the secret resolver (a reference is resolved at the moment of a send, never at start), the scrubber the resolved values are registered with, and the clock.</summary>
public sealed class ChannelContext(ChannelConfig config, ISecretResolver resolver, SecretScrubber scrubber, TimeProvider time)
{
    public ChannelConfig Config { get; } = config;

    public TimeProvider Time { get; } = time;

    public SecretScrubber Scrubber { get; } = scrubber;

    /// <summary>The value of a secret reference of the channel (<c>password</c>, <c>url</c>, ...), or null when the channel has none under that key. A reference that cannot be resolved is <see cref="ChannelUnavailableException"/> naming the key and the reference, never a value.</summary>
    public async ValueTask<string?> SecretAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!Config.Secrets.TryGetValue(key, out var reference)) return null;
        try
        {
            using var value = await resolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            var text = value.RevealString();
            Scrubber.Track(text);
            return text;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ChannelUnavailableException($"The secret for `{key}` of the channel '{Config.Name}' ({reference.ToAuditString()}) could not be read: {e.GetType().Name}.");
        }
    }
}

/// <summary>A channel made from a connection, told by its connection and release channel: what a sender holds back by, and what the screens show.</summary>
internal sealed class BoundChannel(IDeliveryChannel inner, string connection) : IDeliveryChannel
{
    public string Name => inner.Name;

    public string Kind => inner.Kind;

    public string? Connection => connection;

    public int? LimitPerMinute => inner.LimitPerMinute;

    public Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken) => inner.SendAsync(payload, cancellationToken);
}

/// <summary>
/// The delivery channels of an install (plan 22a GP18 fold, plan 21): each delivery channel name is bound, <b>per release channel</b>, to a connection (<see cref="ChannelBinding"/>), and the channel is made from that connection's definition
/// (<see cref="DeliveryConnectionRules"/>) when a message is sent, so an edited connection is used by the next message. A release channel with no binding has no channel (the one rule of <c>docs/channel-bindings.md</c>, <see cref="DbDataKit.Connections.Bindings.SlotBinding.Resolve"/>): <see cref="Resolve"/> never answers with another release channel's connection, which is what keeps a test
/// message from reaching production's Slack. A channel made from a connection is kept until the connection changes (its HTTP client is not made again for each message).
/// </summary>
public sealed class DeliveryChannels(DeliverySettings settings, IDeliveryConnections connections, ChannelKinds kinds, ISecretResolver resolver, SecretScrubber scrubber, TimeProvider? time = null) : IDeliveryChannels
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, IDeliveryChannel> _made = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Names { get; } = [.. settings.Channels.Select(c => c.Name)];

    /// <summary>The bindings of the settings, in order.</summary>
    public IReadOnlyList<ChannelBinding> Bindings => settings.Channels;

    public ChannelBinding? Binding(string name) => settings.Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public IDeliveryChannel? Find(string name) => Resolve(name, null).Channel;

    public ChannelResolution Resolve(string name, string? releaseChannel)
    {
        var release = string.IsNullOrWhiteSpace(releaseChannel) ? DbDataKit.State.DefinitionStore.Production : releaseChannel.Trim();
        if (Binding(name) is not { } binding)
            return new ChannelResolution(null, null, release, $"The delivery channel '{name}' is not configured any more; the message was not sent. Add the channel again and send it again.");
        var bound = binding.Resolve(release);
        if (bound.Primary?.Connection is not { } connectionName)
            return new ChannelResolution(null, null, release, $"{bound.Unbound} The message was not sent.");

        var lookup = connections.Find(connectionName);
        if (lookup.Definition is not { } definition)
            return new ChannelResolution(null, connectionName, release, $"The channel '{binding.Name}' ({release}) uses the connection '{connectionName}', which cannot be used: {lookup.Problem ?? "it does not read"}", Retryable: true);
        var kindName = DeliveryConnectionRules.KindOf(definition, out var problem);
        if (kindName is null) return new ChannelResolution(null, connectionName, release, $"The channel '{binding.Name}' ({release}): {problem}", Retryable: true);
        var kind = kinds.Find(kindName);
        if (kind is null) return new ChannelResolution(null, connectionName, release, $"The channel '{binding.Name}' ({release}): this program has no `{kindName}` channel kind.", Retryable: false);

        var key = $"{binding.Name}\n{release}\n{connectionName}\n{lookup.Version}\n{binding.LimitPerMinute}";
        lock (_gate)
        {
            if (_made.TryGetValue(key, out var existing)) return new ChannelResolution(existing, connectionName, release, null);
            try
            {
                var config = DeliveryConnectionRules.Config(binding.Name, definition, kindName, binding.LimitPerMinute);
                var channel = new BoundChannel(kind.Create(new ChannelContext(config, resolver, scrubber, _time)), connectionName);
                _made[key] = channel;
                return new ChannelResolution(channel, connectionName, release, null);
            }
            catch (DeliverySettingsException e)
            {
                return new ChannelResolution(null, connectionName, release, $"The channel '{binding.Name}' ({release}) cannot be made from the connection '{connectionName}': {e.Message}", Retryable: true);
            }
        }
    }
}
