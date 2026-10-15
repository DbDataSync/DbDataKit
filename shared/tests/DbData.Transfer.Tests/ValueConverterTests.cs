using DbData.Dialect;
using Xunit;

namespace DbData.Transfer.Tests;

public class ValueConverterTests
{
    private static TransferColumn Col(CanonicalType t, bool nullable = true) => new("c", t, nullable);

    private static ConversionPlan Plan(CanonicalType source, CanonicalType target, ErrorPolicy policy = ErrorPolicy.Fail, string engine = "ansi", bool nullable = true) =>
        ValueConverter.Plan(Col(source), Col(target, nullable), null, ConversionOptions.ForDialect(DialectCatalog.For(engine), policy));

    private static object? Conv(CanonicalType source, CanonicalType target, object? value, ErrorPolicy policy = ErrorPolicy.Fail, string engine = "ansi") =>
        Plan(source, target, policy, engine).Convert(value);

    private static readonly CanonicalType Str = CanonicalType.String();
    private static CanonicalType K(CanonicalKind k) => CanonicalType.Of(k);

    // ---------------------------------------------------------------- golden table

    public static TheoryData<string, string, object, object> Golden => new()
    {
        { "int32", "int64", 7, 7L },
        { "int64", "int32", 123L, 123 },
        { "int16", "int8", (short)5, (sbyte)5 },
        { "int32(unsigned)", "int64", 4_000_000_000u, 4_000_000_000L },
        { "string", "int32", "42", 42 },
        { "string", "double", "1.5", 1.5 },
        { "double", "decimal(10,2)", 1.25, 1.25m },
        { "int32", "decimal(10,2)", 3, 3m },
        { "decimal(10,4)", "decimal(10,2)", 1.2500m, 1.25m },
        { "string", "boolean", "yes", true },
        { "string", "boolean", "0", false },
        { "int32", "boolean", 2, true },
        { "string", "guid", "6f9619ff-8b86-d011-b42d-00c04fc964ff", Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff") },
        { "guid", "string", Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), "6f9619ff-8b86-d011-b42d-00c04fc964ff" },
        { "date", "timestamp", new DateOnly(2024, 2, 29), new DateTime(2024, 2, 29) },
        { "timestamp", "date", new DateTime(2024, 2, 29), new DateOnly(2024, 2, 29) },
        { "time", "time(3)", new TimeOnly(1, 2, 3, 456), new TimeOnly(1, 2, 3, 456) },
        { "interval", "time", TimeSpan.FromHours(5), new TimeOnly(5, 0, 0) },
        { "timestamp", "timestamp(tz,6)", new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Unspecified), new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero) },
        { "string", "string(5)", "abcde", "abcde" },
        { "int32", "string", 12, "12" },
        { "bool", "string", true, "true" },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Golden_conversions(string source, string target, object input, object expected)
    {
        var src = CanonicalType.Parse(source == "bool" ? "boolean" : source);
        var tgt = CanonicalType.Parse(target);
        var actual = Plan(src, tgt).Convert(input);
        if (expected is DateTime e) { Assert.Equal(e.Ticks, ((DateTime)actual!).Ticks); return; }      // theory data serialisation changes DateTime.Kind
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetType(), actual!.GetType());
    }

    [Fact]
    public void Output_follows_the_clr_contract_for_every_kind()
    {
        (CanonicalType T, object In)[] cases =
        [
            (K(CanonicalKind.Boolean), true), (K(CanonicalKind.Int8), 1), (new(CanonicalKind.Int8, Unsigned: true), 200), (K(CanonicalKind.Int16), 1),
            (K(CanonicalKind.Int32), 1), (K(CanonicalKind.Int64), 1), (K(CanonicalKind.Float), 1.5), (K(CanonicalKind.Double), 1.5), (CanonicalType.Decimal(5, 2), 1.5),
            (Str, "x"), (CanonicalType.Binary(), new byte[] { 1 }), (K(CanonicalKind.Date), new DateTime(2020, 1, 1)), (CanonicalType.Time(), TimeSpan.FromHours(1)),
            (CanonicalType.Timestamp(), DateTime.UnixEpoch), (CanonicalType.Timestamp(tz: true), DateTime.UnixEpoch), (K(CanonicalKind.Guid), Guid.NewGuid()),
            (K(CanonicalKind.Json), "{}"), (K(CanonicalKind.Xml), "<a/>"), (K(CanonicalKind.Interval), TimeSpan.FromSeconds(1)),
        ];
        foreach (var (t, input) in cases)
        {
            var result = Plan(t, t).Convert(input);      // the converter looks at the runtime value; the source type only decides fidelity
            Assert.IsType(ClrContract.TypeOf(t), result);
        }
    }

    // ---------------------------------------------------------------- null and NOT NULL

    [Fact]
    public void Null_stays_null_and_not_null_columns_refuse_it()
    {
        Assert.Null(Plan(Str, Str).Convert(null));
        Assert.Null(Plan(Str, Str).Convert(DBNull.Value));
        var ex = Assert.Throws<ValueConversionException>(() => Plan(Str, Str, nullable: false).Convert(null));
        Assert.Equal(ConversionFailure.NullNotAllowed, ex.Failure);
        Assert.Equal("c", ex.Column);
    }

    [Fact]
    public void Null_policy_into_a_not_null_column_fails()
    {
        var plan = Plan(Str, K(CanonicalKind.Int32), ErrorPolicy.Null, nullable: false);
        Assert.Throws<ValueConversionException>(() => plan.Convert("not a number"));
    }

    // ---------------------------------------------------------------- error policy matrix

    public static TheoryData<ErrorPolicy, object?> IntOverflow => new()
    {
        { ErrorPolicy.Fail, null }, { ErrorPolicy.Null, null }, { ErrorPolicy.Truncate, null }, { ErrorPolicy.Clamp, (short)32767 },
    };

    [Theory]
    [MemberData(nameof(IntOverflow))]
    public void Integer_overflow_by_policy(ErrorPolicy policy, object? expected)
    {
        var plan = Plan(K(CanonicalKind.Int32), K(CanonicalKind.Int16), policy);
        if (policy is ErrorPolicy.Fail or ErrorPolicy.Truncate)
        {
            var ex = Assert.Throws<ValueConversionException>(() => plan.Convert(100_000));
            Assert.Equal(ConversionFailure.OutOfRange, ex.Failure);
        }
        else Assert.Equal(expected, plan.Convert(100_000));
    }

    [Fact]
    public void Negative_overflow_clamps_to_the_minimum()
    {
        Assert.Equal(short.MinValue, Conv(K(CanonicalKind.Int32), K(CanonicalKind.Int16), -100_000, ErrorPolicy.Clamp));
    }

    [Fact]
    public void Unsigned_target_rejects_negative_values()
    {
        var u = new CanonicalType(CanonicalKind.Int32, Unsigned: true);
        Assert.Throws<ValueConversionException>(() => Conv(K(CanonicalKind.Int32), u, -1));
        Assert.Equal(0u, Conv(K(CanonicalKind.Int32), u, -1, ErrorPolicy.Clamp));
    }

    [Fact]
    public void Fractional_part_into_integer_is_precision_loss()
    {
        var ex = Assert.Throws<ValueConversionException>(() => Conv(K(CanonicalKind.Double), K(CanonicalKind.Int32), 1.5));
        Assert.Equal(ConversionFailure.PrecisionLoss, ex.Failure);
        Assert.Equal(1, Conv(K(CanonicalKind.Double), K(CanonicalKind.Int32), 1.9, ErrorPolicy.Truncate));
        Assert.Null(Conv(K(CanonicalKind.Double), K(CanonicalKind.Int32), 1.9, ErrorPolicy.Null));
    }

    // ---------------------------------------------------------------- decimal

    [Fact]
    public void Decimal_overflow_fails_nulls_or_clamps()
    {
        var t = CanonicalType.Decimal(5, 2);   // max 999.99
        var ex = Assert.Throws<ValueConversionException>(() => Conv(CanonicalType.Decimal(10, 2), t, 1000.00m));
        Assert.Equal(ConversionFailure.OutOfRange, ex.Failure);
        Assert.Null(Conv(CanonicalType.Decimal(10, 2), t, 1000.00m, ErrorPolicy.Null));
        Assert.Equal(999.99m, Conv(CanonicalType.Decimal(10, 2), t, 1000.00m, ErrorPolicy.Clamp));
        Assert.Equal(-999.99m, Conv(CanonicalType.Decimal(10, 2), t, -5000m, ErrorPolicy.Clamp));
        Assert.Equal(999.99m, Conv(CanonicalType.Decimal(10, 2), t, 999.99m));
        Assert.Throws<ValueConversionException>(() => Conv(CanonicalType.Decimal(10, 2), t, 1000.00m, ErrorPolicy.Truncate));
    }

    [Fact]
    public void Decimal_scale_loss_is_truncated_toward_zero_when_allowed()
    {
        var t = CanonicalType.Decimal(10, 2);
        var s = CanonicalType.Decimal(10, 4);
        Assert.Throws<ValueConversionException>(() => Conv(s, t, 1.2399m));
        Assert.Equal(1.23m, Conv(s, t, 1.2399m, ErrorPolicy.Truncate));
        Assert.Equal(-1.23m, Conv(s, t, -1.2399m, ErrorPolicy.Truncate));
        Assert.Null(Conv(s, t, 1.2399m, ErrorPolicy.Null));
    }

    [Fact]
    public void Decimal_wider_than_the_clr_decimal_does_not_throw_a_clr_error()
    {
        var t = CanonicalType.Decimal(38, 10);   // 28 integer digits fit decimal, which is the CLR limit
        Assert.Equal(1.5m, Conv(Str, t, "1.5"));
    }

    // ---------------------------------------------------------------- NaN and infinity

    [Fact]
    public void NaN_and_infinity_into_decimal_fail_with_not_finite()
    {
        var t = CanonicalType.Decimal(10, 2);
        var d = K(CanonicalKind.Double);
        Assert.Equal(ConversionFailure.NotFinite, Assert.Throws<ValueConversionException>(() => Conv(d, t, double.NaN)).Failure);
        Assert.Equal(ConversionFailure.NotFinite, Assert.Throws<ValueConversionException>(() => Conv(d, t, double.PositiveInfinity)).Failure);
        Assert.Null(Conv(d, t, double.NaN, ErrorPolicy.Null));
        Assert.Throws<ValueConversionException>(() => Conv(d, t, double.NaN, ErrorPolicy.Clamp));          // NaN cannot be clamped
    }

    [Fact]
    public void Postgres_and_duckdb_keep_non_finite_doubles_other_engines_refuse_them()
    {
        var d = K(CanonicalKind.Double);
        Assert.Equal(double.NaN, Conv(d, d, double.NaN, engine: "postgres"));
        Assert.Equal(double.PositiveInfinity, Conv(d, d, double.PositiveInfinity, engine: "duckdb"));
        Assert.Equal(ConversionFailure.NotFinite, Assert.Throws<ValueConversionException>(() => Conv(d, d, double.NaN, engine: "sqlserver")).Failure);
        Assert.Equal(double.MaxValue, Conv(d, d, double.PositiveInfinity, ErrorPolicy.Clamp, "sqlserver"));
        Assert.Equal(-double.MaxValue, Conv(d, d, double.NegativeInfinity, ErrorPolicy.Clamp, "sqlserver"));
    }

    [Fact]
    public void Text_NaN_parses_for_float_targets()
    {
        Assert.Equal(double.NaN, Conv(Str, K(CanonicalKind.Double), "NaN", engine: "postgres"));
    }

    [Fact]
    public void Double_beyond_float_range_is_out_of_range()
    {
        Assert.Throws<ValueConversionException>(() => Conv(K(CanonicalKind.Double), K(CanonicalKind.Float), 1e300));
        Assert.Equal(float.MaxValue, Conv(K(CanonicalKind.Double), K(CanonicalKind.Float), 1e300, ErrorPolicy.Clamp));
    }

    // ---------------------------------------------------------------- text edge cases

    [Fact]
    public void Nul_bytes_are_rejected_where_the_engine_cannot_store_them()
    {
        var t = CanonicalType.String();
        var ex = Assert.Throws<ValueConversionException>(() => Conv(Str, t, "a\0b", engine: "postgres"));
        Assert.Equal(ConversionFailure.InvalidText, ex.Failure);
        Assert.Equal("ab", Conv(Str, t, "a\0b", ErrorPolicy.Truncate, "postgres"));
        Assert.Null(Conv(Str, t, "a\0b", ErrorPolicy.Null, "postgres"));
        Assert.Equal("a\0b", Conv(Str, t, "a\0b", engine: "sqlserver"));
    }

    [Fact]
    public void Unpaired_surrogates_fail_or_are_replaced()
    {
        var bad = "x\uD800y";
        var ex = Assert.Throws<ValueConversionException>(() => Conv(Str, Str, bad, engine: "postgres"));
        Assert.Equal(ConversionFailure.InvalidText, ex.Failure);
        Assert.Equal("x�y", Conv(Str, Str, bad, ErrorPolicy.Truncate, "postgres"));
        Assert.Equal("�", Conv(Str, Str, "\uDC00", ErrorPolicy.Clamp, "duckdb"));
        Assert.Equal(bad, Conv(Str, Str, bad, engine: "sqlserver"));      // UTF-16 engine stores it as is
    }

    [Fact]
    public void Valid_surrogate_pairs_survive()
    {
        Assert.Equal("a\U0001F600b", Conv(Str, Str, "a\U0001F600b", engine: "postgres"));
    }

    [Fact]
    public void Text_longer_than_the_column_is_cut_without_splitting_a_surrogate_pair()
    {
        var t = CanonicalType.String(3);
        Assert.Throws<ValueConversionException>(() => Conv(Str, t, "abcd"));
        Assert.Equal("abc", Conv(Str, t, "abcd", ErrorPolicy.Truncate));
        Assert.Equal("ab", Conv(Str, t, "ab\U0001F600", ErrorPolicy.Truncate));      // the pair would straddle the limit
        Assert.Null(Conv(Str, t, "abcd", ErrorPolicy.Null));
    }

    [Fact]
    public void Binary_longer_than_the_column_is_cut()
    {
        var t = CanonicalType.Binary(2);
        var b = CanonicalType.Binary();
        Assert.Throws<ValueConversionException>(() => Conv(b, t, new byte[] { 1, 2, 3 }));
        Assert.Equal(new byte[] { 1, 2 }, Conv(b, t, new byte[] { 1, 2, 3 }, ErrorPolicy.Truncate));
    }

    [Fact]
    public void Streams_are_read_into_binary()
    {
        Assert.Equal(new byte[] { 9, 8 }, Conv(CanonicalType.Binary(), CanonicalType.Binary(), new MemoryStream([9, 8])));
    }

    // ---------------------------------------------------------------- date and time

    [Fact]
    public void DateTimeOffset_into_timestamp_keeps_the_utc_instant()
    {
        var v = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(5));
        var r = (DateTime)Conv(CanonicalType.Timestamp(tz: true), CanonicalType.Timestamp(), v)!;
        Assert.Equal(new DateTime(2024, 1, 1, 7, 0, 0), r);
        Assert.Equal(DateTimeKind.Unspecified, r.Kind);
    }

    [Fact]
    public void DateTimeOffset_into_timestamp_tz_is_normalised_to_utc()
    {
        var v = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(5));
        var r = (DateTimeOffset)Conv(CanonicalType.Timestamp(tz: true), CanonicalType.Timestamp(tz: true), v)!;
        Assert.Equal(TimeSpan.Zero, r.Offset);
        Assert.Equal(v.UtcDateTime, r.UtcDateTime);
    }

    [Fact]
    public void Zone_less_timestamp_into_tz_is_read_as_utc_and_local_is_converted()
    {
        var wall = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var r = (DateTimeOffset)Conv(CanonicalType.Timestamp(), CanonicalType.Timestamp(tz: true), wall)!;
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero), r);
        var local = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var l = (DateTimeOffset)Conv(CanonicalType.Timestamp(), CanonicalType.Timestamp(tz: true), local)!;
        Assert.Equal(local.ToUniversalTime(), l.UtcDateTime);
    }

    [Fact]
    public void Text_with_an_offset_into_timestamp_tz()
    {
        var r = (DateTimeOffset)Conv(Str, CanonicalType.Timestamp(tz: true), "2024-03-01T10:00:00+02:00")!;
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 8, 0, 0, TimeSpan.Zero), r);
    }

    [Fact]
    public void Time_precision_is_enforced()
    {
        var t3 = CanonicalType.Time(3);
        var v = new TimeOnly(1, 2, 3).Add(TimeSpan.FromTicks(1_234_567));     // .1234567 s
        Assert.Equal(ConversionFailure.PrecisionLoss, Assert.Throws<ValueConversionException>(() => Conv(CanonicalType.Time(7), t3, v)).Failure);
        Assert.Equal(new TimeOnly(1, 2, 3, 123), Conv(CanonicalType.Time(7), t3, v, ErrorPolicy.Truncate));
        Assert.Equal(v, Conv(CanonicalType.Time(7), CanonicalType.Time(7), v));
        Assert.Equal(new TimeOnly(1, 2, 3), Conv(CanonicalType.Time(7), CanonicalType.Time(0), v, ErrorPolicy.Truncate));
    }

    [Fact]
    public void Timestamp_precision_is_enforced_for_both_zone_variants()
    {
        var fine = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(1_234_567);
        Assert.Throws<ValueConversionException>(() => Conv(CanonicalType.Timestamp(7), CanonicalType.Timestamp(6), fine));
        Assert.Equal(new DateTime(2024, 1, 1).AddTicks(1_234_560), Conv(CanonicalType.Timestamp(7), CanonicalType.Timestamp(6), fine, ErrorPolicy.Truncate));
        var tz = (DateTimeOffset)Conv(CanonicalType.Timestamp(7), CanonicalType.Timestamp(3, tz: true), fine, ErrorPolicy.Truncate)!;
        Assert.Equal(new DateTime(2024, 1, 1).AddTicks(1_230_000), tz.UtcDateTime);
    }

    [Fact]
    public void Time_outside_a_day_is_out_of_range()
    {
        var t = CanonicalType.Time(7);
        var i = K(CanonicalKind.Interval);
        Assert.Equal(ConversionFailure.OutOfRange, Assert.Throws<ValueConversionException>(() => Conv(i, t, TimeSpan.FromHours(25))).Failure);
        Assert.Equal(new TimeOnly(TimeSpan.TicksPerDay - 1), Conv(i, t, TimeSpan.FromHours(25), ErrorPolicy.Clamp));
        Assert.Equal(TimeOnly.MinValue, Conv(i, t, TimeSpan.FromHours(-1), ErrorPolicy.Clamp));
    }

    [Fact]
    public void Timestamp_with_time_of_day_into_date_is_precision_loss()
    {
        var v = new DateTime(2024, 1, 2, 3, 4, 5);
        Assert.Throws<ValueConversionException>(() => Conv(CanonicalType.Timestamp(), K(CanonicalKind.Date), v));
        Assert.Equal(new DateOnly(2024, 1, 2), Conv(CanonicalType.Timestamp(), K(CanonicalKind.Date), v, ErrorPolicy.Truncate));
    }

    // ---------------------------------------------------------------- invalid text

    [Theory]
    [InlineData("int32")]
    [InlineData("decimal(10,2)")]
    [InlineData("boolean")]
    [InlineData("guid")]
    [InlineData("date")]
    [InlineData("timestamp")]
    [InlineData("time")]
    [InlineData("double")]
    public void Unparsable_text_fails_with_invalid_format_and_never_prints_the_value(string target)
    {
        var secret = "SECRET-VALUE-XYZ";
        var ex = Assert.Throws<ValueConversionException>(() => Conv(Str, CanonicalType.Parse(target), secret));
        Assert.Equal(ConversionFailure.InvalidFormat, ex.Failure);
        Assert.DoesNotContain(secret, ex.Message);
        Assert.Contains("`c`", ex.Message);
    }

    [Fact]
    public void Errors_for_every_failure_kind_never_contain_the_value()
    {
        const string marker = "1234567890123";
        var cases = new Action[]
        {
            () => Conv(K(CanonicalKind.Int64), K(CanonicalKind.Int16), long.Parse(marker)),
            () => Conv(CanonicalType.Decimal(20, 0), CanonicalType.Decimal(5, 0), decimal.Parse(marker)),
            () => Conv(Str, CanonicalType.String(3), marker),
        };
        foreach (var a in cases) Assert.DoesNotContain(marker, Assert.Throws<ValueConversionException>(a).Message);
    }

    // ---------------------------------------------------------------- planning

    [Fact]
    public void Unsupported_pairs_fail_at_plan_time()
    {
        Assert.Equal(ConversionFailure.UnsupportedType, Assert.Throws<ValueConversionException>(() => Plan(CanonicalType.Binary(), K(CanonicalKind.Int32))).Failure);
        Assert.Throws<ValueConversionException>(() => Plan(K(CanonicalKind.List), Str));
        Assert.Throws<ValueConversionException>(() => Plan(Str, CanonicalType.Unmappable));
    }

    [Fact]
    public void Plan_reports_fidelity_and_takes_the_worse_of_rule_and_dialect()
    {
        Assert.Equal(Fidelity.Exact, Plan(K(CanonicalKind.Int32), K(CanonicalKind.Int32)).Fidelity);
        Assert.Equal(Fidelity.Widened, Plan(K(CanonicalKind.Int32), K(CanonicalKind.Int64)).Fidelity);
        Assert.Equal(Fidelity.Lossy, Plan(K(CanonicalKind.Int64), K(CanonicalKind.Int32)).Fidelity);
        Assert.Equal(Fidelity.Lossy, Plan(CanonicalType.Timestamp(tz: true), CanonicalType.Timestamp()).Fidelity);
        var u = new CanonicalType(CanonicalKind.Int64, Unsigned: true);
        var viaDialect = ValueConverter.Plan(Col(u), Col(u), DialectCatalog.For(EngineId.SqlServer));
        Assert.True(viaDialect.Fidelity >= Fidelity.Widened, "SQL Server has no unsigned bigint, so the dialect verdict must not be Exact");
    }

    [Fact]
    public void Plan_uses_the_dialect_for_engine_facts()
    {
        var p = ValueConverter.Plan(Col(Str), Col(Str), DialectCatalog.For(EngineId.Postgres));
        Assert.True(p.Options.RejectNul);
        Assert.Throws<ValueConversionException>(() => p.Convert("a\0"));
    }

    [Fact]
    public void Every_canonical_kind_has_a_plan_to_itself_on_every_builtin_dialect()
    {
        string[] types = ["boolean", "int8", "int16", "int32", "int64", "decimal(18,2)", "float", "double", "string(20)", "binary(8)", "date", "time(3)", "timestamp(6)", "timestamp(tz,6)", "guid", "json", "xml", "interval"];
        foreach (var id in DialectCatalog.Builtin.Ids)
        {
            var d = DialectCatalog.Builtin.Get(id);
            foreach (var t in types)
            {
                var type = CanonicalType.Parse(t);
                if (!d.ColumnType(type).IsMapped) continue;            // the dialect says no native type: planning must refuse, tested below
                var plan = ValueConverter.Plan(Col(type), Col(type), d);
                Assert.NotNull(plan);
            }
        }
    }

    [Fact]
    public void Plan_refuses_a_target_type_the_dialect_cannot_hold()
    {
        foreach (var id in DialectCatalog.Builtin.Ids)
        {
            var d = DialectCatalog.Builtin.Get(id);
            foreach (var k in new[] { "interval", "xml", "json", "guid" })
            {
                var type = CanonicalType.Parse(k);
                if (d.ColumnType(type).IsMapped) continue;
                Assert.Throws<ValueConversionException>(() => ValueConverter.Plan(Col(Str), Col(type), d));
            }
        }
    }

    [Fact]
    public void PlanAll_applies_per_column_policies_and_ConvertBatch_converts_rows()
    {
        var src = new TransferSchema([new("a", Str), new("b", Str)]);
        var tgt = new TransferSchema([new("a", K(CanonicalKind.Int32)), new("b", CanonicalType.String(2))]);
        var plans = ValueConverter.PlanAll(src, tgt, null, null, new Dictionary<string, ErrorPolicy> { ["b"] = ErrorPolicy.Truncate });
        var batch = new RowDataBatch(src, [["1", "xyz"], [null, "q"]]);
        var converted = ValueConverter.ConvertBatch(batch, plans, tgt);
        Assert.Equal(2, converted.Rows);
        Assert.Equal(1, converted.RowValues[0][0]);
        Assert.Equal("xy", converted.RowValues[0][1]);
        Assert.Null(converted.RowValues[1][0]);
        var bad = new RowDataBatch(src, [["not a number", "x"]]);
        Assert.Throws<ValueConversionException>(() => ValueConverter.ConvertBatch(bad, plans, tgt));
    }

    [Fact]
    public void Plans_are_reusable_across_many_values()
    {
        var plan = Plan(Str, K(CanonicalKind.Int32));
        for (var i = 0; i < 1000; i++) Assert.Equal(i, plan.Convert(i.ToString()));
    }
}
