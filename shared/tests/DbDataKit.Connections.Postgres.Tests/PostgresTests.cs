using DbDataKit.Secrets;
using Npgsql;

namespace DbDataKit.Connections.Postgres.Tests;

public class PostgresTests
{
    private const string Hostile = "x};Driver={evil};UID={";

    private static ConnectionContext Ctx(Action<ConnectionDefinition>? edit = null, ResolvedLogin? login = null, LoginRole role = LoginRole.Default)
    {
        var d = new ConnectionDefinition { Name = "pg", Engine = "postgres", Address = new AddressSpec { Host = "db1", Port = 5432, Database = "dw" } };
        edit?.Invoke(d);
        return new ConnectionContext { Definition = d, Login = login, Role = role };
    }

    private static ResolvedLogin Sql(string user, string pw) => new(LoginRole.Default, AuthMode.Sql, "t") { UserName = user, Password = SecretValue.From(pw) };

    [Fact]
    public void Defaults_verify_full_with_timeouts()
    {
        var b = new NpgsqlConnectionStringBuilder(new PostgresComposer().Compose(Ctx(login: Sql("svc", "pw"))));
        Assert.Equal("db1", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("dw", b.Database);
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
        Assert.Equal(15, b.Timeout);
        Assert.Equal(300, b.CommandTimeout);
        Assert.Equal("svc", b.Username);
        Assert.Equal("pw", b.Password);
    }

    [Fact]
    public void Tls_opt_outs_are_explicit()
    {
        Assert.Equal(SslMode.Require, new NpgsqlConnectionStringBuilder(new PostgresComposer().Compose(Ctx(d => d.Tls.TrustServerCertificate = true))).SslMode);
        Assert.Equal(SslMode.Require, new NpgsqlConnectionStringBuilder(new PostgresComposer().Compose(Ctx(d => d.Tls.Verify = false))).SslMode);
        Assert.Equal(SslMode.Disable, new NpgsqlConnectionStringBuilder(new PostgresComposer().Compose(Ctx(d => d.Tls.Encrypt = false))).SslMode);
        var withCa = new NpgsqlConnectionStringBuilder(new PostgresComposer().Compose(Ctx(d => d.Tls.CaFile = "/etc/ca.pem")));
        Assert.Equal("/etc/ca.pem", withCa.RootCertificate);
        Assert.Equal(SslMode.VerifyFull, withCa.SslMode);
    }

    [Fact]
    public void Hostile_credentials_do_not_escape_their_value()
    {
        var text = new PostgresComposer().Compose(Ctx(login: Sql(Hostile, Hostile)));
        var b = new NpgsqlConnectionStringBuilder(text);
        Assert.Equal(Hostile, b.Username);
        Assert.Equal(Hostile, b.Password);
        Assert.Equal("db1", b.Host);
        var plain = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        Assert.False(plain.ContainsKey("Driver"));
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("Username")]
    [InlineData("Host")]
    [InlineData("SSL Mode")]
    [InlineData("Trust Server Certificate")]
    [InlineData("Integrated Security")]
    [InlineData("Krbsrvname")]
    [InlineData("Options")]
    [InlineData("Search Path")]
    [InlineData("Passfile")]
    public void Properties_cannot_reach_credential_host_tls_or_session_keys(string key)
    {
        Assert.Throws<ConnectionPolicyException>(() => new PostgresComposer().Compose(Ctx(d => d.Properties[key] = "x", Sql("svc", "pw"))));
    }

    [Fact]
    public void Pass_through_strips_integrated_keys()
    {
        const string baseText = "Host=h;Krbsrvname=postgres;Passfile=/home/x/.pgpass;Username=admin;Password=adminpw;Application Name=proxy";
        var text = new PostgresComposer().ApplyPassThroughCredentials(baseText, "alice", SecretValue.From(Hostile));
        var b = new NpgsqlConnectionStringBuilder(text);
        Assert.Equal("alice", b.Username);
        Assert.Equal(Hostile, b.Password);
        Assert.Equal("proxy", b.ApplicationName);
        var plain = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        foreach (var key in plain.Keys.Cast<string>()) Assert.False(CredentialPolicy.IsIntegratedKey(key), key);
        Assert.DoesNotContain("adminpw", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_only_recipe_is_a_startup_option_and_a_session_statement()
    {
        var p = new PostgresProvider();
        Assert.Equal(["SET default_transaction_read_only = on"], p.ReadOnly.Statements);
        Assert.Equal(ReadOnlyEnforcement.SessionDefault, p.ReadOnly.Enforcement);
        var b = new NpgsqlConnectionStringBuilder(p.Composer.Compose(Ctx(d => d.Access = AccessMode.Readonly)));
        Assert.Equal("-c default_transaction_read_only=on", b.Options);
        Assert.Null(new NpgsqlConnectionStringBuilder(p.Composer.Compose(Ctx())).Options);
        Assert.Equal("-c default_transaction_read_only=on", new NpgsqlConnectionStringBuilder(p.Composer.Compose(Ctx(role: LoginRole.Read))).Options);
    }

    [Fact]
    public void Create_returns_an_unopened_npgsql_connection()
    {
        var p = new PostgresProvider();
        using var c = p.Create(Ctx(login: Sql("svc", "pw")));
        Assert.IsType<NpgsqlConnection>(c);
        Assert.Equal(System.Data.ConnectionState.Closed, c.State);
        Assert.Equal(DbDataKit.Dialect.EngineId.Postgres, p.Dialect(new ConnectionDefinition { Engine = "postgres" }).Id);
        Assert.True(p.Caps.HasFlag(DbDataKit.Dialect.EngineCaps.UpsertOnConflict));
    }

    [Fact]
    public async Task Unreachable_server_is_a_classified_failure_without_host_text()
    {
        var p = new PostgresProvider();
        var ctx = Ctx(d => { d.Address = new AddressSpec { Host = "127.0.0.1", Port = 1, Database = "secret-db-name" }; d.Timeouts.ConnectS = 2; d.Tls.Encrypt = false; }, Sql("svc", "pw"));
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(async () => await p.OpenAsync(ctx));
        Assert.Contains(ex.Failure.Kind, new[] { ConnectionFailureKind.Network, ConnectionFailureKind.Timeout });
        Assert.DoesNotContain("127.0.0.1", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-db-name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_errors_map_by_sqlstate()
    {
        var m = new PostgresFailureMapper();
        PostgresException Make(string state) => new("m", "ERROR", "ERROR", state);
        Assert.Equal(ConnectionFailureKind.Login, m.Map(Make("28P01"), DbDataKit.Dialect.EngineId.Postgres)!.Kind);
        Assert.Equal(ConnectionFailureKind.Permission, m.Map(Make("25006"), DbDataKit.Dialect.EngineId.Postgres)!.Kind);
        Assert.Equal(ConnectionFailureKind.Syntax, m.Map(Make("42601"), DbDataKit.Dialect.EngineId.Postgres)!.Kind);
        Assert.Equal(ConnectionFailureKind.Timeout, m.Map(Make("57014"), DbDataKit.Dialect.EngineId.Postgres)!.Kind);
        Assert.Equal(ConnectionFailureKind.Other, m.Map(Make("23505"), DbDataKit.Dialect.EngineId.Postgres)!.Kind);
        Assert.Null(m.Map(new InvalidOperationException(), DbDataKit.Dialect.EngineId.Postgres));
    }

    [Fact]
    public async Task Live_read_only_session_blocks_writes_when_a_server_is_configured()
    {
        var cs = Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DBDATA_TEST_POSTGRES to a connection string (credentials included) to run this.");
        var login = new ResolvedLogin(LoginRole.Read, AuthMode.Sql, "env") { ConnectionString = SecretValue.From(cs!) };
        var p = new PostgresProvider();
        var ctx = Ctx(d => { d.Address = null; d.Access = AccessMode.Readonly; d.Tls.Verify = false; }, login, LoginRole.Read);
        await using var c = await p.OpenAsync(ctx);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TEMP TABLE dbdata_ro_probe(x int)";
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => await cmd.ExecuteNonQueryAsync());
        Assert.Equal("25006", ex.SqlState);
    }
}
