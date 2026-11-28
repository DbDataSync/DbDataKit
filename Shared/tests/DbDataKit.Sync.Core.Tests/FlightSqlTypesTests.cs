using DbDataKit.Sync.Core.Sql;

namespace DbDataKit.Sync.Core.Tests;

/// <summary>A Flight SQL column's type is read as ANSI first, then as the fronted engine's own, by the driver and the offline check alike.</summary>
public sealed class FlightSqlTypesTests
{
    [Fact]
    public void An_ansi_name_is_read_as_ansi_whatever_engine_is_behind_the_server()
    {
        var read = FlightSqlTypes.Read(MsSqlDialect.Instance, "double precision");

        Assert.True(read.IsMapped);
        Assert.Equal(AnsiDialect.Instance.ReadColumnType("double precision").Result, read.Result);
    }

    [Fact]
    public void A_name_ansi_does_not_have_is_read_as_the_fronted_engines_own()
    {
        Assert.False(AnsiDialect.Instance.ReadColumnType("uniqueidentifier").IsMapped);

        var read = FlightSqlTypes.Read(MsSqlDialect.Instance, "uniqueidentifier");

        Assert.True(read.IsMapped);
        Assert.Equal(MsSqlDialect.Instance.ReadColumnType("uniqueidentifier").Result, read.Result);
    }

    [Fact]
    public void With_no_engine_named_a_name_ansi_does_not_have_stays_unmapped()
    {
        Assert.False(FlightSqlTypes.Read(AnsiDialect.Instance, "uniqueidentifier").IsMapped);
    }
}
