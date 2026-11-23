using DbDataSync.Core.Sql;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Sync's dialects answer from the shared type tables (<c>DbDataKit.Dialect</c>): what a column read on one engine becomes on another.
/// Design: the shared dialects' README, "Types with no stated size, unsigned integers and fixed-length binary".
/// </summary>
public sealed class SharedTypeAnswersTests
{
    private static string Write(SqlDialect target, SqlDialect source, string native) => target.RenderColumnType(source.ToCanonicalType(native)).Sql;

    [Fact]
    public void SqlServer_tinyint_is_read_as_an_unsigned_8_bit_integer()
    {
        var type = MsSqlDialect.Instance.ToCanonicalType("tinyint");

        Assert.Equal(CanonicalTypeKind.Int8, type.Kind);
        Assert.True(type.IsUnsigned);
        Assert.True(MsSqlDialect.Instance.ReadColumnType("tinyint").Result!.Unsigned);
    }

    [Fact]
    public void SqlServer_tinyint_keeps_values_up_to_255_on_every_target()
    {
        Assert.Equal("tinyint", Write(MsSqlDialect.Instance, MsSqlDialect.Instance, "tinyint"));
        Assert.Equal("tinyint unsigned", Write(MySqlDialect.Instance, MsSqlDialect.Instance, "tinyint"));
        Assert.Equal("utinyint", Write(DuckDbDialect.Instance, MsSqlDialect.Instance, "tinyint"));
        Assert.Equal("smallint", Write(PostgresDialect.Instance, MsSqlDialect.Instance, "tinyint"));
        Assert.Equal("SMALLINT", Write(AnsiDialect.Instance, MsSqlDialect.Instance, "tinyint"));
    }

    [Theory]
    [InlineData("int unsigned", "bigint")]
    [InlineData("bigint unsigned", "numeric(20,0)")]
    public void MySql_unsigned_integers_widen_where_the_target_has_no_unsigned_type(string native, string written)
    {
        Assert.Equal(written, Write(PostgresDialect.Instance, MySqlDialect.Instance, native));
        Assert.Equal(native, Write(MySqlDialect.Instance, MySqlDialect.Instance, native));
    }

    [Fact]
    public void A_history_column_on_MySql_keeps_microseconds()
    {
        var historyColumn = new CanonicalType(CanonicalTypeKind.Timestamp, null, null, null, false, false);

        Assert.Equal("datetime(6)", MySqlDialect.Instance.RenderColumnType(historyColumn).Sql);
        Assert.Equal("datetime2(7)", MsSqlDialect.Instance.RenderColumnType(historyColumn).Sql);
    }

    [Fact]
    public void PostgreSQL_numeric_with_no_precision_keeps_its_fractions()
    {
        Assert.Equal("numeric", Write(PostgresDialect.Instance, PostgresDialect.Instance, "numeric"));
        var onSqlServer = MsSqlDialect.Instance.RenderColumnType(PostgresDialect.Instance.ToCanonicalType("numeric"));
        Assert.Equal("decimal(38,9)", onSqlServer.Sql);
        Assert.Contains("no precision", onSqlServer.Fidelity);
    }

    [Fact]
    public void Fixed_length_binary_stays_fixed_on_SqlServer_and_MySql()
    {
        Assert.Equal("binary(16)", Write(MsSqlDialect.Instance, MsSqlDialect.Instance, "binary(16)"));
        Assert.Equal("binary(16)", Write(MySqlDialect.Instance, MsSqlDialect.Instance, "binary(16)"));
        Assert.Equal("bytea", Write(PostgresDialect.Instance, MsSqlDialect.Instance, "binary(16)"));
        var rowversion = MsSqlDialect.Instance.ToCanonicalType("rowversion");
        Assert.True(rowversion.IsFixed);
        Assert.Equal(8, rowversion.Length);
        Assert.Equal("binary(8)", MsSqlDialect.Instance.RenderColumnType(rowversion).Sql);
    }

    [Theory]
    [InlineData("interval")]
    [InlineData("integer[]")]
    public void Interval_and_array_columns_are_not_replicated(string native)
    {
        var type = PostgresDialect.Instance.ToCanonicalType(native);

        Assert.Equal(CanonicalTypeKind.Unmappable, type.Kind);
        Assert.False(PostgresDialect.Instance.ReadColumnType(native).IsMapped);
    }

    [Fact]
    public void The_JDBC_dialect_reads_and_writes_PostgreSQL_types()
    {
        Assert.Equal("bytea", JdbcDialect.Instance.RenderColumnType(JdbcDialect.Instance.ToCanonicalType("bytea")).Sql);
        Assert.Equal("text", JdbcDialect.Instance.RenderColumnType(JdbcDialect.Instance.ToCanonicalType("character varying")).Sql);
    }

    [Fact]
    public void A_string_with_no_length_is_unbounded()
    {
        var noLength = new CanonicalType(CanonicalTypeKind.String, null, null, null, true, false);

        Assert.Equal("nvarchar(max)", MsSqlDialect.Instance.RenderColumnType(noLength).Sql);
        Assert.Equal("text", PostgresDialect.Instance.RenderColumnType(noLength).Sql);
    }

    [Fact]
    public void Converting_to_the_shared_type_and_back_changes_nothing()
    {
        foreach (var native in new[] { "tinyint", "binary(16)", "rowversion", "datetimeoffset(3)", "nvarchar(50)", "varchar(max)", "decimal(12,4)", "money" })
        {
            var type = MsSqlDialect.Instance.ToCanonicalType(native);
            Assert.Equal(type with { SourceNote = null }, CanonicalType.FromShared(type.ToShared()));
        }
    }
}
