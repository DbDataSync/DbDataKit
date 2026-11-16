using System.Net;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataKit.Lineage;
using DbDataKit.Schema.Planning;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 21 CH06, Build's apply: a plan that drops, renames away or retypes a column a published consumer reads is refused (DDB-444) naming the consumer and its channel, whatever channel it is published in, until the consumer is published without the
/// column or the person allows it by name; the escape is recorded. The Gateway's graph is a fake here (the real Gateway's lineage and its channel property are tested in <c>DbDataGateway.Tests</c>): what is under test is which columns Build asks about, how
/// a table is named, and what is done with the answer.
/// </summary>
public sealed class BuildContractGuardTests
{
    private static PlanStep Ddl(string id, string obj, string description, params string[] reasons) =>
        new(id, StepType.Ddl, obj, description, "ALTER TABLE x", reasons.Contains("col.removed") || reasons.Contains("col.type.other") ? RiskClass.Destructive : RiskClass.Safe, reasons, null, []);

    private static Plan PlanWith(params PlanStep[] steps) => new("2026-11-12-abcd1234", "sqlserver", null, false, "0.1.0", [], [], steps, []);

    private static readonly Dictionary<string, string?> Env = new()
    {
        ["DBDATAKIT_BUILD_SQLSERVER_READ"] = "Server=db.example;Database=Sales;User Id=r;Password=hunter2",
        ["DBDATAKIT_BUILD_SQLSERVER_WRITE"] = "Server=db.example;Database=Sales;User Id=w;Password=hunter2",
    };

    private static ProjectConfig Config()
    {
        var diagnostics = new List<Diagnostic>();
        return ProjectConfigLoader.LoadFromProject(NewProjectDir(), diagnostics);
    }

    [Fact]
    public void Only_the_steps_that_take_a_column_away_are_asked_about()
    {
        var plan = PlanWith(
            Ddl("1", "marts.fct", "drop column price", "col.removed"),
            Ddl("2", "marts.fct", "rename column qty to quantity", "col.renamed"),
            Ddl("3", "marts.fct", "alter column note", "col.type.other"),
            Ddl("4", "marts.fct", "alter column wide", "col.type.widen"),
            Ddl("5", "marts.fct", "alter column opt", "col.null.relax"),
            Ddl("6", "marts.fct", "add column extra", "col.added"));
        Assert.Equal([("1", "drop", "price"), ("2", "rename", "qty"), ("3", "retype", "note")], BuildContractGuard.Steps(plan).Select(s => (s.Step.Id, s.Verb, s.Column)));
    }

    private sealed class Pairings(params GatewayPairing[] all) : IGatewayPairings
    {
        public IReadOnlyList<GatewayPairing> All() => all;
    }

