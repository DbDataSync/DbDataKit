using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Delivery.Email;
using DbDataKit.Delivery.Webhooks;
using DbDataKit.Secrets;

namespace DbDataKit.Delivery;

/// <summary>What a lookup of a connection by name found: its definition and a <see cref="Version"/> that changes when the connection does (the channel made from it is kept until then), or <see cref="Problem"/> in words when there is none that reads.</summary>
public sealed record ConnectionLookup(ConnectionDefinition? Definition, string Version, string? Problem = null)
{
    public static ConnectionLookup Missing(string name) => new(null, "", $"There is no connection named '{name}' (connections are kept on the console's Connections page).");
}

/// <summary>
/// Where the delivery channels find their connections (plan 22a GP18 fold). The console implements it over the files it keeps (<c>connections/&lt;name&gt;.yml</c>); the library does not depend on the console. A lookup is made for each send, so an edited connection is used
/// by the next message (and a deleted one is a problem the message reports).
/// </summary>
public interface IDeliveryConnections
{
    /// <summary>The connection of that name, or why there is none.</summary>
    ConnectionLookup Find(string name);
}

/// <summary>An <see cref="IDeliveryConnections"/> over a fixed set of definitions (a test's, or a host's that keeps its connections in code).</summary>
public sealed class FixedDeliveryConnections(IEnumerable<ConnectionDefinition>? definitions = null) : IDeliveryConnections
{
    private readonly Dictionary<string, (ConnectionDefinition Definition, int Version)> _by = (definitions ?? []).ToDictionary(d => d.Name, d => (d, 1), StringComparer.Ordinal);

    public void Set(ConnectionDefinition definition) => _by[definition.Name] = (definition, _by.TryGetValue(definition.Name, out var old) ? old.Version + 1 : 1);

    public void Remove(string name) => _by.Remove(name);

    public ConnectionLookup Find(string name) => _by.TryGetValue(name, out var found) ? new ConnectionLookup(found.Definition, found.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)) : ConnectionLookup.Missing(name);
}

/// <summary>
/// How a connection becomes a channel (plan 22a GP18 fold): <c>kind: smtp</c> is the <c>email</c> channel; <c>kind: http</c> with a <c>webhook:</c> section is <c>slack</c>, <c>teams</c> or the signed <c>webhook</c> by its <c>format</c>. The channels' own code is unchanged: they still read
/// a <see cref="ChannelConfig"/>, which is now built from the connection (its secret references are the connection's, resolved at the moment of a send).
/// </summary>
public static class DeliveryConnectionRules
{
    /// <summary>The channel kind a connection makes, or null with the reason it is not one a message can be sent through.</summary>
    public static string? KindOf(ConnectionDefinition d, out string? problem)
    {
        problem = null;
        string? kind = null;
        switch (d.KindName)
        {
            case ConnectionKinds.Smtp:
                kind = EmailChannel.KindName;
                break;
            case ConnectionKinds.Http when d.Webhook is { } w:
                kind = w.Format switch { "slack" => SlackChannel.KindName, "teams" => TeamsChannel.KindName, "signed" => SignedWebhookChannel.KindName, _ => null };
                if (kind is null) problem = $"The connection '{d.Name}' cannot be a delivery channel: its webhook format is `{w.Format}`.";
                break;
            case ConnectionKinds.Http:
                problem = $"The connection '{d.Name}' is an HTTP API without a `webhook:` section; a delivery channel sends through `kind: smtp`, or `kind: http` with `webhook: {{ format: slack | teams | signed }}`.";
                break;
            default:
                problem = $"The connection '{d.Name}' is of kind `{d.KindName}`; a delivery channel sends through `kind: smtp`, or `kind: http` with a `webhook:` section.";
                break;
        }

        return kind;
    }

    /// <summary>The configuration of the channel of delivery channel <paramref name="name"/> made from a connection. <paramref name="limitPerMinute"/> overrides the connection's own limit.</summary>
    public static ChannelConfig Config(string name, ConnectionDefinition d, string kind, int? limitPerMinute)
    {
        var values = new JsonObject();
        var secrets = new Dictionary<string, SecretRef>(StringComparer.Ordinal);
        void Secret(string key, string? reference)
        {
            if (reference is not null && SecretRef.TryParse(reference, out var parsed, out _)) secrets[key] = parsed;
        }

        int? connectionLimit;
        if (d.Smtp is { } smtp)
        {
            values["host"] = smtp.Host;
            if (smtp.Port is { } port) values["port"] = port;
            values["tls"] = smtp.Tls;
            values["from"] = smtp.Sender;
            if (smtp.Helo is { } helo) values["helo"] = helo;
            if (smtp.TrustedCertificate is { } certificate) values["trusted_certificate"] = certificate;
            values["timeout_seconds"] = smtp.TimeoutSeconds;
            if (d.SignIn is { Method: "basic" } basic)
            {
                values["username"] = basic.User;
                Secret("password", basic.PasswordSecret);
            }

            connectionLimit = smtp.LimitPerMinute;
        }
        else
        {
            var web = d.Webhook!;
            if (d.Endpoint is { } endpoint)
            {
                if (EndpointTemplate.IsHidden(endpoint)) Secret("url", endpoint);      // the whole address is a secret
                else
                {
                    values["url"] = endpoint;                                           // the template: its placeholders are filled from the secrets when a message is sent
                    foreach (var (part, reference) in d.EndpointSecrets) Secret(WebhookChannel.PartPrefix + part, reference);
                }
            }

            values["timeout_seconds"] = web.TimeoutSeconds;
            if (web.AllowPrivate.Count > 0) values["allow_private"] = new JsonArray([.. web.AllowPrivate.Select(x => (JsonNode?)JsonValue.Create(x))]);
            if (d.SignIn is { Method: "signed_webhook" } signed)
            {
                Secret("secret", signed.KeySecret);
                if (signed.Name is { } header) values["signature_header"] = header;
            }

            connectionLimit = web.LimitPerMinute;
        }

        return new ChannelConfig(name, kind, values, secrets, limitPerMinute ?? connectionLimit);
    }
}
