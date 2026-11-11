using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Support;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 14a SL16 (D37): the explorer's matrix and chart are the engine's <c>ViewRenderer</c>'s, the same code a dashboard's widgets go through (<c>ResultShaper</c>), so the two cannot disagree on a cell. The same request and the
/// same view, run through the explorer's route (a view sent with the run, and the run's kept result rendered again) and through a dashboard's widget, give the same rendered matrix with its totals; a refused encoding comes back
/// as the renderer's own error; a result is the person's who ran it and goes away.
/// </summary>
public sealed partial class ViewsModuleTests
{
    private static JsonObject MatrixView(bool totals) => new()
    {
        ["spec_version"] = "0.3", ["kind"] = "view", ["result"] = new JsonObject { ["ref"] = "result" },
        ["views"] = new JsonArray(new JsonObject
        {
            ["type"] = "matrix", ["id"] = "grid",
            ["rows"] = new JsonArray("customers.region"), ["columns"] = new JsonArray("date.fiscal_quarter"),
            ["values"] = new JsonArray(new JsonObject { ["field"] = "orders.net_revenue" }),
            ["totals"] = totals ? new JsonObject { ["rows"] = true, ["columns"] = true } : null,
        }),
    };

    private static JsonObject MatrixRequest(bool totals)
    {
        var query = new JsonObject { ["attributes"] = new JsonArray("customers.region", "date.fiscal_quarter"), ["measures"] = new JsonArray("orders.net_revenue"), ["limit"] = 10000 };
        if (totals) query["totals"] = new JsonObject { ["subtotal_by"] = new JsonArray("customers.region", "date.fiscal_quarter"), ["grand_total"] = true, ["cube"] = true };
        return new JsonObject { ["spec_version"] = "0.3", ["kind"] = "query", ["query"] = query };
    }

    private static string MatrixDashboard() => $$"""
        schemaVersion: 1
        kind: dashboard
        title: Grid
        widgets:
          grid:
            type: matrix
            encodings:
              rows: [customers.region]
              columns: [date.fiscal_quarter]
              values: [{field: orders.net_revenue}]
              totals: {rows: true, columns: true}
            query: {model: {{Model}}}
        layouts:
          - name: desk
            type: reflow
            default: true
            grid: {columns: 12, rows: 6}
            tiles:
              - {widget: grid, x: 0, y: 0, w: 12, h: 6}
        """;

    private static JsonNode WithoutId(JsonNode? matrix)
    {
        var copy = matrix!.DeepClone().AsObject();
        copy.Remove("id");
        return copy;
    }

    [Fact]
    public async Task The_explorers_matrix_is_the_renderers_and_equals_a_dashboards_matrix_of_the_same_rows_and_a_refused_view_says_why()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (cy, _) = HomeSeed.Person(state, "Cy", "cy@example.invalid", Analysts);
        PublishModel();
        const string runs = "/api/v1/my/runs";

        // the dashboard's own matrix, totals and all
        var board = await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", ada.Id, ServerRole.Viewer, new JsonObject { ["text"] = MatrixDashboard(), ["layout"] = "desk" });
        Assert.Equal(HttpStatusCode.OK, board.Status);
        var dashboard = board.Body!["widgets"]!["grid"]!;
        Assert.True((string?)dashboard["type"] == "matrix", dashboard.ToJsonString());
        Assert.Contains(dashboard["rows"]!.AsArray(), r => r!["total"]?.GetValue<bool>() == true);

