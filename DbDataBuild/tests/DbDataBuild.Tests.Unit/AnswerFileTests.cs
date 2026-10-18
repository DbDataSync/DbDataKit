using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.SchemaConformanceTests;

namespace DbDataBuild.Tests.Unit;

public class AnswerFileTests
{
    public sealed record Case(string Name, string Yaml, bool SchemaValid, params string[] Codes)
    {
        public override string ToString() => Name;
    }

    private const string Id = "Q-history-marts.fct_orders.discount_code";

    public static readonly Case[] Cases =
    [
        new("one choice", $"answers:\n  - id: {Id}\n    choice: not_backfilled\n", true),
        new("choice with value and note", $"answers:\n  - id: Q-rename-marts.dim_customer.cust_nm\n    choice: rename_to\n    value: customer_name\n    note: \"No history exists in source.\"\n", true),
        new("accept inferred", $"answers:\n  - id: Q-define-marts.fct_orders-type-amount\n    accept: inferred\n", true),
        new("accept inferred with a note", $"answers:\n  - id: Q-define-marts.fct_orders-type-amount\n    accept: inferred\n    note: ok\n", true),
        new("several answers", $"answers:\n  - {{id: {Id}, choice: backfilled}}\n  - {{id: Q-adopt-marts.fct_orders, choice: adopt}}\n  - {{id: Q-param-marts.fct_orders-daily-start, choice: use_value, value: \"2026-01-01\"}}\n", true),
        new("no answers", "answers: []\n", true),

        new("missing answers key", "other: 1\n", false, "DDB-104", "DDB-105"),
        new("unknown top key", $"answers: []\nextra: 1\n", false, "DDB-104"),
        new("answers not a list", "answers: x\n", false, "DDB-106"),
        new("answer not a mapping", "answers:\n  - x\n", false, "DDB-106"),
        new("missing id", "answers:\n  - choice: backfilled\n", false, "DDB-105"),
        new("unknown area in id", "answers:\n  - id: Q-bogus-x\n    choice: backfilled\n", false, "DDB-106"),
        new("id with a space", "answers:\n  - id: Q-history-a b\n    choice: backfilled\n", false, "DDB-106"),
        new("neither choice nor accept", $"answers:\n  - id: {Id}\n", false, "DDB-105"),
        new("both choice and accept", $"answers:\n  - id: {Id}\n    choice: backfilled\n    accept: inferred\n", false, "DDB-106"),
        new("choice not snake_case", $"answers:\n  - id: {Id}\n    choice: Not-Backfilled\n", false, "DDB-106"),
        new("accept with another word", $"answers:\n  - id: {Id}\n    accept: yes\n", false, "DDB-106"),
        new("accept with a value", $"answers:\n  - id: {Id}\n    accept: inferred\n    value: x\n", false, "DDB-106"),
        new("unknown key in an answer", $"answers:\n  - id: {Id}\n    choice: backfilled\n    why: x\n", false, "DDB-104"),
        new("empty note", $"answers:\n  - id: {Id}\n    choice: backfilled\n    note: \"\"\n", false, "DDB-106"),
        new("empty value", $"answers:\n  - id: {Id}\n    choice: backfilled\n    value: \"\"\n", false, "DDB-106"),

        // Semantic: only the loader can see duplicates.
        new("duplicate id", $"answers:\n  - {{id: {Id}, choice: backfilled}}\n  - {{id: {Id}, choice: not_backfilled}}\n", true, "DDB-102"),
    ];

    public static TheoryData<Case> Data
    {
        get { var d = new TheoryData<Case>(); foreach (var c in Cases) d.Add(c); return d; }
    }

    [Theory, MemberData(nameof(Data))]
    public void Answers_schema_and_loader_agree(Case c)
    {
        Assert.Equal(c.SchemaValid, SchemaAccepts(LoadSchema("answers"), c.Yaml));
        var diags = new List<Diagnostic>();
        var file = AnswerFileLoader.Load(c.Yaml, "answers.yml", diags);
        if (c.Codes.Length == 0)
        {
            Assert.Empty(diags.Select(DiagnosticFormatter.Format));
            Assert.NotNull(file);
        }
        else
        {
            foreach (var code in c.Codes) Assert.Contains(diags, d => d.Code == code);
            Assert.Null(file);
        }
    }

