using System.Text.Json.Nodes;
using DbDataKit.Connections.Bindings;
using DbDataKit.Secrets;

namespace DbDataKit.Delivery;

/// <summary>The <c>delivery:</c> section is refused: the message names the source (the file) and the key.</summary>
public sealed class DeliverySettingsException(string message) : Exception(message);

/// <summary>What a channel setting holds: text, a whole number, true or false, a list of texts, or a reference to a secret (<c>secret:</c>, <c>env:</c>, <c>file:</c>), which is never a literal.</summary>
public enum ChannelKeyType { Text, Whole, Bool, List, Secret }

/// <summary>One key a kind of channel takes.</summary>
public sealed record ChannelKey(string Key, ChannelKeyType Type = ChannelKeyType.Text, bool Required = false, string? Description = null);

/// <summary>A kind of channel (<c>email</c>, <c>teams</c>): the keys it takes, strictly, and how to make the channel from its configuration.</summary>
public sealed record ChannelKind(string Kind, IReadOnlyList<ChannelKey> Keys, Func<ChannelContext, IDeliveryChannel> Create, string? Description = null);

/// <summary>The kinds of channel this program knows. A host registers the kinds of the library (email, Teams, Slack, webhook) and a test may register more.</summary>
public sealed class ChannelKinds
{
    private readonly Dictionary<string, ChannelKind> _kinds = new(StringComparer.Ordinal);

    public ChannelKinds Register(ChannelKind kind)
    {
        _kinds[kind.Kind] = kind;
        return this;
    }

    public ChannelKind? Find(string kind) => _kinds.GetValueOrDefault(kind);

    public IReadOnlyList<string> Names => [.. _kinds.Keys.Order(StringComparer.Ordinal)];

    public IEnumerable<ChannelKind> All => _kinds.Values;
}

/// <summary>One channel as the settings describe it: <see cref="Values"/> are the plain settings, <see cref="Secrets"/> the references (never the values).</summary>
public sealed record ChannelConfig(string Name, string Kind, JsonObject Values, IReadOnlyDictionary<string, SecretRef> Secrets, int? LimitPerMinute)
{
    public string? Text(string key) => Values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public int? Whole(string key) => Values[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    public bool? Bool(string key) => Values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public IReadOnlyList<string> List(string key) => Values[key] is JsonArray a ? [.. a.Select(x => x!.GetValue<string>())] : [];
}

/// <summary>How the service retries and how long a sender holds a message.</summary>
/// <param name="MaxAttempts">The tries a message gets (the first included); after this many temporary failures it is parked as dead.</param>
/// <param name="Backoff">The wait after the first, second, ... failed try; the last entry repeats.</param>
/// <param name="Lease">How long a sender holds a message it took; a sender that dies loses it after this.</param>
/// <param name="Idle">How long the sender loop waits when there is nothing to do.</param>
public sealed record DeliveryPolicy(int MaxAttempts, IReadOnlyList<TimeSpan> Backoff, TimeSpan Lease, TimeSpan Idle)
{
    public static DeliveryPolicy Default { get; } = new(6, [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6)], TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5));

    /// <summary>The wait after the <paramref name="failedTries"/>-th failed try (from 1).</summary>
    public TimeSpan WaitAfter(int failedTries) => Backoff.Count == 0 ? TimeSpan.Zero : Backoff[Math.Min(Math.Max(failedTries, 1), Backoff.Count) - 1];
}

/// <summary>
/// One delivery channel name of the install and the connection it is bound to in each release channel (plan 22a GP18 fold, plan 27a IX23): <c>alerts: { prod: slack-ops, test: slack-sandbox }</c> in <c>bindings.yml</c>. It is a <see cref="SlotBinding"/> of kind
/// <see cref="SlotKind.Delivery"/> with the channel's limit: <b>the one rule of the suite's bindings</b> decides what a release channel uses (its own entry, else the <c>*</c> written on purpose, else nothing; never another release channel's).
/// </summary>
public sealed record ChannelBinding(string Name, IReadOnlyDictionary<string, string> Connections, int? LimitPerMinute = null)
{
    /// <summary>Every release channel that has no entry of its own.</summary>
    public const string AnyReleaseChannel = BindingSet.AnyReleaseChannel;

    /// <summary>The channel's binding as the shared library reads it.</summary>
    public SlotBinding Slot => new(SlotKind.Delivery, Name, Connections.ToDictionary(c => c.Key, c => (IReadOnlyList<BindingEntry>)[new BindingEntry(c.Value)], StringComparer.Ordinal));

    public static ChannelBinding From(SlotBinding slot, int? limitPerMinute = null) =>
        new(slot.Name, slot.PerChannel.Where(c => c.Value.Count > 0).ToDictionary(c => c.Key, c => c.Value[0].Connection, StringComparer.Ordinal), limitPerMinute);

    /// <summary>What the release channel resolves to (<see cref="SlotBinding.Resolve"/>): the connection, or the reason there is none.</summary>
    public BindingResolution Resolve(string? releaseChannel) => Slot.Resolve(releaseChannel);

    /// <summary>The connection bound for a release channel: its own entry, else the <c>*</c> entry; null when neither says one.</summary>
    public string? For(string releaseChannel) => Resolve(releaseChannel).Primary?.Connection;

