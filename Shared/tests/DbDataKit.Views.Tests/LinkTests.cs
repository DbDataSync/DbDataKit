using System.Text.Json.Nodes;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>
/// The link vocabulary of plan 32 section 9f (DBR35). The refusals themselves are cases of <c>conformance/views/cases.json</c> (run by <see cref="LoaderTests"/> and, with the same documents, by
/// <c>@dbdatakit/visuals</c>); this is what a good document reads as, that its targets are accepted, and the pure type rule both languages share (<c>conformance/views/links.json</c>).
/// </summary>
public class LinkTests
{
    private static ViewDocument Good() => ViewsLoader.Load(Conformance.Read("good/dashboard-links.json"), "good/dashboard-links.json", ViewKind.Dashboard).Document!;

    [Fact]
    public void A_document_declares_links_of_every_target_kind_and_its_widgets_follow_them_by_name()
    {
        var links = Good().Links;
        Assert.Equal(["region-page", "order-form", "order", "year-report", "other-dashboard", "a-view"], links.Keys);
        Assert.Equal(["page", "form", "form", "report", "dashboard", "view"], links.Values.Select(l => l.TargetKind));
        var form = links["order-form"];
        Assert.Equal("new", form.ValuesKey);
        Assert.Equal(("dialog", "refresh", false), (form.As, form.ThenKind, form.HasAddress));
        Assert.Equal(["region"], form.Lock);
        Assert.Equal("hidden", form.Print);
        Assert.Equal(LinkValueKind.Field, form.Values["customer_id"].Kind);
        Assert.Equal("customers.id", form.Values["customer_id"].Text);
        Assert.Equal(("literal", "string"), (form.Values["status"].Kind.ToString().ToLowerInvariant(), form.Values["status"].LiteralType));
        var edit = links["order"];
        Assert.Equal(("edit", "panel", "widgets"), (edit.ValuesKey, edit.As, edit.ThenKind));
        Assert.Equal(["orders_table"], edit.ThenWidgets);
        Assert.Equal(("normal", "normal", "Open the order"), (edit.Print, edit.Image, edit.Label));
        var page = links["region-page"];
        Assert.Equal(("navigate", "none", true, "link"), (page.As, page.ThenKind, page.HasAddress, page.Print));
        Assert.Equal(LinkValueKind.Param, page.Values["year"].Kind);
        Assert.Equal("integer", links["year-report"].Values["year"].LiteralType);
        Assert.Equal("page:regional-detail", page.TargetKey);
    }

    [Fact]
    public void A_document_without_links_has_none() =>
        Assert.Empty(ViewsLoader.Load(Conformance.Read("good/report-minimal.json"), "r.json").Document!.Links);

    [Fact]
    public void The_good_document_is_accepted_by_every_target_it_names()
    {
        var targets = Conformance.Targets(Conformance.Cases("links.json")["targets"]);
        var result = ViewsLoader.Load(Conformance.Read("good/dashboard-links.json"), "good/dashboard-links.json", ViewKind.Dashboard, targets);
        Assert.True(result.Ok, Conformance.Describe(result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void A_widgets_map_read_alone_does_not_resolve_on_click_because_it_has_no_links()
    {
        var text = """{"by_region":{"type":"chart","mark":"bar","encodings":{"x":"customers.region","y":"orders.net_revenue"},"query":{"model":"sales","limit":5},"on_click":"somewhere"}}""";
        var result = ViewsLoader.LoadWidgets(text, "w.json");
        Assert.True(result.Ok, Conformance.Describe(result.Diagnostics));
    }

    [Fact]
    public void A_metric_has_no_on_click()
    {
        var text = """{"m":{"type":"metric","encodings":{"value":"orders.net_revenue"},"query":{"model":"sales","limit":5},"on_click":"x"}}""";
        var result = ViewsLoader.LoadWidgets(text, "w.json");
        Assert.Contains(result.Diagnostics, d => d.Code == ViewsCodes.UnknownKey && d.KeyPath == "widgets.m.on_click");
    }

    public static IEnumerable<object[]> Fits => Conformance.Cases("links.json")["fits"]!.AsArray().Select(c => new object[]
    {
        c!["given"]!.GetValue<string>(), c["many"]!.GetValue<bool>(), c["wanted"]!.GetValue<string>(), c["wantedMany"]!.GetValue<bool>(), c["fits"]!.GetValue<bool>(),
    });

    [Theory, MemberData(nameof(Fits))]
    public void A_value_fits_a_parameter_by_the_shared_rule(string given, bool many, string wanted, bool wantedMany, bool fits) =>
        Assert.Equal(fits, ViewsLoader.LinkTypeFits(given, many, wanted, wantedMany));
}
