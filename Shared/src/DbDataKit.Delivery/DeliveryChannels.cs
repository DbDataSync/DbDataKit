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

/// <summary>The channels of a <see cref="DeliverySettings"/>, built from the kinds. A channel whose kind is unknown at build time is a settings error (the reader has refused it before).</summary>
public sealed class DeliveryChannels : IDeliveryChannels
{
    private readonly Dictionary<string, IDeliveryChannel> _byName;

    public DeliveryChannels(DeliverySettings settings, ChannelKinds kinds, ISecretResolver resolver, SecretScrubber scrubber, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        _byName = new Dictionary<string, IDeliveryChannel>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var config in settings.Channels)
        {
            var kind = kinds.Find(config.Kind) ?? throw new DeliverySettingsException($"delivery.channels.{config.Name}.kind is `{config.Kind}`; the kinds are {string.Join(", ", kinds.Names)}.");
            _byName[config.Name] = kind.Create(new ChannelContext(config, resolver, scrubber, time));
            names.Add(config.Name);
        }

        Names = names;
    }

    public IReadOnlyList<string> Names { get; }

    public IDeliveryChannel? Find(string name) => _byName.GetValueOrDefault(name);
}
