using System.Data.Common;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 29b U01, the N-1 half of the support matrix: the modules that need no feature apply and work on SQL Server 2022 and PostgreSQL 16, the vector module is refused on SQL Server 2022 and applied on PostgreSQL 16
/// (the testenv image carries pgvector). Rows skip unless <c>DBDATAKIT_TEST_SQLSERVER2022</c> / <c>DBDATAKIT_TEST_POSTGRES16</c> name a server (<c>testenv.sh up mssql2022 postgres16</c>, then
/// <c>testenv.sh env dbdatakit-older</c>), and fail instead of skipping with <c>DBDATAKIT_TEST_REQUIRE_SQLSERVER2022</c> / <c>DBDATAKIT_TEST_REQUIRE_POSTGRES16</c> set to true. The whole State suite also runs on them (the CI leg
/// points <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> at them).
/// </summary>
public sealed class StateOlderEngineTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static readonly StateModule Things = new("things", StateScopeKind.Install, ["CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY, label {{text}} NULL);"]);

    private static readonly StateModule Parts = new("parts", StateScopeKind.Install,
        ["CREATE TABLE {table:part} (id {{key}} PRIMARY KEY, thing_id {{key}} NOT NULL REFERENCES {table:things.thing}(id));", "CREATE INDEX {index:part_by_thing} ON {table:part}(thing_id);"], dependsOn: [Things]);

    private static StateModule Knowledge(string engine) => new("knowledge", StateScopeKind.Install,
        [engine == StateEngineIds.MsSql
            ? "CREATE TABLE {table:chunk} (id {{key}} PRIMARY KEY, embedding VECTOR(3) NULL);"
            : "CREATE EXTENSION IF NOT EXISTS vector; CREATE TABLE {table:chunk} (id {{key}} PRIMARY KEY, embedding vector(3) NULL);"],
        requiresFeatures: [EngineFeature.Vector]);

    private static DbConnection Connect(StateEngines.Location location)
    {
        var connection = StateDialect.For(location.Engine).CreateConnection(location.Target);
        connection.Open();
        return connection;
    }

    [Theory]
    [MemberData(nameof(StateEngines.OlderEngines), MemberType = typeof(StateEngines))]
    public void The_engine_is_the_older_supported_release(string variant)
    {
        var location = _engines.ProvisionVariant(variant);
        using var connection = Connect(location);
        using var inspector = StateInspector.Open(StateLayout.Create(location.Engine, [Things]), location.Target);

        var check = EngineSupport.Check(location.Engine, inspector.ServerVersion()!);

        Assert.True(check.Supported, check.Message);
        Assert.True(check.Tested);
        Assert.Equal(variant == "mssql2022" ? "SQL Server 2022" : "PostgreSQL 16", check.Name);
    }

    [Theory]
    [MemberData(nameof(StateEngines.OlderEngines), MemberType = typeof(StateEngines))]
    public void Modules_that_need_no_feature_apply_in_dependency_order_with_a_working_foreign_key(string variant)
    {
        var location = _engines.ProvisionVariant(variant);

        var database = location.Open([Parts]);

        using var connection = database.OpenConnection();
        Insert(database, connection, "INSERT INTO {table:things.thing} (id, label) VALUES ($id, 'a');", "t1");
        Insert(database, connection, "INSERT INTO {table:parts.part} (id, thing_id) VALUES ($id, 't1');", "p1");
        using var count = database.Command(connection, "SELECT COUNT(*) FROM {table:parts.part}");
        Assert.Equal(1L, Convert.ToInt64(count.ExecuteScalar()));
    }

    private static void Insert(StateDatabase database, DbConnection connection, string sql, string id)
    {
        using var command = database.Command(connection, sql);
        command.Bind(database, "id", id);
        command.ExecuteNonQuery();
    }

    [Theory]
    [MemberData(nameof(StateEngines.OlderEngines), MemberType = typeof(StateEngines))]
    public void The_vector_module_is_refused_on_SQL_Server_2022_and_applies_on_PostgreSQL_16_with_pgvector(string variant)
    {
        var location = _engines.ProvisionVariant(variant);
        using var connection = Connect(location);
        var report = EngineSupport.Features(connection, location.Engine);

        if (variant == "mssql2022")
        {
            Assert.False(report.Has(EngineFeature.Vector));
            var ex = Assert.Throws<InvalidOperationException>(() => location.Open([Knowledge(location.Engine)]));
            Assert.Contains("'knowledge'", ex.Message);
            Assert.Contains("'vector'", ex.Message);
            Assert.Contains("SQL Server 2022", ex.Message);
            Assert.Contains("SQL Server 2025 or later", ex.Message);
            Assert.Equal(0L, Convert.ToInt64(location.Scalar("SELECT COUNT(*) FROM sys.schemas WHERE name LIKE 'dbdatakit%'")));
        }
        else
        {
            Assert.True(report.Has(EngineFeature.Vector), "the testenv image for PostgreSQL 16 is pgvector/pgvector:pg16");
            location.Open([Knowledge(location.Engine)]);
            Assert.Equal(1L, Convert.ToInt64(location.Scalar("SELECT COUNT(*) FROM pg_extension WHERE extname = 'vector'")));
        }
    }
}
