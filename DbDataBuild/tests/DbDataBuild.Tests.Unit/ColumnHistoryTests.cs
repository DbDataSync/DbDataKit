using DbDataBuild.Core.Questions;
using DbDataBuild.Planning;

namespace DbDataBuild.Tests.Unit;

public class ColumnHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0);

    private static AppliedPlan Plan(string id, DateTime at, params ResolvedAnswer[] answers) =>
        new(id, "alice", at, new Plan(id, "sqlserver", null, false, "0.1.0", [], answers, [], []));

    private static ResolvedAnswer History(string choice, string? note = null) => new("Q-history-marts.fct.discount_code", choice, null, note, AnswerSource.File);
    private static RecordedShape Shape(string plan, DateTime at, string source = "tool") => new("marts.fct", new string('a', 64), at, source, plan);

    [Fact]
    public void A_not_backfilled_column_reports_when_it_was_added_what_was_decided_and_the_loads_around_it()
    {
        var plans = new[] { Plan("p1", T0), Plan("p2", T0.AddDays(10), History("not_backfilled", "No history in source.")) };
        var shapes = new[] { Shape("p1", T0), Shape("p2", T0.AddDays(10)) };
        var intervals = new[]
        {
            new RecordedInterval("marts.fct", "2026-09-30", null, "load", T0.AddDays(1)),
            new RecordedInterval("marts.fct", "2026-10-01", null, "load", T0.AddDays(2)),
            new RecordedInterval("marts.fct", "2026-10-12", null, "load", T0.AddDays(11)),
            new RecordedInterval("marts.other", "x", null, "load", T0.AddDays(1)),    // another model is not counted
        };
        var entry = Assert.Single(ColumnHistory.Build(plans, shapes, intervals));
        Assert.False(entry.NeedsAttention);
        Assert.Equal(("marts.fct", "discount_code", "not_backfilled"), (entry.Model, entry.Column, entry.Disposition));
        Assert.Contains("schema version 2 of 2", entry.Text);
        Assert.Contains("not_backfilled (file, \"No history in source.\"), applied by alice", entry.Text);
        Assert.Contains("2 recorded load range(s) before the change hold NULL in it; 1 after, of which 0 backfill(s)", entry.Text);
    }

    [Fact]
    public void A_requested_backfill_that_was_never_recorded_needs_attention_until_one_is()
    {
        var plans = new[] { Plan("p2", T0, History("backfill_later")) };
        var shapes = new[] { Shape("p2", T0) };
        Assert.True(Assert.Single(ColumnHistory.Build(plans, shapes, [])).NeedsAttention);
        Assert.Contains("A backfill was requested and none has been recorded.", ColumnHistory.Build(plans, shapes, [])[0].Text);

        var done = new[] { new RecordedInterval("marts.fct", "2026-01-01", "2026-02-01", "backfill", T0.AddDays(3)) };
        Assert.False(Assert.Single(ColumnHistory.Build(plans, shapes, done)).NeedsAttention);

        // a backfill from before the column existed does not count
        var early = new[] { new RecordedInterval("marts.fct", "2026-01-01", "2026-02-01", "backfill", T0.AddDays(-3)) };
        Assert.True(Assert.Single(ColumnHistory.Build(plans, shapes, early)).NeedsAttention);
    }

    [Fact]
    public void Other_answers_and_plans_without_history_decisions_are_not_reported()
    {
        var other = new ResolvedAnswer("Q-rename-marts.fct.a", "drop_and_add", null, null, AnswerSource.File);
        Assert.Empty(ColumnHistory.Build([Plan("p1", T0, other), Plan("p2", T0)], [], []));
    }

    [Fact]
    public void An_unrecorded_shape_is_said_so_not_invented()
    {
        var entry = Assert.Single(ColumnHistory.Build([Plan("p2", T0, History("not_backfilled"))], [], []));
        Assert.Contains("no recorded shape for this plan", entry.Text);
    }

    [Fact]
    public void An_acknowledged_inconsistency_stays_in_the_report_but_stops_needing_attention()
    {
        var plans = new[] { Plan("p2", T0, History("backfill_later")) };
        var shapes = new[] { Shape("p2", T0) };
        var open = Assert.Single(ColumnHistory.Build(plans, shapes, []));
        Assert.True(open.NeedsAttention);
        Assert.Equal("DDB-443|marts.fct.discount_code|p2", open.AckKey);

        var ack = new HistoryAck(open.AckKey!, "bob", "Source has no history for this column; accepted.", T0.AddDays(2));
        var accepted = Assert.Single(ColumnHistory.Build(plans, shapes, [], [ack]));
        Assert.False(accepted.NeedsAttention);
        Assert.Equal(ack, accepted.Acknowledgement);
        Assert.Contains("Acknowledged by bob on 2026-10-03 09:00:00 UTC: \"Source has no history for this column; accepted.\"", accepted.Text);
        Assert.Contains("A backfill was requested and none has been recorded.", accepted.Text);        // the facts are still there

        // an acknowledgement is about one plan's decision: it does not cover the same column decided again by a later plan
        var later = new[] { Plan("p2", T0, History("backfill_later")), Plan("p3", T0.AddDays(5), History("backfill_later")) };
        var both = ColumnHistory.Build(later, [Shape("p2", T0), Shape("p3", T0.AddDays(5))], [], [ack]);
        Assert.Equal([false, true], both.Select(e => e.NeedsAttention));

        // and other keys never match
        var wrong = new HistoryAck("DDB-443|marts.other.col|p2", "bob", "x", T0);
        Assert.True(Assert.Single(ColumnHistory.Build(plans, shapes, [], [wrong])).NeedsAttention);
    }

    [Fact]
    public void A_not_backfilled_decision_has_nothing_to_acknowledge()
    {
        var entry = Assert.Single(ColumnHistory.Build([Plan("p2", T0, History("not_backfilled"))], [Shape("p2", T0)], []));
        Assert.Null(entry.AckKey);
        Assert.False(entry.NeedsAttention);
    }
}
