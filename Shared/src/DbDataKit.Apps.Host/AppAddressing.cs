using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Apps;

/// <summary>One row of the table the host routes by: a name and a path, the instance served there and the address it came from.</summary>
public sealed record AppBinding(string Host, string Path, string Instance, AppAddress Address);

/// <summary>
/// The table of bindings (plan 26 AP08): every address of every production instance, read from the group's shared state (the published <c>app-instance</c> definitions), so a binding that is added or removed at any instance is routed by every
/// instance of the group without a restart. The table is kept for <see cref="Lifetime"/> and read again after it (and at once after <see cref="Invalidate"/>, which this process calls when it saves an instance); a request therefore sees a
/// change within a few seconds, whichever instance of the group made it.
/// </summary>
public sealed class AppBindings(AppInstanceStore instances, TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private IReadOnlyList<AppBinding> _table = [];
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    /// <summary>Reads the table again at the next request.</summary>
    public void Invalidate() { lock (_gate) _readAt = DateTimeOffset.MinValue; }

    public IReadOnlyList<AppBinding> Table()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now - _readAt < Lifetime) return _table;
            _table = [.. instances.List().SelectMany(i => i.Addresses.Select(a => new AppBinding(AppAddresses.NormalizeHost(a.Host), AppAddresses.NormalizePath(a.Path), i.Name, a)))
                .OrderByDescending(b => b.Path.Length).ThenBy(b => b.Host, StringComparer.Ordinal)];
            _readAt = now;
            return _table;
        }
    }

    /// <summary>The binding that serves <paramref name="path"/> at <paramref name="host"/>: the longest path that is the path or a parent of it.</summary>
    public AppBinding? Find(string host, string path)
    {
        host = AppAddresses.NormalizeHost(host);
        foreach (var b in Table())
        {
            if (b.Host != host) continue;
            if (b.Path == "/" || path.Equals(b.Path, StringComparison.Ordinal) || path.StartsWith(b.Path + "/", StringComparison.Ordinal)) return b;
        }
        return null;
    }

    /// <summary>Whether any binding names <paramref name="host"/> (whatever the path).</summary>
    public bool Serves(string host) => Table().Any(b => b.Host == AppAddresses.NormalizeHost(host));

    /// <summary>The bindings of one instance.</summary>
    public IReadOnlyList<AppBinding> Of(string instance) => [.. Table().Where(b => b.Instance == instance)];

    /// <summary>The custom-domain binding of <paramref name="host"/> (what the TLS handshake asks for), or null.</summary>
    public AppBinding? CustomFor(string host) => Table().FirstOrDefault(b => b.Host == AppAddresses.NormalizeHost(host) && b.Address.Kind == AppAddressKind.Custom);
}

/// <summary>
/// The certificate for a TLS handshake by the name the client asks for (SNI, plan 26 AP08): a custom domain's own certificate, loaded from the secrets its binding names, and for every other name (a subdomain under the host's apps domain, the
/// host's own name) the host's certificate. A certificate is loaded once per binding and kept; the key is the names of the secrets, so a binding changed to another certificate loads that one. A name whose certificate cannot be read gets the host's
/// certificate (the browser then says the name does not match, which is the truth), and the reason is reported once.
/// </summary>
public sealed class AppCertificates(AppBindings bindings, Func<X509Certificate2?> hostCertificate, Func<string, string?> secret, Action<string>? problem = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, X509Certificate2?> _loaded = new(StringComparer.Ordinal);

    /// <summary>For <c>ServerCertificateSelector</c>.</summary>
    public X509Certificate2? Select(string? serverName)
    {
        if (!string.IsNullOrEmpty(serverName) && bindings.CustomFor(serverName) is { } custom && custom.Address.Certificate is { } reference)
        {
            var key = $"{AppAddresses.NormalizeHost(serverName)}|{reference}|{custom.Address.CertificatePassword}";
            var certificate = _loaded.GetOrAdd(key, _ => Load(serverName, custom.Address));
            if (certificate is not null) return certificate;
        }
        return hostCertificate();
    }

    private X509Certificate2? Load(string name, AppAddress address)
    {
        try
        {
            var pfx = secret(address.Certificate!) ?? throw new InvalidOperationException($"the secret {address.Certificate} is not set");
            var password = address.CertificatePassword is { } p ? secret(p) : null;
            var certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(pfx.Trim()), password);
            if (!certificate.HasPrivateKey) throw new InvalidOperationException("the PFX holds no private key");
            return certificate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem?.Invoke($"The certificate of {name} ({address.Certificate}) cannot be used: {ex.Message}");
            return null;
        }
    }
}

/// <summary>The addresses that may say, with <c>X-Forwarded-Host</c> and <c>X-Forwarded-Prefix</c>, what name and path the browser used: a reverse proxy's. A header from anyone else is ignored. Entries are an IP address or a CIDR range.</summary>
public sealed class TrustedProxies(IEnumerable<string> entries)
{
    private readonly List<(IPAddress Network, int Bits)> _ranges = [.. entries.Select(Parse)];

    public static readonly TrustedProxies None = new([]);

    private static (IPAddress, int) Parse(string entry)
    {
        var parts = entry.Trim().Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address)) throw new ArgumentException($"'{entry}' is not an IP address or a CIDR range.", nameof(entry));
        var max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var bits = parts.Length == 2 && int.TryParse(parts[1], out var b) && b >= 0 && b <= max ? b : parts.Length == 2 ? throw new ArgumentException($"'{entry}' is not a CIDR range.", nameof(entry)) : max;
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address, bits);
    }

    public bool Trusts(IPAddress? remote)
    {
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        foreach (var (network, bits) in _ranges)
        {
            if (network.AddressFamily != remote.AddressFamily) continue;
            var a = network.GetAddressBytes();
            var b = remote.GetAddressBytes();
            var full = bits / 8;
            var rest = bits % 8;
            if (!a.AsSpan(0, full).SequenceEqual(b.AsSpan(0, full))) continue;
            if (rest == 0 || ((a[full] ^ b[full]) >> (8 - rest)) == 0) return true;
        }
        return false;
    }
}

