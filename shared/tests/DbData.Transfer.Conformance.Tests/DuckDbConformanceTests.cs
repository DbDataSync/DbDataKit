using DbData.Transfer.Conformance;
using Xunit;

namespace DbData.Transfer.Conformance.Tests;

public class DuckDbConformanceTests
{
    private static readonly ConformanceEngine Engine = DuckDbEngine.Create();

    public static IEnumerable<TheoryDataRow<string>> CaseNames() =>
        ConformanceRunner.CasesFor(DuckDbEngine.Create()).Select(c => new TheoryDataRow<string>(c.Name));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Strategy_matches_the_expected_result_and_is_idempotent(string name)
    {
        var c = StrategyCases.All.Single(x => x.Name == name);
        var result = await ConformanceRunner.RunAsync(Engine, c, TestContext.Current.CancellationToken);
        Assert.True(result.Passed, string.Join("\n", result.Differences));
    }

    [Fact]
    public void Every_shared_strategy_has_a_case_on_the_reference_engine()
    {
        var covered = ConformanceRunner.CasesFor(Engine).Select(c => c.Strategy).ToHashSet();
        foreach (var s in DbData.Transfer.StrategyMatrix.Builtin.Strategies) Assert.Contains(s, covered);
    }
}
