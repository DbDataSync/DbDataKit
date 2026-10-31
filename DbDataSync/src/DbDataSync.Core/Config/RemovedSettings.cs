namespace DbDataSync.Core.Config;

/// <summary>
/// The settings Sync no longer has, which are refused by name and never ignored in silence (plan 13, X05; a clean break is never a silent one).
/// <para>
/// Sync's own passkey sign-in is gone: people sign in at the dbdatakit console, which keeps the users and the invitations (U07, U10), and a console's call
/// reaches Sync as a signed token (T01, U08). So <c>Auth:Passkeys:*</c> has no reader, and neither has <c>App:AlternateUrls</c>, which only widened the origins
/// passkeys accepted.
/// </para>
/// </summary>
public static class RemovedSettings
{
    private static readonly string[] Prefixes = ["DbDataSync:Auth:Passkeys", "DbDataSync:App:AlternateUrls"];

    /// <summary>
    /// The message for the first removed setting among <paramref name="keys"/> (colon-separated, as <c>DbDataSyncConfigFile.Read</c> and
    /// <c>IConfiguration</c> flatten them), or null when there is none.
    /// </summary>
    /// <param name="keys">The keys of a configuration.</param>
    /// <param name="where">Where they were read from, for the message (a file's path, or a description of the layers).</param>
    public static string? Refusal(IEnumerable<string> keys, string where)
    {
        // The most specific key is the one to name: 'Auth:Passkeys:RelyingPartyId' and not its section.
        var removed = keys.Where(key => Prefixes.Any(prefix => key.Equals(prefix, StringComparison.OrdinalIgnoreCase) || key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))).ToList();
        var leaves = removed.Where(key => !removed.Any(other => other.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal);
        foreach (var key in leaves)
            return $"The setting '{key}' ({where}) is a setting of an earlier release and is no longer read: Sync has no passkey sign-in of its own, people sign in at the dbdatakit console " +
                   "(`dbdatakit user invite` makes an invitation) and the console's calls reach this server as signed tokens. Delete every DbDataSync:Auth:Passkeys:* and " +
                   "DbDataSync:App:AlternateUrls setting (in server.yaml, the environment and the command line), then start again.";
        return null;
    }
}
