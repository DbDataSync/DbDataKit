using DbDataBuild.Core;
using DbDataBuild.Core.Questions;

namespace DbDataBuild.Tests.Unit;

internal static class Qs
{
    public static Question History(string column = "discount_code") => new(
        QuestionIds.History("marts.fct_orders", column),
        $"Column `{column}` was added to marts.fct_orders. How should rows loaded before this change be treated?",
        [$"Existing rows will have NULL {column}."],
        [
            new QuestionOption("backfilled", "History was backfilled", "needs a backfill step in the plan"),
            new QuestionOption("not_backfilled", "History was not backfilled", "older intervals stay NULL and are reported"),
        ]);

    public static Question Rename(string column = "cust_nm") => new(
        QuestionIds.Rename("marts.dim_customer", column),
        $"`{column}` is gone and a new column of the same type and position appeared. Is this a rename?",
        [],
        [
            new QuestionOption("rename_to", "It is a rename", "data is kept", TakesValue: true, ValueHint: "new column name"),
            new QuestionOption("drop_and_add", "Drop the old column and add the new one", "data in the old column is lost"),
        ],
        new Proposal("rename_to", "customer_name", ProposalCertainty.Normal, ["same type VARCHAR(50)", "same ordinal position 3"]));

    public static Question DefineType() => new(
        QuestionIds.Define("marts.fct_orders", "type-amount"),
        "What is the type of `amount`?",
        [],
        [
            new QuestionOption("use_type", "Use this type", TakesValue: true, ValueHint: "SQL type"),
            new QuestionOption("other_type", "Choose another type later", TakesValue: false),
        ],
        new Proposal("use_type", "DECIMAL(14, 2)", ProposalCertainty.High, ["DuckDB describe of the query resolves DECIMAL(14,2)"]));

    public static Answer FileAnswer(string id, string? choice = null, string? value = null, bool accept = false, string? note = null, int line = 1) =>
        new(id, choice, value, note, accept, new SourceLocation("answers.yml", line, 5));
}

public class QuestionTests
{
    [Fact]
    public void Id_builders_produce_the_documented_shapes()
    {
        Assert.Equal("Q-define-marts.fct_orders-grain", QuestionIds.Define("marts.fct_orders", "grain"));
        Assert.Equal("Q-history-marts.fct_orders.discount_code", QuestionIds.History("marts.fct_orders", "discount_code"));
        Assert.Equal("Q-rename-marts.dim_customer.cust_nm", QuestionIds.Rename("marts.dim_customer", "cust_nm"));
        Assert.Equal("Q-param-marts.fct_orders-reload_period-start", QuestionIds.Param("marts.fct_orders", "reload_period", "start"));
        Assert.Equal("Q-adopt-marts.fct_orders", QuestionIds.Adopt("marts.fct_orders"));
    }

    [Fact]
    public void Ids_are_deterministic()
    {
        Assert.Equal(QuestionIds.History("a.b", "c"), QuestionIds.History("a.b", "c"));
        Assert.Equal(Qs.History().Id, Qs.History().Id);
    }

    [Theory]
    [InlineData("Q-history-marts.fct_orders.discount_code", true)]
    [InlineData("Q-define-m-f", true)]
    [InlineData("Q-adopt-x", true)]
    [InlineData("Q-bogus-x", false)]
    [InlineData("Q-history-", false)]
    [InlineData("Q-history-a b", false)]
    [InlineData("q-history-x", false)]
    [InlineData("history-x", false)]
    [InlineData("", false)]
    public void Id_grammar(string id, bool valid) => Assert.Equal(valid, QuestionIds.IsValid(id));

    [Fact]
    public void Id_builders_reject_empty_parts()
    {
        Assert.Throws<ArgumentException>(() => QuestionIds.History("", "c"));
        Assert.Throws<ArgumentException>(() => QuestionIds.Adopt(""));
    }

