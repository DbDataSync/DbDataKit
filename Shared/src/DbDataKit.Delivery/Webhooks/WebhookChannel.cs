using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DbDataKit.Delivery.Webhooks;

/// <summary>A destination refused because it is a private address that the channel's <c>allow_private</c> does not list.</summary>
public sealed class PrivateAddressException(string message) : IOException(message);

/// <summary>
/// Which addresses a webhook may not reach unless the channel lists them (plan 22a GP09): loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7), link-local (169.254/16, fe80::/10, which holds the cloud metadata address), carrier-grade (100.64/10),
/// the "this network" and benchmark ranges, multicast and broadcast. An IPv4 address inside an IPv6 one is judged as the IPv4 address.
/// </summary>
public static class PrivateAddresses
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC;
        }

        var v = address.GetAddressBytes();
        return v[0] == 10 || v[0] == 0 || v[0] >= 224
            || (v[0] == 172 && v[1] is >= 16 and <= 31) || (v[0] == 192 && v[1] == 168) || (v[0] == 169 && v[1] == 254)
            || (v[0] == 100 && v[1] is >= 64 and <= 127) || (v[0] == 192 && v[1] == 0 && v[2] == 0) || (v[0] == 198 && v[1] is 18 or 19);
    }

    /// <summary>True when <paramref name="entry"/> (a host name, an address or a CIDR range such as <c>10.1.0.0/16</c>) names <paramref name="host"/> or covers <paramref name="address"/>.</summary>
    public static bool Allows(string entry, string host, IPAddress? address)
    {
        if (string.Equals(entry, host, StringComparison.OrdinalIgnoreCase)) return true;
        if (address is null) return false;
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) return IPAddress.TryParse(entry, out var single) && Unmap(single).Equals(Unmap(address));
        if (!IPAddress.TryParse(entry[..slash], out var network) || !int.TryParse(entry[(slash + 1)..], out var bits)) return false;
        network = Unmap(network);
        address = Unmap(address);
        if (network.AddressFamily != address.AddressFamily) return false;
        var n = network.GetAddressBytes();
        var a = address.GetAddressBytes();
        if (bits < 0 || bits > n.Length * 8) return false;
        for (var i = 0; i < bits / 8; i++)
            if (n[i] != a[i]) return false;
        if (bits % 8 == 0) return true;
        var mask = (byte)(0xFF << (8 - bits % 8));
        return (n[bits / 8] & mask) == (a[bits / 8] & mask);
    }

    private static IPAddress Unmap(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    /// <summary>Whether a CIDR range or an address in the settings is written correctly.</summary>
    public static bool IsEntry(string entry)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) return entry.Length > 0 && !entry.Contains(' ', StringComparison.Ordinal);
        return IPAddress.TryParse(entry[..slash], out var network) && int.TryParse(entry[(slash + 1)..], out var bits) && bits >= 0 && bits <= (network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }
}

