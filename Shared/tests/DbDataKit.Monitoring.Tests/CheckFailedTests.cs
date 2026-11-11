using DbDataKit.Monitoring;
using Xunit;

namespace DbDataKit.Monitoring.Tests;

// plan 23a FQ16: a failing check is a finding with the dataset
public class CheckFailedTests
{
    private const string Orders = "sqlserver://db/Sales.dbo.Orders";
    private const string Other = "sqlserver://db/Sales.dbo.Other";

    [Fact]
    public void A_failing_check_is_a_finding_that_names_the_dataset_and_the_numbers_and_a_block_makes_it_an_error()
    {
        var health = new HealthEvaluator().EvaluateChecks(
        [
            new CheckReading(Orders, "orders-not-null", "fail", Blocking: true, Measured: 3, Threshold: 0),
            new CheckReading(Orders, "orders-range", "fail", Blocking: false, Measured: 7, Threshold: 5),
            new CheckReading(Orders, "orders-unique", "pass", Blocking: false),
        ]);
        var one = Assert.Single(health);
        Assert.Equal(Orders, one.JobId);
        Assert.Equal(2, one.Findings.Count);
        Assert.All(one.Findings, f => Assert.Equal(HealthRule.CheckFailed, f.Rule));
        Assert.Equal(Severity.Error, one.Findings[0].Severity);
        Assert.Contains("orders-not-null", one.Findings[0].Message);
        Assert.Contains("measured 3, allowed 0", one.Findings[0].Message);
        Assert.Contains("blocks what depends", one.Findings[0].Message);
        Assert.Equal(Severity.Warning, one.Findings[1].Severity);
        Assert.DoesNotContain("blocks", one.Findings[1].Message);
        Assert.True(one.Has(HealthRule.CheckFailed));
    }

    [Fact]
    public void A_dataset_whose_checks_all_pass_is_not_listed_and_a_check_that_could_not_run_only_warns_unless_it_blocks()
    {
        var evaluator = new HealthEvaluator();
        Assert.Empty(evaluator.EvaluateChecks([new CheckReading(Orders, "a", "pass", false), new CheckReading(Other, "b", "PASS", false)]));
        var errors = evaluator.EvaluateChecks([new CheckReading(Orders, "a", "error", Blocking: false), new CheckReading(Other, "b", "error", Blocking: true)]);
        Assert.Equal(2, errors.Count);
        Assert.Equal(Severity.Warning, errors.Single(h => h.JobId == Orders).Findings.Single().Severity);
        Assert.Equal(Severity.Error, errors.Single(h => h.JobId == Other).Findings.Single().Severity);
        Assert.Contains("could not run", errors.Single(h => h.JobId == Orders).Findings[0].Message);
    }
}
