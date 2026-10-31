using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

public sealed class StrictConnectionFileTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-strict-").FullName;
    private readonly ConfigRepository _repository;
    private readonly string _path;

    public StrictConnectionFileTests()
    {
        Repository.Init(_repoRoot);
        var configRoot = Path.Combine(_repoRoot, "config");
        _repository = new ConfigRepository(configRoot, new GitCommitService(_repoRoot), SecretStore.ForProviders([new InMemorySecretProvider()]));
        _repository.SaveConnection(new ConnectionInput { Name = "orders", DriverType = DriverIds.MsSql, Host = "sql01", AuthMode = AuthMode.IntegratedAuth, Properties = { ["applicationName"] = "x" } }, new GitAuthor("T", "t@example.com"));
        _path = Directory.GetFiles(configRoot, "orders.yaml", SearchOption.AllDirectories).Single();
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private const string Valid = """
        schema: connection/1
        name: orders
        engine: sqlserver
        address: { host: sql01 }
        auth: { mode: integrated }
        extensions:
          sync:
            properties: { applicationName: x }

        """;

    private ConfigFileException Refused(string yaml)
    {
        File.WriteAllText(_path, yaml);
        return Assert.Throws<ConfigFileException>(() => _repository.LoadConnection("orders"));
    }

    [Fact]
    public void A_saved_connection_loads()
    {
        Assert.Equal("sql01", _repository.LoadConnection("orders").Host);
    }

    [Fact]
    public void An_unknown_key_is_an_error_with_its_position()
    {
        var ex = Refused(Valid + "hots: sql02\n");
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal("DBD-104", d.Code);
        Assert.Equal("hots", d.KeyPath);
        Assert.True(d.Line > 1);
        Assert.Contains("orders.yaml", ex.Message);
    }

    [Fact]
    public void An_unknown_key_in_the_shared_part_and_in_extensions_sync_has_a_path_and_a_suggestion()
    {
        var shared = Refused(Valid.Replace("address: { host: sql01 }", "address: { hots: sql01 }"));
        Assert.Contains(shared.Diagnostics, d => d.Code == "DBD-104" && d.Message.Contains("`hots` in `address`. Did you mean `host`?", StringComparison.Ordinal));

        var ext = Refused(Valid.Replace("properties: { applicationName: x }", "testQury: select 1"));
        var d = Assert.Single(ext.Diagnostics);
        Assert.Equal("DBD-104", d.Code);
        Assert.Equal("extensions.sync.testQury", d.KeyPath);
        Assert.Contains("Did you mean `extensions.sync.testQuery`?", d.Message);
    }

    [Fact]
    public void An_unknown_key_inside_a_hook_of_extensions_sync_has_a_path()
    {
        var ex = Refused(Valid.Replace("properties: { applicationName: x }", "hooks:\n      beforeLoad:\n        - bogusField: 1"));
        Assert.Contains(ex.Diagnostics, d => d.KeyPath is { } p && p.StartsWith("extensions.sync.hooks.beforeLoad[0].", StringComparison.Ordinal));
    }

    [Fact]
    public void A_repeated_key_and_a_syntax_error_are_refused_not_silently_resolved()
    {
        Assert.Contains(Refused(Valid + "engine: postgres\n").Diagnostics, d => d.Code == "DBD-102");
        Assert.Contains(Refused("name: [unclosed\n").Diagnostics, d => d.Code == "DBD-101");
    }

    [Fact]
    public void A_value_of_the_wrong_shape_is_refused_with_a_position()
    {
        var ex = Refused(Valid.Replace("address: { host: sql01 }", "address: { host: sql01, port: many }"));
        Assert.Contains(ex.Diagnostics, d => d.Line > 0 && d.KeyPath == "address.port");
    }

    [Fact]
    public void A_damaged_connection_file_is_refused_with_a_diagnostic_or_loads_never_another_exception()
    {
        var original = File.ReadAllText(_path);
        var random = new Random(20261021);
        for (var i = 0; i < 400; i++)
        {
            var chars = original.ToCharArray().ToList();
            for (var m = random.Next(1, 4); m > 0 && chars.Count > 0; m--)
            {
                var at = random.Next(chars.Count);
                switch (random.Next(4))
                {
                    case 0: chars.RemoveAt(at); break;
                    case 1: chars.Insert(at, ":{}[]-#&*!'\"\t "[random.Next(14)]); break;
                    case 2: chars[at] = (char)random.Next(32, 127); break;
                    default: chars.InsertRange(at, chars.Skip(at).Take(random.Next(1, 12)).ToList()); break;
                }
            }
            File.WriteAllText(_path, new string([.. chars]));
            try { _repository.LoadConnection("orders"); }
            catch (ConfigFileException) { }
            catch (InvalidDataException) { }   // the existing semantic checks on a loaded value
        }
    }

    [Fact]
    public void Task_mapping_and_script_manifest_files_refuse_unknown_keys_too()
    {
        var config = Path.Combine(_repoRoot, "config");
        var task = Path.Combine(config, "replications", "r1", "task.yaml");
        var mapping = Path.Combine(config, "replications", "r1", "table-mappings", "m1.yaml");
        var script = Path.Combine(config, "scripts", "s1.yaml");
        foreach (var file in new[] { task, mapping, script }) Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(task, "schemaVersion: 1\nname: r1\nscedule: x\n");
        File.WriteAllText(mapping, "schemaVersion: 1\nname: m1\nsurprise: 1\n");
        File.WriteAllText(script, "schemaVersion: 1\nname: s1\nsurprise: 1\n");

        Assert.Equal("scedule", Assert.Single(Assert.Throws<ConfigFileException>(() => _repository.LoadReplicationTask("r1")).Diagnostics).KeyPath);
        Assert.Equal("surprise", Assert.Single(Assert.Throws<ConfigFileException>(() => _repository.LoadTableMapping("r1", "m1")).Diagnostics).KeyPath);
        Assert.Equal("surprise", Assert.Single(Assert.Throws<ConfigFileException>(() => _repository.LoadScriptManifest("s1")).Diagnostics).KeyPath);
    }

    [Fact]
    public void A_saved_file_carries_its_schema()
    {
        Assert.StartsWith("schema: connection/1", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name: orders\nengine: sqlserver\naddress: { host: sql01 }\n", "`schema` is missing")]
    [InlineData("schema: connection/2\nname: orders\nengine: sqlserver\naddress: { host: sql01 }\n", "`schema` is `connection/2`")]
    public void A_file_without_the_current_schema_is_refused_with_a_message_that_says_why(string yaml, string text)
    {
        var d = Assert.Single(Refused(yaml).Diagnostics);
        Assert.Equal("DBD-CONN-001", d.Code);
        Assert.Contains(text, d.Message);
    }
}