    [Fact]
    public void Loader_reads_every_field_with_positions()
    {
        var yaml = $"answers:\n  - id: {Id}\n    choice: not_backfilled\n    note: n\n  - id: Q-rename-marts.dim_customer.cust_nm\n    choice: rename_to\n    value: customer_name\n  - id: Q-define-marts.fct_orders-type-amount\n    accept: inferred\n";
        var file = AnswerFileLoader.Load(yaml, "answers.yml", [])!;
        Assert.Equal(3, file.Answers.Count);
        Assert.Equal((Id, "not_backfilled", (string?)null, "n", false, 2), (file.Answers[0].QuestionId, file.Answers[0].Choice, file.Answers[0].Value, file.Answers[0].Note, file.Answers[0].AcceptInferred, file.Answers[0].Location.Line));
        Assert.Equal(("rename_to", "customer_name", 5), (file.Answers[1].Choice, file.Answers[1].Value, file.Answers[1].Location.Line));
        Assert.True(file.Answers[2].AcceptInferred);
        Assert.Null(file.Answers[2].Choice);
    }

    [Fact]
    public void Empty_file_answers_nothing_and_malformed_input_never_throws()
    {
        Assert.Empty(AnswerFileLoader.Load("", "answers.yml", [])!.Answers);
        string[] inputs = [":", "[", "- a", "answers: &x []\nb: *x", "answers: {a: b}", "answers:\n  - [a]\n  - {id: [x]}", "---\n---\n", "answers:\n  -\n"];
        foreach (var input in inputs) Assert.Null(Record.Exception(() => AnswerFileLoader.Load(input, "answers.yml", [])));
    }

    [Fact]
    public void A_loaded_file_resolves_questions_end_to_end_with_file_positions_in_errors()
    {
        var yaml = $"answers:\n  - id: {Id}\n    choice: maybe\n";
        var file = AnswerFileLoader.Load(yaml, "answers.yml", [])!;
        var r = QuestionResolver.Resolve([Qs.History()], file, "answers.yml");
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(("DDB-411", "answers.yml", 2), (d.Code, d.Location.File, d.Location.Line));
        Assert.Contains("backfilled, not_backfilled", DiagnosticFormatter.Format(d));
    }
}

public class ConsolePrompterTests
{
    private static (PromptResult? Result, string Output) Ask(Question q, string input)
    {
        var output = new StringWriter();
        var result = new ConsolePrompter(new StringReader(input), output).Ask(q);
        return (result, output.ToString());
    }

    [Fact]
    public void Picks_an_option_by_number_or_key_and_reads_an_optional_note()
    {
        var (byNumber, text) = Ask(Qs.History(), "2\nno history in source\n");
        Assert.Equal(new PromptResult("not_backfilled", null, "no history in source", false), byNumber);
        Assert.Contains("Q-history-marts.fct_orders.discount_code", text);
        Assert.Contains("1. backfilled", text);

        Assert.Equal(new PromptResult("backfilled", null, null, false), Ask(Qs.History(), "backfilled\n\n").Result);
    }

    [Fact]
    public void Asks_for_a_value_when_the_option_takes_one_and_refuses_an_empty_value()
    {
        var (r, text) = Ask(Qs.Rename(), "1\n\ncustomer_name\n\n");
        Assert.Equal(new PromptResult("rename_to", "customer_name", null, false), r);
        Assert.Contains("A value is required", text);
        Assert.Contains("Value (new column name):", text);
    }

    [Fact]
    public void Refuses_input_that_is_not_an_option_instead_of_defaulting()
    {
        var (r, text) = Ask(Qs.History(), "\n7\nnope\n1\n\n");
        Assert.Equal("backfilled", r!.Choice);
        Assert.Equal(3, text.Split("is not one of the options").Length - 1);
    }

    [Fact]
    public void A_accepts_the_proposal_only_when_there_is_one()
    {
        var (r, text) = Ask(Qs.Rename(), "a\nlooks right\n");
        Assert.Equal(new PromptResult("rename_to", "customer_name", "looks right", true), r);
        Assert.Contains("'a' to accept the inferred answer", text);

        var (r2, text2) = Ask(Qs.History(), "a\n1\n\n");                    // no proposal: 'a' is just invalid input
        Assert.Equal("backfilled", r2!.Choice);
        Assert.DoesNotContain("'a' to accept", text2);
        Assert.Contains("is not one of the options", text2);
    }

    [Fact]
    public void End_of_input_stops_without_answering()
    {
        Assert.Null(Ask(Qs.History(), "").Result);
        Assert.Null(Ask(Qs.History(), "nope\n").Result);
        Assert.Null(Ask(Qs.Rename(), "1\n").Result);                        // stopped while asking for the value
    }

    [Fact]
    public void Prompter_plugs_into_the_resolver()
    {
        var prompter = new ConsolePrompter(new StringReader("1\n\n2\nnote\n"), new StringWriter());
        var r = QuestionResolver.Resolve([Qs.Rename(), Qs.History()], null, "answers.yml", prompter);
        Assert.True(r.Complete);
        // sorted by id: history first, then rename
        Assert.Equal(("backfilled", AnswerSource.Interactive), (r.Answers[0].Choice, r.Answers[0].Source));
        Assert.Equal(("drop_and_add", "note"), (r.Answers[1].Choice, r.Answers[1].Note));
    }
}