/// <summary>What a webhook channel sends: the body, its media type and extra headers.</summary>
public sealed record WebhookRequest(string Body, string MediaType, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// The base of the webhook channels (Teams, Slack, the signed generic webhook): a POST to an address that is a secret reference, with
/// <list type="bullet">
/// <item><b>a private address refused</b> unless <c>allow_private</c> lists the host, the address or its range: checked on the address the connection really goes to (so a name that resolves to a private address, or is changed to one between check and connect, is refused too), and a plain-http address is used only for a listed host;</item>
/// <item><b>no redirect followed</b> (a redirect would be a way round the check): a 3xx is a permanent failure that names it;</item>
/// <item><b>the classification of answers</b>: 2xx is sent; 408, 425, 429 and 5xx are temporary (a 429's <c>Retry-After</c> is in the words); 410 is permanent and says the webhook was removed; any other 4xx and 3xx is permanent; a network error or timeout is temporary.</item>
/// </list>
/// The address (it carries a token) is never in a message's words: the failure text names the host only.
/// </summary>
public abstract class WebhookChannel(ChannelContext context, string kind) : IDeliveryChannel
{
    private readonly Lazy<HttpClient> _client = new(() => Create(context));
    private readonly bool _entriesChecked = CheckEntries(context);

    private static bool CheckEntries(ChannelContext context)
    {
        foreach (var entry in context.Config.List("allow_private"))
            if (!PrivateAddresses.IsEntry(entry)) throw new DeliverySettingsException($"delivery.channels.{context.Config.Name}.allow_private: `{entry}` is not a host name, an address or a range such as 10.1.0.0/16.");
        return true;
    }

    protected ChannelContext Context { get; } = context;

    public string Name => Context.Config.Name;

    public string Kind { get; } = kind;

    public int? LimitPerMinute => Context.Config.LimitPerMinute;

    /// <summary>The settings keys every webhook kind takes.</summary>
    public static IReadOnlyList<ChannelKey> CommonKeys { get; } =
    [
        new ChannelKey("url", ChannelKeyType.Secret, Required: true, Description: "A reference to a secret holding the webhook's address (it carries a token)."),
        new ChannelKey("allow_private", ChannelKeyType.List, Description: "Hosts, addresses or ranges (10.1.0.0/16) that may be private; others at private addresses are refused."),
        new ChannelKey("timeout_seconds", ChannelKeyType.Whole),
    ];

    /// <summary>The request for a message (a kind that needs no secret or other await overrides this one).</summary>
    protected virtual WebhookRequest Build(DeliveryPayload payload) => throw new NotSupportedException($"A {Kind} channel builds its request in BuildAsync.");

    /// <summary>The request for a message; the default is <see cref="Build"/>.</summary>
    protected virtual ValueTask<WebhookRequest> BuildAsync(DeliveryPayload payload, CancellationToken cancellationToken) => ValueTask.FromResult(Build(payload));

    private IReadOnlyList<string> AllowPrivate => Context.Config.List("allow_private");

    public async Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken)
    {
        if (!_entriesChecked) throw new InvalidOperationException("unreachable: the settings were checked when the channel was made");
        var address = await Context.SecretAsync("url", cancellationToken).ConfigureAwait(false) ?? throw new ChannelUnavailableException($"The channel '{Name}' has no `url`.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return DeliveryResult.Permanent($"The address of the channel '{Name}' (url) is not an http or https address.");
        var host = uri.Host;
        IPAddress.TryParse(host.Trim('[', ']'), out var literal);
        var listed = AllowPrivate.Any(e => PrivateAddresses.Allows(e, host, literal));
        if (uri.Scheme == "http" && !listed) return DeliveryResult.Permanent($"The channel '{Name}' points at {host} over plain http; only https addresses are used unless allow_private lists the host.");
        if (literal is not null && PrivateAddresses.IsPrivate(literal) && !listed) return DeliveryResult.Permanent(PrivateMessage(host, literal));

        var request = await BuildAsync(payload, cancellationToken).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(request.Body, new UTF8Encoding(false), request.MediaType) };
        foreach (var (key, value) in request.Headers ?? new Dictionary<string, string>()) message.Headers.TryAddWithoutValidation(key, value);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Context.Config.Whole("timeout_seconds") ?? 20));
        try
        {
            using var response = await _client.Value.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            return Classify(host, response, body);
        }
        catch (HttpRequestException e) when (Find<PrivateAddressException>(e) is { } refused)
        {
            return DeliveryResult.Permanent(refused.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeliveryResult.Temporary($"{host} did not answer within {Context.Config.Whole("timeout_seconds") ?? 20} seconds.");
        }
        catch (HttpRequestException e)
        {
            return DeliveryResult.Temporary($"The request to {host} failed: {e.InnerException?.Message ?? e.Message}");
        }
    }

    private static T? Find<T>(Exception e) where T : Exception
    {
        for (var current = e; current is not null; current = current.InnerException)
            if (current is T match) return match;
        return null;
    }

    private static string PrivateMessage(string host, IPAddress address) =>
        $"{host} ({address}) is a private address; the channel refuses it unless allow_private lists the host, the address or its range.";

    private static DeliveryResult Classify(string host, HttpResponseMessage response, string body)
    {
        var code = (int)response.StatusCode;
        var words = $"{code} {response.ReasonPhrase} from {host}" + (body.Length == 0 ? "" : ": " + (body.Length > 200 ? body[..200] + "..." : body.Replace('\n', ' ').Replace('\r', ' ')));
        if (code is >= 200 and < 300) return DeliveryResult.Sent(words);
        if (code == 410) return DeliveryResult.Permanent($"{words}. The webhook was removed at the other end; make a new one and set the channel's url.");
        if (code is 408 or 425 or 429 || code >= 500)
        {
            var wait = response.Headers.RetryAfter?.Delta is { } delta ? $" Retry-After {delta.TotalSeconds:0} seconds." : "";
            return DeliveryResult.Temporary(words + "." + wait);
        }

        if (code is >= 300 and < 400) return DeliveryResult.Permanent($"{words}. A redirect is not followed (it could lead past the address check); set the channel's url to the final address.");
        return DeliveryResult.Permanent(words);
    }

    private static HttpClient Create(ChannelContext context)
    {
        var allow = context.Config.List("allow_private");
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (connect, ct) =>
            {
                var host = connect.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                IPAddress? chosen = null;
                foreach (var candidate in addresses)
                {
                    if (PrivateAddresses.IsPrivate(candidate) && !allow.Any(e => PrivateAddresses.Allows(e, host, candidate))) continue;
                    chosen = candidate;
                    break;
                }

                if (chosen is null) throw new PrivateAddressException(PrivateMessage(host, addresses.FirstOrDefault() ?? IPAddress.None));
                var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(chosen, connect.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
