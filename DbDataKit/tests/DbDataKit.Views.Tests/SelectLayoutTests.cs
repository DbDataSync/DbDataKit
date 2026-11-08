using System.Text.Json.Nodes;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>The goldens of <c>selectLayout</c>: <c>conformance/views/select-layout/cases.json</c>, which the TypeScript twin runs too.</summary>
public class SelectLayoutTests
{
    public static IEnumerable<object[]> Cases() =>
        Conformance.Cases("select-layout/cases.json")["cases"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });

    [Theory, MemberData(nameof(Cases))]
    public void The_golden_holds(int index, string name)
    {
        var c = Conformance.Cases("select-layout/cases.json")["cases"]![index]!;
        Assert.Equal(name, c["name"]!.GetValue<string>());
        var file = "select-layout/" + c["doc"]!.GetValue<string>();
        var loaded = ViewsLoader.Load(Conformance.Read(file), file);
        Assert.True(loaded.Document is not null, Conformance.Describe(loaded.Diagnostics));
        var ctx = c["context"]!;
        var context = new LayoutContext(ctx["width"]?.GetValue<double>(), ctx["height"]?.GetValue<double>(), ctx["medium"]?.GetValue<string>() ?? "screen", ctx["layout"]?.GetValue<string>());
        var selection = LayoutSelector.Select(loaded.Document!, context);
        if (c["refused"] is { } refused)
        {
            Assert.False(selection.Ok);
            Assert.Equal(refused.GetValue<string>(), selection.Refusal);
            return;
        }
        Assert.True(selection.Ok, selection.Refusal);
        Assert.Equal(c["layout"]!.GetValue<string>(), selection.Layout!.Name);
        Assert.Equal(c["choice"]!.GetValue<string>(), selection.Choice!.Value.ToString().ToLowerInvariant());
    }

    [Fact]
    public void Selection_does_not_depend_on_the_order_of_conditions_only_on_their_number()
    {
        var doc = ViewsLoader.Load(Conformance.Read("select-layout/ties.json"), "ties.json").Document!;
        Assert.Equal([0, 1, 1, 2, 1], doc.Layouts.Select(l => l.When?.Specificity ?? 0));
        Assert.True(doc.Layouts[1].When!.SameAs(doc.Layouts[2].When!));
        Assert.False(doc.Layouts[1].When!.SameAs(doc.Layouts[3].When!));
    }

    [Fact]
    public void A_layout_with_no_selector_is_never_a_candidate_even_when_it_is_the_default()
    {
        var doc = ViewsLoader.Load(Conformance.Read("select-layout/dashboard.json"), "d.json").Document!;
        var selection = LayoutSelector.Select(doc, new LayoutContext(1366, 768));
        Assert.Equal(LayoutChoice.Default, selection.Choice);
    }
}
