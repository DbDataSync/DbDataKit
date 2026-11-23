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

    [Fact]
    public void The_preflight_reads_a_structure_behind_the_project_as_an_error_and_a_change_outside_the_tool_as_a_warning()
    {
        using var pending = System.Text.Json.JsonDocument.Parse("""
            {"data":{"connection":"warehouse","objects":[{"name":"marts.fct_orders","state":"in_sync"},{"name":"marts.dim_region","state":"missing"},{"name":"marts.dim_store","state":"out_of_band"}],
              "preview":{"steps":2,"risky":0,"destructive":0,"questions":[],"blocked":["DROP COLUMN marts.dim_store.code needs --allow-destructive"],"skipped":[]},
              "noticed":["the refresh plan nightly has never run"],"summary":{"structure":"pending","steps":2,"risky":0,"destructive":0}},"diagnostics":[]}
            """);
        var found = BuildPreflight.Findings("warehouse", 1, pending);
        Assert.Equal(["error", "warning", "error", "error", "info"], found.Select(f => f.Severity));
        Assert.Contains("marts.dim_region is not on the connection", found[0].Message);
        Assert.Contains("DROP COLUMN marts.dim_store.code", found[2].Message);
        Assert.Contains("2 deploy step(s) are pending", found[3].Message);
        Assert.All(found, f => Assert.Equal("warehouse", f.About));

        using var inStep = System.Text.Json.JsonDocument.Parse("""{"data":{"objects":[{"name":"marts.fct_orders","state":"in_sync"}],"summary":{"structure":"in_sync"}},"diagnostics":[]}""");
        Assert.Equal(("info", "The deployed structure is in step with the project."), (BuildPreflight.Findings("warehouse", 0, inStep).Single().Severity, BuildPreflight.Findings("warehouse", 0, inStep).Single().Message));

        // a command that could not read the connection is an error: where it stands is not known
        using var failed = System.Text.Json.JsonDocument.Parse("""{"diagnostics":[{"code":"DDB-242","title":"The database refused the command","found":"SqlException 18456"}]}""");
        var unknown = Assert.Single(BuildPreflight.Findings("warehouse", 70, failed));
        Assert.Equal("error", unknown.Severity);
        Assert.Contains("DDB-242", unknown.Message);
    }

    [Fact]
    public void The_preflight_takes_the_connections_the_plans_use_and_refuses_an_option_in_their_place()
    {
        var (connections, refusal) = BuildPreflight.Connections("sqlserver, postgres,sqlserver");
        Assert.Equal(["sqlserver", "postgres"], connections);
        Assert.Null(refusal);
        Assert.Contains("--apply", BuildPreflight.Connections("sqlserver,--apply").Refusal);
        Assert.NotNull(BuildPreflight.Connections(" , ").Refusal);
        Assert.Equal("sqlserver,postgres", BuildPreflight.InputOf(["sqlserver", "postgres"]).Default);
    }
}
