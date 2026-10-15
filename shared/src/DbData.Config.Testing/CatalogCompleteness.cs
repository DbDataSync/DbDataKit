using DbData.Config.Yaml;

namespace DbData.Config.Testing;

/// <summary>A fixture for one diagnostic code: produces diagnostics (from a bad input) that must include the code.</summary>
public sealed record CodeFixture(string Code, Func<IEnumerable<ConfigDiagnostic>> Produce)
{
    /// <summary>A fixture that reads <paramref name="yaml"/> with the strict reader (YAML or JSON by the file name).</summary>
    public static CodeFixture FromText(string code, string yaml, string file = "fixture.yml", IDiagnosticCatalog? catalog = null) =>
        new(code, () =>
        {
            var d = new List<ConfigDiagnostic>();
            JsonInput.ReadByExtension(yaml, file, d, catalog);
            return d;
        });
}

/// <summary>
/// Build's rule as a helper: every diagnostic code has a fixture that produces it and fix text. Fails on a code without a fixture, a fixture
/// that does not produce its code, a produced diagnostic without fix text, a fixture for a code that is not in the catalog (stale), and a code that
/// does not use the expected prefix.
/// </summary>
public static class CatalogCompleteness
{
    public static IReadOnlyList<string> Check(IEnumerable<string> codes, IEnumerable<CodeFixture> fixtures, string? prefix = null)
    {
        var problems = new List<string>();
        var codeList = codes.ToList();
        var fixtureList = fixtures.ToList();

        foreach (var dup in codeList.GroupBy(c => c).Where(g => g.Count() > 1)) problems.Add($"{dup.Key}: listed more than once");
        foreach (var c in codeList.Distinct())
        {
            if (prefix != null && !c.StartsWith(prefix, StringComparison.Ordinal)) problems.Add($"{c}: does not start with `{prefix}`");
            var mine = fixtureList.Where(f => f.Code == c).ToList();
            if (mine.Count == 0) { problems.Add($"{c}: no fixture"); continue; }
            foreach (var f in mine)
            {
                List<ConfigDiagnostic> produced;
                try { produced = [.. f.Produce()]; }
                catch (Exception ex) { problems.Add($"{c}: the fixture threw {ex.GetType().Name}: {ex.Message}"); continue; }
                var hit = produced.Where(d => d.Code == c).ToList();
                if (hit.Count == 0) problems.Add($"{c}: the fixture did not produce it (got: {(produced.Count == 0 ? "nothing" : string.Join(", ", produced.Select(d => d.Code).Distinct()))})");
                else if (hit.All(d => string.IsNullOrWhiteSpace(d.Fix))) problems.Add($"{c}: no fix text");
            }
        }
        foreach (var f in fixtureList.Where(f => !codeList.Contains(f.Code))) problems.Add($"{f.Code}: fixture for a code that is not in the catalog");
        return problems;
    }

    public static void Assert(IEnumerable<string> codes, IEnumerable<CodeFixture> fixtures, string? prefix = null)
    {
        var problems = Check(codes, fixtures, prefix);
        if (problems.Count > 0) throw new ConfigTestException("The diagnostics catalog is incomplete:\n" + string.Join("\n", problems));
    }
}
