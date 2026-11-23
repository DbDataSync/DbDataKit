using System.Text.Json.Nodes;
using DbDataKit.Lineage;

namespace DbDataKit.Deploy.Tests;

public sealed class ContractPlansTests
{
    private static readonly Guid PlanRun = Guid.Parse("6f1d2c3b-0000-4000-8000-000000000001");

    private static JsonObject Run(JsonObject? report) =>
        new() { ["task"] = "contract", ["steps"] = report is null ? new JsonArray() : new JsonArray(new JsonObject { ["key"] = "k", ["result"] = report }) };

    [Fact]
    public void A_plan_is_run_only_with_the_hash_the_person_read()
    {
        var wrong = ContractPlans.Read(Run(new JsonObject { ["dryRun"] = true, ["hash"] = "abc" }), PlanRun, "def");
        var notDry = ContractPlans.Read(Run(new JsonObject { ["dryRun"] = false, ["hash"] = "abc" }), PlanRun, "abc");
        var none = ContractPlans.Read(Run(null), PlanRun, "abc");

        Assert.Equal($"The run {PlanRun} did not read a plan with the hash def. Read the plan again and approve the hash it gives.", wrong.Refusal);
        Assert.All([wrong, notDry, none], r => Assert.Null(r.Plan));
        Assert.False(wrong.Blocked);
    }

    [Fact]
    public void A_plan_with_items_a_contract_does_not_do_is_blocked_by_name()
    {
        var reading = ContractPlans.Read(Run(new JsonObject { ["dryRun"] = true, ["hash"] = "ABC", ["blocked"] = new JsonArray("Retyping customer.fax needs a migration.") }), PlanRun, "abc");

        Assert.True(reading.Blocked);
        Assert.Equal("The plan has items a contract does not do, so it is not run: Retyping customer.fax needs a migration.", reading.Refusal);
    }

    [Fact]
    public void An_approved_plan_gives_the_columns_it_drops_and_the_inputs_of_the_run_that_applies_it()
    {
        var reading = ContractPlans.Read(Run(new JsonObject
        {
            ["dryRun"] = true, ["hash"] = "abc", ["schema"] = "crm",
            ["dropped"] = new JsonArray(new JsonObject { ["description"] = "drop the column 'fax' of 'customer'", ["columnId"] = "col:crm.customer.fax", ["datasetId"] = "ds:crm.customer" }),
        }), PlanRun, "abc");

        var plan = Assert.IsType<ContractPlan>(reading.Plan);
        Assert.Equal(new ContractedColumn("drop the column 'fax' of 'customer'", "col:crm.customer.fax", "ds:crm.customer"), Assert.Single(plan.Dropped));
        Assert.Equal(new Dictionary<string, string> { ["approve"] = "abc", ["schema"] = "crm" }, ContractPlans.Inputs(plan, "abc"));
    }

    [Fact]
    public void A_gateway_that_could_not_be_asked_refuses_the_step_until_a_person_allows_it()
    {
        var plan = new ContractPlan([new ContractedColumn("drop the column 'fax' of 'customer'", "col:crm.customer.fax", null)], "crm");

        var refused = ContractPlans.CheckConsumers(plan, [], [("edge", "it answered 500")], []);
        var allowed = ContractPlans.CheckConsumers(plan, [], [("edge", "it answered 500")], ["gateway:edge"]);

        Assert.NotEmpty(refused.Refusals);
        Assert.Empty(allowed.Refusals);
        Assert.Equal(["gateway:edge"], allowed.AllowancesUsed);
    }
}
