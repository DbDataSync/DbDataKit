using System.Data.Common;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 29b U01: a module declares the engine features it needs, and the migrator refuses an engine without them, naming module and feature, before any DDL of the run. The refusal is shown with a fake probe on
/// every engine (so it does not depend on which server is up) and with the real probe on the engines that are.
/// </summary>
public sealed class EngineFeatureTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    /// <summary>A probe that answers as told, and counts how often it was asked.</summary>
    private sealed class FakeProbe(FeatureStatus vector, string? version = "16.0.4135.4") : IEngineFeatureProbe
    {
        public int Asked { get; private set; }

        public EngineFeatureReport Detect(DbConnection connection, string engine)
        {
            Asked++;
            return new EngineFeatureReport(engine, version, new Dictionary<EngineFeature, FeatureStatus> { [EngineFeature.Vector] = vector });
        }
    }

    /// <summary>A module that needs vectors, with the column each engine calls it (SQLite stores bytes; it has no feature, so only a fake probe lets it run).</summary>
    private static StateModule Knowledge(string engine) => new(
        "knowledge", StateScopeKind.Install,
        [engine switch
        {
            StateEngineIds.MsSql => "CREATE TABLE {table:chunk} (id {{key}} PRIMARY KEY, embedding VECTOR(3) NULL);",
            StateEngineIds.Postgres => "CREATE EXTENSION IF NOT EXISTS vector; CREATE TABLE {table:chunk} (id {{key}} PRIMARY KEY, embedding vector(3) NULL);",
            _ => "CREATE TABLE {table:chunk} (id {{key}} PRIMARY KEY, embedding {{blob}} NULL);",
        }],
        requiresFeatures: [EngineFeature.Vector]);

    private static StateModule Plain() => new("things", StateScopeKind.Install, ["CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY);"]);

    /// <summary>How many tables, schemas and registry rows the suite's state made: zero when nothing of a refused run exists.</summary>
    private static long Created(StateEngines.Location location) => Convert.ToInt64(location.Engine switch
    {
        StateEngineIds.Sqlite => location.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'dbdatakit%'"),
        StateEngineIds.MsSql => location.Scalar("SELECT COUNT(*) FROM sys.schemas WHERE name LIKE 'dbdatakit%'"),
        _ => location.Scalar("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name LIKE 'dbdatakit%'"),
    });

    [Fact]
    public void A_module_lists_the_features_it_needs_and_none_does_by_default()
    {
        Assert.Empty(Plain().RequiresFeatures);
        Assert.Equal([EngineFeature.Vector], Knowledge(StateEngineIds.Sqlite).RequiresFeatures);
        var twice = new StateModule("x", StateScopeKind.Install, [], requiresFeatures: [EngineFeature.Vector, EngineFeature.Vector]);
        Assert.Single(twice.RequiresFeatures);
    }

    [Theory]
    [InlineData(StateEngineIds.MsSql, "15.0.2000.5", FeatureStatus.Missing, FeatureStatus.Missing)]
    [InlineData(StateEngineIds.MsSql, "16.0.4135.4", FeatureStatus.Missing, FeatureStatus.Missing)]
    [InlineData(StateEngineIds.MsSql, "17.0.1000.7", FeatureStatus.Missing, FeatureStatus.Enabled)]
    [InlineData(StateEngineIds.MsSql, "18.0.1", FeatureStatus.Missing, FeatureStatus.Enabled)]
    [InlineData(StateEngineIds.Postgres, "16.4", FeatureStatus.Missing, FeatureStatus.Missing)]
    [InlineData(StateEngineIds.Postgres, "17.2", FeatureStatus.Creatable, FeatureStatus.Creatable)]
    [InlineData(StateEngineIds.Postgres, "17.2", FeatureStatus.Enabled, FeatureStatus.Enabled)]
    [InlineData(StateEngineIds.Sqlite, "3.45.1", FeatureStatus.Enabled, FeatureStatus.Missing)]
    public void The_rules_of_each_engine(string engine, string version, FeatureStatus extension, FeatureStatus expected)
    {
        var report = EngineSupport.Features(engine, version, extension);

        Assert.Equal(expected, report.Status(EngineFeature.Vector));
        Assert.Equal(expected != FeatureStatus.Missing, report.Has(EngineFeature.Vector));
        Assert.Equal(expected == FeatureStatus.Missing ? "none" : expected == FeatureStatus.Enabled ? "vector (ready)" : "vector (extension installed, not created yet)", report.Describe());
    }

    [Theory]
    [InlineData(StateEngineIds.MsSql, "16.0.4135.4", "SQL Server 2022 (16.0.4135.4)", "SQL Server 2025 or later")]
    [InlineData(StateEngineIds.Postgres, "16.4", "PostgreSQL 16 (16.4)", "install the pgvector extension")]
    [InlineData(StateEngineIds.Sqlite, "3.45.1", "SQLite (3.45.1)", "pick SQL Server 2025 or later")]
    public void The_refusal_names_the_module_the_feature_the_engine_and_the_fix(string engine, string version, string engineName, string fix)
    {
        var message = EngineFeatures.Refusal(Knowledge(engine), EngineFeature.Vector, engine, EngineFeatures.EngineName(engine, version));

        Assert.Contains("'knowledge'", message);
        Assert.Contains("'vector'", message);
        Assert.Contains(engineName, message);
        Assert.Contains(fix, message);
        Assert.Contains("nothing of the module was created", message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_engine_without_the_feature_refuses_the_module_before_any_DDL_ran(string engine)
    {
        var location = _engines.Provision(engine);
        var probe = new FakeProbe(FeatureStatus.Missing);

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([Plain(), Knowledge(engine)], featureProbe: probe));

        Assert.Contains("'knowledge'", ex.Message);
        Assert.Contains("'vector'", ex.Message);
        Assert.Contains("To fix it:", ex.Message);
        Assert.Equal(1, probe.Asked);
        Assert.Equal(0, Created(location));   // not even the registry or the schema of the module that needs nothing
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_engine_with_the_feature_applies_the_module(string engine)
    {
        var location = _engines.Provision(engine);
        if (engine != StateEngineIds.Sqlite && !EngineSupport.Features(OpenProbeConnection(location), engine).Has(EngineFeature.Vector))
            Assert.Skip($"this {engine} server has no vector support; the refusal is covered by the other tests");

        // the fake says yes on SQLite (which has no feature: its test module stores bytes); on a server the real engine must agree
        var database = location.Open([Knowledge(engine)], featureProbe: engine == StateEngineIds.Sqlite ? new FakeProbe(FeatureStatus.Enabled) : null);

        using var connection = database.OpenConnection();
        using var command = database.Command(connection, "SELECT COUNT(*) FROM {table:knowledge.chunk}");
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_real_probe_decides_alike_for_the_open_and_the_report(string engine)
    {
        var location = _engines.Provision(engine);
        var report = EngineSupport.Features(OpenProbeConnection(location), engine);

        if (report.Has(EngineFeature.Vector))
        {
            location.Open([Knowledge(engine)]);
            Assert.True(Created(location) > 0);
        }
        else
        {
            var ex = Assert.Throws<InvalidOperationException>(() => location.Open([Knowledge(engine)]));
            Assert.Contains("'knowledge'", ex.Message);
            Assert.Equal(0, Created(location));
        }
    }

    [Fact]
    public void A_module_already_at_its_version_is_not_asked_again_and_a_module_without_a_need_never_asks()
    {
        var location = _engines.Provision(StateEngineIds.Sqlite);
        var asked = new FakeProbe(FeatureStatus.Enabled);
        location.Open([Knowledge(StateEngineIds.Sqlite)], featureProbe: asked);
        Assert.Equal(1, asked.Asked);

        // the engine "loses" the feature: what is already applied keeps working, so a start does not fail for it
        var gone = new FakeProbe(FeatureStatus.Missing);
        location.Open([Knowledge(StateEngineIds.Sqlite)], featureProbe: gone);
        Assert.Equal(0, gone.Asked);

        var other = _engines.Provision(StateEngineIds.Sqlite);
        var never = new FakeProbe(FeatureStatus.Missing);
        other.Open([Plain()], featureProbe: never);
        Assert.Equal(0, never.Asked);
    }

    [Fact]
    public void A_later_migration_of_a_module_that_needs_a_feature_is_checked_again()
    {
        var location = _engines.Provision(StateEngineIds.Sqlite);
        StateModule Version(int migrations) => new("knowledge", StateScopeKind.Install,
            [.. new[] { "CREATE TABLE {table:chunk} (id {{key}} PRIMARY KEY);", "CREATE TABLE {table:more} (id {{key}} PRIMARY KEY);" }.Take(migrations)], requiresFeatures: [EngineFeature.Vector]);
        location.Open([Version(1)], featureProbe: new FakeProbe(FeatureStatus.Enabled));

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([Version(2)], featureProbe: new FakeProbe(FeatureStatus.Missing)));

        Assert.Contains("'knowledge'", ex.Message);
        Assert.Equal(0L, Convert.ToInt64(location.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%more'")));
    }

    [Fact]
    public async Task A_snapshot_import_into_an_engine_without_the_feature_is_refused_before_any_DDL()
    {
        var module = Knowledge(StateEngineIds.Sqlite);
        var source = _engines.Provision(StateEngineIds.Sqlite).Open([module], StateScope.Standalone, featureProbe: new FakeProbe(FeatureStatus.Enabled));
        var directory = Path.Combine(Path.GetTempPath(), "dbdatakit-feature-" + Guid.NewGuid().ToString("N"));
        try
        {
            await DbDataKit.State.Snapshots.StateSnapshotWriter.WriteAsync(source, [module], StateScope.Standalone, directory, DbDataKit.State.Snapshots.SnapshotOptions.Default, null, CancellationToken.None);

            var location = _engines.Provision(StateEngineIds.Sqlite);
            var probe = new FakeProbe(FeatureStatus.Missing);
            var layout = StateLayout.Create(StateEngineIds.Sqlite, [module], StateScope.Standalone, null, probe);
            var target = StateDatabase.OpenSqliteForImport(location.Target, layout);

            var refusal = await Assert.ThrowsAsync<DbDataKit.State.Snapshots.SnapshotImportException>(() =>
                DbDataKit.State.Snapshots.StateSnapshotImporter.ImportAsync(directory, target, [module], StateScope.Standalone, DbDataKit.State.Snapshots.ImportOptions.Default, null, CancellationToken.None));

            Assert.Equal(DbDataKit.State.Snapshots.SnapshotImportRefusal.FeatureMissing, refusal.Reason);
            Assert.Contains("'knowledge'", refusal.Message);
            Assert.Contains("'vector'", refusal.Message);
            Assert.Equal(1, probe.Asked);
            Assert.Equal(0L, Convert.ToInt64(location.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'dbdatakit%'")));   // not even the registry
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_extension_the_login_cannot_create_is_reported_by_state_test_and_refused_before_any_DDL()
    {
        var report = EngineSupport.Features(StateEngineIds.Postgres, "17.2", FeatureStatus.Creatable, "CREATE EXTENSION vector needs a superuser and 'app' is not one");
        Assert.True(report.Has(EngineFeature.Vector));   // the server has it
        var unmet = EngineSupport.Unmet([Knowledge(StateEngineIds.Postgres)], report);
        Assert.Single(unmet);
        Assert.Contains("cannot turn it on", unmet[0].Message);
        Assert.Contains("superuser", unmet[0].Message);

        Assert.Empty(EngineSupport.Unmet([Knowledge(StateEngineIds.Postgres)], EngineSupport.Features(StateEngineIds.Postgres, "17.2", FeatureStatus.Creatable)));
    }

    /// <summary>A connection to the scratch database for the real probe; the caller's scratch database is dropped with the harness, so the connection is left to the pool.</summary>
    private static DbConnection OpenProbeConnection(StateEngines.Location location)
    {
        var connection = StateDialect.For(location.Engine).CreateConnection(location.Engine == StateEngineIds.Sqlite ? $"Data Source={location.Target};Pooling=False" : location.Target);
        connection.Open();
        return connection;
    }
}
