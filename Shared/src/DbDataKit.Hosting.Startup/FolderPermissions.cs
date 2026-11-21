using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DbDataKit.Hosting.Startup;

/// <summary>
/// The folder step's check of a server's data folder, and the switch that skips it. The check accepts a folder only when nobody but the service's
/// account and the machine's administrators can change it: on Linux and macOS the service's account owns it and other accounts cannot write to it; on
/// Windows its access list lets no broad group (Everyone, Users, Authenticated Users, Anonymous, Guests, Interactive) write to it. Windows does not check
/// the owner yet: an elevated install still makes the folder, so a workstation install leaves it owned by the administrator who ran it.
/// <para>
/// The switch is <c>--skip-folder-permissions-check</c> on <c>server run</c> (and on <c>service install</c>, which puts it on the registered command line)
/// or <c>startup.skip_folder_permissions_check: true</c> in the server's settings file. A server started with it uses its folder as it is and says so at
/// every start; <c>service check</c> and the console's posture page show it.
/// </para>
/// Design: docs/install.md#skipping-the-folder-permissions-check
/// </summary>
public static class FolderPermissions
{
    /// <summary>The switch on the command line.</summary>
    public const string Flag = "--skip-folder-permissions-check";

    /// <summary>The switch's name as a command line parser takes it, without the dashes.</summary>
    public const string FlagName = "skip-folder-permissions-check";

    /// <summary>The setting, as a person writes it and as the console's posture page names it.</summary>
    public const string Setting = "startup.skip_folder_permissions_check";

    /// <summary>The section of the settings file that holds the setting.</summary>
    public const string Section = "startup";

    /// <summary>The setting's key in its section.</summary>
    public const string Key = "skip_folder_permissions_check";

    /// <summary>
    /// What is wrong with <paramref name="folder"/>, one sentence each; empty when the folder passes the check, or when it is not there (the server makes
    /// it, as its own account).
    /// </summary>
    public static IReadOnlyList<string> Problems(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return OperatingSystem.IsWindows() ? WindowsProblems(folder) : UnixProblems(folder);
    }

    [UnsupportedOSPlatform("windows")]
    private static List<string> UnixProblems(string folder)
    {
        var problems = new List<string>();
        if (!FileOwnership.IsOwnedByCurrentAccount(folder))
            problems.Add("The service's account does not own it, so the folder's owner can change what the service keeps there.");
        UnixFileMode mode;
        try { mode = File.GetUnixFileMode(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return problems; }
        if ((mode & UnixFileMode.OtherWrite) != 0)
            problems.Add($"Every account on the machine can write to it (mode {Convert.ToString((int)mode & 0x1FF, 8).PadLeft(4, '0')}).");
        return problems;
    }

    // The well-known groups that hold every account of the machine, or anyone at all; a write grant to one of them lets any account change the folder.
    private static readonly (string Sid, string Name)[] BroadGroups =
    [
        ("S-1-1-0", "Everyone"),
        ("S-1-5-32-545", "Users"),
        ("S-1-5-11", "Authenticated Users"),
        ("S-1-5-7", "Anonymous"),
        ("S-1-5-32-546", "Guests"),
        ("S-1-5-4", "Interactive"),
    ];

    // Any right that adds, changes or removes something in the folder, or changes who may: write data, append data, write extended attributes,
    // delete children, write attributes, delete, change permissions, take ownership, and the generic write and all bits of rules written as raw masks.
    private const int WriteRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    [SupportedOSPlatform("windows")]
    private static List<string> WindowsProblems(string folder)
    {
        AuthorizationRuleCollection rules;
        try
        {
            rules = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return [];
        }

        var writers = new List<string>();
        foreach (FileSystemAccessRule rule in rules)
        {
            // a rule that only passes to what is inside does not apply to the folder itself
            if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if (((int)rule.FileSystemRights & WriteRights) == 0) continue;
            var sid = rule.IdentityReference.Value;
            foreach (var (broad, name) in BroadGroups)
                if (string.Equals(sid, broad, StringComparison.OrdinalIgnoreCase) && !writers.Contains(name)) writers.Add(name);
        }

        return writers.Count == 0
            ? []
            : [$"Its access list lets {string.Join(" and ", writers)} write to it, so any account of the machine can change what the service keeps there."];
    }

    /// <summary>
    /// Whether the settings file says <c>startup.skip_folder_permissions_check: true</c>. False when the file, the section or the key is not there.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is not YAML, the section holds another key, or the value is not true or false; the message names the file and the line.</exception>
    public static bool SettingIn(string settingsFile)
    {
        if (!File.Exists(settingsFile)) return false;
        var stream = new YamlStream();
        try
        {
            using var reader = new StreamReader(new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            stream.Load(reader);
        }
        catch (YamlException e)
        {
            throw new InvalidOperationException($"{settingsFile}, line {e.Start.Line}: the file is not YAML the server can read: {e.Message}", e);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return false;
        // the Gateway matches its keys without case, so its file may spell the section Startup
        var section = root.Children.FirstOrDefault(e => e.Key is YamlScalarNode { Value: { } k } && string.Equals(k, Section, StringComparison.OrdinalIgnoreCase));
        if (section.Key is null || section.Value is YamlScalarNode { Value: null or "" or "~" }) return false;
        if (section.Value is not YamlMappingNode settings)
            throw new InvalidOperationException($"{settingsFile}, line {section.Value.Start.Line}: '{Section}' holds settings ({Key}: true or false).");

        var found = false;
        foreach (var (key, value) in settings.Children)
        {
            var name = (key as YamlScalarNode)?.Value;
            if (!string.Equals(name, Key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{settingsFile}, line {key.Start.Line}: '{name}' is not a setting of {Section} (it has {Key}).");
            found = value is YamlScalarNode { Value: { } text } && text.Trim().ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => throw new InvalidOperationException($"{settingsFile}, line {value.Start.Line}: {Setting} is true or false, not '{text}'."),
            };
            if (value is not YamlScalarNode)
                throw new InvalidOperationException($"{settingsFile}, line {value.Start.Line}: {Setting} is true or false.");
        }

        return found;
    }
}

/// <summary>
/// Whether a start skips the folder permissions check, and what asked for it: the flag on the command line, or the setting in the named settings file.
/// <c>SkipFolderPermissions.Read(flagGiven, settingsFile)</c> reads both.
/// </summary>
/// <param name="ByFlag">The command line has <c>--skip-folder-permissions-check</c>.</param>
/// <param name="BySettingIn">The settings file that says <c>startup.skip_folder_permissions_check: true</c>, or null.</param>
public sealed record SkipFolderPermissions(bool ByFlag, string? BySettingIn)
{
    /// <summary>The check runs.</summary>
    public static SkipFolderPermissions Off { get; } = new(false, null);

    public bool IsOn => ByFlag || BySettingIn is not null;

    /// <summary>What asked to skip the check, as a warning and <c>service check</c> name it.</summary>
    public string Source => ByFlag
        ? FolderPermissions.Flag
        : BySettingIn is null ? "(not skipped)" : $"{FolderPermissions.Setting}: true in {BySettingIn}";

    /// <summary>The flag and the setting of <paramref name="settingsFile"/>; reading the file can throw as <see cref="FolderPermissions.SettingIn"/> does.</summary>
    public static SkipFolderPermissions Read(bool flag, string settingsFile) =>
        new(flag, FolderPermissions.SettingIn(settingsFile) ? settingsFile : null);
}
