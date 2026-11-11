using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataKit.Connections.FlightSql.Tests;

public class ArrowValueReaderTests
{
    [Fact]
    public void Values_are_read_at_their_own_width_and_null_is_null()
    {
        var longs = new Int64Array.Builder().Append(7).AppendNull().Build();
        Assert.Equal(7L, ArrowValueReader.Value(longs, 0));
        Assert.Null(ArrowValueReader.Value(longs, 1));
        Assert.Equal("a", ArrowValueReader.Value(new StringArray.Builder().Append("a").Build(), 0));
        Assert.Equal(new DateOnly(2024, 2, 29), ArrowValueReader.Value(new Date32Array.Builder().Append(new DateTime(2024, 2, 29)).Build(), 0));
    }

    [Fact]
    public void A_timestamp_without_a_zone_is_a_clock_reading_and_one_with_a_zone_an_instant()
    {
        var instant = new DateTimeOffset(2024, 5, 1, 12, 30, 0, TimeSpan.Zero);
        var plain = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, (string?)null)).Append(instant).Build();
        var zoned = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC")).Append(instant).Build();
        Assert.Equal(DateTimeKind.Unspecified, Assert.IsType<DateTime>(ArrowValueReader.Value(plain, 0)).Kind);
        Assert.Equal(instant, Assert.IsType<DateTimeOffset>(ArrowValueReader.Value(zoned, 0)));
    }

    [Fact]
    public void A_type_with_no_scalar_value_is_refused_by_its_arrow_name()
    {
        var list = new ListArray.Builder(Int32Type.Default).Append().Build();
        Assert.Equal("list", Assert.Throws<ArrowTypeNotCarriedException>(() => ArrowValueReader.Value(list, 0)).ArrowType);
    }

    [Fact]
    public void A_column_type_is_named_as_information_schema_names_it()
    {
        Assert.Equal(new ArrowColumnType("decimal", 18, 4), ArrowValueReader.ColumnType(new Field("d", new Decimal128Type(18, 4), true)));
        Assert.Equal("timestamp with time zone", ArrowValueReader.ColumnType(new Field("t", new TimestampType(TimeUnit.Microsecond, "UTC"), true)).Name);
        Assert.Equal("character varying", ArrowValueReader.ColumnType(new Field("s", new DictionaryType(Int32Type.Default, StringType.Default, false), true)).Name);
        Assert.Equal("list", ArrowValueReader.ColumnType(new Field("l", new ListType(Int32Type.Default), true)).Name);
    }
}
