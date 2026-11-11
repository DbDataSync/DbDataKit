using System.Data.Common;
using DbDataKit.Secrets;
using DuckDB.NET.Data;

namespace DbDataKit.Connections.DuckDb.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbdata-duck-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

public class DuckDbTests
{
    private static ConnectionContext Ctx(string db, AccessMode access = AccessMode.Readonly, LoginRole role = LoginRole.Default) => new()
    {
        Definition = new ConnectionDefinition { Name = "d", Engine = "duckdb", Access = access, Address = new AddressSpec { Database = db } },
        Role = role,
    };

    private static string MakeDatabase(TempDir dir)
    {
        string path = dir.File("sample.duckdb");
        using (var c = new DuckDBConnection("Data Source=" + path))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1), (2), (3);";
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    private static async Task<Exception?> TryRun(DbConnection c, string sql)
    {
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) { }
            return null;
        }
        catch (Exception e) { return e; }
    }

    // ---- offline: what the composer and recipe say ---------------------------------------------

    [Fact]
    public void Recipe_is_the_documented_sandbox_with_the_lock_last()
    {
        var r = DuckDbProvider.ReadOnlyRecipe;
        Assert.Equal(ReadOnlyEnforcement.Engine, r.Enforcement);
        Assert.Equal("READ_ONLY", r.ConnectionStringKeys["access_mode"]);
        Assert.Contains("SET enable_external_access = false", r.Statements);
        Assert.Contains("SET autoinstall_known_extensions = false", r.Statements);
        Assert.Contains("SET autoload_known_extensions = false", r.Statements);
        Assert.Equal("SET lock_configuration = true", r.Statements[^1]);
    }

    [Fact]
    public void Read_only_context_adds_access_mode_and_the_file()
    {
        var p = new DuckDbProvider();
        var ro = new DuckDBConnectionStringBuilder { ConnectionString = p.Composer.Compose(Ctx("/data/x.duckdb")) };
        Assert.Equal("/data/x.duckdb", ro.DataSource);
        Assert.Contains("access_mode=READ_ONLY", p.Composer.Compose(Ctx("/data/x.duckdb")), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_mode", p.Composer.Compose(Ctx("/data/x.duckdb", AccessMode.Readwrite)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access_mode=READ_ONLY", p.Composer.Compose(Ctx("/data/x.duckdb", AccessMode.Readwrite, LoginRole.Read)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Host_port_and_credentials_are_refused()
    {
        var p = new DuckDbProvider();
        var withHost = new ConnectionContext { Definition = new ConnectionDefinition { Name = "d", Engine = "duckdb", Address = new AddressSpec { Host = "h", Database = "x" } } };
        Assert.Throws<ConnectionPolicyException>(() => p.Composer.Compose(withHost));
        var login = new ResolvedLogin(LoginRole.Default, AuthMode.Sql, "t") { UserName = "u", Password = SecretValue.From("p") };
        var ctx = new ConnectionContext { Definition = Ctx("x").Definition, Login = login };
        Assert.Throws<ConnectionPolicyException>(() => p.Composer.Compose(ctx));
        Assert.Throws<ConnectionPolicyException>(() => p.Composer.ApplyPassThroughCredentials("Data Source=x", "u", SecretValue.From("p")));
    }

    [Fact]
    public void Properties_cannot_set_the_sandbox_keys()
    {
        var def = Ctx("x").Definition;
        def.Properties["access_mode"] = "READ_WRITE";
        Assert.Throws<ConnectionPolicyException>(() => new DuckDbProvider().Composer.Compose(new ConnectionContext { Definition = def }));
    }

    [Theory]
    [InlineData("Permission Error: Cannot access file", "Permission Error")]
    [InlineData("Parser Error: syntax error at or near \"x\"", "Parser Error")]
    [InlineData("Catalog Error: Table with name y does not exist", "Catalog Error")]
    [InlineData("Interrupt Error: Interrupted!", "Interrupt Error")]
    [InlineData("Something odd: x", "other")]
    [InlineData("no colon at all", "other")]
    public void Error_class_is_read_from_a_fixed_vocabulary(string message, string expected) =>
        Assert.Equal(expected, DuckDbFailureMapper.ClassOf(message));

    [Fact]
    public void Real_duckdb_errors_map_without_leaking_the_message()
    {
        using var c = new DuckDBConnection("Data Source=:memory:");
        c.Open();
        var mapper = new DuckDbFailureMapper();
        foreach (var (sql, kind) in new[] { ("SELECT * FROM table_that_is_not_there_xyz", ConnectionFailureKind.Syntax), ("SELEC 1", ConnectionFailureKind.Syntax) })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var ex = Assert.ThrowsAny<DuckDBException>(() => cmd.ExecuteNonQuery());
            var f = mapper.Map(ex, DbDataKit.Dialect.EngineId.DuckDb)!;
            Assert.Equal(kind, f.Kind);
            Assert.DoesNotContain("table_that_is_not_there_xyz", f.PublicMessage, StringComparison.Ordinal);
        }
    }

    // ---- in-process: the sandbox really blocks things --------------------------------------------

    [Fact]
    public async Task Read_only_sandbox_reads_data_and_blocks_writes()
    {
        using var dir = new TempDir();
        string db = MakeDatabase(dir);
        await using var c = await new DuckDbProvider().OpenAsync(Ctx(db));

        Assert.Null(await TryRun(c, "SELECT sum(x) FROM t"));
        Assert.NotNull(await TryRun(c, "INSERT INTO t VALUES (4)"));
        Assert.NotNull(await TryRun(c, "CREATE TABLE u(y INT)"));
        Assert.NotNull(await TryRun(c, "DROP TABLE t"));
    }

    [Fact]
    public async Task File_reads_are_blocked_even_for_an_existing_file()
    {
        using var dir = new TempDir();
        string db = MakeDatabase(dir);
        string secret = dir.File("secret.csv");
        await File.WriteAllTextAsync(secret, "a,b\n1,2\n");
        string forward = secret.Replace('\\', '/');
        await using var c = await new DuckDbProvider().OpenAsync(Ctx(db));

        foreach (var sql in new[]
        {
            $"SELECT * FROM read_csv('{forward}')",
            $"SELECT * FROM read_csv_auto('{forward}')",
            $"SELECT * FROM read_text('{forward}')",
            $"SELECT * FROM read_blob('{forward}')",
            $"SELECT * FROM '{forward}'",
            $"SELECT * FROM glob('{forward}')",
            $"COPY t TO '{dir.File("out.csv").Replace('\\', '/')}'",
            $"ATTACH '{dir.File("other.duckdb").Replace('\\', '/')}' AS o",
        })
        {
            Assert.True(await TryRun(c, sql) is not null, "should be blocked: " + sql);
        }
        Assert.False(File.Exists(dir.File("out.csv")));
        Assert.False(File.Exists(dir.File("other.duckdb")));
    }

    [Fact]
    public async Task Extensions_cannot_be_installed_or_loaded()
    {
        using var dir = new TempDir();
        await using var c = await new DuckDbProvider().OpenAsync(Ctx(MakeDatabase(dir)));
        Assert.NotNull(await TryRun(c, "INSTALL httpfs"));
        Assert.NotNull(await TryRun(c, "LOAD httpfs"));
        Assert.NotNull(await TryRun(c, "INSTALL 'C:/nope/evil.duckdb_extension'"));
    }

    [Fact]
    public async Task The_configuration_is_locked_so_the_sandbox_cannot_be_undone()
    {
        using var dir = new TempDir();
        string db = MakeDatabase(dir);
        string secret = dir.File("secret.csv");
        await File.WriteAllTextAsync(secret, "a\n1\n");
        await using var c = await new DuckDbProvider().OpenAsync(Ctx(db));

        Assert.NotNull(await TryRun(c, "SET enable_external_access = true"));
        Assert.NotNull(await TryRun(c, "SET lock_configuration = false"));
        Assert.NotNull(await TryRun(c, "SET autoload_known_extensions = true"));
        Assert.NotNull(await TryRun(c, "SET access_mode = 'read_write'"));
        Assert.NotNull(await TryRun(c, $"SELECT * FROM read_csv('{secret.Replace('\\', '/')}')"));
    }

    [Fact]
    public async Task Control_an_unsandboxed_connection_can_read_the_same_file()
    {
        using var dir = new TempDir();
        string db = MakeDatabase(dir);
        string secret = dir.File("secret.csv");
        await File.WriteAllTextAsync(secret, "a,b\n1,2\n");
        await using var c = await new DuckDbProvider().OpenAsync(Ctx(db, AccessMode.Readwrite));
        Assert.Null(await TryRun(c, $"SELECT * FROM read_csv('{secret.Replace('\\', '/')}')"));
    }

    [Fact]
    public async Task A_read_write_connection_is_not_sandboxed()
    {
        using var dir = new TempDir();
        string db = MakeDatabase(dir);
        await using var c = await new DuckDbProvider().OpenAsync(Ctx(db, AccessMode.Readwrite));
        Assert.Null(await TryRun(c, "INSERT INTO t VALUES (9)"));
    }

    [Fact]
    public async Task Open_of_a_missing_read_only_file_is_a_classified_failure()
    {
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(async () => await new DuckDbProvider().OpenAsync(Ctx(dir.File("missing.duckdb"))));
        Assert.DoesNotContain(dir.Path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_and_setting_changes_in_the_sandbox_are_permission_failures()
    {
        using var dir = new TempDir();
        await using var c = await new DuckDbProvider().OpenAsync(Ctx(MakeDatabase(dir)));
        var mapper = new DuckDbFailureMapper();
        foreach (var sql in new[] { "INSERT INTO t VALUES (4)", "SET enable_external_access = true" })
        {
            var ex = Assert.IsAssignableFrom<Exception>(await TryRun(c, sql));
            Assert.Equal(ConnectionFailureKind.Permission, mapper.Map(ex, DbDataKit.Dialect.EngineId.DuckDb)!.Kind);
        }
    }

    [Fact]
    public async Task Connections_to_the_same_file_can_be_open_together_and_all_stay_sandboxed()
    {
        // DuckDB.NET shares one database instance per file, and the lock is a setting of that instance: a second query while the first is still streaming
        // used to fail on its first SET.
        using var dir = new TempDir();
        string db = MakeDatabase(dir);
        string secret = dir.File("secret.csv");
        await File.WriteAllTextAsync(secret, "a\n1\n");
        var provider = new DuckDbProvider();

        await using var first = await provider.OpenAsync(Ctx(db));
        await using var second = await provider.OpenAsync(Ctx(db));
        await using var third = await provider.OpenAsync(Ctx(db));
        foreach (var c in new[] { first, second, third })
        {
            Assert.Null(await TryRun(c, "SELECT sum(x) FROM t"));
            Assert.NotNull(await TryRun(c, $"SELECT * FROM read_csv('{secret.Replace('\\', '/')}')"));
            Assert.NotNull(await TryRun(c, "INSERT INTO t VALUES (4)"));
            Assert.NotNull(await TryRun(c, "SET enable_external_access = true"));
        }
    }

    [Fact]
    public async Task A_second_connection_with_other_allowed_directories_fails_closed_instead_of_inheriting_the_first()
    {
        using var dir = new TempDir();
        string db = MakeDatabase(dir);
        string a = System.IO.Path.Combine(dir.Path, "a");
        string b = System.IO.Path.Combine(dir.Path, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        await using var first = await new DuckDbProvider(new DuckDbSandboxOptions { AllowedDirectories = [a] }).OpenAsync(Ctx(db));
        await using var same = await new DuckDbProvider(new DuckDbSandboxOptions { AllowedDirectories = [a] }).OpenAsync(Ctx(db));
        var other = await Assert.ThrowsAsync<ConnectionPolicyException>(async () => await new DuckDbProvider(new DuckDbSandboxOptions { AllowedDirectories = [b] }).OpenAsync(Ctx(db)));
        Assert.Contains("different sandbox", other.Message);
        await Assert.ThrowsAsync<ConnectionPolicyException>(async () => await new DuckDbProvider().OpenAsync(Ctx(db)));
    }

    [Fact]
    public async Task Raw_failure_text_is_kept_only_when_asked_for_and_never_in_the_public_message()
    {
        using var dir = new TempDir();
        var missing = Ctx(dir.File("missing.duckdb"));
        var plain = await Assert.ThrowsAsync<ConnectionFailureException>(async () => await new DuckDbProvider().OpenAsync(missing));
        Assert.Null(plain.Failure.Raw);

        var redactor = new DbDataKit.Secrets.Redactor();
        redactor.Register("missing.duckdb");
        var raw = await Assert.ThrowsAsync<ConnectionFailureException>(async () => await new DuckDbProvider(redactor: redactor, includeRawFailures: true).OpenAsync(missing));
        Assert.NotNull(raw.Failure.Raw);
        Assert.DoesNotContain("missing.duckdb", raw.Failure.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain(dir.Path, raw.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_connection_works()
    {
        using var dir = new TempDir();
        var r = await new DuckDbProvider().TestAsync(Ctx(MakeDatabase(dir)));
        Assert.True(r.Success, r.Failure?.ToString());
    }

    // ---- AllowedDirectories: a deliberate exception, not a blanket off -------------------------------

    [Fact]
    public async Task Allowed_directories_permit_reads_below_them_and_nothing_else()
    {
        using var dir = new TempDir();
        string db = MakeDatabase(dir);
        string data = System.IO.Path.Combine(dir.Path, "lake");
        string elsewhere = System.IO.Path.Combine(dir.Path, "private");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(elsewhere);
        await File.WriteAllTextAsync(System.IO.Path.Combine(data, "ok.csv"), "a,b\n1,2\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(elsewhere, "no.csv"), "a,b\n1,2\n");
        string F(string p) => p.Replace('\\', '/');

        var provider = new DuckDbProvider(new DuckDbSandboxOptions { AllowedDirectories = [data] });
        await using var c = await provider.OpenAsync(Ctx(db));

        Assert.Null(await TryRun(c, $"SELECT * FROM read_csv('{F(System.IO.Path.Combine(data, "ok.csv"))}')"));
        Assert.NotNull(await TryRun(c, $"SELECT * FROM read_csv('{F(System.IO.Path.Combine(elsewhere, "no.csv"))}')"));
        Assert.NotNull(await TryRun(c, $"ATTACH '{F(System.IO.Path.Combine(elsewhere, "x.duckdb"))}' AS o"));
        Assert.NotNull(await TryRun(c, "INSTALL httpfs"));
        Assert.NotNull(await TryRun(c, "SET allowed_directories = []"));
        Assert.NotNull(await TryRun(c, "SET enable_external_access = true"));
        Assert.NotNull(await TryRun(c, "INSERT INTO t VALUES (4)"));
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("")]
    public void Allowed_directories_must_be_absolute_and_clean(string directory) =>
        Assert.Throws<ConnectionPolicyException>(() => DuckDbProvider.AllowedDirectoriesStatement([directory]));

    [Fact]
    public void Allowed_directory_quotes_are_doubled()
    {
        string root = OperatingSystem.IsWindows() ? "C:/" : "/";
        Assert.Equal($"SET allowed_directories = ['{root}it''s']", DuckDbProvider.AllowedDirectoriesStatement([root + "it's"]));
    }

    [Fact]
    public async Task An_in_memory_database_works_read_only_with_the_rest_of_the_recipe()
    {
        var p = new DuckDbProvider();
        var ctx = Ctx(":memory:");
        Assert.DoesNotContain("access_mode", p.Composer.Compose(ctx), StringComparison.OrdinalIgnoreCase);
        await using var c = await p.OpenAsync(ctx);
        Assert.Null(await TryRun(c, "SELECT 1"));
        Assert.NotNull(await TryRun(c, "INSTALL httpfs"));
        Assert.NotNull(await TryRun(c, "SET enable_external_access = true"));
    }
}
