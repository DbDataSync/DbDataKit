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
/// One delivery channel name of the install and the connection it is bound to in each release channel (plan 22a GP18 fold, plan 21): <c>alerts: { prod: slack-ops, test: slack-sandbox }</c>. The binding is explicit: <b>a release channel with no entry has no channel</b>
/// (it never uses another release channel's connection, so a test cannot reach production's Slack because production is bound); <c>*</c> binds every release channel that has no entry of its own, and is written on purpose.
/// </summary>
public sealed record ChannelBinding(string Name, IReadOnlyDictionary<string, string> Connections, int? LimitPerMinute = null)
{
    /// <summary>Every release channel that has no entry of its own.</summary>
    public const string AnyReleaseChannel = "*";

    /// <summary>The connection bound for a release channel: its own entry, else the <c>*</c> entry; null when neither says one.</summary>
    public string? For(string releaseChannel) => Connections.TryGetValue(releaseChannel, out var own) ? own : Connections.GetValueOrDefault(AnyReleaseChannel);

    /// <summary>Whether the entry for this release channel is its own (true) or the <c>*</c> one (false).</summary>
    public bool IsOwn(string releaseChannel) => Connections.ContainsKey(releaseChannel);
}

/// <summary>
/// The install's <c>delivery:</c> settings section (plan 22a GP07, reduced by the GP18 fold): the retry policy, and which connection each delivery channel name uses in each release channel. Channels are <b>connections</b> now (<c>kind: smtp</c>, or <c>kind: http</c> with a
/// <c>webhook:</c> section; <see cref="DeliveryConnectionRules"/>), so the section holds no address, host or secret. Read strictly: an unknown key fails naming the source and the key, and a channel written in the old form (<c>kind: email</c>, <c>host</c>, <c>url</c> ...) is refused with the new form.
/// <code>
/// delivery:
///   retry:
///     max_attempts: 6                     # tries per message
///     backoff_seconds: [30, 120, 600, 3600, 21600]
///   lease_seconds: 120
///   channels:
///     mail: smtp-main                     # a name alone binds the production release channel (prod)
///     alerts:                             # a release channel to a connection, each explicit
///       prod: slack-ops
///       test: slack-sandbox
///       dev: slack-sandbox
///   limits:                               # optional, messages per minute per delivery channel (the connection's own limit_per_minute applies otherwise)
///     alerts: 30
/// </code>
/// </summary>
public sealed record DeliverySettings(DeliveryPolicy Policy, IReadOnlyList<ChannelBinding> Channels)
{
    public static DeliverySettings Empty { get; } = new(DeliveryPolicy.Default, []);

    private static readonly string[] SectionKeys = ["retry", "lease_seconds", "channels", "limits"];
    private static readonly string[] RetryKeys = ["max_attempts", "backoff_seconds"];

    /// <summary>Reads the section; <paramref name="source"/> is the file it came from, named in every refusal. A null section is no channels.</summary>
    public static DeliverySettings Read(JsonObject? section, string source)
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

        var bindings = new List<ChannelBinding>();
        if (section["channels"] is { } channelsNode)
        {
            if (channelsNode is not JsonObject map) throw new DeliverySettingsException($"{source}: delivery.channels must be a mapping of channel names to connections.");
            foreach (var (name, node) in map)
            {
                if (!NameOk(name)) throw new DeliverySettingsException($"{source}: delivery.channels.{name}: a channel's name is letters, digits, dashes and underscores, from 1 to 64.");
                if (bindings.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) throw new DeliverySettingsException($"{source}: delivery.channels.{name} is named twice (names are compared ignoring case).");
                bindings.Add(ReadBinding(name, node, source));
            }
        }

        if (section["limits"] is { } limitsNode)
        {
            if (limitsNode is not JsonObject limits) throw new DeliverySettingsException($"{source}: delivery.limits must be a mapping of channel names to messages per minute.");
            foreach (var (name, _) in limits)
            {
                var at = bindings.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (at < 0) throw new DeliverySettingsException($"{source}: delivery.limits.{name} names a channel that is not in delivery.channels ({(bindings.Count == 0 ? "there are none" : string.Join(", ", bindings.Select(c => c.Name)))}).");
                bindings[at] = bindings[at] with { LimitPerMinute = Whole(limits, name, source, "delivery.limits.", 1, 100000) };
            }
        }

        return new DeliverySettings(policy, bindings);
    }

    private static bool NameOk(string name) => name.Length is >= 1 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>A connection's name, as the console keeps it: a letter, then letters, digits, <c>- _ .</c>, up to 64.</summary>
    public static bool ConnectionNameOk(string name) => name.Length is >= 1 and <= 64 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static readonly string[] OldKeys = ["kind", "host", "port", "tls", "from", "username", "password", "helo", "timeout_seconds", "trusted_certificate", "url", "secret", "allow_private", "limit_per_minute"];

    private static ChannelBinding ReadBinding(string name, JsonNode? node, string source)
    {
        var where = $"delivery.channels.{name}";
        var form = $"Channels are connections now: keep the mail server or the webhook as a connection (connections/<name>.yml: `kind: smtp`, or `kind: http` with a `webhook:` section) and write `{name}: <connection>` or `{name}: {{ prod: <connection>, test: <connection> }}`.";
        if (node is JsonValue v && v.TryGetValue<string>(out var single))
        {
            if (!ConnectionNameOk(single)) throw new DeliverySettingsException($"{source}: {where} is `{single}`, which is not a connection's name (a letter, then letters, digits, - _ .). {form}");
            return new ChannelBinding(name, new Dictionary<string, string>(StringComparer.Ordinal) { [ReleaseProd] = single });
        }

        if (node is not JsonObject map || map.Count == 0) throw new DeliverySettingsException($"{source}: {where} is a connection's name, or a mapping of release channels to connection names. {form}");
        var connections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (release, value) in map)
        {
            if (OldKeys.Contains(release)) throw new DeliverySettingsException($"{source}: {where} has the key `{release}` of the earlier form of a channel. {form}");
            if (release != ChannelBinding.AnyReleaseChannel && !NameOk(release)) throw new DeliverySettingsException($"{source}: {where}.{release}: a release channel's name is letters, digits, dashes and underscores (or `*` for every release channel).");
            if (value is not JsonValue cv || !cv.TryGetValue<string>(out var connection) || !ConnectionNameOk(connection))
                throw new DeliverySettingsException($"{source}: {where}.{release} is a connection's name (a letter, then letters, digits, - _ .). {form}");
            connections[release] = connection;
        }

        return new ChannelBinding(name, connections);
    }

    private const string ReleaseProd = "prod";

    private static int? Whole(JsonObject o, string key, string source, string prefix, int min, int max)
    {
        if (o[key] is null) return null;
        if (o[key] is JsonValue v && v.TryGetValue<int>(out var n) && n >= min && n <= max) return n;
        throw new DeliverySettingsException($"{source}: {prefix}{key} is a whole number from {min} to {max}.");
    }
}
