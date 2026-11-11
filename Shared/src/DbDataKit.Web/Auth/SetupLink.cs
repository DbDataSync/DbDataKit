using System.Globalization;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Web.Auth;

/// <summary>
/// How long the setup link of a new install lives (plan 23a FQ19, QF5): <see cref="Lifetime"/> from the moment it is made, 1 hour to 7 days, 24 hours by default. A setting of the host's settings file (<c>setup:</c>, read by <see cref="SetupLinkSettingsFile"/>):
/// <code>
/// setup:
///   link_lifetime_hours: 24   # 1 to 168 (default 24); the link is single use whatever this is
/// </code>
/// </summary>
public sealed record SetupLinkSettings(TimeSpan Lifetime)
{
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);

    public static readonly SetupLinkSettings Default = new(TimeSpan.FromHours(24));
}

/// <summary>Reads the <c>setup:</c> section of a host's settings file. A file without it gives <see cref="SetupLinkSettings.Default"/>; a setting that is not one, or a value out of range, is refused with a message that names the file and the setting.</summary>
public static class SetupLinkSettingsFile
{
    public const string Section = "setup";

    public static SetupLinkSettings Load(string path)
    {
        if (!File.Exists(path)) return SetupLinkSettings.Default;
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics);
        // the file also holds what other parts read: only this section is judged here
        if (root is not YamlMapping mapping || mapping.GetEntry(Section) is not { } entry) return SetupLinkSettings.Default;
        if (entry.Value is not YamlMapping section)
            return entry.Value is YamlScalar { Value.Length: 0 } ? SetupLinkSettings.Default : throw new InvalidOperationException($"{path}, line {entry.Key.Line}: setup is a set of settings (link_lifetime_hours).");

        var result = SetupLinkSettings.Default;
        foreach (var item in section.Entries)
        {
            var key = item.Key.Value;
            if (key != "link_lifetime_hours") throw new InvalidOperationException($"{path}, line {item.Key.Line}: '{key}' is not a setup setting (link_lifetime_hours).");
            var text = item.Value is YamlScalar s ? s.Value : throw new InvalidOperationException($"{path}, line {item.Key.Line}: setup.link_lifetime_hours is a single number.");
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || !double.IsFinite(hours) || hours < SetupLinkSettings.MinimumLifetime.TotalHours || hours > SetupLinkSettings.MaximumLifetime.TotalHours)
                throw new InvalidOperationException($"{path}, line {item.Key.Line}: setup.link_lifetime_hours is a number of hours from 1 to 168, not '{text}'.");
            result = new SetupLinkSettings(TimeSpan.FromHours(hours));
        }

        return result;
    }
}

/// <summary>A setup link that was made: the address to open, the invitation behind it and when it ends.</summary>
public sealed record MadeSetupLink(string Url, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// The setup link of a new install (plan 23a FQ19): a single-use administrator invitation that expires, printed as an address (<c>&lt;public address&gt;/invite#&lt;code&gt;</c>, the code in the fragment, which a browser never sends and an access
/// log never records). It is the first-run invitation, made in one place for the host that starts with no user and for <c>dbdatakit setup link</c> run on the machine. A new link replaces every unused one, so only the newest works; once a user
/// exists there is no link to make (the way in for someone else is an invitation).
/// </summary>
public static class SetupLink
{
    /// <summary>The text of <c>FIRST-RUN.txt</c>: the link, when it ends and the command that makes a new one.</summary>
    public static string FileText(MadeSetupLink link, string command) =>
        $"Open this link once to create the first administrator. It works once and ends {link.ExpiresAtUtc:u}:\n\n" +
        $"    {link.Url}\n\n" +
        $"Run `{command}` on this machine to make a new one if it ends or is lost. This file is removed once the first administrator signs up.\n";

    /// <summary>
    /// Makes the link, replacing the unused ones. <paramref name="baseUrl"/> is the address people open the host at; without one the link is the path <c>/invite#&lt;code&gt;</c> to open on it.
    /// </summary>
    public static MadeSetupLink Make(IInviteStore invites, SetupLinkSettings? settings, string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(invites);
        invites.DeleteBootstrapInvites();
        var minted = invites.Create(UserRole.Admin, forUserId: null, createdByUserId: null, lifetime: (settings ?? SetupLinkSettings.Default).Lifetime);
        return new MadeSetupLink($"{baseUrl?.TrimEnd('/')}/invite#{minted.Code}", minted.Invite.ExpiresAtUtc);
    }
}
