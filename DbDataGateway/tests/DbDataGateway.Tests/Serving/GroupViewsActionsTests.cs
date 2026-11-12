using System.Text.Json.Nodes;
using DbDataGateway.Server.Serving;
using DbDataKit.Views.Hosting;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 32 DBR53: the Store's answers to a change an action link made as the viewer become the views module's (the engines' own writes are the Store's tests).</summary>
public sealed class GroupViewsActionsTests
{
    [Fact]
    public void The_stores_answers_become_the_modules_statuses()
    {
        Assert.Equal(ActionStatus.Applied, GroupViewsActions.Map(StoreAnswer.Ok(new JsonObject { ["applied"] = 1 })).Status);

        var waiting = GroupViewsActions.Map(new StoreAnswer(202, new JsonObject { ["id"] = "cr-7", ["state"] = "pending" }));
        Assert.Equal((ActionStatus.Waiting, "cr-7"), (waiting.Status, waiting.RequestId));

        var conflict = GroupViewsActions.Map(new StoreAnswer(409, new JsonObject { ["error"] = "x", ["expected_version"] = 3, ["current_version"] = 4 }));
        Assert.Equal(ActionStatus.Conflict, conflict.Status);
        Assert.Contains("version 4", conflict.Message);

        var refused = GroupViewsActions.Map(new StoreAnswer(422, new JsonObject
        {
            ["error"] = "The change breaks the model's rules, so nothing was written.",
            ["violations"] = new JsonArray(new JsonObject { ["rule"] = "max", ["entity"] = "orders", ["attribute"] = "units", ["key"] = new JsonObject { ["id"] = 2 }, ["message"] = "units is above the maximum of 100." }),
        }));
        Assert.Equal(ActionStatus.Refused, refused.Status);
        Assert.Equal(("max", "units", "units is above the maximum of 100."), (refused.Violations![0].Rule, refused.Violations[0].Attribute, refused.Violations[0].Message));

        Assert.Equal(ActionStatus.Denied, GroupViewsActions.Map(StoreAnswer.Error(403, "This needs the author role on this group.")).Status);
        Assert.Equal(ActionStatus.Refused, GroupViewsActions.Map(StoreAnswer.Error(404, "`orders` has no such row.")).Status);
        var down = GroupViewsActions.Map(StoreAnswer.Error(502, "The database could not be reached."));
        Assert.Equal((ActionStatus.Failed, "The database could not be reached."), (down.Status, down.Message));
    }
}
