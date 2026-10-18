using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Web;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

public partial class ApplyConformanceTests
{
    /// <summary>The web page's apply, against a real engine: refused without the person's confirmation (the database is untouched), then run as a background job once confirmed, with the same result as the command line.</summary>
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_web_page_applies_a_plan_only_when_the_person_confirmed_it(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        using var http = new HttpClient();
        try
        {
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render --write");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);
            var relative = Path.GetRelativePath(run.Dir, planFile).Replace('\\', '/');
            var id = Path.GetFileName(planFile).Replace(".plan.yml", "");

            using var server = new WebServer(run.Dir, new TuiCommand.CliHost(run.Env), 0, allowApply: true);
            _ = server.StartAsync();
            async Task<(HttpStatusCode Status, string Body)> Post(string path, JsonObject body)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}{path}") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                request.Headers.Add("X-DDB-Token", server.Token);
                var r = await http.SendAsync(request);
                return (r.StatusCode, await r.Content.ReadAsStringAsync());
            }
            async Task<JsonNode> Finished(string job)
            {
                for (var i = 0; i < 1200; i++)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/api/job?id={job}");
                    request.Headers.Add("X-DDB-Token", server.Token);
                    var j = JsonNode.Parse(await (await http.SendAsync(request)).Content.ReadAsStringAsync())!;
                    if ((bool)j["done"]!) return j;
                    await Task.Delay(50);
                }
                throw new TimeoutException("The apply did not finish.");
            }

            // not confirmed: refused, and the database has nothing
            var refused = await Post("/api/apply", new JsonObject { ["plan"] = relative, ["confirm"] = new JsonObject { ["connection"] = "somewhere", ["plan_id"] = id } });
            Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));

            // a dry run needs no confirmation and changes nothing
            var dryJob = (string)JsonNode.Parse((await Post("/api/apply", new JsonObject { ["plan"] = relative, ["dry_run"] = true })).Body)!["job"]!;
            var dry = await Finished(dryJob);
            Assert.Equal(0, (int)dry["exit"]!);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));

            // confirmed: applied, as the command line would have done it
            var started = await Post("/api/apply", new JsonObject { ["plan"] = relative, ["confirm"] = new JsonObject { ["connection"] = name, ["plan_id"] = id } });
            Assert.Equal(HttpStatusCode.Accepted, started.Status);
            var done = await Finished((string)JsonNode.Parse(started.Body)!["job"]!);
            Assert.True((int)done["exit"]! == 0, done.ToJsonString());
            Assert.Equal(3, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(3, await CountAsync(run, "marts.v_orders"));

            // the page's table diff, on the engine: the loaded table equals its source, then one amount is changed
            async Task<JsonNode> Diff(bool values)
            {
                var arguments = new JsonObject { ["table"] = "staging.orders", ["against"] = "marts.fct_orders", ["key"] = new JsonArray("order_id") };
                if (values) arguments["show_values"] = true;
                var reply = await Post("/api/run", new JsonObject { ["command"] = "connection_compare", ["arguments"] = arguments });
                Assert.Equal(HttpStatusCode.OK, reply.Status);
                return JsonNode.Parse(reply.Body)!["document"]!["data"]!;
            }
            var same = await Diff(false);
            Assert.True((bool)same["identical"]!);
            Assert.Equal(3, (int)same["left"]!["rows"]!);
            Assert.Equal(3, (int)same["rows"]!["matched"]!);
            await run.Engine.ExecAsync("UPDATE marts.fct_orders SET amount = 99.00 WHERE order_id = 1");
            var changed = await Diff(false);
            Assert.False((bool)changed["identical"]!);
            Assert.Equal(1, (int)changed["rows"]!["differing"]!);
            Assert.Null(changed["samples"]);                                   // counts only, unless the person asked for values
            var shown = await Diff(true);
            Assert.Equal("99", ((string)shown["samples"]!["differing"]![0]!["columns"]!["amount"]!["right"]!).TrimEnd('0').TrimEnd('.'));

            // the plan has been used: a second apply is refused by the command itself
            var again = (string)JsonNode.Parse((await Post("/api/apply", new JsonObject { ["plan"] = relative, ["confirm"] = new JsonObject { ["connection"] = name, ["plan_id"] = id } })).Body)!["job"]!;
            Assert.NotEqual(0, (int)(await Finished(again))["exit"]!);
        }
        finally { try { Directory.Delete(run.Dir, true); } catch (IOException) { } }
    }
}
