using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Apps;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// Whether a name may be bound to an instance (plan 26 AP08): a name is verified before it is accepted, so an address that would never reach this host, or whose certificate is not for the name, is refused when the administrator adds it
/// and not found by the first person who opens it. A subdomain is <c>&lt;instance&gt;.&lt;apps.domain&gt;</c> and nothing else (the host's own certificate is the wildcard for it). A custom domain is not under the apps domain, resolves to an
/// address of this machine or one listed in <c>apps.addresses</c> (a balancer's, a public one) or else proves it is the administrator's with the DNS challenge (<see cref="AppDomainChallenge"/>), and has a certificate that holds the name, has a private key and has not expired. Every refusal says what to change.
/// </summary>
public sealed class AppAddressVerifier(Func<GatewayOptions> options, Func<string, string?> secret, Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>>? resolve = null, Func<IReadOnlyList<IPAddress>>? local = null, TimeProvider? clock = null, Func<string, CancellationToken, Task<IReadOnlyList<string>>>? resolveTxt = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Why <paramref name="instance"/>'s addresses cannot be accepted, or null.</summary>
    public async Task<string?> VerifyAsync(AppInstance instance, CancellationToken ct)
    {
        var current = options().Apps;
        var domain = string.IsNullOrWhiteSpace(current.Domain) ? null : AppAddresses.NormalizeHost(current.Domain);
        foreach (var address in instance.Addresses)
        {
            var host = AppAddresses.NormalizeHost(address.Host ?? "");
            var under = domain is not null && host.EndsWith("." + domain, StringComparison.Ordinal);
            if (address.Kind == AppAddressKind.Subdomain)
            {
                if (domain is null) return $"The subdomain {host} cannot be accepted: this host has no apps domain (`apps.domain` in server.yaml, such as apps.example.com, with a wildcard DNS name and certificate for *.apps.example.com). Add the name as a custom domain, or set the domain.";
                if (host != $"{instance.Name}.{domain}") return $"A subdomain of this host is {instance.Name}.{domain}; {host} is not it.";
                continue;
            }
            if (under) return $"{host} is under this host's apps domain ({domain}): bind it as a subdomain, which is served with the host's own certificate.";
            if (!await ChallengeHeldAsync(instance.Name, host, ct) && await ProblemOfResolutionAsync(host, current, ct) is { } resolution) return resolution + " " + ChallengeHint(instance.Name, host);
            if (CertificateProblem(host, address) is { } certificate) return certificate;
        }
        return null;
    }

    /// <summary>The TXT record that proves <paramref name="host"/> is the administrator's for <paramref name="instance"/>: where to create it and what it holds (the editor shows it before the name is saved).</summary>
    public AppDomainRecord Challenge(string instance, string host)
    {
        var normal = AppAddresses.NormalizeHost(host);
        return new(AppDomainChallenge.RecordName(normal), "TXT", AppDomainChallenge.Value(options().InstanceId, instance, normal));
    }

    private string ChallengeHint(string instance, string host)
    {
        var record = Challenge(instance, host);
        return $"Or prove the name is yours: create the TXT record {record.Name} with the value {record.Value}, then Verify.";
    }

    private async Task<bool> ChallengeHeldAsync(string instance, string host, CancellationToken ct)
    {
        try
        {
            var found = resolveTxt is null ? await DnsTxtResolver.ResolveAsync(AppDomainChallenge.RecordName(host), ct) : await resolveTxt(AppDomainChallenge.RecordName(host), ct);
            return AppDomainChallenge.Holds(found, Challenge(instance, host).Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return false; }   // no record, no resolver, no answer: the name has to resolve to this host instead
    }

    /// <summary>
    /// What the Verify button of the instance editor asks: whether a custom domain is accepted, by which proof, and the record to create when it is not. A subdomain of the apps domain needs no proof. The certificate is not looked at here (it is checked when the address is saved).
    /// </summary>
    public async Task<AppDomainCheck> CheckAsync(string instance, string host, CancellationToken ct)
    {
        var normal = AppAddresses.NormalizeHost(host);
        var record = Challenge(instance, normal);
        var current = options().Apps;
        var domain = string.IsNullOrWhiteSpace(current.Domain) ? null : AppAddresses.NormalizeHost(current.Domain);
        if (domain is not null && normal.EndsWith("." + domain, StringComparison.Ordinal))
            return new(normal, record, false, null, $"{normal} is under this host's apps domain ({domain}): bind it as a subdomain, which needs no proof.");
        if (await ChallengeHeldAsync(instance, normal, ct)) return new(normal, record, true, "challenge", null);
        var problem = await ProblemOfResolutionAsync(normal, current, ct);
        return problem is null ? new(normal, record, true, "address", null)
            : new(normal, record, false, null, $"The TXT record {record.Name} does not hold {record.Value}, and {char.ToLowerInvariant(problem[0])}{problem[1..]}");
    }

    private async Task<string?> ProblemOfResolutionAsync(string host, AppsOptions current, CancellationToken ct)
    {
        IReadOnlyList<IPAddress> found;
        try { found = resolve is null ? await Dns.GetHostAddressesAsync(host, ct) : await resolve(host, ct); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        { return $"The name {host} does not resolve ({ex.Message.TrimEnd('.')}). Make its DNS record point at this host (or at the balancer in front of it) first; a name is verified before it is accepted."; }
        if (found.Count == 0) return $"The name {host} does not resolve to any address. Make its DNS record point at this host first.";
        var own = new List<IPAddress>(local?.Invoke() ?? LocalAddresses());
        foreach (var listed in current.Addresses)
            if (IPAddress.TryParse(listed, out var ip)) own.Add(ip);
        var mine = new HashSet<IPAddress>(own.Select(Plain));
        if (found.Any(ip => IPAddress.IsLoopback(ip) || mine.Contains(Plain(ip)))) return null;
        return $"The name {host} resolves to {string.Join(", ", found.Select(a => a.ToString()))}, which is not an address of this host. Point it at this host, or list the address in `apps.addresses` when a balancer or a public address stands in front of it.";
    }

    private string? CertificateProblem(string host, AppAddress address)
    {
        X509Certificate2 certificate;
        try
        {
            var pfx = secret(address.Certificate!) ?? throw new InvalidOperationException($"the secret {address.Certificate} is not set");
            var password = address.CertificatePassword is { } p ? secret(p) : null;
            certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(pfx.Trim()), password);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return $"The certificate of {host} cannot be read: {ex.Message.TrimEnd('.')}. Its secret {address.Certificate} holds a base64 PFX (and {address.CertificatePassword ?? "a password secret, when it has a password"})."; }
        using (certificate)
        {
            if (!certificate.HasPrivateKey) return $"The certificate of {host} holds no private key; the PFX must include it.";
            var now = _clock.GetUtcNow();
            if (certificate.NotBefore > now || certificate.NotAfter < now) return $"The certificate of {host} is not valid now (valid {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}).";
            if (!certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: true)) return $"The certificate of {host} (subject {certificate.Subject}) is not for that name: its subject alternative names must include {host}.";
        }
        return null;
    }

    private static IPAddress Plain(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static IReadOnlyList<IPAddress> LocalAddresses()
    {
        try { return [.. NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))]; }
        catch (NetworkInformationException) { return []; }
    }
}

/// <summary>The DNS record that proves a custom domain: its <see cref="Name"/>, <see cref="Type"/> (TXT) and <see cref="Value"/>.</summary>
public sealed record AppDomainRecord(string Name, string Type, string Value);

/// <summary>The answer to a Verify: whether the name is accepted, <see cref="Via"/> `challenge` (the TXT record holds the value) or `address` (it resolves to this host), or <see cref="Reason"/> it is not; <see cref="Record"/> is the record to create.</summary>
public sealed record AppDomainCheck(string Host, AppDomainRecord Record, bool Verified, string? Via, string? Reason);
