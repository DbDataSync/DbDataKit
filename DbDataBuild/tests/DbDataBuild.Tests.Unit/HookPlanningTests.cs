using DbDataBuild.Core.Questions;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Tests.Unit;

/// <summary>Where hooks land in a plan (decision-table row hook.fired). Offline, no database.</summary>
public class HookPlanningTests
{
    private static readonly ProjectConfig Config = ProjectConfig.Default;
    private static readonly DdlGenerator Ddl = TargetRegistry.Get("sqlserver").CreateDdl(Config);
    private static readonly ColumnDefinition[] Basic = [new("id", "BIGINT", false), new("label", "VARCHAR(20)")];

    private static PlannedHook Hook(string name, string ev, string effect = "ddl", string risk = "safe", string? group = null) =>
        new(new ResolvedHook(name, ev, $"hooks/{name}.sql", effect, risk, group), $"-- {name}\nSELECT 1;", Hashing.ScriptHash($"-- {name}\nSELECT 1;"));

    private static ModelDefinition Def(string kind = ModelKinds.Full, ColumnDefinition[]? columns = null) =>
        new("marts.fct", kind, [], null, null, [], null, columns ?? Basic, []);

    private static PlannedModel Model(ModelDefinition def, params PlannedHook[] hooks) => new(def, "SELECT 1 AS id", "models/marts/fct.sql", "h1", [], hooks);

    private static PlanInput Empty(params PlannedModel[] models) => new(
        "sqlserver", Config, models, new Dictionary<string, ObjectShape>(), new HashSet<string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, string>(), new HashSet<string>(), new Dictionary<string, IReadOnlyList<RenderedLoad>>(), new Dictionary<string, ResolverOutcome>());

    private static PlanInput Existing(PlannedModel m, ModelDefinition liveAs)
    {
        var (schema, name) = DdlGenerator.Split(liveAs.Name);
        var live = new ObjectShape(schema, name, ObjectKind.Table, DdlGenerator.ExpectedShape(Ddl.MapAll(liveAs)), []);
        return Empty(m) with
        {
            Live = new Dictionary<string, ObjectShape> { [m.Definition.Name] = live }, LiveSchemaNames = new HashSet<string> { schema },
            RecordedShapeHashes = new Dictionary<string, string> { [m.Definition.Name] = live.ShapeHash },
        };
    }

    private static RenderedLoad Load() => new("default", true, "-- load\nSELECT 1;", Hashing.ScriptHash("-- load\nSELECT 1;"), null, [], null);

    private static string[] Order(PlanResult r) => r.Steps.Select(s => s.Description).ToArray();

