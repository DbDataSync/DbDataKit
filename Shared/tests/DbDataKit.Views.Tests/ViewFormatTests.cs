using System.Text.Json.Nodes;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>The goldens of the format vocabulary and its formatter (DBR40): <c>conformance/views/format/cases.json</c>, which <c>format.test.ts</c> in <c>@dbdatakit/visuals</c> runs too.</summary>
public class ViewFormatTests
{
    private static JsonNode Golden() => Conformance.Cases("format/cases.json");

    public static IEnumerable<object[]> Formats() => Golden()["formats"]!.AsArray().Select((c, i) => new object[] { i, c!["name"]!.GetValue<string>() });
    public static IEnumerable<object[]> Bad() => Golden()["bad"]!.AsArray().Select((c, i) => new object[] { i, c!["format"]?.ToJsonString() ?? "null" });

    [Theory, MemberData(nameof(Formats))]
    public void The_golden_holds(int index, string name)
    {
        var c = Golden()["formats"]![index]!;
        Assert.Equal(name, c["name"]!.GetValue<string>());
        Assert.Equal(c["text"]!.GetValue<string>(), ViewFormat.Text(c["value"], c["format"], c["catalog"] as JsonObject));
    }

    [Fact]
    public void Every_good_golden_is_a_format_the_check_accepts()
    {
        foreach (var c in Golden()["formats"]!.AsArray())
            if (c!["format"] is { } f) Assert.Null(ViewFormat.Check(f));
    }

    [Theory, MemberData(nameof(Bad))]
    public void A_bad_format_is_refused_with_its_key_and_words(int index, string shown)
    {
        _ = shown;
        var c = Golden()["bad"]![index]!;
        var problem = ViewFormat.Check(c["format"]);
        Assert.NotNull(problem);
        Assert.Equal(c["key"]!.GetValue<string>(), problem!.Value.Key);
        Assert.Equal(c["message"]!.GetValue<string>(), problem.Value.Message);
    }

    [Theory]
    [InlineData("catalog"), InlineData("full"), InlineData("compact"), InlineData("percent")]
    public void The_four_presets_of_the_first_release_stay_valid(string preset) => Assert.Null(ViewFormat.Check(JsonValue.Create(preset)));

    [Theory]
    [InlineData(1234567.891, "full"), InlineData(0.5, "full"), InlineData(1234567.891, "compact"), InlineData(0.256, "percent"), InlineData(-1234.5, "full"), InlineData(1.5, "compact"), InlineData(0.0, "percent")]
    public void The_presets_read_as_the_engine_reads_them(double v, string preset) =>
        Assert.Equal(DbDataKit.Semantic.Presentation.MatrixView.Format(v, preset), ViewFormat.Number(v, JsonValue.Create(preset)));

    [Fact]
    public void The_presets_read_as_the_engine_reads_them_with_a_catalog_format()
    {
        var catalog = new JsonObject { ["style"] = "currency", ["currency"] = "EUR", ["decimals"] = 2 };
        foreach (var preset in new[] { "full", "compact", "percent" })
            foreach (var v in new[] { 1.0, 1234.567, -0.004, 2.5e6 })
                Assert.Equal(DbDataKit.Semantic.Presentation.MatrixView.Format(v, preset, catalog), ViewFormat.Number(v, JsonValue.Create(preset), catalog));
    }
}
