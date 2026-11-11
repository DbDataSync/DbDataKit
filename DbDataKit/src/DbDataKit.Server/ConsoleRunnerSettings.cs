using DbDataKit.Config.Yaml;
using DbDataKit.Runner;

namespace DbDataKit.Server;

/// <summary>
/// The settings of the runner embedded in the console (plan 28 section 6a, plan 28a R20), the <c>runner:</c> section of <c>settings.yaml</c> in the console's directory. The embedded runner is <b>opt-in</b>: <c>enabled</c> is false
/// unless the file says true, so a console that is updated gains no tables, no child processes and no dependency on the task host until an administrator turns it on.
/// <code>
/// runner:
///   enabled: true
///   channels: [dev, preview]     # the non-production channels it serves (default: dev, preview); `system` is always served
///   max_concurrent: 2            # runs at once (default 2)
///   data_directory: runner       # cache, scratch and socket (default: runner, in the console's directory)
///   require_separate_account: false
///   retention_every_minutes: 60  # how often the console's state-retention system task runs
/// </code>
/// Serving a production channel is not a setting: it is an administrator's recorded choice (<c>dbdatakit runner accept-production</c>).
/// </summary>
public sealed record ConsoleRunnerSettings
{
    public const string Section = "runner";

    /// <summary>The runner group the embedded runner is: a group of one.</summary>
    public const string Group = "console";

    /// <summary>The channel of the console's own tasks: always served, never production.</summary>
    public const string SystemChannel = "system";

    /// <summary>The id the embedded runner has in the servers list.</summary>
    public const string ServerId = "console-runner";

    public static IReadOnlyList<string> DefaultChannels { get; } = ["dev", "preview"];

    public bool Enabled { get; init; }

    /// <summary>The non-production channels served (besides <see cref="SystemChannel"/>).</summary>
    public IReadOnlyList<string> Channels { get; init; } = DefaultChannels;

    public int MaxConcurrent { get; init; } = 2;

    /// <summary>The directory of the runner's files; relative to the console's directory when not rooted. Null: <c>runner</c> in the console's directory.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>When true, a package other than the console's own is refused unless its tasks would run under an account other than the console's.</summary>
    public bool RequireSeparateAccount { get; init; }

    public TimeSpan RetentionEvery { get; init; } = TimeSpan.FromHours(1);

    // ---- seams for the tests and for a host that needs another shape

    /// <summary>The task host to use; null: the one beside the program.</summary>
    public string? TaskHostPath { get; init; }

    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Runs the tasks that are not the console's own (null: the executor, a process per task). A test passes one that records.</summary>
    public Func<DbDataKit.Runner.Execution.ITaskRunner, DbDataKit.Runner.Execution.ITaskRunner>? WrapRunner { get; init; }

    public static ConsoleRunnerSettings Disabled { get; } = new();

    public string DirectoryFor(string consoleDirectory) =>
        DataDirectory is { Length: > 0 } given ? Path.GetFullPath(given, consoleDirectory) : Path.Combine(consoleDirectory, "runner");

    /// <summary>Reads the <c>runner:</c> section of the console's settings file; disabled when there is no file or no section. A key that is not a setting is refused with the file's name.</summary>
    public static ConsoleRunnerSettings Load(string consoleDirectory)
    {
        var path = ConsoleState.SettingsFileIn(consoleDirectory);
        if (!File.Exists(path)) return Disabled;
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics);
        if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error)
            throw new InvalidOperationException($"{path}: the file is not valid YAML ({error.Message}, line {error.Line}).");
        if (root is not YamlMapping mapping || mapping.GetEntry(Section) is not { } entry) return Disabled;
        if (entry.Value is not YamlMapping section)
            return entry.Value is YamlScalar { Value.Length: 0 } ? Disabled : throw new InvalidOperationException($"{path}: runner is a set of settings (enabled, channels, max_concurrent, ...).");

        var result = new ConsoleRunnerSettings();
        foreach (var item in section.Entries)
        {
            var key = item.Key.Value;
            string Scalar() => item.Value is YamlScalar s ? s.Value : throw new InvalidOperationException($"{path}: runner.{key} is a single value.");
            switch (key)
            {
                case "enabled": result = result with { Enabled = Bool(path, key, Scalar()) }; break;
                case "require_separate_account": result = result with { RequireSeparateAccount = Bool(path, key, Scalar()) }; break;
                case "max_concurrent":
                    if (!int.TryParse(Scalar(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var max) || max < 1)
                        throw new InvalidOperationException($"{path}: runner.max_concurrent is a whole number of at least 1.");
                    result = result with { MaxConcurrent = max };
                    break;
                case "retention_every_minutes":
                    if (!double.TryParse(Scalar(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                        throw new InvalidOperationException($"{path}: runner.retention_every_minutes is a number of minutes above zero.");
                    result = result with { RetentionEvery = TimeSpan.FromMinutes(minutes) };
                    break;
                case "data_directory": result = result with { DataDirectory = Scalar() }; break;
                case "channels":
                    var names = item.Value switch
                    {
                        YamlSequence sequence => sequence.Items.Select(i => i is YamlScalar s ? s.Value : "").ToList(),
                        YamlScalar scalar => scalar.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                        _ => throw new InvalidOperationException($"{path}: runner.channels is a list of channel names."),
                    };
                    foreach (var name in names)
                        if (!PackageManifestValidator.NameRule.IsMatch(name)) throw new InvalidOperationException($"{path}: '{name}' in runner.channels is not a channel name: use 1 to 64 lowercase letters, digits, '-' or '_'.");
                    if (names.Contains(DbDataKit.State.DefinitionStore.Production))
                        throw new InvalidOperationException($"{path}: runner.channels does not take the production channel '{DbDataKit.State.DefinitionStore.Production}'; serving it is an administrator's recorded choice: run 'dbdatakit runner accept-production --yes'.");
                    if (names.Contains(SystemChannel)) names.Remove(SystemChannel);
                    result = result with { Channels = names.Distinct(StringComparer.Ordinal).ToList() };
                    break;
                default:
                    throw new InvalidOperationException($"{path}: '{key}' is not a runner setting (enabled, channels, max_concurrent, data_directory, require_separate_account, retention_every_minutes).");
            }
        }

        return result;
    }

    private static bool Bool(string path, string key, string text) =>
        bool.TryParse(text, out var value) ? value : throw new InvalidOperationException($"{path}: runner.{key} is true or false.");
}
