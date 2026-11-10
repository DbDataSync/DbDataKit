using DbDataKit.Config.Git;
using DbDataKit.Connections.Inventory;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>Plan 28c, E20: a connection's two logins, <c>read</c> and <c>run</c>. One login serves both; a second is its own user and its own secret, kept in the file as a reference and never as a value.</summary>
public sealed class CredentialRolesTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-roles-").FullName;
    private readonly string _configRoot;
    private readonly ConfigRepository _repository;
    private readonly SecretStore _secrets = SecretStore.ForProviders([new InMemorySecretProvider()]);
    private static readonly GitAuthor Author = new("T", "t@example.com");

    public CredentialRolesTests()
    {
        Repository.Init(_repoRoot);
        _configRoot = Path.Combine(_repoRoot, "config");
        _repository = new ConfigRepository(_configRoot, new GitCommitService(_repoRoot), _secrets);
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private string FileOf(string name) => Directory.GetFiles(_configRoot, name + ".yaml", SearchOption.AllDirectories).Single();

    private static ConnectionInput Input(string name, RunLoginInput? run = null) => new()
    {
        Name = name, DriverType = DriverIds.MsSql, Host = "sql01", Database = "App", AuthMode = AuthMode.SqlAuth,
        UserId = "reader", Password = "read-pw-1", RunLogin = run,
    };

    [Fact]
    public void A_connection_with_one_login_serves_both_roles_and_has_no_second_key_in_its_file()
    {
        var saved = _repository.SaveConnection(Input("orders"), Author);

        Assert.False(saved.HasRunLogin);
        var asRun = saved.ForRole(CredentialRole.Run);
        Assert.Equal("reader", asRun.UserId);
        Assert.Equal(SecretRefs.ForConnection("orders"), asRun.CredentialSecretRef);
        var text = File.ReadAllText(FileOf("orders"));
        Assert.DoesNotContain("run_", text);
        Assert.Equal(SecretRefs.ForConnection("orders"), _repository.LoadConnection("orders").CredentialSecretRef);
    }

    [Fact]
    public void A_second_login_is_its_own_user_and_secret_and_the_file_holds_only_the_reference()
    {
        _repository.SaveConnection(Input("orders", new RunLoginInput { UserId = "runner", Password = "run-pw-2" }), Author);

        var loaded = _repository.LoadConnection("orders");
        Assert.True(loaded.HasRunLogin);
        var read = loaded.ForRole(CredentialRole.Read);
        var run = loaded.ForRole(CredentialRole.Run);
        Assert.Equal(("reader", SecretRefs.ForConnection("orders")), (read.UserId, read.CredentialSecretRef));
        Assert.Equal(("runner", SecretRefs.ForConnectionRun("orders")), (run.UserId, run.CredentialSecretRef));
        Assert.Equal("read-pw-1", _secrets.Resolve(read.CredentialSecretRef!));
        Assert.Equal("run-pw-2", _secrets.Resolve(run.CredentialSecretRef!));

        var text = File.ReadAllText(FileOf("orders"));
        Assert.Contains("run_user: runner", text);
        Assert.Contains("run_password_secret: secret:dbdata:sync:connection:orders:run", text);
        Assert.DoesNotContain("read-pw-1", text);
        Assert.DoesNotContain("run-pw-2", text);
        // the copy for a role carries no second login: nothing downstream can pick the other role's secret from it
        Assert.False(run.HasRunLogin);
    }

    [Fact]
    public void A_second_login_without_a_user_uses_the_first_user_and_without_a_password_keeps_the_stored_one()
    {
        _repository.SaveConnection(Input("orders", new RunLoginInput { Password = "run-pw-2" }), Author);
        Assert.Equal("reader", _repository.LoadConnection("orders").ForRole(CredentialRole.Run).UserId);

        _repository.SaveConnection(Input("orders", new RunLoginInput { UserId = "runner" }), Author);
        var run = _repository.LoadConnection("orders").ForRole(CredentialRole.Run);
        Assert.Equal("runner", run.UserId);
        Assert.Equal("run-pw-2", _secrets.Resolve(run.CredentialSecretRef!));
    }

    [Fact]
    public void A_second_login_with_no_password_anywhere_is_refused_naming_the_reference_and_nothing_is_written()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Input("orders", new RunLoginInput { UserId = "runner" }), Author));
        Assert.Contains("dbdata:sync:connection:orders:run", ex.Message);
        Assert.Empty(Directory.GetFiles(_configRoot, "orders.yaml", SearchOption.AllDirectories));
    }

    [Fact]
    public void Taking_the_second_login_away_removes_its_secret_and_the_keys()
    {
        _repository.SaveConnection(Input("orders", new RunLoginInput { UserId = "runner", Password = "run-pw-2" }), Author);
        _repository.SaveConnection(Input("orders"), Author);

        Assert.False(_secrets.TryResolve(SecretRefs.ForConnectionRun("orders"), out _));
        Assert.False(_repository.LoadConnection("orders").HasRunLogin);
        Assert.DoesNotContain("run_", File.ReadAllText(FileOf("orders")));
    }

    [Fact]
    public void Deleting_the_connection_deletes_both_secrets()
    {
        _repository.SaveConnection(Input("orders", new RunLoginInput { Password = "run-pw-2" }), Author);
        _repository.DeleteConnection("orders", Author);

        Assert.False(_secrets.TryResolve(SecretRefs.ForConnection("orders"), out _));
        Assert.False(_secrets.TryResolve(SecretRefs.ForConnectionRun("orders"), out _));
    }

    [Fact]
    public void A_file_from_before_the_change_loads_with_one_login()
    {
        _repository.SaveConnection(Input("orders"), Author);
        var before = File.ReadAllText(FileOf("orders"));

        // a file as the releases before E20 wrote it: the same text, which has none of the second login's keys
        File.WriteAllText(FileOf("orders"), before);
        var loaded = _repository.LoadConnection("orders");

        Assert.False(loaded.HasRunLogin);
        Assert.Equal("reader", loaded.UserId);
    }

    [Theory]
    [InlineData("run_password_secret", "run_pasword_secret", "extensions.sync.run_pasword_secret", "extensions.sync.run_password_secret")]
    [InlineData("run_user", "runuser", "extensions.sync.runuser", "extensions.sync.run_user")]
    public void A_misspelled_key_is_refused_naming_the_file_the_line_and_the_right_key(string right, string key, string keyPath, string suggestion)
    {
        _repository.SaveConnection(Input("orders", new RunLoginInput { UserId = "runner", Password = "run-pw-2" }), Author);
        var path = FileOf("orders");
        var text = File.ReadAllText(path).Replace(right + ":", key + ":", StringComparison.Ordinal);
        File.WriteAllText(path, text);

        var ex = Assert.Throws<ConfigFileException>(() => _repository.LoadConnection("orders"));
        var d = ex.Diagnostics.First(x => x.KeyPath == keyPath);
        Assert.Equal("DBD-104", d.Code);
        Assert.True(d.Line > 1);
        Assert.Contains("orders.yaml", ex.Message);
        Assert.Contains(suggestion, d.Message);
    }

    [Fact]
    public void A_run_user_without_a_run_secret_and_a_run_secret_that_is_not_a_reference_are_refused()
    {
        _repository.SaveConnection(Input("orders", new RunLoginInput { UserId = "runner", Password = "run-pw-2" }), Author);
        var path = FileOf("orders");
        var original = File.ReadAllText(path);

        File.WriteAllText(path, original.Replace("run_password_secret: secret:dbdata:sync:connection:orders:run\n", ""));
        Assert.Contains("run_user", Assert.Throws<ConfigFileException>(() => _repository.LoadConnection("orders")).Message);

        File.WriteAllText(path, original.Replace("secret:dbdata:sync:connection:orders:run", "hunter2"));
        Assert.Contains("run_password_secret", Assert.Throws<ConfigFileException>(() => _repository.LoadConnection("orders")).Message);
    }

    [Fact]
    public void A_file_that_names_another_connections_run_secret_is_refused_rather_than_saved_under_the_wrong_one()
    {
        _repository.SaveConnection(Input("orders", new RunLoginInput { Password = "run-pw-2" }), Author);
        var text = File.ReadAllText(FileOf("orders")).Replace("connection:orders:run", "connection:billing:run");

        var ex = Assert.Throws<ConfigValidationException>(() => _repository.ConnectionInputFromFile(text, "orders", null));
        Assert.Contains("dbdata:sync:connection:orders:run", ex.Message);
    }

    [Fact]
    public void A_file_with_a_second_login_round_trips_through_a_copy_with_the_password_beside_it()
    {
        _repository.SaveConnection(Input("orders", new RunLoginInput { UserId = "runner", Password = "run-pw-2" }), Author);
        var text = _repository.ReadConnectionFileText("orders")!;

        var input = _repository.ConnectionInputFromFile(text, "orders", "read-pw-1", "run-pw-3");

        Assert.Equal("runner", input.RunLogin!.UserId);
        Assert.Equal("run-pw-3", input.RunLogin.Password);
    }

    [Fact]
    public void The_inventory_lists_both_references_and_no_value()
    {
        var one = _repository.SaveConnection(Input("orders"), Author);
        var two = _repository.SaveConnection(Input("billing", new RunLoginInput { UserId = "runner", Password = "run-pw-2" }), Author);

        var inventory = SyncConnectionInventory.Build([one, two], []);
        var json = ConnectionInventoryJson.Write("sync", inventory).ToJsonString();

        Assert.Contains("secret:dbdata:sync:connection:billing:run", json);
        Assert.Contains("\"run_secret_ref\"", json);
        Assert.Equal(1, json.Split("run_secret_ref").Length - 1);
        Assert.DoesNotContain("read-pw-1", json);
        Assert.DoesNotContain("run-pw-2", json);
        Assert.Null(inventory.Single(c => c.Name == "orders").RunSecretRef);
        Assert.Equal(inventory.Count, ConnectionInventoryJson.Read(ConnectionInventoryJson.Write("sync", inventory)).Count);
    }

    [Fact]
    public void A_second_login_needs_a_user_and_password_login()
    {
        var input = new ConnectionInput { Name = "orders", DriverType = DriverIds.MsSql, Host = "sql01", AuthMode = AuthMode.IntegratedAuth, RunLogin = new RunLoginInput { Password = "x" } };
        Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(input, Author));
    }
}
