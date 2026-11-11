using System.Globalization;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

public sealed partial class QisEngine
{
    /// <summary>
    /// Shadow mode: when the engine starts executing a step type, agents may still submit completions for it. The SQL is
    /// validated and run like any completion, but the engine's own result is what comes back, with a warning saying
    /// whether the two agreed. Disagreements are recorded in <see cref="ShadowReports"/>: they become test cases.
    /// </summary>
    private Result CompleteInShadow(Completion completion, Result engineResult)
    {
        var h = completion.RequestHash;
        var resultSets = engineResult.ResultSets!;
        var errors = Sandbox.Validate(completion.Sql, resultSets.Select(d => d.Id).ToHashSet());
        if (errors.Count > 0) return Result.Failed(level, h, errors);

        QueryRows theirs;
        try { theirs = Sandbox.Run(completion.Sql, resultSets, rowCap + 1); }
        catch (Exception e) { return Result.Failed(level, h, [Err("completion_failed", "$.sql", e.Message.Split('\n')[0], "mashup")]); }

        var ours = engineResult.Combined!;
        var columns = ours.Descriptor["schema"]!["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()).ToList();
        var missing = columns.Where(c => !theirs.Columns.Contains(c)).ToList();
        string detail;
        bool match;
        if (missing.Count > 0)
        {
            match = false;
            detail = $"The completion has no column {string.Join(", ", missing)}.";
        }
        else
        {
            // Rows compare as a set, so a different ORDER BY alone isn't a disagreement.
            var a = ours.Rows.Select(r => Key(r, columns)).Order().ToList();
            var b = theirs.Rows.Select(r => Key(r, columns)).Order().ToList();
            match = a.SequenceEqual(b);
            detail = match ? $"Both produced the same {a.Count} rows."
                : $"The engine produced {a.Count} rows and the completion {b.Count}; first difference: " +
                  (a.Except(b).FirstOrDefault() is { } onlyOurs ? $"engine has {onlyOurs}" : $"completion has {b.Except(a).FirstOrDefault()}") + ".";
        }
        ShadowReports.Add(new ShadowReport(h, match, detail));
        var warnings = (engineResult.Warnings ?? []).Append(new Warning(match ? "shadow_match" : "shadow_mismatch", detail)).ToList();
        return engineResult with { Warnings = warnings };
    }

    private static string Key(Dictionary<string, object?> row, List<string> columns) =>
        string.Join(" | ", columns.Select(c => row.GetValueOrDefault(c) switch
        {
            null => "NULL",
            decimal d => decimal.Round(d, 9).ToString("0.#########", CultureInfo.InvariantCulture),
            double d => decimal.Round((decimal)d, 9).ToString("0.#########", CultureInfo.InvariantCulture),
            float f => decimal.Round((decimal)f, 9).ToString("0.#########", CultureInfo.InvariantCulture),
            IFormattable v => v.ToString(null, CultureInfo.InvariantCulture),
            var v => v.ToString(),
        }));
}
