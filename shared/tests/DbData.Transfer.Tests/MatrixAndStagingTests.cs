using System.Security.Cryptography;
using System.Text;
using DbData.Dialect;
using Xunit;

namespace DbData.Transfer.Tests;

public class MatrixTests
{
    private static readonly StrategyMatrix M = StrategyMatrix.Builtin;

    [Fact]
    public void Every_strategy_has_a_row_for_every_builtin_engine_except_ansi()
    {
        foreach (var s in M.Strategies)
            foreach (var e in new[] { "duckdb", "sqlserver", "postgres", "mysql", "oracle", "fabric" })
                Assert.NotNull(M.Entry(s, new EngineId(e)));
        Assert.Equal(6, StrategyCatalog.Strategies.Count);
        Assert.Equal(M.Strategies.Order(), StrategyCatalog.Strategies.Select(s => s.Name).Order());
    }

    [Fact]
    public void Duckdb_is_the_reference_engine_and_fabric_stays_unverified()
    {
        foreach (var s in M.Strategies)
        {
            Assert.Equal(MatrixStatus.Reference, M.Entry(s, EngineId.DuckDb)!.Status);
            Assert.Equal(MatrixStatus.Unverified, M.Entry(s, EngineId.Fabric)!.Status);
        }
    }

    [Fact]
    public void Unverified_combinations_are_allowed_with_a_warning_or_rejected_when_verification_is_required()
    {
        var d = M.Check("full_replace", EngineId.Fabric);
        Assert.True(d.Allowed);
        Assert.Contains("unverified", d.Reason);
        var strict = M.Check("full_replace", EngineId.Fabric, requireVerified: true);
        Assert.False(strict.Allowed);
        Assert.Throws<StrategyNotSupportedException>(() => M.Require("full_replace", EngineId.Fabric, requireVerified: true));
    }

    [Fact]
    public void Postgres_merge_needs_a_minimum_version()
    {
        Assert.False(M.Check("merge_by_key", EngineId.Postgres, new EngineVersion(9, 4)).Allowed);
        Assert.True(M.Check("merge_by_key", EngineId.Postgres, new EngineVersion(14)).Allowed);
        Assert.True(M.Check("merge_by_key", EngineId.Postgres, new EngineVersion(15)).Allowed);
    }

    [Fact]
    public void Unknown_engines_and_strategies_are_rejected()
    {
        Assert.False(M.Check("full_replace", new EngineId("odbc:acme")).Allowed);
        Assert.False(M.Check("scd2", EngineId.Postgres).Allowed);
        Assert.False(M.Check("Snapshot", EngineId.Postgres).Allowed);       // Sync-only kind
        var ex = Assert.Throws<StrategyNotSupportedException>(() => StrategyCatalog.Build("full_replace", Request(), DialectCatalog.For(EngineId.Ansi)));
        Assert.Equal("ansi", ex.Engine);
    }

    [Fact]
    public void Sync_kind_names_resolve_to_shared_names()
    {
        Assert.Equal("merge_by_key", M.Resolve("MsSqlMerge"));
        Assert.Equal("key_reconcile_delete", M.Resolve("KeyReconcileDelete"));
        Assert.Equal("full_replace", M.Resolve("full_replace"));
        Assert.Null(M.Resolve("Scd2"));
    }

    [Fact]
    public void The_matrix_is_data_and_can_be_replaced()
    {
        var m = StrategyMatrix.Parse("""{"strategies":{"x":{"engines":{"duckdb":{"status":"unsupported","note":"no"}}}}}""");
        Assert.False(m.Check("x", EngineId.DuckDb).Allowed);
        Assert.Throws<FormatException>(() => StrategyMatrix.Parse("""{"strategies":{"x":{"engines":{"duckdb":{"status":"bogus"}}}}}"""));
    }

    private static ApplyRequest Request() => new(new TableName(null, "t"), new TableName(null, "s"),
        new TransferSchema([new TransferColumn("a", CanonicalType.Of(CanonicalKind.Int32), IsKey: true)]));
}

