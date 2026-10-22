using ClrKernel.Core.Secrets;
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
    public void An_unknown_key_is_an_error_with_its_position_and_a_suggestion()
    {
        var yaml = File.ReadAllText(_path);
        var ex = Refused(yaml + "hots: sql02\n");
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal("DBD-104", d.Code);
        Assert.Equal("hots", d.KeyPath);
        Assert.Contains("Did you mean `host`?", d.Message);
        Assert.True(d.Line > 1);
        Assert.Contains("orders.yaml", ex.Message);
    }

    [Fact]
    public void An_unknown_key_inside_a_nested_setting_has_a_path()
    {
        var yaml = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(_path), @"^hooks:.*\r?\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        var ex = Refused(yaml + "hooks:\n  beforeRun:\n    - bogusField: 1\n");
        Assert.Contains(ex.Diagnostics, d => d.KeyPath is { } p && p.StartsWith("hooks.beforeRun[0].", StringComparison.Ordinal));
    }

    [Fact]
    public void A_repeated_key_and_a_syntax_error_are_refused_not_silently_resolved()
    {
        var yaml = File.ReadAllText(_path);
        Assert.Contains(Refused(yaml + "host: other\n").Diagnostics, d => d.Code == "DBD-102");
        Assert.Contains(Refused("name: [unclosed\n").Diagnostics, d => d.Code == "DBD-101");
    }

    [Fact]
    public void A_value_of_the_wrong_shape_is_refused_with_a_position()
    {
        var yaml = File.ReadAllText(_path);
        var ex = Refused(yaml + "port: many\n");
        Assert.Contains(ex.Diagnostics, d => d.Code == "DBD-106" && d.Line > 0);
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
}
