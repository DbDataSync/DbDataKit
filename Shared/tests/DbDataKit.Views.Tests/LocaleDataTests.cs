using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>
/// The hand-written locale data of the view formatter (<c>locales.json</c>: separators, month names and abbreviations of en de fr es it pt nl; the currency symbols and the compact suffixes in <c>ViewFormat</c>)
/// against the vendored CLDR snapshot (<c>conformance/locale/cldr-snapshot.json</c>, pinned by <c>cldr-snapshot.pin</c>) and, where it is stable, against the ICU data behind .NET's <see cref="CultureInfo"/>.
/// A failure names the locale and the field. A deliberate difference is a row with a reason in <c>conformance/locale/allowed-deviations.json</c>. <c>localeData.test.ts</c> in <c>@dbdatakit/visuals</c> does the same in TypeScript.
/// What a person must still judge is in <c>docs/locale-data.md</c>.
/// </summary>
public class LocaleDataTests
{
    private sealed record Diff(string Against, string Locale, string Field, string Ours, string Reference)
    {
        public override string ToString() => $"{Against}: locale {Locale}, field {Field}: ours '{Ours}', reference '{Reference}'";
    }

    private static string LocaleRoot => Path.Combine(AppContext.BaseDirectory, "conformance", "locale");
    private static JsonNode Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(LocaleRoot, name)))!;
    private static JsonObject Snapshot() => Read("cldr-snapshot.json").AsObject();

    private static JsonObject OurRaw()
    {
        using var stream = typeof(ViewFormat).Assembly.GetManifestResourceStream("DbDataKit.Views.locales.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static string Hex(string s) => string.Join(" ", s.Select(c => $"U+{(int)c:X4}"));
    private static string Show(string s) => s.All(c => c is > ' ' and < '\u007f') ? s : $"{s} ({Hex(s)})";

    private static List<Diff> CldrDiffs()
    {
        var diffs = new List<Diff>();
        var snapshot = Snapshot();
        foreach (var (code, node) in snapshot["locales"]!.AsObject())
        {
            var reference = node!.AsObject();
            var ours = ViewFormat.Locale(code);
            void Same(string field, string o, string r) { if (!string.Equals(o, r, StringComparison.Ordinal)) diffs.Add(new("cldr", code, field, Show(o), Show(r))); }
            Same("decimal", ours.Decimal, reference["decimal"]!.GetValue<string>());
            Same("group", ours.Group, reference["group"]!.GetValue<string>());
            for (var i = 0; i < 12; i++)
            {
                Same($"months[{i}]", ours.Months[i], reference["months"]![i]!.GetValue<string>());
                Same($"short[{i}]", ours.Short[i], reference["short"]![i]!.GetValue<string>());
            }
        }
        foreach (var (iso, symbol) in snapshot["currencySymbols"]!["symbols"]!.AsObject())
        {
            var text = ViewFormat.Text(JsonValue.Create(1.0), JsonNode.Parse($$"""{"style":"currency","currency":"{{iso}}","decimals":0}"""));
            var ours = text.EndsWith('1') ? text[..^1] : text;
            if (ours != symbol!.GetValue<string>()) diffs.Add(new("cldr", "en", $"currency.{iso}", Show(ours), Show(symbol.GetValue<string>())));
        }
        var compact = snapshot["compactSuffixes"]!.AsObject();
        foreach (var (name, value) in new[] { ("thousand", 1e3), ("million", 1e6), ("billion", 1e9) })
        {
            var text = ViewFormat.Text(JsonValue.Create(value), JsonNode.Parse("""{"style":"compact","decimals":0}"""));
            var ours = text.TrimStart('1');
            var reference = compact[name]!.GetValue<string>().TrimStart('0');
            if (ours != reference) diffs.Add(new("cldr", "en", $"compact.{name}", Show(ours), Show(reference)));
        }
        return diffs;
    }

    /// <summary>What ICU says through .NET: the separators and the stand-alone month names. Abbreviations and currency symbols differ between ICU versions and between format and stand-alone context, so they are not compared here.</summary>
    private static List<Diff> IcuDiffs()
    {
        var diffs = new List<Diff>();
        foreach (var code in ViewFormat.Locales)
        {
            var culture = CultureInfo.GetCultureInfo(code);
            var ours = ViewFormat.Locale(code);
            void Same(string field, string o, string r) { if (!string.Equals(o, r, StringComparison.Ordinal)) diffs.Add(new("icu", code, field, Show(o), Show(r))); }
            Same("decimal", ours.Decimal, culture.NumberFormat.NumberDecimalSeparator);
            Same("group", ours.Group, culture.NumberFormat.NumberGroupSeparator);
            for (var i = 0; i < 12; i++) Same($"months[{i}]", ours.Months[i], culture.DateTimeFormat.MonthNames[i]);
        }
        return diffs;
    }

    /// <summary>Takes the allowed rows off the differences; a row without a reason, or one that matches nothing, is itself a failure.</summary>
    private static List<string> Unexplained(IEnumerable<Diff> diffs, string against)
    {
        var rows = Read("allowed-deviations.json")["deviations"]!.AsArray().Select(r => r!.AsObject()).Where(r => r["against"]!.GetValue<string>() == against).ToList();
        var problems = new List<string>();
        var used = new HashSet<JsonObject>();
        foreach (var d in diffs)
        {
            var row = rows.FirstOrDefault(r => r["locale"]!.GetValue<string>() == d.Locale && r["field"]!.GetValue<string>() == d.Field
                && Show(r["ours"]!.GetValue<string>()) == d.Ours && Show(r["reference"]!.GetValue<string>()) == d.Reference);
            if (row is null) problems.Add(d.ToString());
            else used.Add(row);
        }
        foreach (var r in rows)
        {
            var where = $"allowed-deviations.json, {against}: locale {r["locale"]}, field {r["field"]}";
            if (string.IsNullOrWhiteSpace(r["reason"]?.GetValue<string>())) problems.Add($"{where}: the row has no reason");
            if (!used.Contains(r)) problems.Add($"{where}: the row matches no difference any more (delete it, or its values are out of date)");
        }
        return problems;
    }

    private static void NoProblems(List<string> problems) =>
        Assert.True(problems.Count == 0, "Locale data differs from its reference:" + Environment.NewLine + string.Join(Environment.NewLine, problems));

    [Fact]
    public void The_snapshot_is_the_pinned_one()
    {
        var pin = File.ReadAllLines(Path.Combine(LocaleRoot, "cldr-snapshot.pin")).Where(l => l.Contains('=') && !l.StartsWith('#')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(LocaleRoot, "cldr-snapshot.json")))).ToLowerInvariant();
        Assert.True(pin["SNAPSHOT_SHA256"] == hash, "cldr-snapshot.json changed without cldr-snapshot.pin: regenerate it with scripts/fetch_cldr_snapshot.py --write, never edit it by hand");
        var source = Snapshot()["source"]!;
        Assert.Equal(pin["CLDR_VERSION"], source["version"]!.GetValue<string>());
        Assert.Equal("Unicode-3.0", source["licence"]!.GetValue<string>());
    }

    [Fact]
    public void The_table_has_the_locales_and_fields_of_the_snapshot_and_no_others()
    {
        var problems = new List<string>();
        var ours = OurRaw();
        var reference = Snapshot()["locales"]!.AsObject();
        foreach (var (code, _) in reference) if (!ours.ContainsKey(code)) problems.Add($"locale {code}: missing from locales.json");
        foreach (var (code, _) in ours) if (!reference.ContainsKey(code)) problems.Add($"locale {code}: in locales.json but not in the CLDR snapshot (add it to scripts/fetch_cldr_snapshot.py's input and run --write)");
        Assert.Equal(reference.Select(e => e.Key), ours.Select(e => e.Key));
        foreach (var (code, node) in ours)
        {
            var fields = node!.AsObject().Select(e => e.Key).ToHashSet();
            foreach (var f in new[] { "decimal", "group", "months", "short" }) if (!fields.Contains(f)) problems.Add($"locale {code}, field {f}: missing");
            foreach (var f in fields.Except(["decimal", "group", "months", "short"])) problems.Add($"locale {code}, field {f}: not a field the snapshot checks");
        }
        NoProblems(problems);
    }

    [Fact]
    public void Every_field_is_well_formed()
    {
        var problems = new List<string>();
        foreach (var code in ViewFormat.Locales)
        {
            var l = ViewFormat.Locale(code);
            void Check(string field, string v, bool separator = false)
            {
                if (string.IsNullOrEmpty(v)) { problems.Add($"locale {code}, field {field}: empty"); return; }
                if (v != v.Normalize(NormalizationForm.FormC)) problems.Add($"locale {code}, field {field}: '{v}' is not in Unicode normal form C");
                if (v.Any(char.IsControl)) problems.Add($"locale {code}, field {field}: contains a control character ({Hex(v)})");
                if (separator)
                {
                    if (v.Length != 1) problems.Add($"locale {code}, field {field}: a separator is one character, this is {Hex(v)}");
                    // a plain space would break across lines and is never what CLDR writes: the group separator is a no-break space (U+00A0 or U+202F) or a mark
                    else if (v == " " || (char.IsWhiteSpace(v[0]) && v[0] is not (' ' or ' '))) problems.Add($"locale {code}, field {field}: {Hex(v)} is a breaking space (use U+00A0 or U+202F)");
                }
                else if (v != v.Trim()) problems.Add($"locale {code}, field {field}: '{v}' has a space at its start or end");
            }
            Check("decimal", l.Decimal, true);
            Check("group", l.Group, true);
            if (l.Decimal == l.Group) problems.Add($"locale {code}: decimal and group separator are both '{l.Decimal}'");
            if (l.Months.Count != 12) problems.Add($"locale {code}, field months: {l.Months.Count} names, not 12");
            if (l.Short.Count != 12) problems.Add($"locale {code}, field short: {l.Short.Count} names, not 12");
            for (var i = 0; i < l.Months.Count; i++) Check($"months[{i}]", l.Months[i]);
            for (var i = 0; i < l.Short.Count; i++) Check($"short[{i}]", l.Short[i]);
            if (l.Months.Distinct().Count() != l.Months.Count) problems.Add($"locale {code}, field months: two months have the same name");
            if (l.Short.Distinct().Count() != l.Short.Count) problems.Add($"locale {code}, field short: two months have the same abbreviation");
        }
        NoProblems(problems);
    }

    [Fact]
    public void Every_field_equals_the_cldr_snapshot_or_is_an_allowed_deviation() => NoProblems(Unexplained(CldrDiffs(), "cldr"));

    [Fact]
    public void The_french_group_separator_is_the_narrow_no_break_space()
    {
        Assert.Equal(" ", ViewFormat.Locale("fr").Group);
        Assert.Equal("1 234,5", ViewFormat.Text(JsonValue.Create(1234.5), JsonNode.Parse("""{"style":"number","decimals":1}"""), null, "fr"));
    }

    /// <summary>ICU is not the same on every machine: a build with <c>InvariantGlobalization</c>, or a host without ICU data, has no cultures to ask, and an older ICU writes other names; those runs skip.</summary>
    [Fact]
    public void Separators_and_month_names_equal_what_icu_says_where_icu_is_present()
    {
        var invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out var on) && on;
        Assert.SkipWhen(invariant, "InvariantGlobalization is on: no culture data to compare");
        Assert.SkipUnless(CultureInfo.GetCultureInfo("fr").NumberFormat.NumberDecimalSeparator == "," && CultureInfo.GetCultureInfo("de").DateTimeFormat.MonthNames[2] == "März",
            "this machine's ICU has no usable French and German data");
        NoProblems(Unexplained(IcuDiffs(), "icu"));
    }
}
