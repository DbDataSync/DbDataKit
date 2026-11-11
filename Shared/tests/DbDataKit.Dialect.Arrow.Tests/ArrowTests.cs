using Apache.Arrow.Types;
using DbDataKit.Dialect;
using DbDataKit.Dialect.Arrow;
using Xunit;

namespace DbDataKit.Dialect.Arrow.Tests;

public class ArrowTests
{
    [Theory]
    [InlineData("boolean", "bool")]
    [InlineData("int8", "int8")]
    [InlineData("int8(unsigned)", "uint8")]
    [InlineData("int64(unsigned)", "uint64")]
    [InlineData("float", "float")]
    [InlineData("double", "double")]
    [InlineData("decimal(18,2)", "decimal128(18, 2)")]
    [InlineData("date", "date32")]
    [InlineData("time(6)", "time64")]
    [InlineData("timestamp(6)", "timestamp")]
    [InlineData("binary", "binary")]
    [InlineData("string(unicode)", "utf8")]
    [InlineData("interval", "duration")]
    public void Exact_mappings(string canonical, string arrowName)
    {
        var m = ArrowTypeMap.ToArrow(CanonicalType.Parse(canonical));
        Assert.Equal(Fidelity.Exact, m.Fidelity);
        Assert.StartsWith(arrowName.Split('(')[0], m.Result!.Name);
    }

    [Fact]
    public void Timestamps_are_microseconds_and_utc_when_zone_aware()
    {
        var plain = (TimestampType)ArrowTypeMap.ToArrow(CanonicalType.Timestamp(6)).Result!;
        var tz = (TimestampType)ArrowTypeMap.ToArrow(CanonicalType.Timestamp(3, tz: true)).Result!;
        Assert.Equal(TimeUnit.Microsecond, plain.Unit);
        Assert.True(string.IsNullOrEmpty(plain.Timezone));
        Assert.Equal("UTC", tz.Timezone);
        Assert.Equal(Fidelity.Lossy, ArrowTypeMap.ToArrow(CanonicalType.Timestamp(9)).Fidelity);
    }

    [Fact]
    public void Decimal_precision_guard()
    {
        Assert.IsType<Decimal128Type>(ArrowTypeMap.ToArrow(CanonicalType.Decimal(38, 10)).Result);
        var wide = ArrowTypeMap.ToArrow(CanonicalType.Decimal(50, 10));
        Assert.IsType<Decimal256Type>(wide.Result);
        Assert.Equal(Fidelity.Widened, wide.Fidelity);
        Assert.Equal(Fidelity.Unmappable, ArrowTypeMap.ToArrow(CanonicalType.Decimal(77, 0)).Fidelity);
        var none = ArrowTypeMap.ToArrow(CanonicalType.Of(CanonicalKind.Decimal));
        Assert.Equal(Fidelity.Lossy, none.Fidelity);
        AssertDec(none.Result, 38, 9);
        Assert.Equal(Fidelity.Lossy, ArrowTypeMap.ToArrow(CanonicalType.Decimal(5, 9)).Fidelity);
    }

    [Fact]
    public void Text_carried_kinds_and_unmappable()
    {
        foreach (var k in new[] { CanonicalKind.Guid, CanonicalKind.Json, CanonicalKind.Xml })
        {
            var m = ArrowTypeMap.ToArrow(CanonicalType.Of(k));
            Assert.Equal(Fidelity.Widened, m.Fidelity);
            Assert.IsType<StringType>(m.Result);
        }
        Assert.Equal(Fidelity.Unmappable, ArrowTypeMap.ToArrow(CanonicalType.Of(CanonicalKind.List)).Fidelity);
        Assert.Equal(Fidelity.Unmappable, ArrowTypeMap.ToArrow(CanonicalType.Unmappable).Fidelity);
        Assert.Equal(Fidelity.Widened, ArrowTypeMap.ToArrow(CanonicalType.String(50)).Fidelity);
    }

