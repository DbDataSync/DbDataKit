namespace DbDataKit.State;

/// <summary>Raised when an environment variable of an earlier release is set (plan 33 D14): the message names it and the one that replaces it.</summary>
public sealed class SuiteEnvironmentException(string message) : InvalidOperationException(message);

/// <summary>
/// The one reader of the variables that say where a service keeps its data and which database holds its state (plan 33 section 6c), so no product spells its own:
/// <list type="bullet">
/// <item><c>DBDATAKIT_DATA</c>: the data root of the install; each service keeps a folder under it named for it (<c>server</c>, <c>gateway</c>, <c>runner</c>, <c>sync</c>, <c>build</c>, <c>store</c>, <c>workspace</c>, <c>identity</c>).</item>
/// <item><c>DBDATAKIT_DATA_&lt;X&gt;</c>: one service's folder (X is the service in capitals); it wins over the root.</item>
/// <item><c>DBDATAKIT_STATE_ENGINE</c> and <c>DBDATAKIT_STATE_CONNECTION</c>: the state database of the whole install; <c>DBDATAKIT_STATE_&lt;X&gt;_ENGINE</c> and <c>_CONNECTION</c>: one service's, which wins.</item>
/// </list>
/// A folder is, in this order: the service's variable, the root's folder for it, the default the product passes (what it used before: an interactive run keeps its own, a registered service uses <see cref="MachineFolder"/>).
/// The state is the service's variables, then the install's, then what the product's own file and default say. A command-line option still wins over all of it; that is the product's, not this class's.
/// The variables of earlier releases are <b>not</b> read and have no alias: <see cref="RefuseOldNames"/> stops a service that has one set, naming the new one (decision D14, a clean break with no migration).
/// </summary>
public static class SuiteEnvironment
{
    /// <summary>The data root of the install.</summary>
    public const string DataRootVariable = "DBDATAKIT_DATA";

    /// <summary>The state engine of the whole install.</summary>
    public const string StateEngineVariable = "DBDATAKIT_STATE_ENGINE";

    /// <summary>The state connection string of the whole install.</summary>
    public const string StateConnectionVariable = "DBDATAKIT_STATE_CONNECTION";

    /// <summary>The services and modules that have a folder, as the lowercase names the folders have. The DbDataKit service itself is <c>server</c> (plan 33 D9).</summary>
    public static IReadOnlyList<string> Services { get; } = ["server", "gateway", "runner", "sync", "build", "store", "workspace", "identity"];

    private static string Checked(string service)
    {
        var name = service.Trim().ToLowerInvariant();
        return Services.Contains(name) ? name : throw new ArgumentException($"'{service}' is not a service with a data folder: use one of {string.Join(", ", Services)}.", nameof(service));
    }

    /// <summary>The variable that names one service's folder: <c>DBDATAKIT_DATA_RUNNER</c>.</summary>
    public static string FolderVariable(string service) => $"DBDATAKIT_DATA_{Checked(service).ToUpperInvariant()}";

    /// <summary>The variable that names one service's state engine: <c>DBDATAKIT_STATE_RUNNER_ENGINE</c>.</summary>
    public static string StateEngineVariableOf(string service) => $"DBDATAKIT_STATE_{Checked(service).ToUpperInvariant()}_ENGINE";

    /// <summary>The variable that names one service's state connection string: <c>DBDATAKIT_STATE_RUNNER_CONNECTION</c>.</summary>
    public static string StateConnectionVariableOf(string service) => $"DBDATAKIT_STATE_{Checked(service).ToUpperInvariant()}_CONNECTION";