/// <summary>What the host worked out about a request that came in at a bound name: set by <see cref="AppAddressing.UseAppAddresses"/>, read by the endpoints.</summary>
public sealed record AppAddressContext(AppBinding? Binding, string Host, string PublicPath, string Authority);

/// <summary>
/// Routing by <c>Host</c> and path (plan 26 AP08). A request for a bound name is rewritten to the instance's own address (<c>/apps/&lt;instance&gt;/...</c>) before the routes see it, so one set of routes serves both; on a bound name the host's other
/// pages are not served (a name's page cannot reach another instance's address or the catalog), and an app that asked for <c>origin: own</c> is served only at its names, never at the host's own origin. A name that is not bound is served as before.
/// Behind a reverse proxy the name and the prefix come from <c>X-Forwarded-Host</c> and <c>X-Forwarded-Prefix</c>, only when the request comes from a trusted proxy.
/// </summary>
public static class AppAddressing
{
    public const string ItemKey = "dbdatakit.app-address";

    /// <summary>The paths of the host that are reachable at every name: the shared shell script and the sign-in callback.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.Ordinal) { "-" };

    public static AppAddressContext? Of(HttpContext context) => context.Items.TryGetValue(ItemKey, out var v) ? v as AppAddressContext : null;

    /// <summary>The path the browser used (before the rewrite, with the proxy's prefix): what a redirect that keeps the person where they are must use.</summary>
    public static string PublicPath(HttpContext context) => Of(context)?.PublicPath ?? context.Request.Path.Value ?? "/";

    /// <summary>Adds the routing to the pipeline. Call it before <c>UseRouting</c> (the rewritten path is what the routes match).</summary>
    public static IApplicationBuilder UseAppAddresses(this IApplicationBuilder app, AppBindings bindings, AppInstanceStore instances, TrustedProxies? proxies = null)
    {
        proxies ??= TrustedProxies.None;
        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var trusted = proxies.Trusts(context.Connection.RemoteIpAddress);
            var authority = request.Host.Value ?? "";
            var prefix = "";
            if (trusted)
            {
                if (First(request.Headers["X-Forwarded-Host"]) is { Length: > 0 } forwardedHost && HostString.FromUriComponent(forwardedHost) is var hs && hs.Host.Length > 0) authority = forwardedHost;
                if (First(request.Headers["X-Forwarded-Prefix"]) is { Length: > 0 } forwardedPrefix && forwardedPrefix.StartsWith('/') && !forwardedPrefix.Contains("..", StringComparison.Ordinal))
                    prefix = forwardedPrefix.TrimEnd('/');
            }
            var host = HostString.FromUriComponent(authority).Host.ToLowerInvariant();
            var path = request.Path.Value ?? "/";
            var publicPath = prefix + path;
            var binding = bindings.Find(host, publicPath);
            context.Items[ItemKey] = new AppAddressContext(binding, host, publicPath, authority);

            if (binding is null)
            {
                // an app of its own origin is served only at its names: at the host's own origin it would run with this host's cookies
                if (OwnInstanceAt(instances, path) is { } own)
                {
                    await Text(context, 404, $"The app '{own.Instance.Name}' is served at an address of its own{(own.Instance.Addresses.Count == 0 ? ", and none is bound to it" : "")}: " +
                        (own.Instance.Addresses.Count == 0 ? "add one to the instance." : string.Join(", ", own.Instance.Addresses.Select(a => "https://" + a.Host + (a.Path == "/" ? "/" : a.Path + "/")))));
                    return;
                }
                await next(context);
                return;
            }

            if (path.StartsWith("/apps/", StringComparison.Ordinal) || path == "/apps")
            {
                var second = path.Length > 6 ? path[6..].Split('/', 2)[0] : "";
                if (!Shared.Contains(second) && Uri.UnescapeDataString(second) != binding.Instance) { await Text(context, 404, "No such page at this address."); return; }
                await next(context);
                return;
            }
            // the name's own pages: the binding's path (and the proxy's prefix) taken off, the instance's address put on
            var basePath = binding.Path == "/" ? prefix : binding.Path;
            var rest = publicPath.StartsWith(basePath, StringComparison.Ordinal) ? publicPath[basePath.Length..] : publicPath;
            if (!rest.StartsWith('/')) rest = "/" + rest;
            request.Path = new PathString("/apps/" + Uri.EscapeDataString(binding.Instance) + rest);
            await next(context);
        });
    }

    private static string? First(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 0 ? null : values[0]?.Split(',')[0].Trim();

    private static ResolvedInstance? OwnInstanceAt(AppInstanceStore instances, string path)
    {
        if (!path.StartsWith("/apps/", StringComparison.Ordinal)) return null;
        var name = path[6..].Split('/', 2)[0];
        if (name.Length == 0 || name is "-" or "catalog") return null;
        return instances.Resolve(Uri.UnescapeDataString(name)) is { Manifest.Origin: AppOrigin.Own } own ? own : null;
    }

    /// <summary>The redirect URI the home is asked to return to when a person signs in at this name: the name's own callback, as the browser addressed it; null at the host's own address (the client's configured one).</summary>
    public static string? CallbackFor(HttpContext context) =>
        Of(context) is { Binding: not null } at ? $"https://{at.Authority}{AppsEndpoints.CallbackPath}" : null;

    private static async Task Text(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsync(message, Encoding.UTF8);
    }
}
