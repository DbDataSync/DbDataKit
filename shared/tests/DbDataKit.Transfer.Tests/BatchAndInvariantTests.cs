using System.Text.RegularExpressions;
using Apache.Arrow;
using DbDataKit.Dialect;
using Xunit;

namespace DbDataKit.Transfer.Tests;

public class BatchTests
{
    private static readonly TransferSchema Schema = new(
    [
        new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), Nullable: false),
        new TransferColumn("name", CanonicalType.String()),
        new TransferColumn("amount", CanonicalType.Decimal(10, 2)),
        new TransferColumn("day", CanonicalType.Of(CanonicalKind.Date)),
        new TransferColumn("at", CanonicalType.Timestamp(6)),
        new TransferColumn("atz", CanonicalType.Timestamp(6, tz: true)),
        new TransferColumn("t", CanonicalType.Time(6)),
        new TransferColumn("flag", CanonicalType.Of(CanonicalKind.Boolean)),
        new TransferColumn("bin", CanonicalType.Binary()),
        new TransferColumn("d", CanonicalType.Of(CanonicalKind.Double)),
        new TransferColumn("big", CanonicalType.Of(CanonicalKind.Int64)),
        new TransferColumn("u", new CanonicalType(CanonicalKind.Int16, Unsigned: true)),
    ]);

    private static readonly object?[] Row1 =
    [
        1, "a", 12.34m, new DateOnly(2024, 3, 4), new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Unspecified).AddTicks(10),
        new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero), new TimeOnly(1, 2, 3), true, new byte[] { 1, 2 }, 1.5, 9_000_000_000L, (ushort)65535,
    ];
    private static readonly object?[] Row2 = [2, null, null, null, null, null, null, null, null, null, null, null];

    [Fact]
    public void Row_batch_reader_exposes_values_types_and_nulls()
    {
        using var r = new RowDataBatch(Schema, [Row1, Row2]).AsDataReader();
        Assert.Equal(12, r.FieldCount);
        Assert.Equal("name", r.GetName(1));
        Assert.Equal(typeof(decimal), r.GetFieldType(2));
        Assert.Equal(typeof(DateOnly), r.GetFieldType(3));
        Assert.Equal(typeof(DateTimeOffset), r.GetFieldType(5));
        Assert.Equal(typeof(ushort), r.GetFieldType(11));
        Assert.True(r.HasRows);
        Assert.True(r.Read());
        Assert.Equal(1, r.GetInt32(0));
        Assert.Equal("a", r.GetString(1));
        Assert.Equal(12.34m, r.GetDecimal(2));
        Assert.Equal(new byte[] { 1, 2 }, r.GetValue(8));
        Assert.Equal(9_000_000_000L, r["big"]);
        Assert.True(r.Read());
        Assert.True(r.IsDBNull(1));
        Assert.Equal(DBNull.Value, r.GetValue(1));
        Assert.False(r.Read());
    }

    [Fact]
    public void Reader_before_read_or_unknown_column_throws()
    {
        using var r = new RowDataBatch(Schema, [Row1]).AsDataReader();
        Assert.Throws<InvalidOperationException>(() => r.GetValue(0));
        Assert.Throws<IndexOutOfRangeException>(() => r.GetOrdinal("nope"));
    }

    [Fact]
    public void Each_reader_is_a_fresh_forward_only_view()
    {
        var b = new RowDataBatch(Schema, [Row1, Row2]);
        using (var r1 = b.AsDataReader()) { r1.Read(); r1.Read(); Assert.False(r1.Read()); }
        using var r2 = b.AsDataReader();
        Assert.True(r2.Read());
    }

    [Fact]
    public void Row_width_is_checked()
    {
        Assert.Throws<ArgumentException>(() => new RowDataBatch(Schema, [[1, 2]]));
    }

    [Fact]
    public void Duplicate_column_names_are_rejected_case_insensitively()
    {
        Assert.Throws<ArgumentException>(() => new TransferSchema([new("a", CanonicalType.String()), new("A", CanonicalType.String())]));
    }

    [Fact]
    public void Rows_survive_a_round_trip_through_arrow()
    {
        var batch = new RowDataBatch(Schema, [Row1, Row2]);
        var arrow = batch.AsArrow();
        Assert.Equal(2, arrow.Length);
        Assert.Equal(12, arrow.ColumnCount);
        Assert.False(arrow.Schema.GetFieldByName("id").IsNullable);

        var back = new ArrowDataBatch(arrow);
        Assert.Equal(2, back.Rows);
        using var r = back.AsDataReader();
        Assert.True(r.Read());
        Assert.Equal(1, r.GetValue(0));
        Assert.Equal("a", r.GetValue(1));
        Assert.Equal(12.34m, r.GetValue(2));
        Assert.Equal(new DateOnly(2024, 3, 4), r.GetValue(3));
        Assert.Equal(new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Unspecified).AddTicks(10), r.GetValue(4));
        Assert.Equal(new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero), r.GetValue(5));
        Assert.Equal(new TimeOnly(1, 2, 3), r.GetValue(6));
        Assert.Equal(true, r.GetValue(7));
        Assert.Equal(new byte[] { 1, 2 }, r.GetValue(8));
        Assert.Equal(1.5, r.GetValue(9));
        Assert.Equal(9_000_000_000L, r.GetValue(10));
        Assert.Equal((ushort)65535, r.GetValue(11));
        Assert.True(r.Read());
        for (var i = 1; i < 12; i++) Assert.True(r.IsDBNull(i), $"column {i}");
    }

    [Fact]
    public void Arrow_batch_derives_a_canonical_schema()
    {
        var arrow = new RowDataBatch(Schema, [Row1]).AsArrow();
        var b = new ArrowDataBatch(arrow);
        Assert.Equal(CanonicalKind.Decimal, b.Schema[2].Type.Kind);
        Assert.Equal(true, b.Schema[5].Type.TimeZone);
        Assert.Same(arrow, b.AsArrow());
    }

    [Fact]
    public void Guid_json_and_interval_travel_through_arrow()
    {
        var schema = new TransferSchema([new("g", CanonicalType.Of(CanonicalKind.Guid)), new("i", CanonicalType.Of(CanonicalKind.Interval))]);
        var g = Guid.NewGuid();
        var arrow = new RowDataBatch(schema, [[g, TimeSpan.FromMinutes(90)]]).AsArrow();
        using var r = new ArrowDataBatch(arrow).AsDataReader();
        Assert.True(r.Read());
        Assert.Equal(g.ToString(), r.GetValue(0));         // guid travels as text (Plan 05 Arrow table)
        Assert.Equal(TimeSpan.FromMinutes(90), r.GetValue(1));
    }

    [Fact]
    public void Clr_contract_types()
    {
        Assert.Equal(typeof(sbyte), ClrContract.TypeOf(CanonicalType.Of(CanonicalKind.Int8)));
        Assert.Equal(typeof(byte), ClrContract.TypeOf(new CanonicalType(CanonicalKind.Int8, Unsigned: true)));
        Assert.Equal(typeof(TimeOnly), ClrContract.TypeOf(CanonicalType.Time()));
        Assert.Equal(typeof(DateTime), ClrContract.TypeOf(CanonicalType.Timestamp()));
        Assert.Equal(typeof(string), ClrContract.TypeOf(CanonicalType.Of(CanonicalKind.Json)));
    }
}

