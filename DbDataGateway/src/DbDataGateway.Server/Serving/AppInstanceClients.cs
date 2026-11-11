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

    /// <summary>Removes the instance's client (its refresh tokens are revoked with it). Returns whether there was one.</summary>
    public static bool Remove(OidcClients clients, string instance) =>
        clients.AppClientOf(instance) is { } client && clients.Remove(client.ClientId);

    /// <summary>Gives every instance of the group's store a client; returns the names that got one. An instance whose name the home cannot use (<see cref="OidcClients.RegisterApp"/> refuses a space, ':' or '/') is skipped.</summary>
    public static IReadOnlyList<string> Reconcile(StateDatabase state, AppInstanceStore instances, string callback)
    {
        var clients = new OidcClients(state);
        var made = new List<string>();
        foreach (var instance in instances.List())
        {
            try { if (Ensure(clients, instance.Name, callback)) made.Add(instance.Name); }
            catch (ArgumentException) { }
        }
        return made;
    }
}