    private sealed class Fake(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer());
        }
    }

    private static GatewayLineageClient Client(Fake gateway) =>
        new(new Pairings(new GatewayPairing("edge", "https://127.0.0.1:1", "gateway-1", "ab", DateTimeOffset.UnixEpoch)), _ => Task.FromResult(new ConsoleIdentity("build-1", ConsoleSigningKey.Generate())), product: "build", handler: _ => gateway);

    /// <summary>The Gateway's graph as it holds the table the plan changes: a dashboard reads the price in production and in test, an app reads the table as a whole.</summary>
    private static Fake GatewayWith(ProjectConfig config)
    {
        var definition = BuildLineage.IdentityOf(config, name => Env.GetValueOrDefault(name))("sqlserver");
        var dataset = LineageIdentity.Dataset(definition, "marts", "fct");
        var column = LineageIdentity.Column(definition, dataset, "price");
        LineageNode Consumer(string id, string name, string kind, string channel) => new(id, NodeKind.Consumer, name, false, new Dictionary<string, string> { ["consumer"] = kind, ["channel"] = channel });
        var prod = Consumer("suite://g/dashboard/sales", "sales", "dashboard", "prod");
        var test = Consumer("suite://g/dashboard/sales~test", "sales", "dashboard", "test");
        var graph = new LineageGraph([new(dataset.ToString(), NodeKind.Dataset, "marts.fct"), new(column, NodeKind.Column, "price"), new("suite://g/semantic-model/sales", NodeKind.Serving, "sales"), prod, test],
        [
            new(column, "suite://g/semantic-model/sales", EdgeKind.Binds, Granularity.Column, Confidence.Derived),
            new("suite://g/semantic-model/sales", prod.Id, EdgeKind.Reads, Granularity.Table, Confidence.Declared),
            new("suite://g/semantic-model/sales", test.Id, EdgeKind.Reads, Granularity.Table, Confidence.Declared),
        ]);
        return new Fake(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LineageJson.Write(graph).ToJsonString(), System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task A_dropped_column_a_dashboard_reads_is_refused_naming_the_dashboard_and_each_channel()
    {
        var config = Config();
        var gateway = GatewayWith(config);
        var plan = PlanWith(Ddl("1", "marts.fct", "drop column price", "col.removed"));

        var (refusals, used, note) = await BuildContractGuard.CheckAsync(plan, config, name => Env.GetValueOrDefault(name), [], _ => Client(gateway));

        Assert.Null(note);
        var refusal = Assert.Single(refusals);
        Assert.Equal("DDB-444", refusal.Code);
        Assert.Contains("the dashboard 'sales' published in channel 'test'", refusal.Found);
        Assert.Contains("the dashboard 'sales' published in channel 'prod'", refusal.Found);
        Assert.Contains("drop the column 'price' of 'marts.fct'", refusal.Found);
        Assert.Contains("--allow-consumer sales@test", refusal.Found);
        Assert.Empty(used);
    }

    [Fact]
    public async Task A_column_nobody_reads_and_a_plan_that_drops_nothing_pass()
    {
        var config = Config();
        var gateway = GatewayWith(config);
        var other = PlanWith(Ddl("1", "marts.fct", "drop column discount", "col.removed"));
        Assert.Empty((await BuildContractGuard.CheckAsync(other, config, name => Env.GetValueOrDefault(name), [], _ => Client(gateway))).Refusals);
        var adds = PlanWith(Ddl("1", "marts.fct", "add column extra", "col.added"));
        var before = gateway.Calls;
        Assert.Empty((await BuildContractGuard.CheckAsync(adds, config, name => Env.GetValueOrDefault(name), [], _ => Client(gateway))).Refusals);
        Assert.Equal(before, gateway.Calls);                       // a plan that takes nothing away asks nothing
    }

    [Fact]
    public async Task After_the_consumers_are_republished_without_the_column_the_plan_goes_through()
    {
        var config = Config();
        var republished = new Fake(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LineageJson.Write(LineageGraph.Empty).ToJsonString(), System.Text.Encoding.UTF8, "application/json") });
        var plan = PlanWith(Ddl("1", "marts.fct", "drop column price", "col.removed"));
        var (refusals, _, _) = await BuildContractGuard.CheckAsync(plan, config, name => Env.GetValueOrDefault(name), [], _ => Client(republished));
        Assert.Empty(refusals);
        Assert.Equal(1, republished.Calls);
    }

    [Fact]
    public async Task Each_consumer_is_allowed_by_name_and_channel_and_the_allowances_used_are_returned_for_the_record()
    {
        var config = Config();
        var plan = PlanWith(Ddl("1", "marts.fct", "drop column price", "col.removed"));
        Func<string, string?> env = name => Env.GetValueOrDefault(name);

        var partial = await BuildContractGuard.CheckAsync(plan, config, env, ["sales@prod"], _ => Client(GatewayWith(config)));
        Assert.Contains("channel 'test'", Assert.Single(partial.Refusals).Found);
        Assert.DoesNotContain("channel 'prod'", partial.Refusals[0].Found.Split("To go on")[0]);

        var all = await BuildContractGuard.CheckAsync(plan, config, env, ["sales@prod", "sales@test"], _ => Client(GatewayWith(config)));
        Assert.Empty(all.Refusals);
        Assert.Equal(["sales@prod", "sales@test"], all.AllowancesUsed);

        var byName = await BuildContractGuard.CheckAsync(plan, config, env, ["sales"], _ => Client(GatewayWith(config)));
        Assert.Empty(byName.Refusals);
        Assert.Equal(["sales"], byName.AllowancesUsed);
    }

    [Fact]
    public async Task A_run_given_no_lineage_says_it_asked_nobody()
    {
        var plan = PlanWith(Ddl("1", "marts.fct", "drop column price", "col.removed"));
        var (refusals, _, note) = await BuildContractGuard.CheckAsync(plan, Config(), name => Env.GetValueOrDefault(name), []);
        Assert.Empty(refusals);
        Assert.Contains("no Gateway lineage to ask", note);
    }
}