    /// <summary>Whether the entry for this release channel is its own (true) or the <c>*</c> one (false).</summary>
    public bool IsOwn(string releaseChannel) => Connections.ContainsKey(releaseChannel);
}

/// <summary>
/// The install's <c>delivery:</c> settings section (plan 22a GP07) with the delivery bindings of <c>bindings.yml</c> (plan 27a IX23): the retry policy and the limits live in the section, and <b>which connection each delivery channel name uses in each release channel is
/// a binding</b> (<c>bindings.yml</c>, section <c>delivery:</c>, read by <see cref="BindingsLoader"/>), so the section holds no channel, address, host or secret. Read strictly: an unknown key fails naming the source and the key, and <c>channels</c> in the section (the earlier
/// place of the mapping) is refused with the new place.
/// <code>
/// delivery:
///   retry:
///     max_attempts: 6                     # tries per message
///     backoff_seconds: [30, 120, 600, 3600, 21600]
///   lease_seconds: 120
///   limits:                               # optional, messages per minute per delivery channel (the connection's own limit_per_minute applies otherwise)
///     alerts: 30
/// </code>
/// </summary>
public sealed record DeliverySettings(DeliveryPolicy Policy, IReadOnlyList<ChannelBinding> Channels)
{
    public static DeliverySettings Empty { get; } = new(DeliveryPolicy.Default, []);

    private static readonly string[] SectionKeys = ["retry", "lease_seconds", "limits"];
    private static readonly string[] RetryKeys = ["max_attempts", "backoff_seconds"];

    /// <summary>Reads the section; <paramref name="source"/> is the file it came from, named in every refusal; <paramref name="bindings"/> are the install's bindings (the delivery channels come from them). A null section is the default policy.</summary>
    public static DeliverySettings Read(JsonObject? section, string source, BindingSet? bindings = null)
    {
        var bound = (bindings ?? BindingSet.Empty).OfKind(SlotKind.Delivery).Select(s => ChannelBinding.From(s)).ToList();
        if (section is null) return new DeliverySettings(DeliveryPolicy.Default, bound);
        if (section.ContainsKey("channels"))
            throw new DeliverySettingsException($"{source}: delivery.channels is not kept here any more: which connection a delivery channel uses is a binding. Move it to {BindingsLoader.FileName} (section `delivery:`): `delivery: {{ mail: <connection>, alerts: {{ prod: <connection>, test: <connection> }} }}`.");
        foreach (var (key, _) in section)
            if (!SectionKeys.Contains(key)) throw new DeliverySettingsException($"{source}: delivery has no key `{key}`. Keys: {string.Join(", ", SectionKeys)}.");
        var policy = DeliveryPolicy.Default;
        if (section["retry"] is { } retryNode)
        {
            if (retryNode is not JsonObject retry) throw new DeliverySettingsException($"{source}: delivery.retry must be a mapping.");
            foreach (var (key, _) in retry)
                if (!RetryKeys.Contains(key)) throw new DeliverySettingsException($"{source}: delivery.retry has no key `{key}`. Keys: {string.Join(", ", RetryKeys)}.");
            var attempts = Whole(retry, "max_attempts", source, "delivery.retry.", 1, 50) ?? policy.MaxAttempts;
            var backoff = policy.Backoff;
            if (retry["backoff_seconds"] is { } b)
            {
                if (b is not JsonArray list || list.Count == 0) throw new DeliverySettingsException($"{source}: delivery.retry.backoff_seconds is a list of seconds, at least one.");
                backoff = [.. list.Select(x => x is JsonValue v && v.TryGetValue<int>(out var s) && s is >= 0 and <= 7 * 86400 ? TimeSpan.FromSeconds(s) : throw new DeliverySettingsException($"{source}: delivery.retry.backoff_seconds holds whole seconds from 0 to 604800."))];
            }
            policy = policy with { MaxAttempts = attempts, Backoff = backoff };
        }

        if (Whole(section, "lease_seconds", source, "delivery.", 5, 3600) is { } lease) policy = policy with { Lease = TimeSpan.FromSeconds(lease) };

        if (section["limits"] is { } limitsNode)
        {
            if (limitsNode is not JsonObject limits) throw new DeliverySettingsException($"{source}: delivery.limits must be a mapping of channel names to messages per minute.");
            foreach (var (name, _) in limits)
            {
                var at = bound.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (at < 0) throw new DeliverySettingsException($"{source}: delivery.limits.{name} names a channel that is not bound in {BindingsLoader.FileName} ({(bound.Count == 0 ? "there are none" : string.Join(", ", bound.Select(c => c.Name)))}).");
                bound[at] = bound[at] with { LimitPerMinute = Whole(limits, name, source, "delivery.limits.", 1, 100000) };
            }
        }

        return new DeliverySettings(policy, bound);
    }

    private static int? Whole(JsonObject o, string key, string source, string prefix, int min, int max)
    {
        if (o[key] is null) return null;
        if (o[key] is JsonValue v && v.TryGetValue<int>(out var n) && n >= min && n <= max) return n;
        throw new DeliverySettingsException($"{source}: {prefix}{key} is a whole number from {min} to {max}.");
    }
}
