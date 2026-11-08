using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Gateway's own users are retired (plan 13a P13; plan 16 "Sign-in decisions" item 2): a configuration that still lists them, or a Windows <c>AuthType</c> entry, is refused with a message naming the file (or the layer) and the key, never silently
/// ignored. An empty list lists none.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class RetiredUsersTests
{
    private const string Erp = "schema: connection/1\nname: erp\nengine: sqlserver\nconnection_string: 'Server=s;Database=d'\nextensions:\n  gateway:\n    groups: [finance]\n";
    private const string UsersFile = "users:\n  - name: reporting\n    passwordHash: PBKDF2-SHA256$1$a$b\n    dataSources: [erp]\n    defaultDataSource: erp\n";

    private static (int Code, string Err) Check(TempConfig config)
    {
        var err = new StringWriter();
        var code = ConfigCommand.Run(["check", "--file", config.Directory], new StringWriter(), err);
        return (code, err.ToString());
    }

    [Fact]
    public void A_users_file_that_lists_a_user_is_refused_naming_the_file_the_line_and_the_key_and_what_replaces_it()
    {
        using var config = TempConfig.Create(("server.yaml", "schemaVersion: 2\n"), ("connections/erp.yaml", Erp), ("gateway/users.yaml", UsersFile));

        var (code, err) = Check(config);

        Assert.Equal(2, code);
        Assert.Contains(Path.Combine("gateway", "users.yaml") + ":2", err);
        Assert.Contains("Gateway:Users", err);
        Assert.Contains("is retired", err);
        Assert.Contains("identity home", err);
        Assert.Contains("extensions.gateway.groups", err);
        Assert.DoesNotContain("PBKDF2", err);      // never a hash
    }

    [Fact]
    public void A_windows_account_entry_is_refused_the_same_way()
    {
        using var config = TempConfig.Create(("connections/erp.yaml", Erp), ("gateway/users.yaml", "users:\n  - name: 'CORP\\bob'\n    authType: windows\n    dataSources: [erp]\n"));

        var (code, err) = Check(config);

        Assert.Equal(2, code);
        Assert.Contains(Path.Combine("gateway", "users.yaml"), err);
        Assert.Contains("is retired", err);
    }

    [Fact]
    public void Users_written_in_server_yaml_are_refused_naming_that_file()
    {
        using var config = TempConfig.Create(("server.yaml", "schemaVersion: 2\nusers:\n  - name: bob\n    passwordHash: h\n"), ("connections/erp.yaml", Erp));

        var (code, err) = Check(config);

        Assert.Equal(2, code);
        Assert.Contains("server.yaml:3", err);
        Assert.Contains("is retired", err);
    }

    [Fact]
    public void An_empty_list_of_users_lists_none_and_is_accepted()
    {
        using var config = TempConfig.Create(("connections/erp.yaml", Erp), ("gateway/users.yaml", "users: []\n"));

        Assert.Equal(0, Check(config).Code);
    }

    [Fact]
    public void Users_from_the_environment_are_refused_naming_the_layer_and_the_server_does_not_start()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbgateway-retired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var env = new EnvScope(("DBGATEWAY_Gateway__Users__0__Name", "bob"), ("DBGATEWAY_Gateway__Users__0__PasswordHash", "h"));
            var ex = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={dir}", "--Gateway:Listen:Port=0", "--Gateway:Tls:Mode=None"]));
            Assert.Contains("Gateway:Users", ex.Message);
            Assert.Contains("is retired", ex.Message);
            Assert.Contains("environment", ex.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_users_file_stops_the_server_from_starting_and_the_message_names_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbgateway-retired-" + Guid.NewGuid().ToString("N"));
        var users = Path.Combine(GatewayConfigFile.DirectoryIn(dir), "gateway", "users.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(users)!);
        File.WriteAllText(users, UsersFile);
        try
        {
            var ex = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={dir}", "--Gateway:Listen:Port=0", "--Gateway:Tls:Mode=None"]));
            Assert.Contains(users, ex.Message);
            Assert.Contains("Gateway:Users", ex.Message);
            Assert.Equal(GatewayConfigException.ExitCode, 2);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_data_source_file_names_its_groups_and_everyone_with_strictly_read_keys()
    {
        using var config = TempConfig.Create(
            ("connections/erp.yaml", Erp.Replace("groups: [finance]", "groups: [finance, Analysts]\n    default: true")),
            ("connections/scratch.yaml", "schema: connection/1\nname: scratch\nengine: duckdb\nconnection_string: 'Data Source=:memory:'\nextensions:\n  gateway:\n    everyone: true\n"),
            ("connections/misspelt.yaml", "schema: connection/1\nname: misspelt\nengine: duckdb\nconnection_string: 'Data Source=:memory:'\nextensions:\n  gateway:\n    group: [finance]\n"));

        var load = ConfigTestSupport.LoadDirectory(config.Directory);

        // the misspelt key is refused naming the file and the key, with the suggestion
        Assert.False(load.Ok);
        var unknown = Assert.Single(load.Diagnostics, d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error);
        Assert.EndsWith("misspelt.yaml", unknown.Path);
        Assert.Contains("Group", unknown.KeyPath ?? "");
        Assert.Contains("Did you mean `Groups`?", unknown.Message);

        File.Delete(config.Path("connections/misspelt.yaml"));
        var ok = ConfigTestSupport.LoadDirectory(config.Directory);
        Assert.True(ok.Ok, ConfigTestSupport.Format(ok));
        Assert.Equal(["finance", "Analysts"], ok.Value!.DataSources["erp"].Groups);
        Assert.True(ok.Value.DataSources["erp"].Default);
        Assert.True(ok.Value.DataSources["scratch"].Everyone);
        Assert.Empty(ok.Value.DataSources["scratch"].Groups);
    }

    [Fact]
    public void A_data_source_that_names_no_group_and_is_not_open_is_a_warning_naming_it_and_a_pass_through_source_is_not()
    {
        using var config = TempConfig.Create(
            ("connections/closed.yaml", "schema: connection/1\nname: closed\nengine: duckdb\nconnection_string: 'Data Source=:memory:'\n"),
            ("connections/pass.yaml", "schema: connection/1\nname: pass\nengine: sqlserver\nconnection_string: 'Server=s'\nextensions:\n  gateway:\n    pass_through: true\n"));

        var load = ConfigTestSupport.LoadDirectory(config.Directory);

        Assert.True(load.Ok, ConfigTestSupport.Format(load));
        var warning = Assert.Single(load.Diagnostics, d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Warning && (d.KeyPath ?? "").Contains("Groups"));
        Assert.Contains("closed", warning.Message);
        Assert.Contains("no user of the identity home", warning.Message);
        Assert.Contains("everyone", warning.Fix);
    }
}
