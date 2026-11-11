namespace DbDataKit.Runner.Packaging.Tests;

public sealed class IntegrationTesterTests
{
    [Fact]
    public async Task A_test_script_runs_the_job_in_the_simulator_and_passes()
    {
        using var s = new Scratch();
        s.Standard();
        var result = await IntegrationTester.TestAsync(s.Project, s.Options());
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        var test = Assert.Single(result.Tests);
        Assert.True(test.Passed, test.Message);
        Assert.Equal("tests/greet.csx", test.File);
    }

    [Fact]
    public async Task A_failing_check_fails_the_test_with_its_message_and_the_others_still_run()
    {
        using var s = new Scratch();
        s.Standard();
        s.Write("tests/a-wrong.csx", "var sim = new RunSimulator<Greeting.Greet>();\nawait sim.RunAsync();\nEqual(99L, sim.Counted(Greeting.GreetCounters.Letters), \"letters\");\n");
        var result = await IntegrationTester.TestAsync(s.Project, s.Options());
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Tests.Count);
        var wrong = Assert.Single(result.Tests, t => !t.Passed);
        Assert.Equal("tests/a-wrong.csx", wrong.File);
        Assert.Contains("letters Expected 99, got 12", wrong.Message);
        Assert.Contains(result.Tests, t => t.Passed && t.File == "tests/greet.csx");
        Assert.Contains(result.Diagnostics, d => d.Code == "DBD-INT-TEST" && d.File == "tests/a-wrong.csx");
    }

    [Fact]
    public async Task A_test_that_does_not_compile_says_where()
    {
        using var s = new Scratch();
        s.Standard("var x = ;\n");
        var result = await IntegrationTester.TestAsync(s.Project, s.Options());
        var test = Assert.Single(result.Tests);
        Assert.False(test.Passed);
        Assert.Contains("tests/greet.csx:1:", test.Message);
    }

    [Fact]
    public async Task A_project_with_no_tests_builds_and_says_it_has_none()
    {
        using var s = new Scratch();
        s.Standard(test: null);
        var result = await IntegrationTester.TestAsync(s.Project, s.Options());
        Assert.True(result.Succeeded);
        Assert.Empty(result.Tests);
        Assert.Contains(result.Diagnostics, d => d.Code == "DBD-INT-NOTESTS");
    }

    [Fact]
    public async Task A_project_that_does_not_build_is_not_tested()
    {
        using var s = new Scratch();
        s.Standard();
        s.Write("src/greet.csx", "namespace X { class Broken : Missing { } }\n");
        var result = await IntegrationTester.TestAsync(s.Project, s.Options());
        Assert.False(result.Succeeded);
        Assert.Empty(result.Tests);
    }
}