    /// <summary>
    /// Where a registered service keeps its folder when nothing says otherwise: <c>%ProgramData%\DbDataKit\&lt;service&gt;</c> on Windows, <c>/var/lib/dbdatakit/&lt;service&gt;</c> on Linux (and the platform's own
    /// place on macOS and FreeBSD, which Sync has always used).
    /// </summary>
    public static string MachineFolder(string service)
    {
        var name = Checked(service);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.Create), "DbDataKit", name);
        if (OperatingSystem.IsMacOS()) return $"/Library/Application Support/DbDataKit/{name}";
        if (OperatingSystem.IsFreeBSD()) return $"/var/db/dbdatakit/{name}";
        return $"/var/lib/dbdatakit/{name}";
    }

    /// <summary>The folder from the environment alone (the service's variable, else the root's folder for it), or null when it names none.</summary>
    public static string? NamedFolder(string service, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (environment(FolderVariable(service)) is { Length: > 0 } own && !string.IsNullOrWhiteSpace(own)) return own;
        if (environment(DataRootVariable) is { Length: > 0 } root && !string.IsNullOrWhiteSpace(root)) return Path.Combine(root, Checked(service));
        return null;
    }

    /// <summary>
    /// The folder a service uses when no option names one: <c>DBDATAKIT_DATA_&lt;X&gt;</c>, else <c>&lt;DBDATAKIT_DATA&gt;/&lt;service&gt;</c>, else <paramref name="fallback"/> (the product's own default).
    /// </summary>
    public static string ResolveDataFolder(string service, Func<string, string?>? environment, string fallback) => NamedFolder(service, environment) ?? fallback;

    /// <summary>
    /// The folder <c>service install</c> registers and a registered service runs in: the environment's (as <see cref="ResolveDataFolder"/>), else <see cref="MachineFolder"/>. A service never uses a
    /// folder under a person's profile.
    /// </summary>
    public static string ResolveServiceFolder(string service, Func<string, string?>? environment = null) => NamedFolder(service, environment) ?? MachineFolder(service);

    /// <summary>
    /// The state settings the environment names for a service: its variables, else the install's; an option's value (<paramref name="engineFlag"/>, <paramref name="connectionFlag"/>) wins. Null when nothing names any,
    /// which is the product's default (SQLite in its folder, or what its settings file says).
    /// </summary>
    public static StateSettings? ResolveState(string service, Func<string, string?>? environment = null, string? engineFlag = null, string? connectionFlag = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var engine = !string.IsNullOrWhiteSpace(engineFlag) ? engineFlag : First(environment, StateEngineVariableOf(service), StateEngineVariable);
        var connection = !string.IsNullOrWhiteSpace(connectionFlag) ? connectionFlag : First(environment, StateConnectionVariableOf(service), StateConnectionVariable);
        return string.IsNullOrWhiteSpace(engine) && string.IsNullOrWhiteSpace(connection) ? null : new StateSettings(engine, connection);
    }

    /// <summary>The inputs of <see cref="StateSettingsResolver"/> for a service: its variables first, then the install's, so the origin shown names the variable that gave each setting.</summary>
    public static StateSettingsInputs StateInputs(string service, string? settingsFile, string? engineFlag, string engineFlagName, string? connectionFlag, string connectionFlagName, Func<string, string?>? environment) =>
        new(settingsFile, engineFlag, engineFlagName, connectionFlag, connectionFlagName,
            StateEngineVariableOf(service), StateConnectionVariableOf(service), environment ?? Environment.GetEnvironmentVariable)
        {
            FallbackEngineVariable = StateEngineVariable,
            FallbackConnectionVariable = StateConnectionVariable,
        };

    private static string? First(Func<string, string?> environment, params string[] names)
    {
        foreach (var name in names)
            if (environment(name) is { } value && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    // ---- the names of earlier releases

    /// <summary>One variable of an earlier release and the one that replaces it.</summary>
    public sealed record OldName(string Old, string New);

    private static IEnumerable<OldName> OldNameList()
    {
        yield return new("DBDATAKIT_CONSOLE_DIR", FolderVariable("server"));
        yield return new("DBDATAKIT_IDENTITY_DIR", FolderVariable("identity"));
        yield return new("DBDATARUNNER_SERVER_DIR", FolderVariable("runner"));
        yield return new("DBDATASTORE_SERVER_DIR", FolderVariable("store"));
        yield return new("DBDATAWORKSPACE_SERVER_DIR", FolderVariable("workspace"));
        yield return new("DBDATABUILD_SERVER_DIR", FolderVariable("build"));
        // Sync's and the Gateway's folders were ASP.NET configuration, written as environment variables in the framework's spelling
        yield return new("DbDataSync__App__RepoRoot", FolderVariable("sync"));
        yield return new("DBGATEWAY_Gateway__DataDirectory", FolderVariable("gateway"));
        foreach (var (prefix, service) in new[]
        {
            ("DBDATAKIT_CONSOLE_STATE", "server"), ("DBDATAKIT_IDENTITY_STATE", "identity"), ("DBDATARUNNER_STATE", "runner"), ("DBDATASTORE_STATE", "store"),
            ("DBDATAWORKSPACE_STATE", "workspace"), ("DBDATABUILD_STATE", "build"),
        })
        {
            yield return new($"{prefix}_ENGINE", StateEngineVariableOf(service));
            yield return new($"{prefix}_CONNECTION", StateConnectionVariableOf(service));
        }

        yield return new("DBGATEWAY_Gateway__State__Engine", StateEngineVariableOf("gateway"));
        yield return new("DBGATEWAY_Gateway__State__Connection", StateConnectionVariableOf("gateway"));

        // SC31a: announcing and pairing (the last two lose CONSOLE; DBDATAKIT_ADVERTISE_URL and DBDATAKIT_INSTANCE_NAME stay)
        yield return new("DBDATAKIT_CONSOLE_URL", "DBDATAKIT_URL");
        yield return new("DBDATAKIT_CONSOLE_ANNOUNCE_URL", "DBDATAKIT_ANNOUNCE_URL");
        yield return new("DBDATAKIT_CONSOLE_ANNOUNCE_LISTEN", "DBDATAKIT_ANNOUNCE_LISTEN");
        // keys, passwords and upgrade (the DBDATA_SECRET_<NAME> variables are a prefix rule, below)
        yield return new("DBDATA_SECRETS_KEY_FILE", "DBDATAKIT_SECRETS_KEY_FILE");
        yield return new("DBDATA_SIGNING_KEY", "DBDATAKIT_SIGNING_KEY");
        yield return new("DBGATEWAY_PASSWORD", "DBDATAKIT_GATEWAY_PASSWORD");
        yield return new("DBGATEWAY_WORKER_SECRET", "DBDATAKIT_GATEWAY_WORKER_SECRET");
        foreach (var setting in new[] { "CHANNEL", "MIRROR", "REPO" })
            yield return new($"DBDATA_UPGRADE_{setting}", $"DBDATAKIT_UPGRADE_{setting}");
        // a setting of one service: DBDATAKIT_<SERVICE>_<NAME>
        foreach (var setting in new[] { "CHANNELS", "GROUP", "MAX_ARTIFACT_BYTES", "ACTIVATION_MAX_WAIT_SECONDS", "TASK_ENVIRONMENT", "CLIENT_DIR" })
            yield return new($"DBDATARUNNER_{setting}", $"DBDATAKIT_RUNNER_{setting}");
        yield return new("DBDATAGATEWAY_CLIENT_DIR", "DBDATAKIT_GATEWAY_CLIENT_DIR");
        foreach (var setting in new[] { "API_URL", "RUNNER_TOKEN", "STATE_ENDPOINT" })
            yield return new($"DBDATASYNC_{setting}", $"DBDATAKIT_SYNC_{setting}");
        foreach (var setting in new[] { "ACTOR", "CHECK_RESULTS", "DUCKDB_CLOCK_OVERRIDES", "POLYGLOT_PATH", "STATEMENT_LOG_DIR" })
            yield return new($"DBDATABUILD_{setting}", $"DBDATAKIT_BUILD_{setting}");
    }

    /// <summary>A family of variables of an earlier release that share a prefix: the prefix becomes <see cref="NewPrefix"/> and the rest of the name stays (<c>DBGATEWAY_Gateway__Listen__Port</c> is <c>DBDATAKIT_Gateway__Listen__Port</c>).</summary>
    /// <param name="Prefix">The old prefix, matched without regard to case (a Windows environment has none).</param>
    /// <param name="NewPrefix">What replaces it.</param>
    /// <param name="Except">Names (after the prefix) the rule leaves alone: the variables of tests and harnesses, which plan 33 SC31b renames; empty once it has.</param>
    /// <param name="Rename">The rest of the name in the new spelling, when more than the prefix changed.</param>
    public sealed record OldPrefix(string Prefix, string NewPrefix, Func<string, bool>? Except = null, Func<string, string>? Rename = null)
    {
        /// <summary>The replacement for a variable that has this prefix.</summary>
        public string Replacement(string name)
        {
            var rest = name[Prefix.Length..];
            return NewPrefix + (Rename?.Invoke(rest) ?? rest);
        }

        /// <summary>True when the variable belongs to this family and is not one of the exceptions.</summary>
        public bool Matches(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && name.Length > Prefix.Length && Except?.Invoke(name[Prefix.Length..]) != true;
    }

    // SC31b renames these (the variables of tests, harnesses and CI); until then a prefix rule does not refuse them
    private static bool IsTestOrHarness(string rest) =>
        rest.StartsWith("TEST_", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("E2E_", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("REQUIRE_", StringComparison.OrdinalIgnoreCase) ||
        rest.StartsWith("LIVE_", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("UPDATE_", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("ADMIN_TESTS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every family of earlier variables that is refused by prefix, after <see cref="OldNames"/> has had its say. This is the one list.</summary>
    public static IReadOnlyList<OldPrefix> OldPrefixes { get; } =
    [
        new("DBDATA_SECRET_", "DBDATAKIT_SECRET_"),
        new("DBDATA_BUILD_", "DBDATAKIT_BUILD_"),
        new("DBGATEWAY_", "DBDATAKIT_", IsTestOrHarness),
        new("DBPROXY_", "DBDATAKIT_", IsTestOrHarness, rest => rest.StartsWith("Proxy__", StringComparison.OrdinalIgnoreCase) ? "Gateway__" + rest["Proxy__".Length..] : rest),
    ];

    /// <summary>Every variable of an earlier release that is refused, with the one that replaces it. This is the one list.</summary>
    public static IReadOnlyList<OldName> OldNames { get; } = [.. OldNameList()];

    private static string Message(string old, string replacement) =>
        $"The environment variable {old} is no longer read: it was renamed to {replacement}, and there is no alias for the old name. Set {replacement} (and unset {old}) and start again.";

    /// <summary>
    /// The message for the first old variable that is set (to anything, an empty value included, since a script that sets it means it), or null when none is. It names both variables and says there is no alias.
    /// The exact names of <see cref="OldNames"/> are asked of <paramref name="environment"/>; the families of <see cref="OldPrefixes"/> need the names that are set, which are
    /// <paramref name="names"/>, or those of the process when no <paramref name="environment"/> is given (an injected environment that cannot list its names is checked for the exact names only).
    /// </summary>
    public static string? OldNameProblem(Func<string, string?>? environment = null, Func<IEnumerable<string>>? names = null)
    {
        names ??= environment is null ? ProcessNames : null;
        environment ??= Environment.GetEnvironmentVariable;
        foreach (var (old, replacement) in OldNames)
            if (environment(old) is not null)
                return Message(old, replacement);
        if (names is not null)
            foreach (var name in names().Order(StringComparer.Ordinal))
                foreach (var rule in OldPrefixes)
                    if (rule.Matches(name) && environment(name) is not null)
                        return Message(name, rule.Replacement(name));
        return null;
    }

    private static IEnumerable<string> ProcessNames() => Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList();

    /// <summary>Stops with <see cref="SuiteEnvironmentException"/> when a variable of an earlier release is set (<see cref="OldNameProblem"/>). Every server's <c>server run</c> and every command that takes <c>--repo</c> calls it first.</summary>
    public static void RefuseOldNames(Func<string, string?>? environment = null, Func<IEnumerable<string>>? names = null)
    {
        if (OldNameProblem(environment, names) is { } problem) throw new SuiteEnvironmentException(problem);
    }
}
