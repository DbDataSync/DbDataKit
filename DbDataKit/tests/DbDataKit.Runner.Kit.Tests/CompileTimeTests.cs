using System.Runtime.CompilerServices;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>Plan 28b: what the types promise at compile time (a wrong name or type is an error, a step with no recovery has nothing to run) and what the kit's analyzer adds (duplicate names, a step inside a step).</summary>
public sealed class CompileTimeTests
{
    private static string[] ErrorIds(string body) => [.. RoslynHelper.Errors(RoslynHelper.Job(body)).Select(d => d.Id)];

    private static string Messages(string body) => RoslynHelper.Describe(RoslynHelper.Errors(RoslynHelper.Job(body)));

    [Fact]
    public void A_correct_job_compiles_without_a_diagnostic()
    {
        var source = RoslynHelper.Job("""
            Progress.At = DateTimeOffset.UtcNow;
            string packed = await Step(S.Pack, new DateOnly(2026, 11, 7)).Repeatable().RunAsync(ct => Task.FromResult("done"), ct);
            PayloadRef file = await Step(S.Dump, 1).Ask().StreamAsync(async (sink, ct) => await sink.WriteAsync(new byte[1], ct), ct);
            string? found = await Step(S.Pack, new DateOnly(2026, 11, 8)).Check(ct => Task.FromResult<string?>(null)).RunAsync(ct => Task.FromResult("x"), ct);
            await ForEachAsync(S.Item, new[] { 1, 2 }, 2, (n, ct) => Task.CompletedTask, ct);
            Count(S.Rows, 3);
            """);

        Assert.Empty(RoslynHelper.Errors(source));
    }

    [Fact]
    public void A_progress_value_of_the_wrong_type_or_a_name_that_does_not_exist_is_a_compile_error()
    {
        Assert.Equal(["CS0029"], ErrorIds("""Progress.At = "yesterday";"""));                       // a string for a DateTimeOffset
        Assert.Equal(["CS1061"], ErrorIds("""var x = Progress.Nope;"""));                           // a member the Progress class does not have
        Assert.Equal(["CS0029"], ErrorIds("""int cursor = Progress.Cursor!;"""));                  // the type of a state value
    }

    [Fact]
    public void A_misspelled_step_a_wrong_input_a_wrong_result_or_a_wrong_counter_is_a_compile_error()
    {
        Assert.Equal(["CS0117"], ErrorIds("""await Step(S.Packk, new DateOnly()).Repeatable().RunAsync(ct => Task.FromResult("x"), ct);"""));
        Assert.Equal(["CS1503"], ErrorIds("""await Step(S.Pack, 5).Repeatable().RunAsync(ct => Task.FromResult("x"), ct);"""));             // the input type of the step
        Assert.Contains(ErrorIds("""await Step(S.Pack, new DateOnly()).Repeatable().RunAsync(ct => Task.FromResult(5), ct);"""), id => id is "CS1662" or "CS4010" or "CS0029");   // the result type
        Assert.Equal(["CS0029"], ErrorIds("""int packed = await Step(S.Pack, new DateOnly()).Repeatable().RunAsync(ct => Task.FromResult("x"), ct);"""));
        Assert.Equal(["CS1503"], ErrorIds("""Count("rows", 1);"""));                                    // a counter is declared, not a string
        Assert.Equal(["CS1503"], ErrorIds("""await ForEachAsync(S.Item, new[] { "a" }, 1, (n, ct) => Task.CompletedTask, ct);"""));      // the unit's item type
    }

    [Fact]
    public void A_step_with_no_recovery_chosen_has_nothing_to_run_and_a_stream_has_only_its_own_method()
    {
        Assert.Equal(["CS1061"], ErrorIds("""await Step(S.Pack, new DateOnly()).RunAsync(ct => Task.FromResult("x"), ct);"""));            // "forgot to say what a crash means" does not compile
        Assert.Equal(["CS1061"], ErrorIds("""await Step(S.Dump, 1).StreamAsync((s, ct) => Task.CompletedTask, ct);"""));
        Assert.Equal(["CS1061"], ErrorIds("""await Step(S.Dump, 1).Repeatable().RunAsync(ct => Task.FromResult("x"), ct);"""));              // a stream step is streamed
        Assert.Equal(["CS1061"], ErrorIds("""await Step(S.Pack, new DateOnly()).Repeatable().StreamAsync((s, ct) => Task.CompletedTask, ct);"""));   // and a value step is not
        Assert.Contains(ErrorIds("""await Step(S.Pack, new DateOnly()).Check(ct => Task.FromResult(5)).RunAsync(ct => Task.FromResult("x"), ct);"""), id => id is "CS1503" or "CS0029" or "CS1662" or "CS4010");   // a check returns the step's result type
        Assert.Contains("RunAsync", Messages("""await Step(S.Pack, new DateOnly()).RunAsync(ct => Task.FromResult("x"), ct);"""), StringComparison.Ordinal);
    }

    // ---- the analyzer

    private static async Task<string[]> Ids(string source) => [.. (await RoslynHelper.Analyze(source)).Select(d => d.Id)];

    [Fact]
    public async Task The_analyzer_is_quiet_about_a_correct_job()
    {
        var source = RoslynHelper.Job("""
            await Step(S.Pack, new DateOnly()).Repeatable().RunAsync(ct => Task.FromResult("a"), ct);
            await Step(S.Dump, 1).Repeatable().StreamAsync((s, ct) => Task.CompletedTask, ct);
            """);

        Assert.Empty(await RoslynHelper.Analyze(source));
    }

