using System.Text.Json.Nodes;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>
/// Actions (plan 32 DBR53): the <c>change</c> and <c>task</c> link targets. The refusals are cases of <c>conformance/views/cases.json</c> (run by <see cref="LoaderTests"/> and by <c>@dbdatakit/visuals</c>); this is what the good
/// document reads as and the derivation of the confirmation popup, whose cases (<c>conformance/views/actions.json</c>) the TypeScript package runs too.
/// </summary>
public class ActionTests
{
    private static ViewDocument Good() => ViewsLoader.Load(Conformance.Read("good/dashboard-actions.json"), "good/dashboard-actions.json", ViewKind.Dashboard).Document!;

    [Fact]
    public void A_document_declares_actions_that_change_a_row_or_run_a_task()
    {
        var links = Good().Links;
        Assert.Equal(["close-order", "set-status", "new-order", "drop-order", "rerun-load"], links.Keys);
        Assert.Equal([("change", "sales.orders", "edit"), ("change", "sales.orders", "edit"), ("change", "sales.orders", "insert"), ("change", "sales.orders", "delete"), ("task", "nightly.load", "run")],
            links.Values.Select(l => (l.TargetKind, l.Target, l.Action!.Operation)));
        Assert.All(links.Values, l => Assert.False(l.HasAddress));
        var close = links["close-order"].Action!;
        Assert.Equal(("sales", "orders"), (close.Model, close.Entity));
        Assert.Equal("orders.version", close.Version!.Text);
        Assert.Equal("Closed", close.Set["status"].Text);
        Assert.Equal(["orders.number", "orders.customer"], close.Confirm.Show);
        Assert.Equal("refresh", links["close-order"].ThenKind);
        Assert.Equal("widgets", links["set-status"].ThenKind);
        Assert.Equal(["orders_table"], links["set-status"].ThenWidgets);
        var ask = links["set-status"].Action!;
        Assert.Equal(LinkValueKind.Ask, ask.Set["status"].Kind);
        Assert.Equal(["Open", "Held", "Closed"], ask.Ask["status"].Choices);
        Assert.False(ask.Ask["note"].Required);
        Assert.Equal(("nightly", "load"), (links["rerun-load"].Action!.Package, links["rerun-load"].Action!.Task));
        Assert.Equal(["integer", "boolean"], [links["rerun-load"].Action!.Set["days"].LiteralType, links["rerun-load"].Action!.Set["full"].LiteralType]);
    }

    [Fact]
    public void The_good_document_loads_against_the_targets_of_the_app_too()
    {
        var targets = Conformance.Targets(Conformance.Cases("links.json")["targets"]);
        var result = ViewsLoader.Load(Conformance.Read("good/dashboard-actions.json"), "good/dashboard-actions.json", ViewKind.Dashboard, targets);
        Assert.True(result.Ok, Conformance.Describe(result.Diagnostics));
    }

    public static IEnumerable<object[]> Cases => Conformance.Cases("actions.json")["cases"]!.AsArray().Select(c => new object[] { c!["name"]!.GetValue<string>() });

    [Theory, MemberData(nameof(Cases))]
    public void The_popup_says_what_the_declaration_and_the_row_say(string name)
    {
        var golden = Conformance.Cases("actions.json");
        var c = golden["cases"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var link = Good().Links[c["link"]!.GetValue<string>()];
        var answers = (c["answers"] as JsonObject)?.ToDictionary(p => p.Key, p => (string?)p.Value!.GetValue<string>()) ?? [];
        var resolved = ActionResolution.Resolve(link, c["row"] as JsonObject, c["params"] as JsonObject, answers);
        Assert.True(JsonNode.DeepEquals(c["expect"], resolved.ToJson()), $"expected {c["expect"]!.ToJsonString()}{Environment.NewLine}actual   {resolved.ToJson().ToJsonString()}");
    }

    [Fact]
    public void A_link_that_is_not_an_action_cannot_be_resolved() =>
        Assert.Throws<ArgumentException>(() => ActionResolution.Resolve(ViewsLoader.Load(Conformance.Read("good/dashboard-links.json"), "x.json", ViewKind.Dashboard).Document!.Links["order-form"]));
}
