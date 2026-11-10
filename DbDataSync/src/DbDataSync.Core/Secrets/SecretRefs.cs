namespace DbDataSync.Core.Secrets;

/// <summary>
/// The names of Sync's secrets in the shared store: <c>dbdata:sync:&lt;kind&gt;:&lt;name&gt;</c> (see <c>DbDataKit.Secrets.SecretName</c>).
/// The old <c>dbdatasync:...</c> names are not read.
/// </summary>
public static class SecretRefs
{
    public static string ForConnection(string connectionName) => $"dbdata:sync:connection:{connectionName}";

    /// <summary>The password of a connection's <c>run</c> login, when it has a second one (plan 28c, E20): the secret name of <see cref="ForConnection"/> with the part <c>run</c>.</summary>
    public static string ForConnectionRun(string connectionName) => $"{ForConnection(connectionName)}:{RunPart}";

    public const string RunPart = "run";

    /// <summary>
    /// The secret ref for a <c>DbDataSync:*</c> app setting that must never be written into
    /// <c>server.yaml</c> itself — <c>StateConnectionString</c>'s password, so far, the only
    /// one. Fixed and non-overridable: unlike <see cref="ForConnection"/>, this is not a name an
    /// operator chooses per connection, it is the one ref phase 79's starter file documents, so there
    /// is exactly one thing to remember and exactly one command that sets it
    /// (<c>dbdatasync secret set dbdata:sync:config:&lt;key&gt; "..."</c>).
    /// </summary>
    public static string ForAppSetting(string key) => $"dbdata:sync:config:{key}";
}
