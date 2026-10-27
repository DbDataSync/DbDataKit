using DbDataKit.Semantic.Ssas;

namespace DbDataKit.Semantic.Tests;

/// <summary>Agent-written text for an Analysis Services source must be a DAX query: the command behind it also runs MDX, DMX and DMV queries (plan 19, J22).</summary>
public class SsasExecutorGuardTests
{
    [Theory]
    [InlineData("EVALUATE 'Customer'")]
    [InlineData("  \n evaluate row(\"a\", 1)")]
    [InlineData("DEFINE MEASURE 'Budget'[x] = 1 EVALUATE ROW(\"a\", [x])")]
    [InlineData("// a comment\nEVALUATE 'Customer'")]
    [InlineData("-- another\n/* block */ EVALUATE 'Customer'")]
    public void ADaxQueryIsAllowed(string text) => Assert.True(SsasExecutor.IsDaxQuery(text));

    [Theory]
    [InlineData("SELECT [Measures].[Budget Amount] ON 0 FROM [Model]")]
    [InlineData("SELECT * FROM $SYSTEM.DISCOVER_SESSIONS")]
    [InlineData("{\"refresh\":{\"type\":\"full\",\"objects\":[{\"database\":\"qis_budget\"}]}}")]
    [InlineData("EVALUATED 'Customer'")]
    [InlineData("/* unterminated EVALUATE 'Customer'")]
    [InlineData("")]
    public void AnythingElseIsRefused(string text) => Assert.False(SsasExecutor.IsDaxQuery(text));

    [Fact]
    public void NativeTextThatIsNotDaxIsRefusedBeforeAnyConnection()
    {
        var executor = new SsasExecutor("Data Source=nowhere", null!);
        var e = Assert.Throws<NotSupportedException>(() =>
            executor.ExecuteNativeText("ssas", new DbDataKit.Semantic.Abstractions.NativeQuery("dax", "SELECT [Measures].[x] ON 0 FROM [Model]"), 10));
        Assert.Contains("DAX query", e.Message);
    }
}