    [Fact]
    public async Task The_analyzer_reports_two_steps_units_or_counters_with_one_name_in_a_class()
    {
        var source = RoslynHelper.Header.Replace("public static class S", "public static class Twice")
            + """

            public static class Dups
            {
                public static readonly ValueStep<int, string> A = Step.Value<int, string>("same", i => "k");
                public static readonly StreamStep<int> B = Step.Stream<int>("same", i => "k");
                public static readonly ValueStep<int, string> C = Step.Value<int, string>("other", i => "k");
                public static readonly Unit<int> U1 = Unit.Of<int>("unit", i => "k");
                public static readonly Unit<int> U2 = Unit.Of<int>("unit", i => "k");
                public static readonly Counter K1 = new("rows");
                public static readonly Counter K2 = new("rows");
                public static readonly Counter K3 = new Counter("rows");
                public static readonly Unit<int> StepNamedLikeAUnit = Unit.Of<int>("same", i => "k");     // another kind: not a duplicate
            }
            """;

        var found = await RoslynHelper.Analyze(source);

        Assert.Equal(["DBKR001", "DBKR001", "DBKR001", "DBKR001"], found.Select(d => d.Id));            // the second step, the second unit, the second and third counter
        Assert.Contains(found, d => d.GetMessage().Contains("step name 'same'", StringComparison.Ordinal) && d.GetMessage().Contains("Dups", StringComparison.Ordinal));
        Assert.Contains(found, d => d.GetMessage().Contains("unit name 'unit'", StringComparison.Ordinal));
        Assert.Equal(2, found.Count(d => d.GetMessage().Contains("counter name 'rows'", StringComparison.Ordinal)));
        Assert.All(found, d => Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task The_analyzer_reports_a_duplicate_inside_the_job_class_and_leaves_one_name_in_two_classes_alone()
    {
        var source = RoslynHelper.Header + """

            public static class One { public static readonly ValueStep<int, string> A = Step.Value<int, string>("pack", i => "k"); }
            public static class Two { public static readonly ValueStep<int, string> A = Step.Value<int, string>("pack", i => "k"); }

            [RunnerTask("x")]
            public sealed class J : RunnerJob<NoInput, P, NoSlots>
            {
                // inside a job class the name Step is the job's method, so the declaring class is named in full
                static readonly ValueStep<int, string> First = DbDataKit.Runner.Kit.Step.Value<int, string>("mine", i => "k");
                static readonly ValueStep<int, string> Second = DbDataKit.Runner.Kit.Step.Value<int, string>("mine", i => "k");

                protected override Task RunAsync(CancellationToken ct) => Task.CompletedTask;
            }
            """;

        var found = await RoslynHelper.Analyze(source);

        var one = Assert.Single(found);
        Assert.Equal("DBKR001", one.Id);
        Assert.Contains("'mine'", one.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("in J", one.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_analyzer_reports_a_step_called_inside_the_body_of_another()
    {
        var source = RoslynHelper.Job("""
            await Step(S.Pack, new DateOnly()).Repeatable().RunAsync(async ct =>
            {
                await Step(S.Dump, 1).Repeatable().StreamAsync((s, c) => Task.CompletedTask, ct);
                return "x";
            }, ct);
            await Step(S.Dump, 2).Ask().StreamAsync(async (sink, ct) =>
            {
                await Step(S.Pack, new DateOnly()).Repeatable().RunAsync(c => Task.FromResult("y"), ct);
            }, ct);
            """);

        var found = await RoslynHelper.Analyze(source);

        Assert.Equal(["DBKR002", "DBKR002"], found.Select(d => d.Id));
        Assert.Contains("'dump'", found[0].GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'pack'", found[0].GetMessage(), StringComparison.Ordinal);
        Assert.Contains("inside the body of the step 'pack'", found[0].GetMessage(), StringComparison.Ordinal);
        Assert.Contains("inside the body of the step 'dump'", found[1].GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_inside_a_unit_or_after_another_step_is_not_a_step_inside_a_step()
    {
        var source = RoslynHelper.Job("""
            await ForEachAsync(S.Item, new[] { 1, 2 }, 2, async (n, c) =>
            {
                await Step(S.Pack, new DateOnly()).Repeatable().RunAsync(x => Task.FromResult("a"), c);
            }, ct);
            await Step(S.Pack, new DateOnly(2000, 1, 1)).Check(c => Task.FromResult<string?>(null)).RunAsync(x => Task.FromResult("b"), ct);
            """);

        Assert.Empty(await RoslynHelper.Analyze(source));
    }

    [Fact]
    public async Task The_sample_jobs_of_the_kit_pass_the_analyzer_and_the_compiler()
    {
        var path = Path.Combine(Path.GetDirectoryName(Here())!, "SampleJobs.cs");
        var source = (await File.ReadAllTextAsync(path)).Replace("namespace DbDataKit.Runner.Kit.Tests.Samples;", "namespace SampleJobsCheck;\nusing DbDataKit.Runner.Kit;");
        source = "global using System; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks; global using System.Collections.Generic; global using System.Net.Http;\n" + source;
        Assert.Empty(RoslynHelper.Errors(source));
        var found = await RoslynHelper.Analyze(source);

        // the one job that nests a step on purpose (to test the run-time refusal) is the one thing found
        var nested = Assert.Single(found);
        Assert.Equal("DBKR002", nested.Id);
        Assert.Contains("'inner'", nested.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'charge'", nested.GetMessage(), StringComparison.Ordinal);
    }

    private static string Here([CallerFilePath] string path = "") => path;
}
