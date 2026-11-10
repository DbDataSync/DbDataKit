using DbDataKit.Config.Yaml;

namespace DbDataKit.Upgrade;

/// <summary>
/// Where the console (and a server that prepares its own upgrade) learns which releases exist (plan 24a QU2, the setting <c>upgrade.feed</c>): the GitHub releases of the suite's repository by default, or a <b>mirror</b> for a site
/// without internet, a folder or an address. In <c>settings.yaml</c>:
/// <code>
/// upgrade:
///   feed:
///     mirror: D:\releases        # a folder (or an https address) that holds the releases; without it the GitHub releases are read
///     repo: DbDataSync/DbDataKit # the repository whose releases are read (default as shown)
///     channel: stable            # stable (default), beta or snapshot: the least stable release that is offered
/// </code>
/// A mirror folder is laid out as <c>&lt;product&gt;/v&lt;version&gt;/</c> with the release's files in it (the zips, the MSI and <c>SHA256SUMS</c>, as a release has them), or holds one <c>releases.json</c> (what
/// <c>GET /repos/&lt;repo&gt;/releases</c> of GitHub answers, saved) with the files beside it; a mirror address serves that <c>releases.json</c>.
/// </summary>
public sealed record UpgradeFeedSettings
{
    public const string Section = "upgrade";
    public const string DefaultRepo = "DbDataSync/DbDataKit";
    public const string DefaultApiBase = "https://api.github.com";

    /// <summary>A folder or an http(s) address; null reads GitHub.</summary>
    public string? Mirror { get; init; }

    public string Repo { get; init; } = DefaultRepo;

    public string ApiBase { get; init; } = DefaultApiBase;

    /// <summary>The least stable channel that is offered (<c>stable</c>, <c>beta</c> or <c>snapshot</c>).</summary>
    public string Channel { get; init; } = "stable";

    public static UpgradeFeedSettings Default { get; } = new();

    public Update.Channel ParsedChannel => Channel switch { "stable" => Update.Channel.Stable, "beta" => Update.Channel.Beta, "snapshot" => Update.Channel.Snapshot, _ => Update.Channel.Stable };

    public const string MirrorVariable = "DBDATAKIT_UPGRADE_MIRROR";
    public const string RepoVariable = "DBDATAKIT_UPGRADE_REPO";
    public const string ChannelVariable = "DBDATAKIT_UPGRADE_CHANNEL";

    /// <summary>
    /// The feed a server reads when it prepares its own upgrade: <see cref="MirrorVariable"/>, <see cref="RepoVariable"/> and <see cref="ChannelVariable"/> (set in the service or the unit), else the default. A server's files are read strictly by the
    /// product, so the feed is not a section of them; the console keeps its own in <c>settings.yaml</c> (<see cref="Load"/>). A value that is not a setting is refused naming the variable.
    /// </summary>
    public static UpgradeFeedSettings FromEnvironment(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var result = Default;
        if (environment(MirrorVariable) is { Length: > 0 } mirror) result = result with { Mirror = mirror };
        if (environment(RepoVariable) is { Length: > 0 } repo)
            result = System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ? result with { Repo = repo } : throw new InvalidOperationException($"{RepoVariable} is <owner>/<repository>, not '{repo}'.");
        if (environment(ChannelVariable) is { Length: > 0 } channel)
            result = channel is "stable" or "beta" or "snapshot" ? result with { Channel = channel } : throw new InvalidOperationException($"{ChannelVariable} is stable, beta or snapshot, not '{channel}'.");
        return result;
    }

    /// <summary>Reads <c>upgrade.feed</c> of a settings file; the default when there is no file, no section or no <c>feed</c>. A key that is not a setting is refused with the file's name and line.</summary>
    public static UpgradeFeedSettings Load(string settingsFile)
    {
        if (!File.Exists(settingsFile)) return Default;
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(settingsFile), settingsFile, diagnostics);
        if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error)
            throw new InvalidOperationException($"{settingsFile}: the file is not valid YAML ({error.Message}, line {error.Line}).");
        if (root is not YamlMapping mapping || mapping.GetEntry(Section) is not { } upgrade) return Default;
        if (upgrade.Value is not YamlMapping upgradeSection)
            return upgrade.Value is YamlScalar { Value.Length: 0 } ? Default : throw new InvalidOperationException($"{settingsFile}, line {upgrade.Key.Line}: upgrade is a set of settings (feed).");
        var result = Default;
        foreach (var item in upgradeSection.Entries)
        {
            if (item.Key.Value != "feed") throw new InvalidOperationException($"{settingsFile}, line {item.Key.Line}: '{item.Key.Value}' is not an upgrade setting (feed).");
            if (item.Value is YamlScalar { Value.Length: 0 }) continue;
            if (item.Value is not YamlMapping feed) throw new InvalidOperationException($"{settingsFile}, line {item.Key.Line}: upgrade.feed is a set of settings (mirror, repo, channel).");
            foreach (var entry in feed.Entries)
            {
                var key = entry.Key.Value;
                if (entry.Value is not YamlScalar scalar) throw new InvalidOperationException($"{settingsFile}, line {entry.Key.Line}: upgrade.feed.{key} is a single value.");
                switch (key)
                {
                    case "mirror": result = result with { Mirror = scalar.Value.Length == 0 ? null : scalar.Value }; break;
                    case "repo":
                        if (!System.Text.RegularExpressions.Regex.IsMatch(scalar.Value, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new InvalidOperationException($"{settingsFile}, line {entry.Key.Line}: upgrade.feed.repo is <owner>/<repository>.");
                        result = result with { Repo = scalar.Value };
                        break;
                    case "api_base":
                        if (!Uri.TryCreate(scalar.Value, UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException($"{settingsFile}, line {entry.Key.Line}: upgrade.feed.api_base is an https address.");
                        result = result with { ApiBase = scalar.Value };
                        break;
                    case "channel":
                        if (scalar.Value is not ("stable" or "beta" or "snapshot")) throw new InvalidOperationException($"{settingsFile}, line {entry.Key.Line}: upgrade.feed.channel is stable, beta or snapshot.");
                        result = result with { Channel = scalar.Value };
                        break;
                    default: throw new InvalidOperationException($"{settingsFile}, line {entry.Key.Line}: '{key}' is not a setting of upgrade.feed (mirror, repo, api_base, channel).");
                }
            }
        }

        return result;
    }
}
