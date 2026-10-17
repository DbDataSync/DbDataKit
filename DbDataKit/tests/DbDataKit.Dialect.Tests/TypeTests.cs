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
        "decimal(18,2)", "decimal(10)", "decimal(38,10)", "decimal(50,5)", "float", "double",
        "string(50)", "string(unicode,50)", "string", "string(unicode)", "string(5000)", "string(unicode,5000)", "string(unicode,20000)",
        "binary(16)", "binary", "binary(9000)",
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
    [InlineData("oracle", "NUMBER", "decimal", Fidelity.Lossy)]
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
                // canonical form must be stable under a second render+read (Oracle NUMBER(3) reads back as decimal(3,0) by policy)
                var again = d.ColumnType(back.Result!);
                Assert.True(again.IsMapped, $"{id}: {back.Result} not renderable");
                Assert.Equal(back.Result, d.ToCanonical(again.Result!).Result);
            }
        }
    }

    [Fact]
    public void Ansi_and_unmappable_edge_cases()
    {
        Assert.False(D("ansi").ColumnType(CanonicalType.Of(CanonicalKind.Json)).IsMapped);
        Assert.False(D("sqlserver").ColumnType(CanonicalType.Unmappable).IsMapped);
        Assert.Equal("int", D("sqlserver").ColumnType(CanonicalType.Of(CanonicalKind.Int32)).Result);
    }
}
