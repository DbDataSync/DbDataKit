using DbDataKit.Authoring.Server;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Server;

/// <summary>
/// The settings of the Workspace hosted in the console (plan 33 section 3, SC05), the <c>workspace:</c> section of <c>settings.yaml</c> in the console's directory. Hosting it is <b>opt-in</b> until the console's package builds apps (SC08):
/// <c>enabled</c> is false unless the file says true, so an updated console gains no tables and no folder.
/// <code>
/// workspace:
///   enabled: true
///   directory: /fast-disk/workspace   # projects/, workspaces/ and builds/ (default: DBDATAKIT_DATA_WORKSPACE, else the folder workspace under DBDATAKIT_DATA, else workspace in the console's directory)
/// apps:                               # how an app is built: the keys of the Workspace server's `apps:` section (packageStore, allowedPackages, packageStoreSha256, isolation, buildTimeoutSeconds, bun)
///   packageStore: /var/lib/dbdatakit/package-store
/// </code>
/// The folder is the Workspace's own, outside the console's state and key ring: the console keeps project ids and relative names, never the folder.
/// </summary>
public sealed record ConsoleWorkspaceSettings
{
    public const string Section = "workspace";

    /// <summary>The id the hosted Workspace has in the servers list.</summary>
    public const string ServerId = "console-workspace";

    /// <summary>The module's name in the console's audit: every Workspace event is recorded as <c>workspace.&lt;noun&gt;.&lt;verb&gt;</c>.</summary>
    public const string AuditPrefix = "workspace.";

    public bool Enabled { get; init; }

    /// <summary>The Workspace's folder; null: <see cref="DirectoryFor"/> decides.</summary>
    public string? Directory { get; init; }

    /// <summary>How apps are built, when something other than the file says (tests); null: the <c>apps:</c> section of <c>settings.yaml</c> is read for every build.</summary>
    public WorkspaceAppSettings? Apps { get; init; }

    public static ConsoleWorkspaceSettings Disabled { get; } = new();

    /// <summary>The Workspace's folder: the setting, else <c>DBDATAKIT_DATA_WORKSPACE</c> or <c>workspace</c> under <c>DBDATAKIT_DATA</c>, else <c>workspace</c> in the console's directory.</summary>
    public string DirectoryFor(string consoleDirectory, Func<string, string?>? environment = null) =>
        Directory is { Length: > 0 } given
            ? Path.GetFullPath(given, consoleDirectory)
            : Path.GetFullPath(SuiteEnvironment.ResolveDataFolder("workspace", environment, Path.Combine(consoleDirectory, "workspace")), consoleDirectory);

    public static ConsoleWorkspaceSettings Load(string consoleDirectory)
    {
        var path = ConsoleState.SettingsFileIn(consoleDirectory);
        if (!File.Exists(path)) return Disabled;
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics);
        if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error)
            throw new InvalidOperationException($"{path}: the file is not valid YAML ({error.Message}, line {error.Line}).");
        if (root is not YamlMapping mapping || mapping.GetEntry(Section) is not { } entry) return Disabled;
        if (entry.Value is not YamlMapping section)
            return entry.Value is YamlScalar { Value.Length: 0 } ? Disabled : throw new InvalidOperationException($"{path}: workspace is a set of settings (enabled, directory).");

        var result = new ConsoleWorkspaceSettings();
        foreach (var item in section.Entries)
        {
            var key = item.Key.Value;
            string Scalar() => item.Value is YamlScalar s ? s.Value : throw new InvalidOperationException($"{path}: workspace.{key} is a single value.");
            switch (key)
            {
                case "enabled":
                    result = result with { Enabled = bool.TryParse(Scalar(), out var on) ? on : throw new InvalidOperationException($"{path}: workspace.enabled is true or false.") };
                    break;
                case "directory": result = result with { Directory = Scalar() }; break;
                default: throw new InvalidOperationException($"{path}: '{key}' is not a workspace setting (enabled, directory).");
            }
        }

        return result;
    }
}