    [Fact]
    public void Hooks_wrap_the_steps_of_their_event_in_the_order_written()
    {
        var def = Def() with { DeclaredIndexes = [new IndexDefinition("ix_label", ["label"], false, [], null)] };
        var m = Model(def, Hook("b_after", "post_create"), Hook("a_before", "pre_create"), Hook("a_after", "post_create"), Hook("pre", "pre_load"), Hook("post", "post_load"), Hook("late", "post_load"));
        var input = Empty(m) with { Loads = new Dictionary<string, IReadOnlyList<RenderedLoad>> { ["marts.fct"] = [Load()] } };
        var r = Planner.Plan(input, []);

        Assert.Equal(["create schema marts", "hook a_before (pre_create)", "create table marts.fct", "create index ix_label", "hook b_after (post_create)", "hook a_after (post_create)",
            "hook pre (pre_load)", "load marts.fct (default)", "hook post (post_load)", "hook late (post_load)"], Order(r));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => i.ToString()), r.Steps.Select(s => s.Id));          // one numbering across the whole plan

        var hook = r.Steps.Single(s => s.Hook == "a_before");
        Assert.Equal((StepType.Hook, "pre_create", "ddl", RiskClass.Safe, "hook.fired"), (hook.Type, hook.Operation, hook.Effect, hook.Risk, hook.Reasons[0]));
        Assert.Equal("-- a_before\nSELECT 1;", hook.Text);
        Assert.Equal(Hashing.ScriptHash(hook.Text), hook.FileHash);
    }

    [Fact]
    public void Alter_hooks_wrap_the_changes_to_an_existing_object_and_do_not_fire_without_changes()
    {
        var before = Def(columns: Basic);
        var after = Def(columns: [.. Basic, new ColumnDefinition("note", "VARCHAR(5)")]);
        var hooks = new[] { Hook("lock", "pre_alter"), Hook("unlock", "post_alter"), Hook("on_create", "post_create") };

        var changed = Planner.Plan(Existing(Model(after, hooks), before), [Ans("Q-history-marts.fct.note", "not_backfilled")]);
        Assert.Equal(["hook lock (pre_alter)", "add column note", "hook unlock (post_alter)"], Order(changed));      // create hooks never fire for an existing object

        Assert.Empty(Planner.Plan(Existing(Model(before, hooks), before), []).Steps);                                  // nothing changed, so nothing is wrapped
    }

    private static ResolvedAnswer Ans(string id, string choice) => new(id, choice, null, null, AnswerSource.File);

    [Fact]
    public void Groups_effect_and_risk_are_carried_to_the_step()
    {
        var m = Model(Def(), Hook("stats", "post_load", effect: "data", risk: "risky", group: "standard"));
        var input = Empty(m) with { Loads = new Dictionary<string, IReadOnlyList<RenderedLoad>> { ["marts.fct"] = [Load()] } };
        var step = Planner.Plan(input, []).Steps.Single(s => s.Type == StepType.Hook);
        Assert.Equal(("data", RiskClass.Risky, "stats"), (step.Effect, step.Risk, step.Hook));
        Assert.Equal(["hook.fired", "event post_load", "group standard"], step.Reasons);
    }

    [Fact]
    public void A_view_gets_create_and_alter_hooks_around_its_statement()
    {
        var view = new ModelDefinition("marts.v", ModelKinds.View, [], null, null, [], null, [new("id", "BIGINT", false)], []);
        var m = new PlannedModel(view, "SELECT 1 AS id", "models/marts/v.sql", "h", [], [Hook("pre", "pre_create"), Hook("post", "post_create")]);
        Assert.Equal(["create schema marts", "hook pre (pre_create)", "create view marts.v", "hook post (post_create)"], Order(Planner.Plan(Empty(m), [])));
    }

    [Fact]
    public void Backfill_hooks_fire_only_for_a_backfill_and_load_hooks_only_for_a_load()
    {
        var m = Model(Def(), Hook("pl", "pre_load"), Hook("pb", "pre_backfill"));
        var load = Load();
        var input = Existing(m, Def()) with { Loads = new Dictionary<string, IReadOnlyList<RenderedLoad>> { ["marts.fct"] = [load] } };
        Assert.Equal(["hook pl (pre_load)", "load marts.fct (default)"], Order(Planner.Plan(input, [])));
        var backfill = input with { Backfills = new HashSet<string> { "marts.fct" }, OperationChoice = new Dictionary<string, string> { ["marts.fct"] = "default" } };
        Assert.Equal(["hook pb (pre_backfill)", "backfill marts.fct (default)"], Order(Planner.Plan(backfill, [])));
    }

    [Fact]
    public void A_blocked_or_skipped_model_runs_none_of_its_hooks()
    {
        var bad = Model(Def(columns: [new("id", "BIGINT", false), new("big", "HUGEINT")]), Hook("x", "post_create"));
        var r = Planner.Plan(Empty(bad), []);
        Assert.Equal("DDB-321", Assert.Single(r.Blocks).Code);
        Assert.DoesNotContain(r.Steps, s => s.Type == StepType.Hook);
    }

    [Fact]
    public void A_hook_runs_nothing_when_the_plan_has_no_step_for_its_event()
    {
        // an up-to-date table with no load rendered: there is nothing for load hooks to wrap
        var m = Model(Def(), Hook("pl", "pre_load"), Hook("po", "post_load"), Hook("pc", "pre_create"));
        Assert.Empty(Planner.Plan(Existing(m, Def()), []).Steps);
    }
}