    [Theory]
    [InlineData("Order Id", "Order_u20_Id")]
    [InlineData("semi;colon", "semi_u3b_colon")]
    [InlineData("naïve", "na_uef_ve")]
    [InlineData("plain_name.v2-x", "plain_name.v2-x")]
    public void Unusual_names_are_sanitized_deterministically_into_valid_ids(string name, string part)
    {
        Assert.Equal(part, QuestionIds.Sanitize(name));
        var id = QuestionIds.History("marts.fct_orders", name);
        Assert.Equal($"Q-history-marts.fct_orders.{part}", id);
        Assert.True(QuestionIds.IsValid(id));
        Assert.Equal(id, QuestionIds.History("marts.fct_orders", name));
    }

    private static Question Make(string id = "Q-history-a.b", string prompt = "p", QuestionOption[]? options = null, Proposal? proposal = null) =>
        new(id, prompt, [], options ?? [new("a", "A"), new("b", "B")], proposal);

    [Fact]
    public void Malformed_questions_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Make(id: "nope"));
        Assert.Throws<ArgumentException>(() => Make(prompt: " "));
        Assert.Throws<ArgumentException>(() => Make(options: [new("a", "A")]));                                   // needs two explicit options
        Assert.Throws<ArgumentException>(() => Make(options: [new("a", "A"), new("a", "again")]));                // unique keys
        Assert.Throws<ArgumentException>(() => Make(options: [new("A", "A"), new("b", "B")]));                    // snake_case keys
        Assert.Throws<ArgumentException>(() => Make(options: [new("a", ""), new("b", "B")]));                     // described
        Assert.Throws<ArgumentException>(() => Make(options: [new("a", "A", ValueHint: "x"), new("b", "B")]));    // hint without a value
    }

    [Fact]
    public void Proposals_must_name_an_option_fit_its_value_and_carry_evidence()
    {
        var opts = new QuestionOption[] { new("a", "A", TakesValue: true), new("b", "B") };
        Assert.NotNull(Make(options: opts, proposal: new Proposal("a", "v", ProposalCertainty.Normal, ["e"])));
        Assert.Throws<ArgumentException>(() => Make(options: opts, proposal: new Proposal("zzz", null, ProposalCertainty.Normal, ["e"])));
        Assert.Throws<ArgumentException>(() => Make(options: opts, proposal: new Proposal("a", null, ProposalCertainty.Normal, ["e"])));   // value missing
        Assert.Throws<ArgumentException>(() => Make(options: opts, proposal: new Proposal("b", "v", ProposalCertainty.Normal, ["e"])));    // value not allowed
        Assert.Throws<ArgumentException>(() => Make(options: opts, proposal: new Proposal("b", null, ProposalCertainty.Normal, [])));      // evidence required
    }

    [Fact]
    public void Describe_lists_context_options_consequences_and_the_proposal_with_evidence()
    {
        var text = QuestionText.Describe(Qs.Rename());
        Assert.StartsWith("Q-rename-marts.dim_customer.cust_nm", text);
        Assert.Contains("1. rename_to", text);
        Assert.Contains("2. drop_and_add", text);
        Assert.Contains("consequence: data in the old column is lost", text);
        Assert.Contains("Inferred (normal certainty): rename_to = customer_name", text);
        Assert.Contains("evidence: same type VARCHAR(50)", text);
        Assert.Contains("Context:", QuestionText.Describe(Qs.History()));
    }

    [Fact]
    public void Answer_template_gives_paste_ready_yaml_for_each_option()
    {
        var text = QuestionText.AnswerTemplate(Qs.Rename());
        Assert.Contains("  - id: Q-rename-marts.dim_customer.cust_nm\n    choice: rename_to\n    value: <new column name>", text);
        Assert.Contains("    choice: drop_and_add\n", text);
        Assert.EndsWith("    accept: inferred", text);
        Assert.DoesNotContain("accept: inferred", QuestionText.AnswerTemplate(Qs.History()));
    }

    [Fact]
    public void Formatter_indents_continuation_lines_of_multi_line_fields()
    {
        var d = new Diagnostic(DiagnosticCatalog.QuestionUnanswered, new("answers.yml", 0, 0), "line one\nline two", Fix: "fix one\n  fix two");
        var text = DiagnosticFormatter.Format(d);
        Assert.Contains("  line one\n    line two\n", text);
        Assert.Contains("  Fix: fix one\n      fix two\n", text);
    }
}
