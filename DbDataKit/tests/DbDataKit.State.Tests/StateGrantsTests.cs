using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q23: a login granted only its namespaces can open its modules and cannot read another's, on SQL Server and PostgreSQL.</summary>
public sealed class StateGrantsTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static StateModule Module(string name, params string[] extra) => new(name, StateScopeKind.Install, [$"CREATE TABLE {{table:item}} (id {{{{key}}}} PRIMARY KEY, label {{{{text}}}} NULL);", .. extra]);

    public static TheoryData<string> ServerEngines => [StateEngineIds.MsSql, StateEngineIds.Postgres];

    private static string LimitedConnection(string engine, string target, string login, string password) => engine == StateEngineIds.MsSql
        ? new SqlConnectionStringBuilder(target) { UserID = login, Password = password }.ConnectionString
        : new NpgsqlConnectionStringBuilder(target) { Username = login, Password = password }.ConnectionString;

    private static void CreateLogin(StateEngines.Location location, string login, string password)
    {
        if (location.Engine == StateEngineIds.MsSql)
        {
            location.Scalar($"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF;");
            location.Scalar($"CREATE USER [{login}] FOR LOGIN [{login}];");
        }
        else
            location.Scalar($"CREATE ROLE {login} LOGIN PASSWORD '{password}';");
    }

    private static void DropLogin(StateEngines.Location location, string login)
    {
        try
        {
            if (location.Engine == StateEngineIds.MsSql)
            {
                SqlConnection.ClearAllPools();
                location.Scalar($"DROP USER [{login}]; DROP LOGIN [{login}];");
            }
            else
            {
                NpgsqlConnection.ClearAllPools();
                location.Scalar($"DROP OWNED BY {login}; DROP ROLE {login};");
            }
        }
        catch (Exception) { /* a login left behind is untidy; its name carries a GUID */ }
    }

    private static void Run(StateDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void The_statements_name_only_the_namespaces_of_the_layout_and_refuse_a_name_that_is_not_plain()
    {
        var layout = StateLayout.Create(StateEngineIds.Postgres, [Module("alpha")]);

        var runtime = StateGrants.Statements(layout, "svc", GrantLevel.Runtime);
        var migrate = StateGrants.Statements(layout, "svc", GrantLevel.Migrate);

        Assert.Contains("GRANT USAGE ON SCHEMA dbdatakit_alpha TO svc;", runtime);
        Assert.Contains("GRANT SELECT ON dbdatakit.modules TO svc;", runtime);
        Assert.DoesNotContain(runtime, s => s.Contains("CREATE"));
        Assert.Contains("GRANT CREATE ON SCHEMA dbdatakit_alpha TO svc;", migrate);
        Assert.DoesNotContain(runtime, s => s.Contains("beta"));
        Assert.Throws<ArgumentException>(() => StateGrants.Statements(layout, "svc; DROP TABLE x", GrantLevel.Runtime));
        Assert.Throws<InvalidOperationException>(() => StateGrants.Statements(StateLayout.Create(StateEngineIds.Sqlite, [Module("alpha")]), "svc"));
    }

    [Theory]
    [MemberData(nameof(ServerEngines))]
    public void A_login_granted_only_its_namespaces_opens_its_modules_and_cannot_read_another(string engine)
    {
        var location = _engines.Provision(engine);
        var alpha = Module("alpha");
        var beta = Module("beta");
        var admin = location.Open([alpha, beta]);
        Run(admin, "INSERT INTO " + admin.Layout!.Table("beta", "item") + " (id, label) VALUES ('b1', 'private to beta');");

        var login = "grant_" + Guid.NewGuid().ToString("N")[..12];
        const string password = "Gr@nt_Test_Pw1";
        CreateLogin(location, login, password);
        try
        {
            foreach (var statement in StateGrants.Statements(StateLayout.Create(engine, [alpha]), login, GrantLevel.Runtime))
                location.Scalar(statement);
            var limited = LimitedConnection(engine, location.Target, login, password);

            // the modules it uses open (nothing to apply, so no DDL) and work
            var own = new StateDatabase(engine, limited, StateLayout.Create(engine, [alpha]));
            Run(own, "INSERT INTO " + own.Layout!.Table("alpha", "item") + " (id, label) VALUES ('a1', 'mine');");
            using (var connection = own.OpenConnection())
            {
                using var read = connection.CreateCommand();
                read.CommandText = "SELECT label FROM " + own.Layout.Table("alpha", "item") + ";";
                Assert.Equal("mine", read.ExecuteScalar());
            }

            // another module's tables are refused to it
            var denied = Assert.ThrowsAny<System.Data.Common.DbException>(() => Run(own, "SELECT label FROM " + admin.Layout.Table("beta", "item") + ";"));
            Assert.Contains("permission", denied.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { DropLogin(location, login); }
    }

    [Theory]
    [MemberData(nameof(ServerEngines))]
    public void Applying_a_migration_needs_the_migrate_level_and_the_runtime_level_cannot(string engine)
    {
        var location = _engines.Provision(engine);
        var v1 = Module("alpha");
        location.Open([v1]);
        var v2 = Module("alpha", "CREATE TABLE {table:extra} (id {{key}} PRIMARY KEY);");

        var runtimeLogin = "rt_" + Guid.NewGuid().ToString("N")[..12];
        var migrateLogin = "mg_" + Guid.NewGuid().ToString("N")[..12];
        const string password = "Gr@nt_Test_Pw1";
        CreateLogin(location, runtimeLogin, password);
        CreateLogin(location, migrateLogin, password);
        try
        {
            foreach (var statement in StateGrants.Statements(StateLayout.Create(engine, [v1]), runtimeLogin, GrantLevel.Runtime)) location.Scalar(statement);
            foreach (var statement in StateGrants.Statements(StateLayout.Create(engine, [v1]), migrateLogin, GrantLevel.Migrate)) location.Scalar(statement);

            // the service that runs cannot upgrade the database under itself
            Assert.ThrowsAny<System.Data.Common.DbException>(() => new StateDatabase(engine, LimitedConnection(engine, location.Target, runtimeLogin, password), StateLayout.Create(engine, [v2])));

            // the login that upgrades can, and the runtime login then opens the new version
            var upgraded = new StateDatabase(engine, LimitedConnection(engine, location.Target, migrateLogin, password), StateLayout.Create(engine, [v2]));
            Run(upgraded, "INSERT INTO " + upgraded.Layout!.Table("alpha", "extra") + " (id) VALUES ('e1');");
            foreach (var statement in StateGrants.Statements(StateLayout.Create(engine, [v2]), runtimeLogin, GrantLevel.Runtime)) location.Scalar(statement);
            var running = new StateDatabase(engine, LimitedConnection(engine, location.Target, runtimeLogin, password), StateLayout.Create(engine, [v2]));
            Run(running, "INSERT INTO " + running.Layout!.Table("alpha", "extra") + " (id) VALUES ('e2');");
        }
        finally
        {
            DropLogin(location, runtimeLogin);
            DropLogin(location, migrateLogin);
        }
    }
}