    [Fact]
    public void Every_mapped_canonical_type_round_trips_through_arrow()
    {
        foreach (var c in new[] { "boolean", "int8", "int8(unsigned)", "int16", "int16(unsigned)", "int32", "int32(unsigned)", "int64", "int64(unsigned)",
                                  "float", "double", "decimal(18,2)", "decimal(50,5)", "string(unicode)", "binary", "date", "time(6)", "timestamp(6)", "timestamp(tz,6)", "interval" })
        {
            var type = CanonicalType.Parse(c);
            var arrow = ArrowTypeMap.ToArrow(type);
            Assert.True(arrow.IsMapped, c);
            var back = ArrowTypeMap.FromArrow(arrow.Result!);
            Assert.Equal(type, back.Result);
        }
    }

    [Fact]
    public void FromArrow_other_types()
    {
        Assert.Equal(CanonicalType.Time(3), ArrowTypeMap.FromArrow(new Time32Type(TimeUnit.Millisecond)).Result);
        Assert.Equal(CanonicalType.Timestamp(9), ArrowTypeMap.FromArrow(new TimestampType(TimeUnit.Nanosecond, (string?)null)).Result);
        Assert.Equal(CanonicalKind.List, ArrowTypeMap.FromArrow(new ListType(Int32Type.Default)).Result!.Kind);
        Assert.Equal(Fidelity.Lossy, ArrowTypeMap.FromArrow(new FixedSizeBinaryType(16)).Fidelity);
        Assert.Equal(Fidelity.Unmappable, ArrowTypeMap.FromArrow(NullType.Default).Fidelity);
    }

    [Theory]
    [InlineData(typeof(bool), null, typeof(BooleanType))]
    [InlineData(typeof(sbyte), null, typeof(Int8Type))]
    [InlineData(typeof(byte), null, typeof(UInt8Type))]
    [InlineData(typeof(ushort), null, typeof(UInt16Type))]
    [InlineData(typeof(uint), null, typeof(UInt32Type))]
    [InlineData(typeof(ulong), null, typeof(UInt64Type))]
    [InlineData(typeof(int?), null, typeof(Int32Type))]
    [InlineData(typeof(float), null, typeof(FloatType))]
    [InlineData(typeof(DateTime), "date", typeof(Date32Type))]
    [InlineData(typeof(DateTime), "datetime2", typeof(TimestampType))]
    [InlineData(typeof(DateTimeOffset), null, typeof(TimestampType))]
    [InlineData(typeof(DateOnly), null, typeof(Date32Type))]
    [InlineData(typeof(TimeSpan), null, typeof(Time64Type))]
    [InlineData(typeof(TimeOnly), null, typeof(Time64Type))]
    [InlineData(typeof(byte[]), null, typeof(BinaryType))]
    [InlineData(typeof(Guid), null, typeof(StringType))]
    [InlineData(typeof(string), null, typeof(StringType))]
    [InlineData(typeof(object), null, typeof(StringType))]
    public void Matches_proxy_clr_decisions(Type clr, string? typeName, Type arrow)
        => Assert.IsType(arrow, ClrCanonicalMap.ToArrow(clr, typeName).Result);

    private static void AssertDec(IArrowType? t, int p, int s) { var d = Assert.IsType<Decimal128Type>(t); Assert.Equal((p, s), (d.Precision, d.Scale)); }

    [Fact]
    public void Proxy_decimal_fallbacks()
    {
        AssertDec(ClrCanonicalMap.ToArrow(typeof(decimal), null, 10, 2).Result, 10, 2);
        AssertDec(ClrCanonicalMap.ToArrow(typeof(decimal), null, 10, 99).Result, 10, 9);
        var none = ClrCanonicalMap.ToArrow(typeof(decimal));
        AssertDec(none.Result, 38, 9);
        Assert.Equal(Fidelity.Lossy, none.Fidelity);
        Assert.Equal("UTC", ((TimestampType)ClrCanonicalMap.ToArrow(typeof(DateTimeOffset)).Result!).Timezone);
        Assert.Equal(Fidelity.Lossy, ClrCanonicalMap.ToArrow(typeof(object)).Fidelity);
    }
}
