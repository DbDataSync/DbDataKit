using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;

namespace DbDataBuild.Tests.Unit;

/// <summary>`plan --param model.operation.parameter=value`: the answer to a parameter's question without an answers file.</summary>
public class ParamOptionTests
{
    [Fact]
    public void A_param_becomes_the_answer_to_that_parameters_question()
    {
        var error = new StringWriter();
        Assert.True(PlanCommand.ParseParams(["marts.fct_events.reload_period.start=2024-01-01 00:00:00", "marts.fct_events.reload_period.end=2024-02-01"], null, error, out var answers));
        Assert.Equal("", error.ToString());
        Assert.Equal(["Q-param-marts.fct_events-reload_period-start", "Q-param-marts.fct_events-reload_period-end"], answers.Select(a => a.QuestionId));
        Assert.All(answers, a => Assert.Equal("provide", a.Choice));
        Assert.Equal("2024-01-01 00:00:00", answers[0].Value);
        Assert.Equal(QuestionIds.Param("marts.fct_events", "reload_period", "start"), answers[0].QuestionId);       // the same id the planner asks
    }

    [Theory]
    [InlineData("start=2024-01-01")]                 // no model or operation
    [InlineData("a.b=2024-01-01")]                   // no parameter
    [InlineData("marts.fct.r.start")]                // no value
    [InlineData("marts.fct.r.start=")]               // empty value
    [InlineData("marts..r.start=1")]                 // an empty part
    public void A_malformed_param_is_a_usage_error_that_shows_the_form(string arg)
    {
        var error = new StringWriter();
        Assert.False(PlanCommand.ParseParams([arg], null, error, out _));
        Assert.Contains("--param takes model.operation.parameter=value", error.ToString());
    }

    [Fact]
    public void A_parameter_answered_twice_is_refused_even_across_param_and_the_answers_file()
    {
        var error = new StringWriter();
        Assert.False(PlanCommand.ParseParams(["m.s.r.start=1", "m.s.r.start=2"], null, error, out _));
        Assert.Contains("answered more than once", error.ToString());
        var file = new AnswerFile([new Answer("Q-param-m.s-r-start", "provide", "3", null, false, new SourceLocation("answers.yml", 2, 1))]);
        var error2 = new StringWriter();
        Assert.False(PlanCommand.ParseParams(["m.s.r.start=1"], file, error2, out _));
        Assert.Contains("--param and the answers file", error2.ToString());
    }

    [Fact]
    public void The_plan_command_lists_the_option_with_its_description()
    {
        var o = new StringWriter();
        Assert.Equal(0, CliApp.Run(["connection", "deploy", "--write-plan", "--help"], o, new StringWriter()));
        Assert.Contains("--param", o.ToString());
        Assert.Contains("model.operation.parameter=value", o.ToString());
    }
}
