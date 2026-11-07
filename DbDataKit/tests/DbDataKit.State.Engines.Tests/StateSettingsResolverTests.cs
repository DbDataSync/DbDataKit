namespace DbDataKit.State.Tests;

/// <summary>Plan 29b C07: the order every product reads its state settings in (flag, environment, the file's <c>state:</c> section, SQLite) and where each value came from.</summary>
public sealed class StateSettingsResolverTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("state-resolver").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File_ => Path.Combine(_directory, "server.yaml");

    private static StateSettingsInputs Inputs(string? file, string? engineFlag = null, string? connectionFlag = null, Dictionary<string, string>? environment = null) =>
        new(file, engineFlag, "--state-engine", connectionFlag, "--state-connection", "APP_STATE_ENGINE", "APP_STATE_CONNECTION", name => environment is not null && environment.TryGetValue(name, out var v) ? v : null);

    [Fact]
    public void Nothing_named_is_the_default_from_the_default_and_the_file_is_not_created()
    {
        var resolved = StateSettingsResolver.Resolve(Inputs(File_));

        Assert.True(resolved.Settings.IsSqlite);
        Assert.Equal(StateSources.Defaults, resolved.Sources);
        Assert.Equal("default", resolved.Sources.Engine.ToString());
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void The_file_is_used_when_nothing_beats_it_and_names_its_path()
    {
        File.WriteAllText(File_, "listen: x\nstate:\n  engine: postgres\n  connection: Host=db;Password=env:PW\n  retention: { days: 7 }\n");

        var resolved = StateSettingsResolver.Resolve(Inputs(File_));

        Assert.Equal(("postgres", "Host=db;Password=env:PW"), (resolved.Settings.Engine, resolved.Settings.ConnectionString));
        Assert.Equal(7, resolved.Settings.Retention.Days);
        var file = new StateSource(StateSourceKind.File, Path.GetFullPath(File_));
        Assert.Equal(new StateSources(file, file, StateSource.Default, file), resolved.Sources);
        Assert.Equal($"file {Path.GetFullPath(File_)}", resolved.Sources.Engine.ToString());
    }

    [Fact]
    public void The_environment_beats_the_file_and_the_flag_beats_the_environment_each_setting_on_its_own()
    {
        File.WriteAllText(File_, "state:\n  engine: postgres\n  connection: Host=file\n");
        var environment = new Dictionary<string, string> { ["APP_STATE_ENGINE"] = "mssql", ["APP_STATE_CONNECTION"] = "Server=env" };

        var fromEnvironment = StateSettingsResolver.Resolve(Inputs(File_, environment: environment));
        Assert.Equal(("mssql", "Server=env"), (fromEnvironment.Settings.Engine, fromEnvironment.Settings.ConnectionString));
        Assert.Equal("environment variable APP_STATE_ENGINE", fromEnvironment.Sources.Engine.ToString());
        Assert.Equal("environment variable APP_STATE_CONNECTION", fromEnvironment.Sources.Connection.ToString());

        var fromFlag = StateSettingsResolver.Resolve(Inputs(File_, engineFlag: "sqlite", environment: environment));
        Assert.Equal(("sqlite", "Server=env"), (fromFlag.Settings.Engine, fromFlag.Settings.ConnectionString));
        Assert.Equal("flag --state-engine", fromFlag.Sources.Engine.ToString());
        Assert.Equal(StateSourceKind.Environment, fromFlag.Sources.Connection.Kind);

        var both = StateSettingsResolver.Resolve(Inputs(File_, "postgres", "Host=flag", environment));
        Assert.Equal("flag --state-connection", both.Sources.Connection.ToString());
    }

    [Fact]
    public void A_blank_variable_is_not_a_setting()
    {
        File.WriteAllText(File_, "state:\n  engine: mssql\n  connection: Server=file\n");

        var resolved = StateSettingsResolver.Resolve(Inputs(File_, environment: new() { ["APP_STATE_ENGINE"] = "  " }));

        Assert.Equal(StateSourceKind.File, resolved.Sources.Engine.Kind);
    }

    [Fact]
    public void A_typo_in_the_file_is_refused_with_a_suggestion_and_the_files_path()
    {
        File.WriteAllText(File_, "state:\n  engin: postgres\n");

        var refused = Assert.Throws<InvalidOperationException>(() => StateSettingsResolver.Resolve(Inputs(File_)));

        Assert.Contains(Path.GetFullPath(File_), refused.Message);
        Assert.Contains("state.engin is not a state setting", refused.Message);
        Assert.Contains("did you mean 'engine'", refused.Message);
    }
}
