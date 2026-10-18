using DbDataBuild.Core.Questions;
using DbDataBuild.Models;

namespace DbDataBuild.Define;

/// <summary>
/// The questions `define` asks (DESIGN.md 6.5), built in one place. Every inferred value is a proposal with evidence; accepting it is an
/// explicit answer. Field names in ids: name, kind, targets, grain, unique_key, time_column, lookback, columns.&lt;c&gt;.type,
/// columns.&lt;c&gt;.nullable, columns.&lt;c&gt;.remove, indexes.
/// </summary>
internal static class DefineQuestions
{
    public const string SkipModel = "skip_model";
    public const string KeepDeclared = "keep_declared";

    private static QuestionOption Skip(string why) => new(SkipModel, why);

    public static Question Name(string model, string queryFile) => new(
        QuestionIds.Define(model, "name"),
        $"What is the name of the model defined by `{queryFile}`?",
        ["The name must equal the path under models/ with '/' replaced by '.'; a different name will be refused."],
        [new("use_name", "Use this name", TakesValue: true, ValueHint: "model name"), Skip("Skip this model for now; move the file so its path implies the name you want")],
        new Proposal("use_name", model, ProposalCertainty.High, [$"the path `{queryFile}` implies `{model}`"]));

    public static Question Kind(string model) => new(
        QuestionIds.Define(model, "kind"),
        $"What kind of model is `{model}`? Kind is never inferred.",
        [],
        [
            new("view", "View over its upstream tables", "DDL only; apply issues no data statements"),
            new("full", "Table that is fully reloaded", "delete and insert of the whole contents in one transaction"),
            new("incremental_by_unique_key", "Table upserted by a unique key", "needs a unique key and a grain"),
            new("incremental_by_time_range", "Table loaded in time slices", "the range comes from MAX(time column) in the target, minus a lookback; needs a time column and a grain"),
        ]);

    public static Question Targets(string model, IReadOnlyList<string> projectDefault, IReadOnlyList<string> portability) => new(
        QuestionIds.Define(model, "connections"),
        $"Which engines must `{model}` be valid for?",
        [$"Project default connections: {string.Join(", ", projectDefault)}", .. portability],
        [
            new("use_project_default", "Use the project default", $"`connections` is left out and the project default ({string.Join(", ", projectDefault)}) applies"),
            new("choose_targets", "Name the targets", TakesValue: true, ValueHint: "comma-separated: sqlserver, fabric, postgres"),
        ],
        new Proposal("use_project_default", null, ProposalCertainty.Normal, [$"project defaults.connections: {string.Join(", ", projectDefault)}"]));

    public static Question ColumnType(string model, InferredColumn c, string? declaredType = null)
    {
        var context = new List<string> { $"DuckDB resolves the query column to {c.DuckDbType}.", c.Type.Reason + "." };
        if (declaredType != null) context.Insert(0, $"Declared: {declaredType}.");
        if (c.Upstream.Count > 0) context.Add($"Lineage: {c.TransformKind} of {string.Join(", ", c.Upstream.Select(u => $"{u.Table}.{u.Column}"))}.");
        var options = new List<QuestionOption> { new("use_type", "Use this SQL type", TakesValue: true, ValueHint: "SQL type, for example VARCHAR(50)") };
        options.Add(declaredType != null ? new QuestionOption(KeepDeclared, "Keep the declared type", "the definition stays out of sync with the query")
                                         : Skip("Skip this model for now; change the query (for example add a CAST) and run define again"));
        return new Question(QuestionIds.Define(model, $"columns.{c.Name}.type"),
            declaredType != null ? $"The type of column `{c.Name}` changed. Which type should the definition declare?" : $"What is the type of column `{c.Name}`?",
            context, options,
            c.Type.HasProposal ? new Proposal("use_type", c.Type.LogicalType, c.Type.Certainty!.Value, [c.Type.Reason]) : null);
    }

    public static Question ColumnNullable(string model, InferredColumn c) => new(
        QuestionIds.Define(model, $"columns.{c.Name}.nullable"),
        $"Can column `{c.Name}` be NULL?",
        [c.Nullability.Reason + "."],
        [new("nullable", "It can be NULL"), new("not_nullable", "It is never NULL", "the column is declared NOT NULL")],
        c.Nullability.HasProposal ? new Proposal(c.Nullability.Nullable == true ? "nullable" : "not_nullable", null, ProposalCertainty.High, [c.Nullability.Reason]) : null);

