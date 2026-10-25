using System.Text.RegularExpressions;

namespace DbDataKit.Web.Trust;

/// <summary>
/// A server's identity in its <c>server.yaml</c> (plan 13, W12): an instance id made the first time and read after, which every token names as its audience, above the paired clients the same file lists.
/// </summary>
public static partial class ServerIdentity
{
    /// <param name="serverYaml">The server's <c>server.yaml</c>; made if there is none.</param>
    /// <param name="prefix">What the id starts with (<c>build</c>, <c>sync</c>, <c>proxy</c>), so a person reading a list of servers can tell them apart.</param>
    /// <param name="pairingCommand">What the comment in the file tells a person to run to list paired clients.</param>
    public static string InstanceId(string serverYaml, string prefix, string pairingCommand)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(serverYaml))!);
        if (File.Exists(serverYaml) && InstanceLine().Match(File.ReadAllText(serverYaml)) is { Success: true } found) return found.Groups["id"].Value;

        var id = $"{prefix}-{Guid.NewGuid():N}";
        var existing = File.Exists(serverYaml) ? File.ReadAllText(serverYaml) : "";
        File.WriteAllText(serverYaml, $"# This server's identity and the clients paired with it. '{pairingCommand} clients' lists them; '{pairingCommand} unpair' removes one.\ninstanceId: {id}\n" + existing);
        return id;
    }

    [GeneratedRegex(@"^instanceId:\s*(?<id>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex InstanceLine();
}
