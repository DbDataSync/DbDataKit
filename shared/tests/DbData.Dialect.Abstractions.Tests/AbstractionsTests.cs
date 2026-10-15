using DbData.Dialect;
using Xunit;

namespace DbData.Dialect.Abstractions.Tests;

public class AbstractionsTests
{
    [Theory]
    [InlineData("int32")]
    [InlineData("int64(unsigned)")]
    [InlineData("string")]
    [InlineData("string(unicode,50)")]
    [InlineData("binary(16)")]
    [InlineData("decimal(18,2)")]
    [InlineData("decimal(38)")]
    [InlineData("timestamp(tz,6)")]
    [InlineData("time(3)")]
    [InlineData("guid")]
    [InlineData("list")]
    public void CanonicalType_text_round_trips(string text)
        => Assert.Equal(text, CanonicalType.Parse(text).ToString());

    [Theory]
    [InlineData("nonsense")]
    [InlineData("string(x)")]
    [InlineData("int32(5)")]
    [InlineData("decimal(1,2,3)")]
    [InlineData("string(5")]
    public void CanonicalType_rejects_garbage(string text)
    {
        Assert.False(CanonicalType.TryParse(text, out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void CanonicalType_is_a_value_record()
    {
        Assert.Equal(CanonicalType.Decimal(10, 2), CanonicalType.Parse("decimal(10,2)"));
        Assert.NotEqual(CanonicalType.Timestamp(6), CanonicalType.Timestamp(6, tz: true));
    }

    [Fact]
    public void EngineId_normalises_case()
    {
        Assert.Equal(new EngineId("SqlServer"), EngineId.SqlServer);
        Assert.Throws<ArgumentException>(() => new EngineId(" "));
    }

    [Theory]
    [InlineData("16.0.1000", 16, 0, 1000)]
    [InlineData("15.4 (Debian)", 15, 4, 0)]
    [InlineData("8", 8, 0, 0)]
    public void EngineVersion_parses(string text, int a, int b, int c)
    {
        Assert.True(EngineVersion.TryParse(text, out var v));
        Assert.Equal(new EngineVersion(a, b, c), v);
    }

    [Fact]
    public void EngineVersion_orders_and_rejects_text()
    {
        Assert.True(new EngineVersion(12).CompareTo(new EngineVersion(11, 9)) > 0);
        Assert.False(EngineVersion.TryParse("abc", out _));
    }

    [Fact]
    public void TypeMapping_unmappable_cannot_be_required()
    {
        var m = TypeMapping<string>.Unmappable("no");
        Assert.False(m.IsMapped);
        Assert.Throws<InvalidOperationException>(() => m.Require());
        Assert.Equal("x", TypeMapping<string>.Exact("x").Require());
    }

    [Fact]
    public void IdentifierRules_fold()
    {
        var r = new IdentifierRules(IdentifierCase.Upper, 30, new HashSet<string> { "SELECT" });
        Assert.Equal("ABC", r.FoldCase("abc"));
        Assert.True(r.IsReserved("select"));
    }
}
