using System.Text.Json;
using DbDataKit.Cli;
using DbDataKit.Quality.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;
using Json.Schema;

namespace DbDataKit.Quality.Runner.Tests;

/// <summary>Plan 23a FQ08: <c>quality run</c> and <c>quality show</c> on the shared command surface, with their JSON documents checked against the router's schema.</summary>
public sealed class QualityNounTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-08T12:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly QualityData _data = new();

    public static TheoryData<string> Engines => StateEngines.Engines;

    public void Dispose()
    {
        _data.Dispose();
        _engines.Dispose();
    }

    private NounRouter Router(string engine, IReadOnlyList<CheckDefinition> checks, out QualityStore store)
    {
        var opened = new QualityStore(_engines.Provision(engine).Open([QualityModule.Module], time: new StateTestClock(Start)));
        store = opened;
        return new NounRouter("demo", [QualityNoun.Create(_data.Source(checks), () => opened)]);
    }

    private static async Task<(int Exit, string Out, string Err)> Text(NounRouter router, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await router.RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private static async Task<(int Exit, JsonElement Root)> Json(NounRouter router, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await router.RunAsync([.. args, "--format", "json"], new CliContext(output, error, TextReader.Null));
        var root = JsonDocument.Parse(output.ToString()).RootElement;
        Assert.True(JsonSchema.FromText(router.OutputSchema()).Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, root.ToString());
        return (exit, root);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Run_evaluates_the_checks_records_them_and_exits_1_when_one_fails(string engine)
    {
        var router = Router(engine, [QualityData.NotNullCustomer(), QualityData.Unique()], out var store);

        var (exit, output, _) = await Text(router, "quality", "run", "--dataset", "orders");

        Assert.Equal(1, exit);
        Assert.Contains("FAIL  customer_present (not_null, block) measured 2, allowed 0  BLOCKS dependents", output);
        Assert.Contains("keys: id=2 | id=4", output);
        Assert.Contains("PASS  id_unique", output);
        Assert.Contains("2 checks: 1 passed, 1 failed, 0 could not run; 1 blocking.", output);
        Assert.Single(store.Blocks());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Run_exits_0_when_every_check_passes(string engine)
    {
        var router = Router(engine, [QualityData.Unique()], out _);

        var (exit, _) = await Json(router, "quality", "run");

        Assert.Equal(0, exit);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_run_document_has_the_checks_their_measures_and_sample_keys_and_the_totals(string engine)
    {
        var router = Router(engine, [QualityData.NotNullCustomer(), QualityData.Unique()], out _);

        var (exit, root) = await Json(router, "quality", "run", "--dataset", QualityData.Orders);

        Assert.Equal(1, exit);
        var data = root.GetProperty("data");
        Assert.Equal((2, 1, 1, 0, 1), (data.GetProperty("evaluated").GetInt32(), data.GetProperty("passed").GetInt32(), data.GetProperty("failed").GetInt32(), data.GetProperty("errors").GetInt32(), data.GetProperty("blocking").GetInt32()));
        var dataset = data.GetProperty("datasets")[0];
        Assert.Equal(QualityData.Orders, dataset.GetProperty("dataset").GetString());
        var check = dataset.GetProperty("checks")[0];
        Assert.Equal(("customer_present", "not_null", "block", "fail", true), (check.GetProperty("id").GetString(), check.GetProperty("kind").GetString(), check.GetProperty("severity").GetString(), check.GetProperty("status").GetString(), check.GetProperty("blocking").GetBoolean()));
        Assert.Equal(2, check.GetProperty("measured").GetDecimal());
        Assert.Equal(["id=2", "id=4"], check.GetProperty("sample_keys").EnumerateArray().Select(k => k.GetString()).ToArray());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_check_that_could_not_run_is_an_error_in_the_document_and_exits_1(string engine)
    {
        var opened = new QualityStore(_engines.Provision(engine).Open([QualityModule.Module], time: new StateTestClock(Start)));
        var router = new NounRouter("demo", [QualityNoun.Create(_data.Source([QualityData.NotNullCustomer()], down: true), () => opened)]);

        var (exit, root) = await Json(router, "quality", "run");

        Assert.Equal(1, exit);
        var check = root.GetProperty("data").GetProperty("datasets")[0].GetProperty("checks")[0];
        Assert.Equal("error", check.GetProperty("status").GetString());
        Assert.StartsWith("The connection could not be opened", check.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("measured").ValueKind);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task An_unknown_dataset_exits_1_and_a_wrong_use_exits_2(string engine)
    {
        var router = Router(engine, [QualityData.Unique()], out _);

        var (unknown, _, error) = await Text(router, "quality", "run", "--dataset", "nowhere");
        var (usage, _, usageError) = await Text(router, "quality", "run", "extra");
        var (badShow, _, _) = await Text(router, "quality", "show", "--history", "0");

        Assert.Equal(1, unknown);
        Assert.Contains("'nowhere'", error);
        Assert.Equal(2, usage);
        Assert.Contains("Usage: quality run", usageError);
        Assert.Equal(2, badShow);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Show_lists_the_latest_status_of_each_check_and_what_blocks_and_reads_state_only(string engine)
    {
        var router = Router(engine, [QualityData.NotNullCustomer(), QualityData.Unique()], out var store);

        var (emptyExit, empty, _) = await Text(router, "quality", "show");
        Assert.Equal((0, "No check has a result yet.\n"), (emptyExit, empty.Replace("\r\n", "\n")));

        await Text(router, "quality", "run");
        var (exit, root) = await Json(router, "quality", "show");

        Assert.Equal(0, exit);
        var data = root.GetProperty("data");
        Assert.Equal(1, data.GetProperty("blocking").GetInt32());
        var rows = data.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("check").GetString()!);
        Assert.Equal("fail", rows["customer_present"].GetProperty("status").GetString());
        Assert.True(rows["customer_present"].GetProperty("blocking").GetBoolean());
        Assert.Equal("pass", rows["id_unique"].GetProperty("status").GetString());
        Assert.Equal(2, store.History(QualityData.Orders, "customer_present", 5).Count + 1);   // show evaluated nothing: still the one result of the run

        var (_, blocking) = await Json(router, "quality", "show", "--blocking");
        Assert.Equal(["customer_present"], blocking.GetProperty("data").GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("check").GetString()).ToArray());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Show_with_history_lists_the_earlier_evaluations_with_their_sample_keys(string engine)
    {
        var router = Router(engine, [QualityData.NotNullCustomer()], out _);
        await Text(router, "quality", "run");
        await Text(router, "quality", "run");

        var (_, root) = await Json(router, "quality", "show", "--dataset", "orders", "--history", "5");
        var history = root.GetProperty("data").GetProperty("checks")[0].GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("fail", history[0].GetProperty("status").GetString());
        Assert.Equal(["id=2", "id=4"], history[0].GetProperty("sample_keys").EnumerateArray().Select(k => k.GetString()).ToArray());

        var (text, output, _) = await Text(router, "quality", "show", "--dataset", "orders", "--history", "1");
        Assert.Equal(0, text);
        Assert.Contains("keys: id=2 | id=4", output);
    }

    [Fact]
    public void The_verbs_have_data_schemas_and_the_noun_is_in_the_help()
    {
        var router = new NounRouter("demo", [QualityNoun.Create(new StaticQualitySource([]), () => throw new InvalidOperationException("no state is opened to read the help"))]);

        Assert.Contains("quality", router.Help());
        Assert.Contains("quality run [--dataset <identity or name>]", router.NounHelp(router.Nouns.Single()));
        Assert.Contains("data_quality_run", router.OutputSchema());
        Assert.Contains("data_quality_show", router.OutputSchema());
    }
}