    public static Question Grain(string model, IReadOnlyList<GrainCandidate> candidates, IReadOnlyList<string> outputs)
    {
        var options = candidates.Select((c, i) => new QuestionOption($"candidate_{i + 1}", $"({string.Join(", ", c.Columns)})", c.Evidence)).ToList();
        options.Add(new("custom_columns", "Name the columns", TakesValue: true, ValueHint: "comma-separated output columns"));
        options.Add(Skip("Skip this model for now"));
        return new Question(QuestionIds.Define(model, "grain"),
            $"Which columns uniquely identify a row of `{model}`? (the grain, required for incremental kinds)",
            [$"Output columns: {string.Join(", ", outputs)}", candidates.Count == 0 ? "No candidate was found from GROUP BY, DISTINCT or upstream keys." : "Candidates come from GROUP BY, DISTINCT and upstream keys."],
            options,
            candidates.Count == 1 ? new Proposal("candidate_1", null, ProposalCertainty.Normal, [candidates[0].Evidence]) : null);
    }

    public static Question UniqueKey(string model, IReadOnlyList<string> grain) => new(
        QuestionIds.Define(model, "unique_key"),
        $"Which columns is `{model}` upserted by (unique_key)?",
        [$"Grain: {string.Join(", ", grain)}. For incremental_by_unique_key the unique key must equal the grain."],
        [new("same_as_grain", "Use the grain"), new("custom_columns", "Name the columns", TakesValue: true, ValueHint: "comma-separated output columns"), Skip("Skip this model for now")],
        new Proposal("same_as_grain", null, ProposalCertainty.Normal, ["unique_key must equal grain"]));

    public static Question TimeColumn(string model, IReadOnlyList<string> candidates) => new(
        QuestionIds.Define(model, "time_column"),
        $"Which column is the time column of `{model}`?",
        [candidates.Count == 0 ? "No output column is a DATE or TIMESTAMP." : $"DATE and TIMESTAMP output columns: {string.Join(", ", candidates)}"],
        [new("use_column", "Use this column", TakesValue: true, ValueHint: "column name"), Skip("Skip this model for now")],
        candidates.Count == 1 ? new Proposal("use_column", candidates[0], ProposalCertainty.Normal, ["the only DATE or TIMESTAMP output column"]) : null);

    public static Question Lookback(string model) => new(
        QuestionIds.Define(model, "lookback"),
        $"How far back should each load of `{model}` re-read, from MAX(time column)?",
        ["A lookback re-reads recent rows to catch late-arriving data."],
        [new("use_lookback", "Use this lookback", TakesValue: true, ValueHint: "for example 3 days"), new("no_lookback", "No lookback")]);

    public static Question Indexes(string model, IReadOnlyList<IndexAdvice> advice) => new(
        QuestionIds.Define(model, "indexes"),
        $"The loads of `{model}` would use these indexes. Declare them under `indexes:`?",
        [.. advice.Select(a => $"{IndexAdvisor.Yaml(a)}   ({(a.Reason == IndexReason.MergeKey ? "key of a load" : "column a load reads")}; {string.Join(", ", a.Targets)})"),
         "The tool creates only indexes you declare, and never infers one from unique_key. Declaring `unique: true` makes the engine reject duplicate keys; leave it off to merge on a key without enforcing it."],
        [new("add_suggested", "Declare the suggested indexes", "the next plan will create them"), new("no_indexes", "Declare none", "the advice stays as a lint note on `project compile` and `connection deploy`")],
        new Proposal("add_suggested", null, ProposalCertainty.Normal, [$"{advice.Count} access path(s) without an index"]));

    public static Question Rename(string model, RenameCandidate c, IReadOnlyList<string> references)
    {
        var context = new List<string> { $"`{c.Removed.Name}` was declared as {c.Removed.Type}; the query now returns `{c.Added.Name}` ({c.Added.Type.LogicalType ?? c.Added.DuckDbType}) at position {c.Position + 1}." };
        context.AddRange(references);
        return new Question(QuestionIds.Rename(model, c.Removed.Name),
            $"Column `{c.Removed.Name}` is no longer returned and a new column of the same type appeared in the same position. Is this a rename?",
            context,
            [
                new("rename_to", "It is a rename", "the data is kept; the rename is recorded under `renames:`", TakesValue: true, ValueHint: "new column name"),
                new("drop_and_add", "Drop the old column and add the new one", "the data in the old column is lost"),
            ],
            new Proposal("rename_to", c.Added.Name, ProposalCertainty.Normal, [$"same type {c.Removed.Type}", $"same ordinal position {c.Position + 1}"]));
    }

    public static Question Remove(string model, ColumnDefinition d) => new(
        QuestionIds.Define(model, $"columns.{d.Name}.remove"),
        $"Declared column `{d.Name}` is no longer returned by the query. Remove it from the definition?",
        [$"Declared as {d.Type}."],
        [new("remove_column", "Remove the declared column", "planning treats this as a destructive drop of the column"), new(KeepDeclared, "Keep the declaration", "the definition stays out of sync with the query")]);
}
