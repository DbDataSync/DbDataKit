namespace DbDataKit.State;

/// <summary>Where one effective state setting came from (plan 29b C07).</summary>
public enum StateSourceKind
{
    /// <summary>Nothing named it: SQLite in the product's directory, the default namespaces, the default retention.</summary>
    Default,

    /// <summary>The <c>state:</c> section of the product's settings file.</summary>
    File,

    /// <summary>An environment variable.</summary>
    Environment,

    /// <summary>An option on the command line.</summary>
    Flag,
}

/// <summary>One setting's origin: its kind and the name that identifies it (the file's path, the variable's name, the option as typed).</summary>
public sealed record StateSource(StateSourceKind Kind, string? Name = null)
{
    public static StateSource Default { get; } = new(StateSourceKind.Default);

    /// <summary>The origin as a sentence part: <c>environment variable NAME</c>, <c>file /path/server.yaml</c>, <c>flag --state-engine</c>, <c>default</c>.</summary>
    public override string ToString() => Kind switch
    {
        StateSourceKind.Flag => $"flag {Name}",
        StateSourceKind.Environment => $"environment variable {Name}",
        StateSourceKind.File => $"file {Name}",
        _ => "default",
    };

    /// <summary>The origin as a JSON-friendly value: <c>kind</c> (<c>flag</c>, <c>environment</c>, <c>file</c>, <c>default</c>) and <c>name</c>.</summary>
    public Dictionary<string, object?> ToData() => new() { ["kind"] = Kind.ToString().ToLowerInvariant(), ["name"] = Name };
}

/// <summary>Where each part of the effective state settings came from.</summary>
public sealed record StateSources(StateSource Engine, StateSource Connection, StateSource Namespaces, StateSource Retention)
{
    public static StateSources Defaults { get; } = new(StateSource.Default, StateSource.Default, StateSource.Default, StateSource.Default);
}

/// <summary>The effective state settings and where each came from.</summary>
public sealed record ResolvedState(StateSettings Settings, StateSources Sources);

/// <summary>What <see cref="StateSettingsResolver.Resolve"/> reads. Every part is optional: a product passes the ones it has.</summary>
/// <param name="SettingsFile">The product's settings file; its <c>state:</c> section is read (strictly, errors name the file). Absent or without the section: nothing from it. The file is never created.</param>
/// <param name="EngineFlag">The value of the product's engine option.</param>
/// <param name="EngineFlagName">How the engine option is typed (<c>--state-engine</c>), for the origin shown.</param>
/// <param name="ConnectionFlag">The value of the product's connection option.</param>
/// <param name="ConnectionFlagName">How the connection option is typed.</param>
/// <param name="EngineVariable">The name of the environment variable that gives the engine, read through <paramref name="Environment"/>.</param>
/// <param name="ConnectionVariable">The name of the environment variable that gives the connection string.</param>
/// <param name="Environment">Reads an environment variable; null reads the process's.</param>
public sealed record StateSettingsInputs(
    string? SettingsFile = null,
    string? EngineFlag = null, string EngineFlagName = "--state-engine",
    string? ConnectionFlag = null, string ConnectionFlagName = "--state-connection",
    string? EngineVariable = null, string? ConnectionVariable = null,
    Func<string, string?>? Environment = null);

/// <summary>
/// One order of precedence for a product's state settings (plan 29b M7, C07): the command line, then the environment, then the <c>state:</c> section of the product's settings file, then the default (SQLite). The engine and the
/// connection are each taken from the first place that names them; the namespaces and the retention (<c>registry</c>, <c>namespaces</c>, <c>retention</c>) come from the file, because only a file can say them. The result says where each
/// came from, so <c>state show</c> can tell an administrator why a product uses the engine it does.
/// </summary>
public static class StateSettingsResolver
{
    public static ResolvedState Resolve(StateSettingsInputs inputs)
    {
        var environment = inputs.Environment ?? System.Environment.GetEnvironmentVariable;
        StateSettings fromFile = new();
        IReadOnlySet<string> keys = new HashSet<string>();
        string? file = null;
        if (inputs.SettingsFile is { Length: > 0 } path)
        {
            file = Path.GetFullPath(path);
            (fromFile, keys) = StateSettingsWriter.LoadSection(file);
        }

        StateSource FromFile(string key) => keys.Contains(key) ? new StateSource(StateSourceKind.File, file) : StateSource.Default;

        var (engine, engineSource) = Pick(inputs.EngineFlag, inputs.EngineFlagName, inputs.EngineVariable, environment, fromFile.Engine, FromFile("engine"));
        var (connection, connectionSource) = Pick(inputs.ConnectionFlag, inputs.ConnectionFlagName, inputs.ConnectionVariable, environment, fromFile.ConnectionString, FromFile("connection"));
        var settings = new StateSettings(engine, connection)
        {
            Namespaces = fromFile.Namespaces,
            Retention = fromFile.Retention,
        };
        var namespacesSource = keys.Contains("namespaces") || keys.Contains("registry") ? new StateSource(StateSourceKind.File, file) : StateSource.Default;
        return new ResolvedState(settings, new StateSources(engineSource, connectionSource, namespacesSource, FromFile("retention")));
    }

    private static (string? Value, StateSource Source) Pick(string? flag, string flagName, string? variable, Func<string, string?> environment, string? fromFile, StateSource fileSource)
    {
        if (!string.IsNullOrWhiteSpace(flag)) return (flag, new StateSource(StateSourceKind.Flag, flagName));
        if (variable is { Length: > 0 } && environment(variable) is { } value && !string.IsNullOrWhiteSpace(value)) return (value, new StateSource(StateSourceKind.Environment, variable));
        return string.IsNullOrWhiteSpace(fromFile) ? (null, StateSource.Default) : (fromFile, fileSource);
    }
}
