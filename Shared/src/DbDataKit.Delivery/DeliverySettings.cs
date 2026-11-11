using System.Text.Json.Nodes;
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
/// The install's <c>delivery:</c> settings section (plan 22a GP07), read strictly: an unknown key fails naming the source and the key, a secret that is written out instead of referenced fails naming the key.
/// <code>
/// delivery:
///   retry:
///     max_attempts: 6                     # tries per message
///     backoff_seconds: [30, 120, 600, 3600, 21600]
///   lease_seconds: 120
///   channels:
///     mail:                               # the channel's name
///       kind: email                       # email | teams | slack | webhook
///       limit_per_minute: 30              # optional, every kind
///       host: smtp.example.com            # the keys are the kind's (see its README)
///       password: secret:dbdata/delivery/mail/password
/// </code>
/// </summary>
public sealed record DeliverySettings(DeliveryPolicy Policy, IReadOnlyList<ChannelConfig> Channels)
{
    public static DeliverySettings Empty { get; } = new(DeliveryPolicy.Default, []);

    private static readonly string[] SectionKeys = ["retry", "lease_seconds", "channels"];
    private static readonly string[] RetryKeys = ["max_attempts", "backoff_seconds"];

    /// <summary>Reads the section; <paramref name="source"/> is the file it came from, named in every refusal. A null section is no channels.</summary>
    public static DeliverySettings Read(JsonObject? section, string source, ChannelKinds kinds)
    {
        if (section is null) return Empty;
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

        var channels = new List<ChannelConfig>();
        if (section["channels"] is { } channelsNode)
        {
            if (channelsNode is not JsonObject map) throw new DeliverySettingsException($"{source}: delivery.channels must be a mapping of channel names.");
            foreach (var (name, node) in map)
            {
                if (!NameOk(name)) throw new DeliverySettingsException($"{source}: delivery.channels.{name}: a channel's name is letters, digits, dashes and underscores, from 1 to 64.");
                if (channels.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) throw new DeliverySettingsException($"{source}: delivery.channels.{name} is named twice (names are compared ignoring case).");
                channels.Add(ReadChannel(name, node as JsonObject ?? throw new DeliverySettingsException($"{source}: delivery.channels.{name} must be a mapping."), source, kinds));
            }
        }

        return new DeliverySettings(policy, channels);
    }

    private static bool NameOk(string name) => name.Length is >= 1 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static ChannelConfig ReadChannel(string name, JsonObject node, string source, ChannelKinds kinds)
    {
        var where = $"delivery.channels.{name}";
        if (node["kind"] is not JsonValue kv || !kv.TryGetValue<string>(out var kindName) || kindName.Length == 0)
            throw new DeliverySettingsException($"{source}: {where}.kind is required: {string.Join(", ", kinds.Names)}.");
        var kind = kinds.Find(kindName) ?? throw new DeliverySettingsException($"{source}: {where}.kind is `{kindName}`; the kinds are {string.Join(", ", kinds.Names)}.");
        var known = kind.Keys.Select(k => k.Key).Append("kind").Append("limit_per_minute").ToArray();
        foreach (var (key, _) in node)
            if (!known.Contains(key)) throw new DeliverySettingsException($"{source}: {where} has no key `{key}` for a {kindName} channel. Keys: {string.Join(", ", known)}.");

        var values = new JsonObject();
        var secrets = new Dictionary<string, SecretRef>(StringComparer.Ordinal);
        foreach (var key in kind.Keys)
        {
            var node2 = node[key.Key];
            if (node2 is null)
            {
                if (key.Required) throw new DeliverySettingsException($"{source}: {where}.{key.Key} is required for a {kindName} channel.");
                continue;
            }

            switch (key.Type)
            {
                case ChannelKeyType.Secret:
                    if (node2 is not JsonValue sv || !sv.TryGetValue<string>(out var text) || !SecretRef.TryParse(text, out var reference, out _))
                        throw new DeliverySettingsException($"{source}: {where}.{key.Key} is a reference to a secret (`secret:...`, `env:...` or `file:...`), never the value itself" + (node2 is JsonValue ? "." : "; it is not text."));
                    secrets[key.Key] = reference;
                    break;
                case ChannelKeyType.Text:
                    values[key.Key] = node2 is JsonValue tv && tv.TryGetValue<string>(out var t) && t.Length > 0 ? t : throw new DeliverySettingsException($"{source}: {where}.{key.Key} must be a non-empty text.");
                    break;
                case ChannelKeyType.Whole:
                    values[key.Key] = node2 is JsonValue wv && wv.TryGetValue<int>(out var w) ? w : throw new DeliverySettingsException($"{source}: {where}.{key.Key} must be a whole number.");
                    break;
                case ChannelKeyType.Bool:
                    values[key.Key] = node2 is JsonValue bv && bv.TryGetValue<bool>(out var b) ? b : throw new DeliverySettingsException($"{source}: {where}.{key.Key} must be true or false.");
                    break;
                case ChannelKeyType.List:
                    values[key.Key] = node2 is JsonArray a && a.All(x => x is JsonValue xv && xv.TryGetValue<string>(out var s) && s.Length > 0) ? a.DeepClone() : throw new DeliverySettingsException($"{source}: {where}.{key.Key} must be a list of texts.");
                    break;
            }
        }

        var limit = Whole(node, "limit_per_minute", source, where + ".", 1, 100000);
        return new ChannelConfig(name, kindName, values, secrets, limit);
    }

    private static int? Whole(JsonObject o, string key, string source, string prefix, int min, int max)
    {
        if (o[key] is null) return null;
        if (o[key] is JsonValue v && v.TryGetValue<int>(out var n) && n >= min && n <= max) return n;
        throw new DeliverySettingsException($"{source}: {prefix}{key} is a whole number from {min} to {max}.");
    }
}
