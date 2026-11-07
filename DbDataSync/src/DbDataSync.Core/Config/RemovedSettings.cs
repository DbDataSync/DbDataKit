namespace DbDataSync.Core.Config;

/// <summary>
/// The settings Sync no longer has, which are refused by name and never ignored in silence (plan 13, X05 and plan 13a, X06; a clean break is never a silent one).
/// <para>
/// Sync keeps no sign-in of its own. People sign in at the dbdatakit console, which keeps the users and the invitations (U07, U10), and a console's call reaches Sync as a signed
/// token (T01, U08). So <c>Auth:Passkeys:*</c> has no reader, and neither has <c>App:AlternateUrls</c>, which only widened the origins passkeys accepted. X06 removed the rest:
/// Windows group sign-in (<c>Auth:Windows:*</c>) and network trust (<c>Auth:Network:*</c>, any request from this host or from anywhere accepted as an administrator or a viewer).
/// </para>
/// </summary>
public static class RemovedSettings
{
    private static readonly (string Prefix, string Reason)[] Removed =
    [
        ("DbDataSync:Auth:Passkeys", "Sync has no passkey sign-in of its own"),
        ("DbDataSync:App:AlternateUrls", "it only widened the origins passkeys accepted, and Sync has no passkey sign-in of its own"),
        ("DbDataSync:Auth:Windows", "Sync has no Windows sign-in of its own"),
        ("DbDataSync:Auth:Network", "Sync no longer trusts a request because of the network it came from"),
    ];

    /// <summary>
    /// The message for the first removed setting among <paramref name="keys"/> (colon-separated, as <c>DbDataSyncConfigFile.Read</c> and
    /// <c>IConfiguration</c> flatten them), or null when there is none.
    /// </summary>
    /// <param name="keys">The keys of a configuration.</param>
    /// <param name="where">Where they were read from, for the message (a file's path, or a description of the layers).</param>
    public static string? Refusal(IEnumerable<string> keys, string where)
    {
        var all = keys.ToList();
        // The most specific key is the one to name: 'Auth:Passkeys:RelyingPartyId' and not its section.
        var removed = all.Where(key => Removed.Any(r => Matches(key, r.Prefix))).ToList();
        var leaves = removed.Where(key => !removed.Any(other => other.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal);
        foreach (var key in leaves)
        {
            var reason = Removed.First(r => Matches(key, r.Prefix)).Reason;
            return $"The setting '{key}' ({where}) is a setting of an earlier release and is no longer read: {reason}. People sign in at the dbdatakit console " +
                   "(`dbdatakit user invite` makes an invitation) and the console's calls reach this server as signed tokens; the way in when no console answers is the `dbdatasync` command line on the server. " +
                   "Delete every DbDataSync:Auth:Passkeys:*, DbDataSync:Auth:Windows:*, DbDataSync:Auth:Network:* and DbDataSync:App:AlternateUrls setting " +
                   "(in server.yaml, the environment and the command line), then start again.";
        }
        return null;
    }

    private static bool Matches(string key, string prefix) =>
        key.Equals(prefix, StringComparison.OrdinalIgnoreCase) || key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase);
}
