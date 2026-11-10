using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;

namespace DbDataRunner.Server;

/// <summary>Where a DbDataRunner server keeps what is its own, where it listens and how it schedules.</summary>
/// <param name="Directory">
/// The server's directory (<c>--repo</c>): <c>server.yaml</c>, the certificate, <c>state.db</c> (when the state is SQLite), <c>cache/</c> (the materialized package versions), <c>scratch/</c> (a directory per run, removed after it)
/// and <c>run/</c> (the socket the task processes talk to, mode 0700).
/// </param>
/// <param name="Listen">The address of the management API; HTTPS, and a name or address of this machine.</param>
public sealed record RunnerSettings(string Directory, Uri Listen)
{
    public const string Product = "dbdatarunner";
    public const string DefaultListen = "https://127.0.0.1:8495";

    /// <summary>The service's name in the suite's folders and variables (<see cref="SuiteEnvironment"/>): <c>runner</c>.</summary>
    public const string ServiceName = "runner";

    /// <summary>The variable that names the server's directory for every command (a container sets it once, so <c>docker exec</c> reaches the same files the server uses): <c>DBDATAKIT_DATA_RUNNER</c>, or the folder <c>runner</c> under <c>DBDATAKIT_DATA</c>.</summary>
    public static string DirectoryVariable => SuiteEnvironment.FolderVariable(ServiceName);

    /// <summary>The state engine (<c>sqlite</c>, <c>mssql</c> or <c>postgres</c>); unset is SQLite in the server's directory: <c>DBDATAKIT_STATE_RUNNER_ENGINE</c>, else <c>DBDATAKIT_STATE_ENGINE</c>.</summary>
    public static string StateEngineVariable => SuiteEnvironment.StateEngineVariableOf(ServiceName);

    /// <summary>The connection string of a server engine; a <c>secret:</c> or <c>env:</c> reference in it is resolved when the state is opened: <c>DBDATAKIT_STATE_RUNNER_CONNECTION</c>, else <c>DBDATAKIT_STATE_CONNECTION</c>.</summary>
    public static string StateConnectionVariable => SuiteEnvironment.StateConnectionVariableOf(ServiceName);

    /// <summary>The channels the runner serves, separated by commas; default <see cref="DefaultChannels"/>.</summary>
    public const string ChannelsVariable = "DBDATAKIT_RUNNER_CHANNELS";

    /// <summary>The size cap of one uploaded file, in bytes (<see cref="RunnerPublishing.DefaultMaxArtifactBytes"/> when unset).</summary>
    public const string MaxArtifactBytesVariable = "DBDATAKIT_RUNNER_MAX_ARTIFACT_BYTES";

    /// <summary>The longest an activation waits for the runs in flight of a package, in seconds (default 600: ten minutes); after it the activation is not applied, names the busy tasks and is tried again.</summary>
    public const string ActivationMaxWaitVariable = "DBDATAKIT_RUNNER_ACTIVATION_MAX_WAIT_SECONDS";

    /// <summary>The runner group's name (the state's <c>StateScope.Group</c>): servers that share a database and a group name are one group. Default <see cref="RunnerState.DefaultGroup"/>.</summary>
    public const string GroupVariable = "DBDATAKIT_RUNNER_GROUP";

    /// <summary>
    /// Names of variables of the runner's own environment that task processes also see, separated by commas (test grade: how the logins of a Build refresh reach it before the runner resolves connection slots, plan 28c E02).
    /// They are visible to every task of the runner; never in a package, argv or a file.
    /// </summary>
    public const string TaskEnvironmentVariable = "DBDATAKIT_RUNNER_TASK_ENVIRONMENT";

    public static IReadOnlyList<string> DefaultChannels { get; } = ["prod", "dev"];

    /// <summary>The state database's engine and connection; SQLite by default.</summary>
    public StateSettings State { get; init; } = new();

    /// <summary>The size cap of one file uploaded through the publish API.</summary>
    public long MaxArtifactBytes { get; init; } = RunnerPublishing.DefaultMaxArtifactBytes;

    /// <summary>Whether this runner prepares staged versions and activates them when every live runner is ready (plan 28a R17). On; a test of the manual publish API or a diagnosis turns it off.</summary>
    public bool Staging { get; init; } = true;

    /// <summary>What staging says of the contracts a package was built against (null: <see cref="RunnerContracts.Check"/>). Lets this instance judge them as an older runner of a mixed group would, which a test uses.</summary>
    public Func<DbDataKit.Runner.PackageManifest, string?>? StagingContracts { get; init; }

    /// <summary>The platform this runner is, as a runtime identifier (null: the process's own, <see cref="DbDataKit.Runner.RuntimePlatforms.Current"/>). It picks the runtime package a definition runs on; a test gives the other platform than the one it runs on.</summary>
    public string? Platform { get; init; }

    /// <summary>The longest an activation waits for the runs of a package in flight (plan 28a R18): ten minutes by default.</summary>
    public TimeSpan ActivationMaxWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Checks the signature of a published version (plan 30 section 6.2); null: the default, which finds every version unsigned.</summary>
    public IVersionSignatureVerifier? SignatureVerifier { get; init; }

    /// <summary>Where the secrets a manifest names are looked up (a webhook trigger's <c>secret</c>); null: the environment (<c>DBDATAKIT_SECRET_&lt;NAME&gt;</c>).</summary>
    public DbDataKit.Secrets.ISecretResolver? Secrets { get; init; }

    /// <summary>Variables of the runner's environment a task process sees in addition to the executor's default list (<see cref="TaskEnvironmentVariable"/>).</summary>
    public IReadOnlyList<string> TaskEnvironment { get; init; } = [];

