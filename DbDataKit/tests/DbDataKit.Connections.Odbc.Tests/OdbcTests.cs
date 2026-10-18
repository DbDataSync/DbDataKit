using System.Data.Odbc;
using DbDataKit.Dialect;
using DbDataKit.Odbc.Metadata;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Odbc.Tests;

public class OdbcTests
{
    private const string Hostile = "x};Driver={evil};UID={";

    private static ConnectionContext Ctx(string cs, ResolvedLogin? login = null, AccessMode access = AccessMode.Readwrite, ConnectionPolicy? policy = null) => new()
    {
        Definition = new ConnectionDefinition { Name = "o", Engine = "odbc", Access = access, ConnectionString = cs },
        Login = login,
        Policy = policy ?? ConnectionPolicy.Default,
    };

    private static ResolvedLogin Sql(string u, string p) => new(LoginRole.Default, AuthMode.Sql, "t") { UserName = u, Password = SecretValue.From(p) };

    [Fact]
    public void Hostile_credentials_cannot_add_a_driver_or_uid()
    {
        var p = new OdbcProvider();
        var text = p.Composer.Compose(Ctx("Driver={ODBC Driver 18 for SQL Server};Server=h", Sql(Hostile, Hostile)));
        Assert.True(OdbcConnectionString.TryParseStrict(text, out var pairs, out var err), err);
        Assert.Single(pairs, x => x.Key == "driver");
        Assert.Single(pairs, x => x.Key == "uid");
        Assert.Equal(Hostile, pairs.Single(x => x.Key == "uid").Value);
        Assert.Equal(Hostile, pairs.Single(x => x.Key == "pwd").Value);
        // and the system's own parser sees one connection string
        using var conn = p.Create(Ctx("Driver={ODBC Driver 18 for SQL Server};Server=h", Sql(Hostile, Hostile)));
        Assert.IsType<OdbcConnection>(conn);
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
    }

    [Fact]
    public void Allow_list_and_file_dsn_policy_apply_through_the_provider()
    {
        var p = new OdbcProvider();
        var policy = ConnectionPolicy.Default with { AllowedOdbcDrivers = new HashSet<string> { "ODBC Driver 18 for SQL Server" } };
        p.Create(Ctx("Driver={ODBC Driver 18 for SQL Server};Server=h", policy: policy)).Dispose();
        Assert.Throws<ConnectionPolicyException>(() => p.Create(Ctx("Driver={Some Other};Server=h", policy: policy)));
        Assert.Throws<ConnectionPolicyException>(() => p.Create(Ctx("FILEDSN=c:\\x.dsn")));
        Assert.Throws<ConnectionPolicyException>(() => p.Create(Ctx("DSN=x;SAVEFILE=c:\\y.dsn")));
    }

    [Fact]
    public void Read_only_context_adds_the_driver_specific_keyword()
    {
        var p = new OdbcProvider();
        Assert.Equal("ReadOnly", OdbcConnectionString.Get(p.Composer.Compose(Ctx("Driver={ODBC Driver 17 for SQL Server};Server=h", access: AccessMode.Readonly)), "ApplicationIntent"));
        Assert.Equal("1", OdbcConnectionString.Get(p.Composer.Compose(Ctx("Driver={PostgreSQL ANSI};Server=h", access: AccessMode.Readonly)), "ReadOnly"));
        Assert.Equal("READ_ONLY", OdbcConnectionString.Get(p.Composer.Compose(Ctx("Driver={DuckDB};Database=x", access: AccessMode.Readonly)), "access_mode"));
        Assert.Null(OdbcConnectionString.Get(p.Composer.Compose(Ctx("DSN=mystery", access: AccessMode.Readonly)), "ReadOnly"));
    }

    [Fact]
    public void Recipe_is_a_hint_and_says_so()
    {
        var p = new OdbcProvider();
        Assert.Equal(ReadOnlyEnforcement.SessionDefault, p.ReadOnly.Enforcement);
        Assert.Contains("hint", p.ReadOnly.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Access_mode_on_a_closed_or_foreign_connection_reports_not_applied()
    {
        using var closed = new OdbcConnection("DSN=nothing");
        var r = OdbcAccessMode.TryApplyReadOnly(closed);
        Assert.False(r.Applied);
        Assert.Contains("not open", r.Reason, StringComparison.Ordinal);
        Assert.False(OdbcAccessMode.TryApplyReadOnly(new DbDataKit.Connections.Odbc.Tests.NotOdbc()).Applied);
    }

    [Fact]
    public void Named_parameters_become_positional_markers_bound_in_order()
    {
        using var conn = new OdbcConnection("DSN=nothing");
        var sqlServer = DialectCatalog.Builtin.Get(EngineId.SqlServer);
        using var cmd = OdbcProvider.CreateCommand(conn, "SELECT * FROM t WHERE a = @a AND b = @b AND c = @a", sqlServer,
            new Dictionary<string, object?> { ["a"] = 1, ["b"] = "x" }, 42);
        Assert.Equal("SELECT * FROM t WHERE a = ? AND b = ? AND c = ?", cmd.CommandText);
        Assert.Equal(3, cmd.Parameters.Count);
        Assert.Equal(new object[] { 1, "x", 1 }, cmd.Parameters.Cast<OdbcParameter>().Select(p => p.Value).ToArray());
        Assert.Equal(42, cmd.CommandTimeout);
    }

    [Fact]
    public async Task Missing_driver_is_a_classified_unsupported_failure_without_the_driver_text()
    {
        var p = new OdbcProvider();
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(async () => await p.OpenAsync(Ctx("Driver={No Such Driver Anywhere 99};Server=secret-host")));
        Assert.DoesNotContain("secret-host", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("No Such Driver", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ex.Failure.Kind, new[] { ConnectionFailureKind.Unsupported, ConnectionFailureKind.Other });
    }

    [Fact]
    public async Task Live_parameterised_query_with_read_only_access_when_configured()
    {
        var cs = Environment.GetEnvironmentVariable("DBDATA_TEST_ODBC");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DBDATA_TEST_ODBC to a full ODBC connection string (credentials included) to run this.");
        var login = new ResolvedLogin(LoginRole.Read, AuthMode.Sql, "env") { ConnectionString = SecretValue.From(cs!) };
        var ctx = new ConnectionContext
        {
            // The test servers (testenv/) use throwaway self-signed certificates: encrypt, but do not verify.
            Definition = new ConnectionDefinition { Name = "o", Engine = "odbc", Access = AccessMode.Readonly, Tls = { TrustServerCertificate = true } },
            Login = login, Role = LoginRole.Read,
        };
        var provider = new OdbcProvider();
        await using var c = await provider.OpenAsync(ctx);
        using var cmd = OdbcProvider.CreateCommand(c, "SELECT @a", DialectCatalog.Builtin.Get(EngineId.SqlServer), new Dictionary<string, object?> { ["a"] = 7 }, 30);
        Assert.NotNull(await cmd.ExecuteScalarAsync());
    }
}
