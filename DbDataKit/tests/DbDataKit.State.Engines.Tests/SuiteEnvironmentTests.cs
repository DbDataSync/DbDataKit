namespace DbDataKit.State.Tests;

/// <summary>Plan 33 section 6c: the one reader of the data folder and state variables, its order, and the refusal of the names of earlier releases (D14).</summary>
public sealed class SuiteEnvironmentTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] set) =>
        name => set.Where(s => s.Name == name).Select(s => s.Value).FirstOrDefault();

    public static TheoryData<string> AllServices => [.. SuiteEnvironment.Services];

    [Fact]
    public void The_services_are_the_eight_of_plan_33()
    {
        Assert.Equal(["server", "gateway", "runner", "sync", "build", "store", "workspace", "identity"], SuiteEnvironment.Services);
        Assert.Equal("DBDATAKIT_DATA_SERVER", SuiteEnvironment.FolderVariable("server"));
        Assert.Equal("DBDATAKIT_DATA_WORKSPACE", SuiteEnvironment.FolderVariable("Workspace"));
        Assert.Equal("DBDATAKIT_STATE_GATEWAY_ENGINE", SuiteEnvironment.StateEngineVariableOf("gateway"));
        Assert.Equal("DBDATAKIT_STATE_IDENTITY_CONNECTION", SuiteEnvironment.StateConnectionVariableOf("identity"));
        Assert.Throws<ArgumentException>(() => SuiteEnvironment.FolderVariable("console"));
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public void A_folder_is_the_services_variable_then_the_root_then_the_products_own_default(string service)
    {
        var root = Path.Combine(Path.GetTempPath(), "suite-root");
        var own = Path.Combine(Path.GetTempPath(), "suite-own");

        Assert.Equal("fallback", SuiteEnvironment.ResolveDataFolder(service, Env(), "fallback"));
        Assert.Equal("fallback", SuiteEnvironment.ResolveDataFolder(service, Env((SuiteEnvironment.DataRootVariable, "  ")), "fallback"));

        Assert.Equal(Path.Combine(root, service), SuiteEnvironment.ResolveDataFolder(service, Env((SuiteEnvironment.DataRootVariable, root)), "fallback"));

        var both = Env((SuiteEnvironment.DataRootVariable, root), (SuiteEnvironment.FolderVariable(service), own));
        Assert.Equal(own, SuiteEnvironment.ResolveDataFolder(service, both, "fallback"));

        // another service's variable does not move this one
        var other = SuiteEnvironment.Services.First(s => s != service);
        Assert.Equal("fallback", SuiteEnvironment.ResolveDataFolder(service, Env((SuiteEnvironment.FolderVariable(other), own)), "fallback"));
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public void A_registered_service_uses_the_machine_folder_unless_the_environment_names_one(string service)
    {
        var machine = SuiteEnvironment.MachineFolder(service);
        Assert.Equal(machine, SuiteEnvironment.ResolveServiceFolder(service, Env()));
        Assert.EndsWith(Path.Combine("DbDataKit", service).Replace('\\', Path.DirectorySeparatorChar), machine.Replace('\\', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux()) Assert.Equal($"/var/lib/dbdatakit/{service}", machine);
        Assert.Equal(Path.Combine("x", service), SuiteEnvironment.ResolveServiceFolder(service, Env((SuiteEnvironment.DataRootVariable, "x"))));
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public void State_is_the_flag_then_the_services_variables_then_the_installs_then_nothing(string service)
    {
        Assert.Null(SuiteEnvironment.ResolveState(service, Env()));

        var install = Env((SuiteEnvironment.StateEngineVariable, "postgres"), (SuiteEnvironment.StateConnectionVariable, "Host=install"));
        var fromInstall = SuiteEnvironment.ResolveState(service, install)!;
        Assert.Equal(("postgres", "Host=install"), (fromInstall.Engine, fromInstall.ConnectionString));

        var both = Env(
            (SuiteEnvironment.StateEngineVariable, "postgres"), (SuiteEnvironment.StateConnectionVariable, "Host=install"),
            (SuiteEnvironment.StateEngineVariableOf(service), "mssql"), (SuiteEnvironment.StateConnectionVariableOf(service), "Server=own"));
        var own = SuiteEnvironment.ResolveState(service, both)!;
        Assert.Equal(("mssql", "Server=own"), (own.Engine, own.ConnectionString));

        // each setting on its own: the service names the engine, the install the connection
        var mixed = SuiteEnvironment.ResolveState(service, Env((SuiteEnvironment.StateEngineVariableOf(service), "mssql"), (SuiteEnvironment.StateConnectionVariable, "Server=install")))!;
        Assert.Equal(("mssql", "Server=install"), (mixed.Engine, mixed.ConnectionString));

        var flagged = SuiteEnvironment.ResolveState(service, both, "sqlite", "Host=flag")!;
        Assert.Equal(("sqlite", "Host=flag"), (flagged.Engine, flagged.ConnectionString));

        // another service's variables do not apply
        var other = SuiteEnvironment.Services.First(s => s != service);
        Assert.Null(SuiteEnvironment.ResolveState(service, Env((SuiteEnvironment.StateEngineVariableOf(other), "postgres"))));
    }

    [Fact]
    public void The_resolver_inputs_name_the_variable_that_gave_each_setting()
    {
        var environment = Env((SuiteEnvironment.StateEngineVariable, "postgres"), (SuiteEnvironment.StateConnectionVariableOf("store"), "Host=own"));
        var resolved = StateSettingsResolver.Resolve(SuiteEnvironment.StateInputs("store", null, null, "--state-engine", null, "--state-connection", environment));

        Assert.Equal("environment variable DBDATAKIT_STATE_ENGINE", resolved.Sources.Engine.ToString());
        Assert.Equal("environment variable DBDATAKIT_STATE_STORE_CONNECTION", resolved.Sources.Connection.ToString());
        Assert.Equal(("postgres", "Host=own"), (resolved.Settings.Engine, resolved.Settings.ConnectionString));
    }

    [Fact]
    public void Nothing_is_refused_when_no_old_name_is_set()
    {
        Assert.Null(SuiteEnvironment.OldNameProblem(Env()));
        SuiteEnvironment.RefuseOldNames(Env((SuiteEnvironment.DataRootVariable, "x"), (SuiteEnvironment.StateEngineVariable, "postgres")));
    }

    public static TheoryData<string, string> EveryOldName => new(SuiteEnvironment.OldNames.Select(o => (o.Old, o.New)));

    [Theory]
    [MemberData(nameof(EveryOldName))]
    public void Each_old_name_that_is_set_stops_the_service_naming_the_new_one_and_saying_there_is_no_alias(string old, string replacement)
    {
        var environment = Env((old, "anything"));

        var problem = SuiteEnvironment.OldNameProblem(environment);

        Assert.NotNull(problem);
        Assert.Contains(old, problem);
        Assert.Contains(replacement, problem);
        Assert.Contains("no alias", problem);
        var thrown = Assert.Throws<SuiteEnvironmentException>(() => SuiteEnvironment.RefuseOldNames(environment));
        Assert.Equal(problem, thrown.Message);
        // an empty value is a set variable too
        Assert.NotNull(SuiteEnvironment.OldNameProblem(Env((old, ""))));
    }

    [Fact]
    public void The_list_is_the_one_in_plan_33_and_no_old_name_is_also_a_new_one()
    {
        string[] expectedOld =
        [
            "DBDATAKIT_CONSOLE_DIR", "DBDATAKIT_IDENTITY_DIR", "DBDATARUNNER_SERVER_DIR", "DBDATASTORE_SERVER_DIR", "DBDATAWORKSPACE_SERVER_DIR", "DBDATABUILD_SERVER_DIR",
            "DbDataSync__App__RepoRoot", "DBGATEWAY_Gateway__DataDirectory", "DBGATEWAY_Gateway__State__Engine", "DBGATEWAY_Gateway__State__Connection",
            "DBDATAKIT_CONSOLE_STATE_ENGINE", "DBDATAKIT_CONSOLE_STATE_CONNECTION", "DBDATAKIT_IDENTITY_STATE_ENGINE", "DBDATAKIT_IDENTITY_STATE_CONNECTION",
            "DBDATARUNNER_STATE_ENGINE", "DBDATARUNNER_STATE_CONNECTION", "DBDATASTORE_STATE_ENGINE", "DBDATASTORE_STATE_CONNECTION",
            "DBDATAWORKSPACE_STATE_ENGINE", "DBDATAWORKSPACE_STATE_CONNECTION", "DBDATABUILD_STATE_ENGINE", "DBDATABUILD_STATE_CONNECTION",
        ];
        Assert.Equal(expectedOld.Order(), SuiteEnvironment.OldNames.Select(o => o.Old).Order());
        var news = SuiteEnvironment.OldNames.Select(o => o.New).ToHashSet();
        Assert.DoesNotContain(SuiteEnvironment.OldNames, o => news.Contains(o.Old));
        // a new name is never refused
        foreach (var name in news) Assert.Null(SuiteEnvironment.OldNameProblem(Env((name, "x"))));
    }
}
