using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Runs the shared conformance fixtures (fixtures, catalog, seed data and schemas under qis/)
/// against the C# engine. Also checks run-record replay for completed runs.
/// </summary>
public class ConformanceTests
{
    internal static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");
    internal static readonly Catalog Cat = Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));

    internal static readonly string Seed = File.ReadAllText(Path.Combine(Root, "data", "seed.sql"));

    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(Path.Combine(Root, "fixtures"), "*.json", SearchOption.AllDirectories)
            .Order()
            .Select(f => new object[] { Path.GetRelativePath(Path.Combine(Root, "fixtures"), f) });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture(string relativePath) => RunOnStandIn(relativePath, "native");

    /// <summary>Subtotals computed by the engine, or by the source only where needed, give the same rows as the source computing them all (the same fixtures, another mode).</summary>
    public static IEnumerable<object[]> TotalsFixtures() =>
        Fixtures().Where(f => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", (string)f[0])))!["request"]!["query"]?["totals"] is not null);

    [Theory]
    [MemberData(nameof(TotalsFixtures))]
    public void FixtureWithEngineSubtotals(string relativePath) => RunOnStandIn(relativePath, "engine");

    [Theory]
    [MemberData(nameof(TotalsFixtures))]
    public void FixtureWithAutoSubtotals(string relativePath) => RunOnStandIn(relativePath, "auto");

    private static void RunOnStandIn(string relativePath, string totalsCompute)
    {
        using var standIn = new DuckDbStandInExecutor(Seed);
        RunFixture(relativePath, totalsCompute, standIn);
    }

    /// <summary>Runs one fixture through the executor: the engine's result must match the fixture's expectations, validate against the result schema and replay from its run record.</summary>
    internal static void RunFixture(string relativePath, string totalsCompute, ISourceExecutor executor, Func<JsonObject, JsonObject>? rewrite = null, Func<Catalog>? catalog = null)
    {
        var fx = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", relativePath)))!.AsObject();
        var request = fx["request"]!.AsObject();
        // a test may hand the engine the same request by another road (through semantic SQL); what the fixture expects does not change
        if (rewrite is not null && request["kind"]?.GetValue<string>() == "query") { request = rewrite(request.DeepClone().AsObject()); fx["request"] = request; }
        // A fixture that pins where subtotals are computed (a hint in its request, or what it says about the _g result sets) isn't run in other modes.
        if (totalsCompute != "native" && (fx["request"]!["query"]!["totals"]?["compute"] is not null || fx["expect"]!["result_sets_absent"] is not null || fx["expect"]!["result_set_covers"] is not null))
            return;
        if (totalsCompute != "native" && fx["expect"]!["native_query_contains"] is JsonObject text)
            foreach (var id in text.Select(p => p.Key).Where(k => k.EndsWith("_g")).ToList()) text.Remove(id);

        if (request["kind"]!.GetValue<string>() == "native_batch" && !executor.RunsNativeText)
            return; // Needs an executor that runs agent-written text: the engine fixture job (J02).

        var cat = catalog?.Invoke() ?? Cat.Clone();   // ad hoc dimensions are registered on this fixture's copy only
        var validator = new Validator(cat);
        var runDir = Path.Combine(Path.GetTempPath(), "qis-runs-" + Guid.NewGuid().ToString("N"));
        var level = fx["level"]!.GetValue<int>();
        var engine = new QisEngine(cat, executor, validator, level, runDir: runDir);
        engine.TotalsCompute = totalsCompute;
        // Relative ranges are resolved against a fixed date, so a fixture's rows don't change with the calendar.
        if (fx["today"] is { } today) engine.Today = () => DateOnly.Parse(today.GetValue<string>());

        // Ad hoc tables, bindings and maps are registered first, as separate documents.
        var failures = new List<string>();
        foreach (var doc in fx["setup"]?.AsArray() ?? [])
        {
            var set = ToNode(engine.Run(doc!.DeepClone().AsObject()));
            if (set["status"]!.GetValue<string>() != "complete")
                failures.Add($"setup {doc["kind"]}: {set["errors"]?.ToJsonString()}");
        }

        var result = ToNode(engine.Run(request));
        failures.AddRange(validator.SchemaErrors(result, "result", "core").Select(e => $"result schema: {e.Path} {e.Message}"));
        failures.AddRange(FixtureChecker.Check(fx["expect"]!.AsObject(), result));

        if (fx["then_complete"] is JsonObject then && result["status"]!.GetValue<string>() == "partial")
        {
            var completion = then.DeepClone().AsObject();
            completion["kind"] = "completion";
            completion["request_hash"] = result["request_hash"]!.GetValue<string>();
            var after = ToNode(engine.Complete(completion));
            failures.AddRange(validator.SchemaErrors(after, "result", "core").Select(e => $"completion schema: {e.Path} {e.Message}"));
            failures.AddRange(FixtureChecker.Check(fx["expect_after_completion"]!.AsObject(), after).Select(f => "after completion: " + f));

            if (after["status"]!.GetValue<string>() == "complete")
                failures.AddRange(CheckReplay(runDir, result["request_hash"]!.GetValue<string>(), after, executor, level, catalog));
        }
        else if (request["kind"]!.GetValue<string>() == "query" && result["status"]!.GetValue<string>() == "complete")
            // Computed by the engine alone: the record carries the tables, bindings and maps, and replays in a new session.
            failures.AddRange(CheckReplay(runDir, result["request_hash"]!.GetValue<string>(), result, executor, level, catalog));
        if (Directory.Exists(runDir)) Directory.Delete(runDir, true);

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>The run was saved, and replaying it in a fresh session (a new catalog copy, nothing registered) gives the same rows.</summary>
    private static List<string> CheckReplay(string runDir, string hash, JsonObject original, ISourceExecutor executor, int level, Func<Catalog>? catalog = null)
    {
        var saved = Path.Combine(runDir, hash + ".json");
        if (!File.Exists(saved)) return ["replay: run record wasn't saved"];
        var fresh = catalog?.Invoke() ?? Cat.Clone();
        var replayed = ToNode(new QisEngine(fresh, executor, new Validator(fresh), level).Replay(saved));
        return JsonNode.DeepEquals(replayed["combined"]?["rows"], original["combined"]?["rows"]) ? [] : ["replay: rows differ from the original run"];
    }

    private static JsonObject ToNode(Result r) => JsonSerializer.SerializeToNode(r, QisJson.Options)!.AsObject();
}

/// <summary>Checks a result against a fixture's expectations: every expected key must match; lists match element by element.</summary>
public static class FixtureChecker
{
    public static List<string> Check(JsonObject expect, JsonObject r)
    {
        var f = new List<string>();
        f.AddRange(Subset(expect["status"], r["status"], "status"));
        if (r["status"]?.GetValue<string>() == "failed" && expect["status"]?.GetValue<string>() != "failed")
            f.Add($"unexpected errors: {r["errors"]?.ToJsonString()}");
        if (expect["reason_code"] is { } rc) f.AddRange(Subset(rc, r["reason_code"], "reason_code"));
        if (expect["errors"] is JsonArray errs)
            f.AddRange(Subset(errs, Codes(r["errors"]), "errors"));
        if (expect["errors_include"] is JsonArray inc)
            foreach (var code in inc)
                if (!Codes(r["errors"]).Any(c => c!.GetValue<string>() == code!.GetValue<string>()))
                    f.Add($"errors: expected a {code} error, got {Codes(r["errors"]).ToJsonString()}");
        if (expect["error_suggestions_include"] is JsonObject si)
            foreach (var (i, s) in si)
            {
                var sugg = r["errors"]?[int.Parse(i)]?["suggestions"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
                if (!sugg.Contains(s!.GetValue<string>())) f.Add($"errors[{i}].suggestions: {s} not in [{string.Join(", ", sugg)}]");
            }
        if (expect["warnings"] is JsonArray w) f.AddRange(Subset(w, Codes(r["warnings"]), "warnings"));
        if (expect["result_sets"] is JsonArray ds) f.AddRange(Subset(ds, r["result_sets"] ?? new JsonArray(), "result_sets"));
        if (expect["remaining"] is JsonArray rem) f.AddRange(Subset(rem, r["remaining"] ?? new JsonArray(), "remaining"));
        foreach (var id in expect["result_sets_absent"]?.AsArray() ?? [])
            if ((r["result_sets"] as JsonArray ?? []).Any(d => d!["id"]!.GetValue<string>() == id!.GetValue<string>()))
                f.Add($"result_sets: {id} should not be in the result");
        if (expect["result_set_covers"] is JsonObject covers)
            foreach (var (id, measures) in covers)
            {
                var found = (r["result_sets"] as JsonArray ?? []).FirstOrDefault(d => d!["id"]!.GetValue<string>() == id);
                if (found is null) f.Add($"result_sets: expected {id}");
                else f.AddRange(Subset(measures, found["covers"]!["measures"], $"{id}.covers.measures"));
            }
        if (expect["resolved"] is JsonArray resolved) f.AddRange(Subset(resolved, r["resolved"] ?? new JsonArray(), "resolved"));

        var combined = r["combined"] as JsonObject ?? new JsonObject();
        if (expect["rows"] is JsonArray rows)
        {
            var actual = combined["rows"]?.AsArray() ?? new JsonArray();
            JsonNode exp = rows, act = actual;
            if (expect["ordered"]?.GetValue<bool>() != true)
            {
                exp = SortRows(rows);
                act = SortRows(actual);
            }
            f.AddRange(Subset(exp, act, "rows"));
        }
        if (expect["descriptor"] is JsonObject d) f.AddRange(Subset(d, combined["descriptor"], "combined.descriptor"));
        if (expect["native_query_contains"] is JsonObject nqc)
            foreach (var (id, needles) in nqc)
            {
                var text = r["result_sets"]?.AsArray().FirstOrDefault(x => x!["id"]!.GetValue<string>() == id)?["native_query"]?["text"]?.GetValue<string>() ?? "";
                foreach (var n in needles!.AsArray())
                    if (!text.Contains(n!.GetValue<string>())) f.Add($"result_sets[{id}].native_query: missing '{n}'");
            }
        return f;
    }

    private static JsonArray Codes(JsonNode? list) =>
        new((list?.AsArray() ?? new JsonArray()).Select(e => (JsonNode)e!["code"]!.GetValue<string>()).ToArray());

    private static JsonArray SortRows(JsonArray rows) =>
        new(rows.Select(x => x!.DeepClone()).OrderBy(x => Canonical(x), StringComparer.Ordinal).ToArray());

    private static string Canonical(JsonNode? n) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{Canonical(p.Value)}")) + "}",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<decimal>().ToString("0.############"),
        _ => n?.ToJsonString() ?? "null",
    };

    /// <summary>Every key in expected must match actual; lists match element by element; numbers within 1e-9.</summary>
    /// <summary>A JSON number as a decimal, whichever .NET type holds it (a parsed document, or a long or decimal an engine wrote).</summary>
    private static decimal Decimal(JsonValue v) =>
        v.TryGetValue<decimal>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : (decimal)v.GetValue<double>();

    public static List<string> Subset(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case JsonObject eo:
                if (actual is not JsonObject ao) return [$"{path}: expected an object, got {actual?.ToJsonString()}"];
                return eo.SelectMany(p => ao.ContainsKey(p.Key)
                    ? Subset(p.Value, ao[p.Key], $"{path}.{p.Key}")
                    : [$"{path}.{p.Key}: missing"]).ToList();
            case JsonArray ea:
                if (actual is not JsonArray aa || aa.Count != ea.Count)
                    return [$"{path}: expected {ea.Count} items, got {actual?.ToJsonString()}"];
                return ea.Select((e, i) => Subset(e, aa[i], $"{path}[{i}]")).SelectMany(x => x).ToList();
            case JsonValue ev when ev.GetValueKind() == JsonValueKind.Number:
                if (actual is JsonValue av && av.GetValueKind() == JsonValueKind.Number &&
                    Math.Abs(Decimal(ev) - Decimal(av)) < 0.000000001m) return [];
                return [$"{path}: expected {ev}, got {actual?.ToJsonString()}"];
            default:
                return JsonNode.DeepEquals(expected, actual) ? [] : [$"{path}: expected {expected?.ToJsonString()}, got {actual?.ToJsonString()}"];
        }
    }
}
