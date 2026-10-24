using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>Sync keeps the bare PostgreSQL type names its value binding switches on (plan 13, A11).</summary>
public sealed class PostgresNativeTypeTests
{
    [Theory]
    [InlineData("timestamp(3) without time zone", "timestamp without time zone")]
    [InlineData("timestamp(6) with time zone", "timestamp with time zone")]
    [InlineData("time(4) without time zone", "time without time zone")]
    [InlineData("interval(2)", "interval")]
    [InlineData("integer[]", "array")]
    [InlineData("character varying(50)", "character varying(50)")]
    [InlineData("numeric(18,2)", "numeric(18,2)")]
    [InlineData("timestamp without time zone", "timestamp without time zone")]
    public void A_precision_is_dropped_from_a_time_type_and_any_array_is_array(string reported, string expected) =>
        Assert.Equal(expected, PostgresCatalog.NativeType(reported));
}
