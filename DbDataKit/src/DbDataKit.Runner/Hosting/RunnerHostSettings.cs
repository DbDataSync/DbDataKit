using DbDataKit.Runner.Connections;
using DbDataKit.Runner.Execution;
using DbDataKit.Secrets;
using DbDataKit.Runner.Staging;

namespace DbDataKit.Runner;

/// <summary>
/// What a host of the runner engine (<see cref="RunnerHost"/>) is configured with: where it keeps what is its own, which group it belongs to, the channels it serves, how many tasks it runs at once and the timing of its loops.
/// <c>dbdatarunner</c> builds it from its settings; the console's embedded runner builds it from the <c>runner:</c> section of its <c>settings.yaml</c>.
/// </summary>
/// <param name="Directory">
/// The host's directory for the runner's files: <c>cache/</c> (the materialized package versions), <c>scratch/</c> (a directory per run, removed after it) and <c>run/</c> (the socket the task processes talk to, mode 0700).
/// </param>
public sealed record RunnerHostSettings(string Directory)
{
    /// <summary>The channels whose tasks this runner schedules. A host may pass a collection that changes while it runs (the console's embedded runner starts serving production the moment an administrator chooses it); the scheduler reads it at every tick.</summary>
    public required IReadOnlyCollection<string> Channels { get; init; }

    /// <summary>The runner group this instance belongs to; instances that share a database and a group name share their queue and instances.</summary>
    public string Group { get; init; } = RunnerNames.DefaultGroup;

    /// <summary>The service the instance registers under and the audit names (<c>dbdatarunner</c>; the console's embedded runner is <c>dbdatakit</c>).</summary>
    public string ServiceId { get; init; } = RunnerNames.ServiceId;

    /// <summary>The version the instance reports; null: the library's.</summary>
    public string? Version { get; init; }

    /// <summary>How many tasks this runner runs at once (the cap that keeps a heavy run from starving the host).</summary>
    public int MaxConcurrentRuns { get; init; } = 4;

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

    /// <summary>The task host to start for an assembly package; null: the one published beside the program (<see cref="RunnerTaskHost"/>).</summary>
    public string? TaskHostPath { get; init; }

    /// <summary>Whether this runner prepares staged versions and activates them when every live runner is ready (plan 28a R17).</summary>
    public bool Staging { get; init; } = true;

    /// <summary>What staging says of the contracts a package was built against (null: <see cref="RunnerContracts.Check(PackageManifest)"/>).</summary>
    public Func<PackageManifest, string?>? StagingContracts { get; init; }

    /// <summary>The longest an activation waits for the runs of a package in flight (plan 28a R18): ten minutes by default.</summary>
    public TimeSpan ActivationMaxWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The registry of definition kinds the runner accepts; null: <see cref="RunnerPackages.Kinds"/>.</summary>
    public DbDataKit.State.DefinitionKinds? Kinds { get; init; }

    /// <summary>Wraps the executor the scheduler runs tasks through (the console runs its own system tasks in its process and hands every other task on); null: tasks run in processes of their own through the executor.</summary>
    public Func<ITaskRunner, ITaskRunner>? WrapRunner { get; init; }

    /// <summary>The connections a channel's slot binding may name (plan 28c E02); null: the <c>connection/1</c> files in <see cref="ConnectionsDirectory"/>.</summary>
    public IConnectionCatalog? Connections { get; init; }

    /// <summary>The runner's secrets, which the connections' references resolve against; null: <see cref="DefaultSecrets"/> (the runner's environment and the credentials directory).</summary>
    public ISecretResolver? Secrets { get; init; }

    /// <summary>The environment and the credentials directory (<c>CREDENTIALS_DIRECTORY</c>, systemd and container secret mounts), the providers of the shared secret library that need no key.</summary>
    public static ISecretResolver DefaultSecrets()
    {
        var credentials = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        return new SecretResolverChain([new EnvSecretProvider(), new FileSecretProvider(new FileSecretOptions { SecretsDirectory = string.IsNullOrWhiteSpace(credentials) ? null : credentials })]);
    }

    public string ConnectionsDirectory => Path.Combine(Directory, "connections");

    public string CacheDirectory => Path.Combine(Directory, "cache");

    public string ScratchDirectory => Path.Combine(Directory, "scratch");

    /// <summary>The socket's directory. Short, because a Unix socket path has a small limit.</summary>
    public string SocketDirectory => Path.Combine(Directory, "run");
}