    /// <summary>The channels whose tasks this runner schedules.</summary>
    public IReadOnlyList<string> Channels { get; init; } = DefaultChannels;

    /// <summary>How often the scheduler looks for due tasks.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a task has to stop when the runner stops or the task is cancelled, before its process is killed.</summary>
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often this instance renews its registration and the leases of the items it runs (plan 28a QR3: 30 seconds).</summary>
    public TimeSpan InstanceHeartbeat { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A lease other than three heartbeats (QR3: 90 seconds with the default heartbeat); must be longer than the heartbeat.</summary>
    public TimeSpan? InstanceLeaseOverride { get; init; }

    /// <summary>An instance that has not renewed its registration for this long is not live to the others, and the work items it holds are taken by another runner.</summary>
    public TimeSpan InstanceLease => InstanceLeaseOverride ?? InstanceHeartbeat * 3;

    /// <summary>The task host to start for an assembly package; null: the one published beside this program (<see cref="RunnerTaskHost"/>).</summary>
    public string? TaskHostPath { get; init; }

    /// <summary>A name for the instance's group; instances that share a database and a group name share their queue, instances and pairings.</summary>
    public string Group { get; init; } = RunnerState.DefaultGroup;

    /// <summary>What the engine of <c>DbDataKit.Runner</c> is configured with: this server's directory, group, channels and timing.</summary>
    public DbDataKit.Runner.RunnerHostSettings ToHostSettings() => new(Directory)
    {
        Channels = Channels,
        Group = Group,
        Version = typeof(RunnerSettings).Assembly.GetName().Version?.ToString(),
        Generation = DbDataKit.Web.Trust.SuiteCompatibility.Generation,
        Tick = Tick,
        CancellationGrace = CancellationGrace,
        InstanceHeartbeat = InstanceHeartbeat,
        InstanceLeaseOverride = InstanceLeaseOverride,
        TaskHostPath = TaskHostPath,
        Staging = Staging,
        StagingContracts = StagingContracts,
        Platform = Platform,
        TaskEnvironment = TaskEnvironment,
        ActivationMaxWait = ActivationMaxWait,
    };

    public string ServerYaml => Path.Combine(Directory, "server.yaml");

    public string CacheDirectory => Path.Combine(Directory, "cache");

    public string ScratchDirectory => Path.Combine(Directory, "scratch");

    /// <summary>The socket's directory. Short, because a Unix socket path has a small limit.</summary>
    public string SocketDirectory => Path.Combine(Directory, "run");

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/> or the runner's folder under <c>DBDATAKIT_DATA</c>, else a folder under the user's local application data (an interactive run; the registered service names its folder, <see cref="SuiteEnvironment.ResolveServiceFolder"/>).</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        SuiteEnvironment.ResolveDataFolder(ServiceName, environment, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatarunner", "server"));

    /// <summary>The state settings the environment names; SQLite in the directory when it names none.</summary>
    public static StateSettings StateFrom(Func<string, string?>? environment, string? engineFlag = null) =>
        SuiteEnvironment.ResolveState(ServiceName, environment, engineFlag) ?? new StateSettings();

    public static TimeSpan ActivationMaxWaitFrom(Func<string, string?>? environment)
    {
        var text = (environment ?? Environment.GetEnvironmentVariable)(ActivationMaxWaitVariable);
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.FromMinutes(10);
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new ArgumentException($"{ActivationMaxWaitVariable} is a number of seconds, such as 600.");
    }

    public static IReadOnlyList<string> TaskEnvironmentFrom(Func<string, string?>? environment)
    {
        var text = (environment ?? Environment.GetEnvironmentVariable)(TaskEnvironmentVariable);
        return string.IsNullOrWhiteSpace(text) ? [] : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    public static long MaxArtifactBytesFrom(Func<string, string?>? environment)
    {
        var text = (environment ?? Environment.GetEnvironmentVariable)(MaxArtifactBytesVariable);
        if (string.IsNullOrWhiteSpace(text)) return RunnerPublishing.DefaultMaxArtifactBytes;
        return long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) && bytes > 0
            ? bytes
            : throw new ArgumentException($"{MaxArtifactBytesVariable} is a whole number of bytes, such as 536870912.");
    }

    /// <summary>The group the flag or <see cref="GroupVariable"/> names, <c>main</c> when neither does.</summary>
    public static string GroupFrom(Func<string, string?>? environment, string? flag = null)
    {
        var text = flag ?? (environment ?? Environment.GetEnvironmentVariable)(GroupVariable);
        if (string.IsNullOrWhiteSpace(text)) return RunnerState.DefaultGroup;
        text = text.Trim();
        if (!DbDataKit.Runner.PackageManifestValidator.NameRule.IsMatch(text))
            throw new ArgumentException($"'{text}' is not a group name: use 1 to 64 lowercase letters, digits, '-' or '_', starting with a letter or digit.");
        return text;
    }

    public static IReadOnlyList<string> ChannelsFrom(Func<string, string?>? environment, string? flag = null)
    {
        var text = flag ?? (environment ?? Environment.GetEnvironmentVariable)(ChannelsVariable);
        if (string.IsNullOrWhiteSpace(text)) return DefaultChannels;
        var channels = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
        foreach (var channel in channels)
            if (!DbDataKit.Runner.PackageManifestValidator.NameRule.IsMatch(channel))
                throw new ArgumentException($"'{channel}' is not a channel name: use 1 to 64 lowercase letters, digits, '-' or '_', starting with a letter or digit.");
        return channels.Count == 0 ? DefaultChannels : channels;
    }
}
