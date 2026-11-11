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
        Assert.Equal(c["text"]!.GetValue<string>(), ViewFormat.Text(c["value"], c["format"], c["catalog"] as JsonObject, c["locale"]?.GetValue<string>()));
    }

    [Fact]
    public void The_locale_table_has_the_seven_locales_with_twelve_months_each()
    {
        Assert.Equal(["en", "de", "fr", "es", "it", "pt", "nl"], ViewFormat.Locales);
        foreach (var code in ViewFormat.Locales)
        {
            var l = ViewFormat.Locale(code);
            Assert.Equal(12, l.Months.Count);
            Assert.Equal(12, l.Short.Count);
            Assert.NotEqual(l.Decimal, l.Group);
        }
        Assert.Equal("en", ViewFormat.Locales[0]);
        Assert.Same(ViewFormat.Locale("en"), ViewFormat.Locale("klingon"));
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

    [Fact]
    public void A_member_label_reads_the_catalogs_date_format_when_the_widget_gives_none()
    {
        var catalog = new JsonObject { ["date"] = "month-year" };
        Assert.Equal("Mar 2026", ViewFormat.Label("2026-03", null, catalog));
        Assert.Equal("Mar 2026", ViewFormat.Label("2026-03", JsonValue.Create("catalog"), catalog));
        Assert.Equal("2026", ViewFormat.Label("2026-03", new JsonObject { ["date"] = "yyyy" }, catalog));   // the widget's own format wins
        Assert.Equal("2026-03", ViewFormat.Label("2026-03", null, null));
        Assert.Equal("not a date", ViewFormat.Label("not a date", null, catalog));                           // text that is no date is left as it is
    }

    [Theory]
    [InlineData("full", null, ",.2~f"), InlineData("compact", null, ".2~s"), InlineData("percent", null, ".0%")]
    public void A_preset_maps_to_a_d3_string(string preset, string? unused, string expected)
    {
        _ = unused;
        Assert.Equal(expected, ViewFormat.D3Number(JsonValue.Create(preset)));
    }

    [Fact]
    public void A_number_mapping_maps_to_a_d3_string_and_a_date_mapping_has_none()
    {
        static JsonNode F(string json) => JsonNode.Parse(json)!;
        Assert.Equal("$,.0f", ViewFormat.D3Number(F("""{"style":"currency","currency":"USD","decimals":0}""")));
        Assert.Equal(",.2f", ViewFormat.D3Number(F("""{"style":"currency","currency":"EUR"}""")));              // d3 cannot say the euro sign: digits only
        Assert.Equal(",.0f", ViewFormat.D3Number(F("""{"style":"currency","currency":"JPY"}""")));
        Assert.Equal(".1%", ViewFormat.D3Number(F("""{"style":"percent","decimals":1}""")));
        Assert.Equal(".3f", ViewFormat.D3Number(F("""{"style":"number","decimals":3,"grouping":false}""")));
        Assert.Equal(",.2~f", ViewFormat.D3Number(F("""{"style":"number"}""")));
        Assert.Equal(".2~s", ViewFormat.D3Number(F("""{"style":"compact"}""")));
        Assert.Equal(".4~s", ViewFormat.D3Number(F("""{"style":"compact","decimals":3}""")));
        Assert.Null(ViewFormat.D3Number(F("""{"date":"month"}""")));
        Assert.Equal("$,.0f", ViewFormat.D3Number(JsonValue.Create("catalog"), new JsonObject { ["style"] = "currency", ["currency"] = "USD", ["decimals"] = 0 }));
    }

    [Fact]
    public void Every_d3_string_matches_the_pattern_the_client_checks()
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"^\$?,?\.[0-9]{1,2}~?[fs%]$");
        foreach (var style in new[] { "number", "currency", "percent", "compact" })
            foreach (var decimals in new int?[] { null, 0, 2, 10 })
                foreach (var grouping in new[] { true, false })
                {
                    var f = new JsonObject { ["style"] = style, ["grouping"] = grouping };
                    if (decimals is { } d) f["decimals"] = d;
                    if (style == "currency") f["currency"] = "USD";
                    Assert.Matches(pattern, ViewFormat.D3Number(f)!);
                }
    }

    [Fact]
    public void A_label_reads_a_date_or_a_number_written_as_text_and_leaves_other_text()
    {
        Assert.Equal("Jan", ViewFormat.Label("2026-01-01", new JsonObject { ["date"] = "MMM" }));
        Assert.Equal("202,601", ViewFormat.Label("202601", new JsonObject { ["style"] = "number" }));
        Assert.Equal("West", ViewFormat.Label("West", new JsonObject { ["style"] = "number" }));
        Assert.Equal("(none)", ViewFormat.Label("(none)", new JsonObject { ["date"] = "year" }));
        Assert.Equal("West", ViewFormat.Label("West", null));
    }
}
