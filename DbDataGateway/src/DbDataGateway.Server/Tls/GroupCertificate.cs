using System.Security.Cryptography.X509Certificates;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Tls;

/// <summary>
/// The certificate a group of self-signed instances presents (plan 20 O04): a console pins the certificate it paired against, so every instance behind the group's address must present the same one. The first instance
/// keeps the certificate it made in the state, encrypted with the group's key (<see cref="GroupSessionKey"/>); every other instance serves that one, and the instance that renews it first replaces it there, so the others
/// take the renewed one at their next check. A certificate from a file or the certificate store is the operator's to give every instance, and is not kept here.
/// </summary>
public sealed class GroupCertificate(StateDatabase state, GroupSessionKey key)
{
    public const string Setting = "tls-certificate";

    /// <summary>Whether this instance is one of a group (a SQL Server or PostgreSQL state).</summary>
    public bool Enabled => GatewayState.IsGroup(state);

    /// <summary>
    /// The certificate to serve: the group's while it is not due for renewal; otherwise <paramref name="local"/>'s (this instance's own self-signed one, made or renewed by the caller), which becomes the group's unless
    /// another instance renewed it first, in which case that one is served.
    /// </summary>
    public X509Certificate2 Choose(Func<X509Certificate2> local, Func<X509Certificate2, bool> needsRenewal)
    {
        var settings = new GroupSettings(state);
        var stored = settings.Get(Setting);
        if (stored is not null && Read(stored) is { } group && !needsRenewal(group)) return group;
        var mine = local();
        var ours = Write(mine);
        var written = settings.Put(Setting, ours, stored);
        // another instance put its own there first: the group serves that one
        return written == ours || Read(written) is not { } theirs ? mine : theirs;
    }

    /// <summary>Makes <paramref name="renewed"/> the group's certificate (an operator renewed it on purpose); every other instance takes it at its next check.</summary>
    public void Replace(X509Certificate2 renewed)
    {
        var settings = new GroupSettings(state);
        var ours = Write(renewed);
        for (var attempt = 0; attempt < 5 && settings.Put(Setting, ours, settings.Get(Setting)) != ours; attempt++) { }
    }

    private X509Certificate2? Read(string stored)
    {
        try { return X509CertificateLoader.LoadPkcs12(key.Unprotect(Convert.FromBase64String(stored)), null, X509KeyStorageFlags.Exportable); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { return null; }
    }

    private string Write(X509Certificate2 certificate) => Convert.ToBase64String(key.Protect(certificate.Export(X509ContentType.Pkcs12)));
}
