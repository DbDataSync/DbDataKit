using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.State;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The identity home's OpenID client of each app instance (plan 26 AP19): one public PKCE client per instance, registered when the instance is saved and removed when it is retired, so the home can make the instance's access token
/// (<see cref="AppInstanceGrants"/> says what the approved manifest allows). A client for an instance that has none is also made when the apps module starts, which covers an instance saved with the offline command or at another
/// instance of the group. The redirect URI is the apps host's callback, which every instance shares.
/// </summary>
public static class AppInstanceClients
{
    /// <summary>The redirect URI of every instance's client: the apps host's callback on its public address.</summary>
    public static string CallbackOf(Uri appsPublicUrl) => new Uri(appsPublicUrl, AppsEndpoints.CallbackPath).AbsoluteUri;

    /// <summary>Makes the instance's client when it has none. Returns whether one was made.</summary>
    public static bool Ensure(OidcClients clients, string instance, string callback)
    {
        if (clients.AppClientOf(instance) is not null) return false;
        clients.RegisterApp(instance, [callback]);
        return true;
    }

    /// <summary>The redirect URI of a name bound to an instance (plan 26 AP08): the name's own callback, on the apps host's public port.</summary>
    public static string CallbackOfName(string host, Uri appsPublicUrl) =>
        new UriBuilder(Uri.UriSchemeHttps, host, appsPublicUrl.IsDefaultPort ? -1 : appsPublicUrl.Port, AppsEndpoints.CallbackPath).Uri.AbsoluteUri;

    /// <summary>
    /// Makes the client's redirect URIs the instance's: the apps host's callback and the callback of each bound name, so a binding added is a redirect URI of the instance's client and one removed is gone. When the apps host signs people in with a client
    /// of its own that this home holds (<paramref name="hostClientId"/>), the names' callbacks are added to it too (a person signs in through it), and the ones this instance no longer has are taken off unless another instance still binds them. A host client the
    /// home does not hold (a home elsewhere) is not touched: its redirect URIs are the administrator's, and the answer of the save lists the callbacks to add.
    /// </summary>
    public static IReadOnlyList<string> SyncRedirects(OidcClients clients, AppInstance instance, Uri appsPublicUrl, string? hostClientId, IEnumerable<AppInstance> all)
    {
        var primary = CallbackOf(appsPublicUrl);
        var wanted = new List<string> { primary };
        foreach (var address in instance.Addresses)
        {
            var callback = CallbackOfName(address.Host, appsPublicUrl);
            if (!wanted.Contains(callback, StringComparer.Ordinal)) wanted.Add(callback);
        }
        Ensure(clients, instance.Name, primary);
        var own = clients.AppClientOf(instance.Name)!;
        var removed = own.RedirectUris.Except(wanted, StringComparer.Ordinal).ToList();
        if (!own.RedirectUris.SequenceEqual(wanted, StringComparer.Ordinal)) clients.SetRedirectUris(own.ClientId, wanted);
        if (!string.IsNullOrWhiteSpace(hostClientId) && clients.Find(hostClientId) is { } host)
        {
            var stillBound = all.Where(o => o.Name != instance.Name).SelectMany(o => o.Addresses.Select(a => CallbackOfName(a.Host, appsPublicUrl))).ToHashSet(StringComparer.Ordinal);
            var next = host.RedirectUris.Where(u => u.Length > 0 && (!removed.Contains(u, StringComparer.Ordinal) || stillBound.Contains(u) || u == primary)).ToList();
            foreach (var callback in wanted.Where(c => !next.Contains(c, StringComparer.Ordinal))) next.Add(callback);
            if (!host.RedirectUris.SequenceEqual(next, StringComparer.Ordinal)) clients.SetRedirectUris(host.ClientId, next);
        }
        return wanted;
    }

    /// <summary>Removes the instance's client (its refresh tokens are revoked with it). Returns whether there was one.</summary>
    public static bool Remove(OidcClients clients, string instance) =>
        clients.AppClientOf(instance) is { } client && clients.Remove(client.ClientId);

    /// <summary>Gives every instance of the group's store a client; returns the names that got one. An instance whose name the home cannot use (<see cref="OidcClients.RegisterApp"/> refuses a space, ':' or '/') is skipped.</summary>
    public static IReadOnlyList<string> Reconcile(StateDatabase state, AppInstanceStore instances, string callback, string? hostClientId = null)
    {
        var clients = new OidcClients(state);
        var made = new List<string>();
        foreach (var instance in instances.List())
        {
            try
            {
                if (Ensure(clients, instance.Name, callback)) made.Add(instance.Name);
                if (instance.Addresses.Count > 0 && Uri.TryCreate(callback, UriKind.Absolute, out var at))
                    SyncRedirects(clients, instance, new Uri(at.GetLeftPart(UriPartial.Authority) + "/"), hostClientId, instances.List());
            }
            catch (ArgumentException) { }
        }
        return made;
    }
}
