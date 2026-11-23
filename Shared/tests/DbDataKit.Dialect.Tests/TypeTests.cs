using System.Runtime.CompilerServices;
using System.Text;
using DbDataKit.Dialect;
using Xunit;

namespace DbDataKit.Dialect.Tests;

public class TypeTests
{
    private static ISqlDialect D(string id) => DialectCatalog.Builtin.Get(id);

    public static readonly string[] CanonicalTypes =
    [
        "boolean", "int8", "int8(unsigned)", "int16", "int16(unsigned)", "int32", "int32(unsigned)", "int64", "int64(unsigned)",
        "decimal(18,2)", "decimal(10)", "decimal(38,10)", "decimal(50,5)", "decimal", "float", "double",
        "string(50)", "string(unicode,50)", "string", "string(unicode)", "string(5000)", "string(unicode,5000)", "string(unicode,20000)",
        "binary(16)", "binary", "binary(9000)", "binary(fixed,16)", "binary(fixed,300)",
        "date", "time", "time(3)", "time(7)", "timestamp", "timestamp(0)", "timestamp(3)", "timestamp(7)", "timestamp(tz)", "timestamp(tz,3)", "timestamp(tz,9)",
        "guid", "json", "xml", "interval", "list", "unmappable",
    ];

    public static string Render()
    {
        var sb = new StringBuilder();
        sb.Append("# canonical type -> native rendering per engine. '| Fidelity' marks a non-Exact mapping; '-' is Unmappable.\n");
        sb.Append("# Regenerate with UPDATE_GOLDEN=1 dotnet test, then review the diff.\n");
        foreach (var c in CanonicalTypes)
        {
            sb.Append('"').Append(c).Append("\":\n");
            var type = CanonicalType.Parse(c);
            foreach (var id in DialectCatalog.Builtin.Ids)
            {
                var m = D(id).ColumnType(type);
                string v = !m.IsMapped ? "-" : m.Fidelity == Fidelity.Exact ? m.Result! : $"{m.Result} | {m.Fidelity}";
                sb.Append("  ").Append(id).Append(": \"").Append(v).Append("\"\n");
            }
        }
        return sb.ToString();
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    [Fact]
    public void Golden_table_matches()
    {
        string here = ThisFile();
        string actual = Render();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(here)!, "types.golden.yml"), actual);
        string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "types.golden.yml"));
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1") expected = actual;
        Assert.Equal(expected.Replace("\r\n", "\n"), actual);
    }

    [Fact]
    public void Every_non_exact_rendering_has_a_note()
    {
        foreach (var id in DialectCatalog.Builtin.Ids)
            foreach (var c in CanonicalTypes)
            {
                var m = D(id).ColumnType(CanonicalType.Parse(c));
                if (m.Fidelity != Fidelity.Exact) Assert.False(string.IsNullOrWhiteSpace(m.Note), $"{id} {c}");
            }
    }

    [Theory]
    [InlineData("sqlserver", "nvarchar(50)", "string(unicode,50)", Fidelity.Exact)]
    [InlineData("sqlserver", "NVARCHAR(MAX)", "string(unicode)", Fidelity.Exact)]
    [InlineData("sqlserver", "varchar", "string(1)", Fidelity.Exact)]
    [InlineData("sqlserver", "decimal(10, 2)", "decimal(10,2)", Fidelity.Exact)]
    [InlineData("sqlserver", "int", "int32", Fidelity.Exact)]
    [InlineData("sqlserver", "tinyint", "int8(unsigned)", Fidelity.Exact)]
    [InlineData("sqlserver", "money", "decimal(19,4)", Fidelity.Lossy)]
    [InlineData("sqlserver", "datetime2", "timestamp(7)", Fidelity.Exact)]
    [InlineData("sqlserver", "datetimeoffset(3)", "timestamp(tz,3)", Fidelity.Exact)]
    [InlineData("sqlserver", "uniqueidentifier", "guid", Fidelity.Exact)]
    [InlineData("postgres", "character varying(20)", "string(unicode,20)", Fidelity.Exact)]
    [InlineData("postgres", "timestamp with time zone", "timestamp(tz,6)", Fidelity.Exact)]
    [InlineData("postgres", "timestamp(3) without time zone", "timestamp(3)", Fidelity.Exact)]
    [InlineData("postgres", "numeric", "decimal", Fidelity.Widened)]
    [InlineData("postgres", "numeric(12,4)", "decimal(12,4)", Fidelity.Exact)]
    [InlineData("postgres", "int4", "int32", Fidelity.Exact)]
    [InlineData("postgres", "jsonb", "json", Fidelity.Exact)]
    [InlineData("mysql", "tinyint(1)", "boolean", Fidelity.Lossy)]
    [InlineData("mysql", "int(11) unsigned", "int32(unsigned)", Fidelity.Exact)]
    [InlineData("mysql", "bigint unsigned", "int64(unsigned)", Fidelity.Exact)]
    [InlineData("mysql", "datetime(6)", "timestamp(6)", Fidelity.Exact)]
    [InlineData("oracle", "NUMBER(10,2)", "decimal(10,2)", Fidelity.Exact)]
    [InlineData("oracle", "NUMBER", "decimal(38,10)", Fidelity.Lossy)]
    [InlineData("oracle", "NUMBER(9,0)", "int32", Fidelity.Exact)]
    [InlineData("oracle", "NUMBER(19,0)", "decimal(19,0)", Fidelity.Exact)]
    [InlineData("oracle", "VARCHAR2(30 CHAR)", "string(30)", Fidelity.Exact)]
    [InlineData("oracle", "DATE", "timestamp(0)", Fidelity.Widened)]
    [InlineData("duckdb", "HUGEINT", "decimal(38,0)", Fidelity.Lossy)]
    [InlineData("duckdb", "TIMESTAMP_MS", "timestamp(3)", Fidelity.Exact)]
    [InlineData("duckdb", "UBIGINT", "int64(unsigned)", Fidelity.Exact)]
    public void Native_to_canonical(string engine, string native, string canonical, Fidelity fidelity)
    {
        var m = D(engine).ToCanonical(native);
        Assert.Equal(CanonicalType.Parse(canonical), m.Result);
        Assert.Equal(fidelity, m.Fidelity);
    }

    [Theory]
    [InlineData("sqlserver", "hierarchyid")]
    [InlineData("sqlserver", "sql_variant")]
    [InlineData("postgres", "int4range")]
    [InlineData("postgres", "text[]")]
    [InlineData("mysql", "geometry")]
    public void Unknown_native_types_are_unmappable_with_a_note(string engine, string native)
    {
        var m = D(engine).ToCanonical(native);
        Assert.Equal(Fidelity.Unmappable, m.Fidelity);
        Assert.Equal(CanonicalKind.Unmappable, m.Result!.Kind);
        Assert.Contains(native, m.Note);
    }

    [Fact]
    public void Exact_native_types_round_trip_through_canonical()
    {
        foreach (var id in DialectCatalog.Builtin.Ids)
        {
            var d = D(id);
            foreach (var c in CanonicalTypes)
            {
                var type = CanonicalType.Parse(c);
                var written = d.ColumnType(type);
                if (written.Fidelity != Fidelity.Exact) continue;
                var back = d.ToCanonical(written.Result!);
                Assert.True(back.IsMapped, $"{id}: {c} -> {written.Result} did not read back");
                // render+read must reach a fixed point (Oracle NUMBER(3) reads back as int16, whose NUMBER(5) reads back as int32 because
                // five digits may not fit an int16; int32 then holds)
                var current = back.Result!;
                for (int step = 0; ; step++)
                {
                    var again = d.ColumnType(current);
                    Assert.True(again.IsMapped, $"{id}: {current} not renderable");
                    var next = d.ToCanonical(again.Result!).Result!;
                    if (next == current) break;
                    Assert.True(step < 3, $"{id}: {c} does not settle under render+read");
                    current = next;
                }
            }
        }
    }

    // Unsigned integers keep their range on every engine: an engine with unsigned types writes them, the others widen to the next signed type.
    [Theory]
    [InlineData("sqlserver", "sqlserver", "tinyint", "tinyint", Fidelity.Exact)]
    [InlineData("sqlserver", "postgres", "tinyint", "smallint", Fidelity.Widened)]
    [InlineData("sqlserver", "mysql", "tinyint", "tinyint unsigned", Fidelity.Exact)]
    [InlineData("sqlserver", "duckdb", "tinyint", "utinyint", Fidelity.Exact)]
    [InlineData("sqlserver", "oracle", "tinyint", "number(3)", Fidelity.Exact)]
    [InlineData("mysql", "sqlserver", "int unsigned", "bigint", Fidelity.Widened)]
    [InlineData("mysql", "postgres", "bigint unsigned", "numeric(20,0)", Fidelity.Widened)]
    [InlineData("mysql", "mysql", "bigint unsigned", "bigint unsigned", Fidelity.Exact)]
    [InlineData("duckdb", "duckdb", "ubigint", "ubigint", Fidelity.Exact)]
    public void An_unsigned_integer_keeps_its_range(string source, string engine, string sourceNative, string native, Fidelity fidelity)
    {
        var read = D(source).ToCanonical(sourceNative);
        Assert.True(read.Result!.Unsigned);
        var written = D(engine).ColumnType(read.Result);
        Assert.Equal(native, written.Result);
        Assert.Equal(fidelity, written.Fidelity);
    }

    // A time or timestamp with no stated precision is written with microseconds; MySQL reads a type with no precision as whole seconds, its own default.
    [Theory]
    [InlineData("time", "time(6)", Fidelity.Exact)]
    [InlineData("timestamp", "datetime(6)", Fidelity.Exact)]
    [InlineData("timestamp(tz)", "timestamp(6)", Fidelity.Lossy)]
    [InlineData("time(3)", "time(3)", Fidelity.Exact)]
    [InlineData("timestamp(0)", "datetime(0)", Fidelity.Exact)]
    public void MySql_writes_a_time_with_no_precision_with_microseconds(string canonical, string native, Fidelity fidelity)
    {
        var m = D("mysql").ColumnType(CanonicalType.Parse(canonical));
        Assert.Equal(native, m.Result);
        Assert.Equal(fidelity, m.Fidelity);
    }

    [Theory]
    [InlineData("datetime", "timestamp(0)")]
    [InlineData("time", "time(0)")]
    [InlineData("timestamp", "timestamp(tz,0)")]
    public void MySql_reads_a_time_with_no_precision_as_whole_seconds(string native, string canonical) =>
        Assert.Equal(CanonicalType.Parse(canonical), D("mysql").ToCanonical(native).Result);

    // A decimal with no precision (PostgreSQL's unconstrained numeric) is written exactly where the engine has such a type, and with 38 digits, 9
    // after the point, elsewhere, with a note.
    [Theory]
    [InlineData("postgres", "numeric", Fidelity.Exact)]
    [InlineData("sqlserver", "decimal(38,9)", Fidelity.Lossy)]
    [InlineData("fabric", "decimal(38,9)", Fidelity.Lossy)]
    [InlineData("mysql", "decimal(38,9)", Fidelity.Lossy)]
    [InlineData("oracle", "number(38,9)", Fidelity.Lossy)]
    [InlineData("duckdb", "decimal(38,9)", Fidelity.Lossy)]
    [InlineData("ansi", "numeric(38,9)", Fidelity.Lossy)]
    public void A_decimal_with_no_precision_keeps_its_fractions(string engine, string native, Fidelity fidelity)
    {
        var read = D("postgres").ToCanonical("numeric").Result!;
        Assert.Null(read.Precision);
        var m = D(engine).ColumnType(read);
        Assert.Equal(native, m.Result);
        Assert.Equal(fidelity, m.Fidelity);
        if (fidelity != Fidelity.Exact) Assert.Contains("no precision", m.Note);
    }

    // SQL Server's and MySQL's binary(n) keep their fixed length; an engine with one binary type writes a fixed binary as that type.
    [Theory]
    [InlineData("sqlserver", "binary(16)", "binary(fixed,16)", Fidelity.Exact)]
    [InlineData("sqlserver", "rowversion", "binary(fixed,8)", Fidelity.Lossy)]
    [InlineData("sqlserver", "timestamp", "binary(fixed,8)", Fidelity.Lossy)]
    [InlineData("sqlserver", "varbinary(16)", "binary(16)", Fidelity.Exact)]
    [InlineData("mysql", "binary(16)", "binary(fixed,16)", Fidelity.Exact)]
    [InlineData("mysql", "binary", "binary(fixed,1)", Fidelity.Exact)]
    public void Fixed_binary_is_read_as_fixed(string engine, string native, string canonical, Fidelity fidelity)
    {
        var m = D(engine).ToCanonical(native);
        Assert.Equal(CanonicalType.Parse(canonical), m.Result);
        Assert.Equal(fidelity, m.Fidelity);
    }

    [Theory]
    [InlineData("sqlserver", "binary(16)", Fidelity.Exact)]
    [InlineData("fabric", "binary(16)", Fidelity.Exact)]
    [InlineData("mysql", "binary(16)", Fidelity.Exact)]
    [InlineData("postgres", "bytea", Fidelity.Widened)]
    [InlineData("oracle", "raw(16)", Fidelity.Exact)]
    [InlineData("duckdb", "blob", Fidelity.Widened)]
    public void Fixed_binary_is_written_as_fixed_where_the_engine_has_it(string engine, string native, Fidelity fidelity)
    {
        var m = D(engine).ColumnType(CanonicalType.Parse("binary(fixed,16)"));
        Assert.Equal(native, m.Result);
        Assert.Equal(fidelity, m.Fidelity);
    }

    [Fact]
    public void A_fixed_binary_too_long_for_the_fixed_type_is_written_as_variable_length()
    {
        Assert.Equal("varbinary(max)", D("sqlserver").ColumnType(CanonicalType.Parse("binary(fixed,9000)")).Result);
        Assert.Equal("varbinary(300)", D("mysql").ColumnType(CanonicalType.Parse("binary(fixed,300)")).Result);
        Assert.Equal("varbinary(16)", D("sqlserver").ColumnType(CanonicalType.Parse("binary(16)")).Result);
    }

    // Oracle has no time-of-day type: a time is written as a timestamp, whose date part means nothing, and reads back as a timestamp.
    [Theory]
    [InlineData("time", "timestamp(6)")]
    [InlineData("time(0)", "timestamp(0)")]
    [InlineData("time(9)", "timestamp(9)")]
    public void Oracle_writes_a_time_as_a_timestamp(string canonical, string native)
    {
        var m = D("oracle").ColumnType(CanonicalType.Parse(canonical));
        Assert.Equal(native, m.Result);
        Assert.Equal(Fidelity.Lossy, m.Fidelity);
        Assert.Contains("TIME-only", m.Note);
    }

    // The type names a Flight SQL client gives an Arrow column, read by the flightsql dialect; the shared ansi dialect does not read them all.
    [Theory]
    [InlineData("tinyint", "int8")]
    [InlineData("varbinary", "binary")]
    [InlineData("time", "time")]
    [InlineData("varchar", "string(unicode)")]
    [InlineData("decimal(20,0)", "decimal(20,0)")]
    [InlineData("double precision", "double")]
    [InlineData("timestamp with time zone", "timestamp(tz,6)")]
    [InlineData("bool", "boolean")]
    [InlineData("text", "string(unicode)")]
    public void Flightsql_reads_the_names_of_Arrow_types(string native, string canonical) =>
        Assert.Equal(CanonicalType.Parse(canonical), D("flightsql").ToCanonical(native).Result);

    [Theory]
    [InlineData("int8", "SMALLINT", Fidelity.Widened)]
    [InlineData("int64(unsigned)", "DECIMAL(20,0)", Fidelity.Widened)]
    [InlineData("decimal(18,2)", "DECIMAL(18,2)", Fidelity.Exact)]
    [InlineData("decimal", "DECIMAL(38,9)", Fidelity.Lossy)]
    [InlineData("string(unicode,50)", "VARCHAR(50)", Fidelity.Exact)]
    [InlineData("string", "VARCHAR(4000)", Fidelity.Lossy)]
    [InlineData("binary", "VARBINARY(4000)", Fidelity.Lossy)]
    [InlineData("binary(16)", "VARBINARY(4000)", Fidelity.Widened)]
    [InlineData("time", "TIME(6)", Fidelity.Exact)]
    [InlineData("timestamp(tz)", "TIMESTAMP(6) WITH TIME ZONE", Fidelity.Exact)]
    [InlineData("guid", "CHAR(36)", Fidelity.Widened)]
    [InlineData("json", "VARCHAR(4000)", Fidelity.Lossy)]
    public void Flightsql_writes_the_types_of_a_target_whose_engine_is_not_named(string canonical, string native, Fidelity fidelity)
    {
        var m = D("flightsql").ColumnType(CanonicalType.Parse(canonical));
        Assert.Equal(native, m.Result);
        Assert.Equal(fidelity, m.Fidelity);
    }

    [Fact]
    public void The_ansi_dialect_is_unchanged_by_flightsql()
    {
        Assert.False(D("ansi").ToCanonical("tinyint").IsMapped);
        Assert.Equal("clob", D("ansi").ColumnType(CanonicalType.String()).Result);
    }

    [Fact]
    public void Ansi_and_unmappable_edge_cases()
    {
        Assert.False(D("ansi").ColumnType(CanonicalType.Of(CanonicalKind.Json)).IsMapped);
        Assert.False(D("sqlserver").ColumnType(CanonicalType.Unmappable).IsMapped);
        Assert.Equal("int", D("sqlserver").ColumnType(CanonicalType.Of(CanonicalKind.Int32)).Result);
    }
}