public class StagingTests
{
    // Build's algorithm (CopyModels.StagingTable), reproduced here as the oracle for parity.
    private static string BuildName(string modelName)
    {
        var plain = "stg_" + modelName.Replace(".", "__");
        if (Encoding.UTF8.GetByteCount(plain) <= 60) return plain;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelName)))[..8].ToLowerInvariant();
        return plain[..Math.Min(plain.Length, 51)] + "_" + hash;
    }

    private static readonly ISqlDialect Pg = DialectCatalog.For(EngineId.Postgres);

    [Theory]
    [InlineData("marts", "fct_orders")]
    [InlineData("a", "b")]
    [InlineData("a_very_long_schema_name_for_marts", "a_very_long_table_name_that_goes_past_the_postgres_limit")]
    [InlineData("s", "exactly_fifty_six_characters_long_table_name_padded_x")]
    public void Names_match_Builds_algorithm_with_the_build_compatible_policy(string schema, string table)
    {
        var name = StagingNamer.Name(StagingPolicy.BuildCompatible("tracking"), Pg, new TableName(schema, table));
        Assert.Equal("tracking", name.Schema);
        Assert.Equal(BuildName($"{schema}.{table}"), name.Table);
    }

    [Fact]
    public void Names_never_exceed_the_engine_limit_and_are_deterministic()
    {
        var target = new TableName("s", new string('t', 300));
        foreach (var id in new[] { "postgres", "sqlserver", "mysql", "oracle", "duckdb" })
        {
            var d = DialectCatalog.For(new EngineId(id));
            var limit = d.Identifiers.MaxLength > 0 ? d.Identifiers.MaxLength : 63;
            var a = StagingNamer.Name(new StagingPolicy(), d, target);
            var b = StagingNamer.Name(new StagingPolicy(), d, target);
            Assert.Equal(a, b);
            Assert.True(Encoding.UTF8.GetByteCount(a.Table) <= limit, id);
        }
    }

    [Fact]
    public void Different_long_names_do_not_collide()
    {
        var p = new StagingPolicy { MaxIdentifierBytes = 30 };
        var a = StagingNamer.Name(p, Pg, new TableName("s", "a_very_long_table_name_number_one_x"));
        var b = StagingNamer.Name(p, Pg, new TableName("s", "a_very_long_table_name_number_two_x"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Multi_byte_names_are_cut_on_character_boundaries()
    {
        var name = StagingNamer.Name(new StagingPolicy { MaxIdentifierBytes = 20 }, Pg, new TableName("s", new string('é', 30)));
        Assert.True(Encoding.UTF8.GetByteCount(name.Table) <= 20);
        Assert.DoesNotContain('�', name.Table);
    }

    [Fact]
    public void Run_scoped_names_differ_per_run_and_keep_the_limit()
    {
        var p = new StagingPolicy { RunScoped = true };
        var t = new TableName("s", new string('x', 100));
        var a = StagingNamer.Name(p, Pg, t, "run-1");
        var b = StagingNamer.Name(p, Pg, t, "run-2");
        Assert.NotEqual(a, b);
        Assert.Equal(a, StagingNamer.Name(p, Pg, t, "run-1"));
        Assert.True(a.Table.Length <= 63);
        Assert.Matches("_r[0-9a-f]{8}$", a.Table);
        var short1 = StagingNamer.Name(p, Pg, new TableName("s", "t"), "run-1");
        Assert.Equal("stg_s__t" + a.Table[^10..], short1.Table);
    }

    [Fact]
    public void Staging_lives_in_the_policy_schema_or_the_targets()
    {
        Assert.Equal("trk", StagingNamer.Name(new StagingPolicy { Schema = "trk" }, Pg, new TableName("s", "t")).Schema);
        Assert.Equal("s", StagingNamer.Name(new StagingPolicy(), Pg, new TableName("s", "t")).Schema);
    }

    [Fact]
    public void Temp_names_have_no_schema_and_sql_server_adds_the_hash_prefix()
    {
        var pg = StagingNamer.Name(new StagingPolicy(), Pg, new TableName("s", "t"), temporary: true);
        Assert.Null(pg.Schema);
        Assert.Equal("stg_s__t", pg.Table);
        var ms = StagingNamer.Name(new StagingPolicy(), DialectCatalog.For(EngineId.SqlServer), new TableName("s", "t"), temporary: true);
        Assert.Equal("#stg_s__t", ms.Table);
    }

    [Theory]
    [InlineData(StagingLifecycle.DropOnSuccess, true, true)]
    [InlineData(StagingLifecycle.DropOnSuccess, false, false)]
    [InlineData(StagingLifecycle.DropAlways, false, true)]
    [InlineData(StagingLifecycle.Retain, true, false)]
    public void Lifecycle(StagingLifecycle l, bool succeeded, bool drop) => Assert.Equal(drop, StagingNamer.ShouldDrop(l, succeeded));

    private static StagingTable Table(ISqlDialect d, bool temp) =>
        new(StagingNamer.Name(new StagingPolicy { Schema = "trk" }, d, new TableName("s", "t"), temporary: temp),
            new TransferSchema([new("id", CanonicalType.Of(CanonicalKind.Int32)), new("n", CanonicalType.String(10))]), temp, StagingLifecycle.DropOnSuccess);

    [Fact]
    public void Create_ddl_per_engine_for_persistent_and_temp_tables()
    {
        Assert.Equal("CREATE TABLE \"trk\".\"stg_s__t\" (\"id\" integer, \"n\" varchar(10))".ToLowerInvariant(),
            StagingDdl.Create(Pg, Table(Pg, false)).Text.ToLowerInvariant().Replace("character varying", "varchar"));
        var temp = StagingDdl.Create(Pg, Table(Pg, true)).Text;
        Assert.StartsWith("CREATE TEMP TABLE \"stg_s__t\"", temp);
        var ms = DialectCatalog.For(EngineId.SqlServer);
        Assert.StartsWith("CREATE TABLE [#stg_s__t]", StagingDdl.Create(ms, Table(ms, true)).Text);
        var my = DialectCatalog.For(EngineId.MySql);
        Assert.StartsWith("CREATE TEMPORARY TABLE", StagingDdl.Create(my, Table(my, true)).Text);
        var ora = DialectCatalog.For(EngineId.Oracle);
        Assert.Contains("ON COMMIT PRESERVE ROWS", StagingDdl.Create(ora, Table(ora, true)).Text);
        Assert.All(new[] { Pg, ms, my, ora }, d => Assert.Equal(WriteKind.Ddl, StagingDdl.Create(d, Table(d, false)).Intent.Kind));
    }

    [Fact]
    public void Drop_is_if_exists_except_on_oracle()
    {
        Assert.StartsWith("DROP TABLE IF EXISTS", StagingDdl.Drop(Pg, Table(Pg, false)).Text);
        var ora = DialectCatalog.For(EngineId.Oracle);
        Assert.StartsWith("DROP TABLE \"", StagingDdl.Drop(ora, Table(ora, false)).Text);
    }

    [Fact]
    public void Staging_ddl_refuses_a_column_type_the_engine_cannot_hold()
    {
        var my = DialectCatalog.For(EngineId.MySql);
        var interval = new StagingTable(new TableName(null, "t"), new TransferSchema([new("i", CanonicalType.Of(CanonicalKind.List))]), false, StagingLifecycle.Retain);
        Assert.Throws<TransferException>(() => StagingDdl.Create(my, interval));
    }
}
