using DbDataKit.Dialect;
using DbDataKit.Sync.Core.Sql;
using MySqlDialect = DbDataKit.Sync.Core.Sql.MySqlDialect;
using OracleDialect = DbDataKit.Sync.Core.Sql.OracleDialect;

namespace DbDataKit.Sync.Core.Tests;

/// <summary>
/// Sync's dialects answer from the shared type tables (<c>DbDataKit.Dialect</c>): what a column read on one engine becomes on another.
/// </summary>
public sealed class SharedTypeAnswersTests
{
    private static CanonicalType Read(SqlDialect dialect, string native) =>
        dialect.ReadColumnType(native) is { IsMapped: true, Result: { } type } ? type : CanonicalType.Unmappable;

    private static RenderedColumnType Carry(SqlDialect target, SqlDialect source, string native)
    {
        var read = source.ReadColumnType(native);
        return target.WriteColumnType(read.Result!, read.Fidelity == Fidelity.Exact ? null : read.Note);
    }

    private static string Write(SqlDialect target, SqlDialect source, string native) => Carry(target, source, native).Sql;

    [Fact]
    public void SqlServer_tinyint_is_read_as_an_unsigned_8_bit_integer()
    {
        var type = Read(MsSqlDialect.Instance, "tinyint");

        Assert.Equal(CanonicalKind.Int8, type.Kind);
        Assert.True(type.Unsigned);
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
        var historyColumn = CanonicalType.Timestamp();

        Assert.Equal("datetime(6)", MySqlDialect.Instance.WriteColumnType(historyColumn).Sql);
        Assert.Equal("datetime2(7)", MsSqlDialect.Instance.WriteColumnType(historyColumn).Sql);
    }

    [Fact]
    public void PostgreSQL_numeric_with_no_precision_keeps_its_fractions()
    {
        Assert.Equal("numeric", Write(PostgresDialect.Instance, PostgresDialect.Instance, "numeric"));
        var onSqlServer = Carry(MsSqlDialect.Instance, PostgresDialect.Instance, "numeric");
        Assert.Equal("decimal(38,9)", onSqlServer.Sql);
        Assert.Contains("no precision", onSqlServer.Fidelity);
    }

    [Fact]
    public void Fixed_length_binary_stays_fixed_on_SqlServer_and_MySql()
    {
        Assert.Equal("binary(16)", Write(MsSqlDialect.Instance, MsSqlDialect.Instance, "binary(16)"));
        Assert.Equal("binary(16)", Write(MySqlDialect.Instance, MsSqlDialect.Instance, "binary(16)"));
        Assert.Equal("bytea", Write(PostgresDialect.Instance, MsSqlDialect.Instance, "binary(16)"));
        var rowversion = Read(MsSqlDialect.Instance, "rowversion");
        Assert.True(rowversion.Fixed);
        Assert.Equal(8, rowversion.Length);
        Assert.Equal("binary(8)", MsSqlDialect.Instance.WriteColumnType(rowversion).Sql);
    }

    [Fact]
    public void A_time_on_Oracle_is_a_timestamp()
    {
        var rendered = Carry(OracleDialect.Instance, MsSqlDialect.Instance, "time(3)");

        Assert.Equal("timestamp(3)", rendered.Sql);
        Assert.Contains("TIME-only", rendered.Fidelity);
    }

    [Theory]
    [InlineData("tinyint", CanonicalKind.Int8)]
    [InlineData("varbinary", CanonicalKind.Binary)]
    [InlineData("time", CanonicalKind.Time)]
    public void The_Flight_SQL_names_of_Arrow_types_are_read_through_the_flightsql_dialect(string native, CanonicalKind kind) =>
        Assert.Equal(kind, Read(AnsiDialect.Instance, native).Kind);

    [Theory]
    [InlineData("interval")]
    [InlineData("integer[]")]
    public void Interval_and_array_columns_are_not_replicated(string native)
    {
        var read = PostgresDialect.Instance.ReadColumnType(native);

        Assert.False(read.IsMapped);
        if (native == "interval") Assert.Contains("Sync does not replicate", read.Note);
    }

    [Fact]
    public void The_JDBC_dialect_reads_and_writes_PostgreSQL_types()
    {
        Assert.Equal("bytea", Write(JdbcDialect.Instance, JdbcDialect.Instance, "bytea"));
        Assert.Equal("text", Write(JdbcDialect.Instance, JdbcDialect.Instance, "character varying"));
    }

    [Fact]
    public void A_string_with_no_length_is_unbounded()
    {
        var noLength = CanonicalType.String();

        Assert.Equal("nvarchar(max)", MsSqlDialect.Instance.WriteColumnType(noLength).Sql);
        Assert.Equal("text", PostgresDialect.Instance.WriteColumnType(noLength).Sql);
    }

    [Fact]
    public void A_reading_note_is_joined_to_the_writing_note()
    {
        var rendered = Carry(PostgresDialect.Instance, MsSqlDialect.Instance, "money");

        Assert.Equal("numeric(19,4)", rendered.Sql);
        Assert.Contains("currency", rendered.Fidelity);
    }
}