        // one round trip: the run answers its rows and the view drawn from them; the matrix is the dashboard's cell for cell
        var withView = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["request"] = MatrixRequest(true), ["view"] = MatrixView(true), ["limit"] = 10000 });
        Assert.True(withView.Status == HttpStatusCode.OK, withView.Body?.ToJsonString());
        Assert.Equal("ok", (string)withView.Body!["status"]!);
        Assert.NotEmpty(withView.Body["rows"]!.AsArray());
        Assert.NotNull(withView.Body["totals"]);
        Assert.Contains(withView.Body["columns"]!.AsArray(), c => (string?)c!["binding"] == "customers.region");
        var rendered = withView.Body["rendered_view"]!;
        Assert.Equal(("rendered_view", "complete"), (((string)rendered["kind"]!), (string)rendered["status"]!));
        Assert.Equal(WithoutId(dashboard).ToJsonString(), WithoutId(rendered["views"]![0]).ToJsonString());

        // the kept result is drawn again, as another view, with nothing run again: the same matrix, and a chart of the same rows
        var id = (string)withView.Body["result_id"]!;
        var runsBefore = (await ManagementAsync(HttpMethod.Get, runs, ada.Id, ServerRole.Viewer)).Body!["runs"]!.AsArray().Count;
        var again = await ManagementAsync(HttpMethod.Post, $"{runs}/{id}/render", ada.Id, ServerRole.Viewer, new JsonObject { ["view"] = MatrixView(true) });
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(rendered["views"]!.ToJsonString(), again.Body!["rendered_view"]!["views"]!.ToJsonString());
        var chart = await ManagementAsync(HttpMethod.Post, $"{runs}/{id}/render", ada.Id, ServerRole.Viewer, new JsonObject
        {
            ["view"] = new JsonObject
            {
                ["spec_version"] = "0.3", ["kind"] = "view", ["result"] = new JsonObject { ["ref"] = "result" },
                ["views"] = new JsonArray(new JsonObject
                {
                    ["type"] = "chart", ["id"] = "bars",
                    ["vega_lite"] = new JsonObject { ["mark"] = "bar", ["encoding"] = new JsonObject
                    {
                        ["x"] = new JsonObject { ["field"] = "customers.region", ["type"] = "nominal" },
                        ["y"] = new JsonObject { ["field"] = "orders.net_revenue", ["type"] = "quantitative" },
                        ["color"] = new JsonObject { ["field"] = "date.fiscal_quarter", ["type"] = "nominal" },
                    } },
                }),
            },
        });
        Assert.Equal("complete", (string)chart.Body!["rendered_view"]!["status"]!);
        Assert.Equal("chart", (string)chart.Body["rendered_view"]!["views"]![0]!["type"]!);
        Assert.Equal(runsBefore, (await ManagementAsync(HttpMethod.Get, runs, ada.Id, ServerRole.Viewer)).Body!["runs"]!.AsArray().Count);

        // a view the result cannot give: the renderer's own code and words, as data (the run's rows are still there)
        var noTotals = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["request"] = MatrixRequest(false), ["view"] = MatrixView(true) });
        Assert.Equal(HttpStatusCode.OK, noTotals.Status);
        Assert.NotEmpty(noTotals.Body!["rows"]!.AsArray());
        Assert.Null(noTotals.Body["totals"]);
        Assert.Equal("failed", (string)noTotals.Body["rendered_view"]!["status"]!);
        Assert.Equal("totals_missing", (string)noTotals.Body["rendered_view"]!["errors"]![0]!["code"]!);
        var unknown = await ManagementAsync(HttpMethod.Post, $"{runs}/{(string)noTotals.Body["result_id"]!}/render", ada.Id, ServerRole.Viewer, new JsonObject
        {
            ["view"] = new JsonObject
            {
                ["spec_version"] = "0.3", ["kind"] = "view", ["result"] = new JsonObject { ["ref"] = "result" },
                ["views"] = new JsonArray(new JsonObject { ["type"] = "matrix", ["id"] = "grid", ["rows"] = new JsonArray("customers.nowhere"), ["values"] = new JsonArray(new JsonObject { ["field"] = "orders.net_revenue" }) }),
            },
        });
        Assert.Equal("unknown_field", (string)unknown.Body!["rendered_view"]!["errors"]![0]!["code"]!);

        // a result is its runner's alone, and a gone or invented one is a 410 that says to run again; a body without a view is a 400, and the route takes POST
        Assert.Equal(HttpStatusCode.Gone, (await ManagementAsync(HttpMethod.Post, $"{runs}/{id}/render", cy.Id, ServerRole.Viewer, new JsonObject { ["view"] = MatrixView(true) })).Status);
        var gone = await ManagementAsync(HttpMethod.Post, $"{runs}/nothing/render", ada.Id, ServerRole.Viewer, new JsonObject { ["view"] = MatrixView(true) });
        Assert.Equal(HttpStatusCode.Gone, gone.Status);
        Assert.Equal("result_gone", (string)gone.Body!["code"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await ManagementAsync(HttpMethod.Post, $"{runs}/{id}/render", ada.Id, ServerRole.Viewer, new JsonObject())).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await ManagementAsync(HttpMethod.Get, $"{runs}/{id}/render", ada.Id, ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ManagementAsync(HttpMethod.Post, $"{runs}/{id}/render", ada.Id, ServerRole.None, new JsonObject { ["view"] = MatrixView(true) })).Status);

        // a run that asks for no view is the old answer: no result id, no rendered view
        var plain = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["request"] = MatrixRequest(false) });
        Assert.Null(plain.Body!["result_id"]);
        Assert.Null(plain.Body["rendered_view"]);
    }

    /// <summary>An answer of a string column and a binary one, the kind of table the views cannot read; the semantic engine's own answers are text, numbers and booleans, so a source of another kind is stood in for.</summary>
    private sealed class BinaryAnswer : DbDataKit.Serving.IServingAnswer
    {
        private readonly Apache.Arrow.RecordBatch batch;

        public BinaryAnswer()
        {
            Schema = new Apache.Arrow.Schema([new Apache.Arrow.Field("region", Apache.Arrow.Types.StringType.Default, true), new Apache.Arrow.Field("logo", Apache.Arrow.Types.BinaryType.Default, true)], null);
            batch = new Apache.Arrow.RecordBatch(Schema, [new Apache.Arrow.StringArray.Builder().Append("West").Append("East").Build(), new Apache.Arrow.BinaryArray.Builder().Append((byte)1).Append((byte)2).Build()], 2);
        }

        public Apache.Arrow.Schema Schema { get; }
        public DbDataKit.Serving.ServingProvenance Provenance { get; } = new("x", null, "test");

        public async IAsyncEnumerable<Apache.Arrow.RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return batch;
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            batch.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_column_a_view_cannot_read_gives_the_table_and_a_notice_that_names_its_column_and_type_and_nothing_is_kept_or_drawn()
    {
        var runs = new GatewaySemanticRuns(null!, null!, null!, null!, null!, null, null!);
        var who = new DbDataKit.Serving.ServingCaller("ada", "Ada", new HashSet<string>(), new Dictionary<string, string> { ["email"] = "ada@example.invalid" });
        foreach (var (draw, view) in new[] { (true, (JsonObject?)MatrixView(false)), (true, null) })
        {
            await using var answer = new BinaryAnswer();
            var result = await runs.ShapeAsync(answer, 100, draw, view, who, CancellationToken.None);
            Assert.Equal("ok", (string)result["status"]!);
            Assert.Equal(2, result["rows"]!.AsArray().Count);
            Assert.Null(result["result_id"]);
            Assert.Null(result["rendered_view"]);
            var notice = Assert.Single(result["notices"]!.AsArray())!;
            Assert.Equal(("view_unavailable", "logo", "binary"), ((string)notice["code"]!, (string)notice["column"]!, (string)notice["type"]!));
            Assert.Equal("matrix and chart are unavailable: column logo has type binary, which a view cannot read. The table shows what the REST answer holds of it (a value of that type is empty).", (string)notice["message"]!);
        }

        // a plain run (nothing to draw) is the old answer, without a notice
        await using var plain = new BinaryAnswer();
        var table = await runs.ShapeAsync(plain, 100, draw: false, null, who, CancellationToken.None);
        Assert.Null(table["notices"]);
        Assert.Equal(2, table["rows"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_replay_with_a_view_draws_the_matrix_of_a_normal_run_keeps_the_view_out_of_the_record_draws_for_an_administrator_with_their_own_access_and_a_gone_version_is_still_410()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (cy, _) = HomeSeed.Person(state, "Cy", "cy@example.invalid", "visitors");
        PublishModel();
        const string runs = "/api/v1/my/runs";

        var normal = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["request"] = MatrixRequest(true), ["view"] = MatrixView(true), ["limit"] = 10000 });
        Assert.True(normal.Status == HttpStatusCode.OK, normal.Body?.ToJsonString());
        var record = (long)(await ManagementAsync(HttpMethod.Get, runs, ada.Id, ServerRole.Viewer)).Body!["runs"]![0]!["id"]!;
        var before = (await ManagementAsync(HttpMethod.Get, $"{runs}/{record}", ada.Id, ServerRole.Viewer)).Body!.ToJsonString();

        // the replay is one run: rows, the hash comparison, a kept result and the view drawn, the same matrix as the normal run's
        var replay = await ManagementAsync(HttpMethod.Post, $"{runs}/{record}/replay", ada.Id, ServerRole.Viewer, new JsonObject { ["keep"] = true, ["view"] = MatrixView(true) });
        Assert.True(replay.Status == HttpStatusCode.OK, replay.Body?.ToJsonString());
        Assert.True((bool)replay.Body!["same_hash"]!);
        Assert.Equal(normal.Body!["rows"]!.ToJsonString(), replay.Body["rows"]!.ToJsonString());
        Assert.False(string.IsNullOrEmpty((string?)replay.Body["result_id"]));
        Assert.Equal("complete", (string)replay.Body["rendered_view"]!["status"]!);
        Assert.Equal(normal.Body["rendered_view"]!["views"]!.ToJsonString(), replay.Body["rendered_view"]!["views"]!.ToJsonString());
        Assert.NotNull(replay.Body["totals"]);

        // the kept result of a replay is drawn again, and the view is nowhere in the record
        var again = await ManagementAsync(HttpMethod.Post, $"{runs}/{(string)replay.Body["result_id"]!}/render", ada.Id, ServerRole.Viewer, new JsonObject { ["view"] = MatrixView(true) });
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(before, (await ManagementAsync(HttpMethod.Get, $"{runs}/{record}", ada.Id, ServerRole.Viewer)).Body!.ToJsonString());
        var newest = (await ManagementAsync(HttpMethod.Get, runs, ada.Id, ServerRole.Viewer)).Body!["runs"]![0]!;
        Assert.Equal(record, (long)newest["replay_of"]!);
        var detail = (await ManagementAsync(HttpMethod.Get, $"{runs}/{(long)newest["id"]!}", ada.Id, ServerRole.Viewer)).Body!.ToJsonString();
        Assert.DoesNotContain("rendered_view", detail);
        Assert.DoesNotContain("\"views\"", detail);

        // no body is the replay as it was: no result kept, nothing drawn
        var bare = await ManagementAsync(HttpMethod.Post, $"{runs}/{record}/replay", ada.Id, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, bare.Status);
        Assert.Null(bare.Body!["result_id"]);
        Assert.Null(bare.Body["rendered_view"]);

        // an administrator replaying Ada's record runs and draws as the administrator: Cy is not in the group that reads the data, so the replay is refused in the Gateway's words and nothing is drawn
        var admin = await ManagementAsync(HttpMethod.Post, $"{runs}/{record}/replay", cy.Id, ServerRole.Admin, new JsonObject { ["view"] = MatrixView(true) });
        Assert.NotEqual(HttpStatusCode.OK, admin.Status);
        Assert.Null(admin.Body!["rendered_view"]);

        // a version that is gone is a 410 whatever the body asks
        var gone = Get<SemanticRunStore>().Add(new SemanticRun("ada@example.invalid", Model, 99, "query", "x", MatrixRequest(true), "h", "complete", null, null, null, null, DateTimeOffset.UtcNow, 1, 0));
        var refused = await ManagementAsync(HttpMethod.Post, $"{runs}/{gone}/replay", ada.Id, ServerRole.Viewer, new JsonObject { ["keep"] = true, ["view"] = MatrixView(true) });
        Assert.Equal(HttpStatusCode.Gone, refused.Status);
        Assert.Equal("version_gone", (string)refused.Body!["code"]!);
    }
}
