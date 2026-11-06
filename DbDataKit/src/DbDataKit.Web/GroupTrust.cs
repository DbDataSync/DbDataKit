using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Web.Trust;

/// <summary>A group's settings (the <c>trust</c> module's <c>setting</c> table): a value under a name, written once or replaced only when it is still the value read.</summary>
public interface IGroupSettings
{
    string? Get(string name);

    /// <summary>Writes <paramref name="value"/> when there is no value yet, or when the value is still <paramref name="expected"/>; returns what is there afterwards.</summary>
    string Put(string name, string value, string? expected = null);
}

/// <summary>The group's instance id (plan 20 O04): one for every instance, so a console's token for the group is good at any of them.</summary>
public static class GroupIdentity
{
    public const string InstanceIdSetting = "instance-id";

    /// <summary>
    /// The id the group has; for the first instance to ask, the one its <c>server.yaml</c> has (or makes), so a server that becomes a group keeps the pairings its clients made with it. An instance whose own
    /// <c>server.yaml</c> names another id serves as the group: <see cref="Differs"/> says so, for the host to log.
    /// </summary>
    public static string InstanceId(StateDatabase state, string serverYaml, string prefix, string pairingCommand) =>
        InstanceId(new GroupSettings(state), serverYaml, prefix, pairingCommand);

    /// <inheritdoc cref="InstanceId(StateDatabase, string, string, string)"/>
    public static string InstanceId(IGroupSettings settings, string serverYaml, string prefix, string pairingCommand)
    {
        return settings.Get(InstanceIdSetting) ?? settings.Put(InstanceIdSetting, ServerIdentity.InstanceId(serverYaml, prefix, pairingCommand));
    }

    /// <summary>The id in <paramref name="serverYaml"/> when it is not the group's (that server was paired as itself before it joined the group); null when it is the same or there is none.</summary>
    public static string? Differs(string groupId, string serverYaml) =>
        File.Exists(serverYaml) && System.Text.RegularExpressions.Regex.Match(File.ReadAllText(serverYaml), @"^instanceId:\s*(\S+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } found
            && found.Groups[1].Value != groupId ? found.Groups[1].Value : null;
}

