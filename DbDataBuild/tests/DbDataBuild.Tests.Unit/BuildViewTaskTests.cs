using DbDataBuild.Cli.Runner;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 33 SC20p: the database-facing views as read-only manual tasks: the command each runs, what it refuses, and how a document is recorded.</summary>
public sealed class BuildViewTaskTests
{
    [Fact]
    public void Each_view_runs_its_command_with_json_and_the_inputs_as_options()
    {
        Assert.Equal(["project", "tests", "run", "naming.x", "--tag", "nightly", "--kind", "metadata", "--project", "/p", "--format", "json"],
            BuildViews.Arguments(BuildViews.Tests, new Dictionary<string, string> { ["tests"] = "naming.x", ["tags"] = "nightly", ["kind"] = "metadata" }, "/p"));
        Assert.Equal(["connection", "status", "sqlserver", "hourly", "--project", "/p", "--format", "json"],
            BuildViews.Arguments(BuildViews.Status, new Dictionary<string, string> { ["connection"] = "sqlserver", ["plan"] = "hourly" }, "/p"));
        Assert.Equal(["connection", "compare", "sqlserver", "dbo.orders", "--against-schema", "dev", "--key", "id", "region", "--show-values", "--project", "/p", "--format", "json"],
            BuildViews.Arguments(BuildViews.Compare, new Dictionary<string, string> { ["connection"] = "sqlserver", ["table"] = "dbo.orders", ["against_schema"] = "dev", ["key"] = "id, region", ["show_values"] = "true" }, "/p"));
    }

    [Fact]
    public void An_option_in_the_place_of_a_name_is_refused_before_anything_runs()
    {
        Assert.Contains("connection", BuildViews.Refusal(BuildViews.Status, new Dictionary<string, string> { ["connection"] = "--apply" }));
        Assert.Contains("table", BuildViews.Refusal(BuildViews.Compare, new Dictionary<string, string> { ["connection"] = "sqlserver", ["table"] = "--show-values" }));
        Assert.Contains("kind", BuildViews.Refusal(BuildViews.Tests, new Dictionary<string, string> { ["kind"] = "semantic" }));
        Assert.Null(BuildViews.Refusal(BuildViews.Tests, new Dictionary<string, string>()));
        Assert.Contains("not a view", BuildViews.Refusal("apply_plan", new Dictionary<string, string>()));
    }

    [Fact]
    public void A_document_is_recorded_whole_when_it_fits_and_as_its_head_when_it_does_not()
    {
        using var small = System.Text.Json.JsonDocument.Parse("""{"data":{"ok":true}}""");
        var whole = BuildViews.Recorded(BuildViews.Status, 1, small);
        Assert.Equal((1, true), ((int)whole["exit_code"]!, (bool)whole["document"]!["data"]!["ok"]!));
        using var large = System.Text.Json.JsonDocument.Parse("{\"data\":\"" + new string('x', BuildViews.MaxDocumentBytes + 10) + "\"}");
        var head = BuildViews.Recorded(BuildViews.Compare, 0, large);
        Assert.True((bool)head["truncated"]!);
        Assert.Null(head["document"]);
        Assert.True(head.ToJsonString().Length < 64 * 1024);
    }
}