public class InvariantTests
{
    private static string Src()
    {
        var root = SourceInvariants.FindRoot(AppContext.BaseDirectory, "DbDataKit.Shared.slnx") ?? throw new InvalidOperationException("shared/ root not found");
        return Path.Combine(root, "src");
    }

    // Only the executor and the bulk writers may create or run driver commands in the Transfer packages.
    private static readonly string[] Allowed =
    [
        "DbDataKit.Transfer/Gate/AdoWriteExecutor.cs",
        "DbDataKit.Transfer.Conformance/ConformanceRunner.cs",     // reads the target back (SELECT only); every write goes through the gate
        "DbDataKit.Transfer.SqlServer/SqlServerBulkWriter.cs",
        "DbDataKit.Transfer.Postgres/PostgresBulkWriter.cs",
        "DbDataKit.Transfer/Gate/SourceInvariants.cs",              // the regex itself names the APIs
    ];

    [Fact]
    public void Driver_commands_are_created_only_in_the_allowed_files()
    {
        var src = Src();
        var offenders = new List<string>();
        foreach (var pkg in Directory.EnumerateDirectories(src, "DbDataKit.Transfer*"))
            offenders.AddRange(SourceInvariants.FindOffenders(pkg, SourceInvariants.DriverCalls, [])
                .Select(f => $"{Path.GetFileName(pkg)}/{f}").Where(f => !Allowed.Contains(f)));
        Assert.True(offenders.Count == 0, "Writes must go through WriteGate. Driver calls found in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scanner_flags_a_violation_and_honours_the_allow_list()
    {
        var dir = Directory.CreateTempSubdirectory("inv").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bad.cs"), "class A { void M(System.Data.Common.DbConnection c) { var cmd = c.CreateCommand(); cmd.ExecuteNonQuery(); } }");
            File.WriteAllText(Path.Combine(dir, "Good.cs"), "// ExecuteNonQuery in a comment\nclass B { string s = \"CreateCommand in a string\"; }");
            Directory.CreateDirectory(Path.Combine(dir, "Gate"));
            File.WriteAllText(Path.Combine(dir, "Gate", "Exec.cs"), "class C { void M(System.Data.Common.DbConnection c) { c.CreateCommand(); } }");
            Assert.Equal(["Bad.cs", "Gate/Exec.cs"], SourceInvariants.FindOffenders(dir, SourceInvariants.DriverCalls, []));
            Assert.Equal(["Bad.cs"], SourceInvariants.FindOffenders(dir, SourceInvariants.DriverCalls, ["Gate/"]));
            Assert.Empty(SourceInvariants.FindOffenders(dir, SourceInvariants.DriverCalls, ["Bad.cs", "Gate/Exec.cs"]));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Stripping_removes_verbatim_raw_and_char_literals()
    {
        var code = "var a = @\"x \"\"CreateCommand\"\" y\"; var b = \"\"\"ExecuteScalar\"\"\"; var c = '\"'; var d = 1; // SqlBulkCopy";
        var stripped = SourceInvariants.StripCommentsAndStrings(code);
        Assert.DoesNotMatch(SourceInvariants.DriverCalls, stripped);
        Assert.Contains("var d = 1", stripped);
    }

    [Fact]
    public void The_regex_covers_the_bulk_routes()
    {
        foreach (var api in new[] { "SqlBulkCopy", "BeginBinaryImportAsync", "NpgsqlBatch", "ExecuteReaderAsync", "MySqlBulkCopy" })
            Assert.Matches(SourceInvariants.DriverCalls, $"x.{api}(");
    }

    [Fact]
    public void FindRoot_walks_up_and_returns_null_when_absent()
    {
        Assert.NotNull(SourceInvariants.FindRoot(AppContext.BaseDirectory, "DbDataKit.Shared.slnx"));
        Assert.Null(SourceInvariants.FindRoot(AppContext.BaseDirectory, "definitely-not-a-file-" + Guid.NewGuid().ToString("N")));
    }
}
