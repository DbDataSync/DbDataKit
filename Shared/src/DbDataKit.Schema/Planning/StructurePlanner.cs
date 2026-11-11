using DbDataKit.Schema.Ddl;
using DbDataKit.Schema.Questions;

namespace DbDataKit.Schema.Planning;

/// <summary>How much a change can hurt: safe changes are applied directly, risky ones need the person's allowance, destructive ones their approval.</summary>
public enum RiskClass { Safe, Risky, Destructive }

/// <summary>What the plan assumed about an object, verified against the live target at apply (a mismatch is a stale plan).</summary>
public sealed record ObjectBase(string Object, ObjectState State, string? LiveShapeHash, string? RecordedShapeHash);

/// <summary>One thing the structure planner would do: run DDL, or record a shape the tool now treats as the baseline. <see cref="Reasons"/> is the reason chain (a decision-table row, a change, an answer).</summary>
/// <param name="HashAfter">For DDL on tables: the shape hash the object must have afterwards. Null when it cannot be predicted.</param>
/// <param name="ShapeSource">For a recorded shape: `adopted` or `out_of_band`.</param>
/// <param name="Expect">For an index step: what the catalog must report afterwards, `index:&lt;name&gt;=&lt;canonical definition&gt;`.</param>
public sealed record StructureStep(bool IsTrack, string Object, string Description, string Text, RiskClass Risk, IReadOnlyList<string> Reasons, string? HashAfter = null, string? ShapeSource = null, string? Expect = null);

public enum StructureProblemKind { AdoptionDeclined, ObjectChangedOutsideTool, AnswerValueMismatch, IndexNotSupported, IndexNameIsConstraint }

/// <summary>Something that stops a table from being planned, in words; the product gives it its own code and says where in the project it is.</summary>
public sealed record StructureProblem(StructureProblemKind Kind, string Message);

/// <summary>What the structure planner needs from the product around it: a way to ask the person, and a place to say what it noticed.</summary>
public interface IStructureHost
{
    /// <summary>The person's answer to the question, or null when there is none yet (the question is then open and no plan may be made).</summary>
    ResolvedAnswer? Answer(Question question);

    void Notice(string text);
}

/// <summary>A column renamed on purpose: stated in the declaration, never inferred.</summary>
public sealed record RenameSpec(string From, string To);

/// <summary>A table as the product wants it: columns in the engine's own types, renames that were declared, and indexes (those that apply to the engine being planned).</summary>
public sealed record DesiredTable(string Name, IReadOnlyList<NativeColumn> Columns, IReadOnlyList<RenameSpec> Renames, IReadOnlyList<IndexSpec> Indexes);

public enum Gate { Proceed, Blocked, Waiting }

/// <summary>
/// The decisions about a table's structure: whether an existing object is known to the tool (adopt it, or stop for a change made behind its back), which columns are added, dropped, renamed or altered and how risky each is, and
/// which indexes are created or rebuilt. A pure function of the desired table, the live one and the answers; it reads nothing and writes nothing. Moved out of Build's planner without a change of what it decides (plan 19, H02).
/// </summary>
public static class StructurePlanner
{
    private static StructureStep Ddl(string obj, string description, string text, RiskClass risk, IEnumerable<string> reasons, string? hashAfter = null, string? expect = null) =>
        new(false, obj, description, text, risk, reasons.ToList(), hashAfter, null, expect);

    private static StructureStep Track(string obj, string description, string text, string hash, IEnumerable<string> reasons, string shapeSource) =>
        new(true, obj, description, text, RiskClass.Safe, reasons.ToList(), hash, shapeSource);

    /// <summary>The steps for a table that does not exist yet: its schema (when it is not there either) and the table.</summary>
    public static IEnumerable<StructureStep> Create(DesiredTable table, TableDdl ddl, bool needsSchema)
    {
        var (schema, name) = TableDdl.Split(table.Name);
        if (needsSchema) yield return Ddl(table.Name, $"create schema {schema}", ddl.CreateSchema(schema), RiskClass.Safe, ["obj.missing.table"]);
        yield return Ddl(table.Name, $"create table {table.Name}", ddl.CreateTable(schema, name, table.Columns), RiskClass.Safe, ["obj.missing.table"], Hashing.ShapeHash(TableDdl.ExpectedShape(table.Columns)));
    }

    /// <summary>
    /// The object-level rows shared by every kind: missing, untracked (adopt), out of band (block until acknowledged), in sync. With no tracking there is no baseline to drift from, so an object that exists is judged against the declaration alone.
    /// </summary>
    /// <param name="acknowledged">Whether the person already allowed the change made outside the tool, for the live shape with this hash.</param>
    public static Gate Baseline(string obj, ObjectShape? live, string? recorded, bool tracked, int declaredColumns, Func<string, bool> acknowledged, IStructureHost host,
        List<StructureStep> steps, List<ObjectBase> bases, List<StructureProblem> problems, out ObjectState state)
    {
        state = tracked ? Drift.Classify(live, recorded) : live == null ? ObjectState.Missing : ObjectState.InSync;
        bases.Add(new ObjectBase(obj, state, live?.ShapeHash, recorded));
        switch (state)
        {
            case ObjectState.Missing or ObjectState.InSync:
                return Gate.Proceed;

            case ObjectState.Untracked:
            {
                var q = new Question(QuestionIds.Adopt(obj),
                    $"`{obj}` exists on the target, but the tool has no record of creating it. Adopt it?",
                    [$"Live shape hash: {live!.ShapeHash}", $"The model declares {declaredColumns} column(s); the object has {live.Columns.Count}."],
                    [new("adopt", "Record the live shape as the baseline and plan any difference from the model", "A track step is added to the plan; later changes are diffed against this shape."),
                     new("stop", "Do not plan this model", "The model and everything downstream of it are skipped.")]);
                var a = host.Answer(q);
                if (a == null) return Gate.Waiting;
                if (a.Choice == "stop")
                {
                    problems.Add(new StructureProblem(StructureProblemKind.AdoptionDeclined, $"{obj} exists on the target and adoption was declined (answer `{q.Id}` is `stop`)."));
                    return Gate.Blocked;
                }
                steps.Add(Track(obj, $"adopt {obj}", $"record shape {live.ShapeHash}", live.ShapeHash, ["obj.untracked", $"answer {q.Id} = adopt"], "adopted"));
                return Gate.Proceed;
            }

            default: // out of band
            {
                if (!acknowledged(live!.ShapeHash))
                {
                    problems.Add(new StructureProblem(StructureProblemKind.ObjectChangedOutsideTool,
                        $"{obj} has shape {live.ShapeHash[..12]}, but the last recorded shape is {recorded![..12]}. Something changed it outside the tool."));
                    return Gate.Blocked;
                }
                steps.Add(Track(obj, $"record acknowledged change to {obj}", $"record shape {live.ShapeHash}", live.ShapeHash, ["obj.drift.acknowledged"], "out_of_band"));
                return Gate.Proceed;
            }
        }
    }

    /// <summary>
    /// Declared indexes against the live ones, by name. Indexes are what the operator declared: nothing is inferred, an undeclared live index is left alone and reported. Returns false when the table cannot be planned (a problem was added).
    /// </summary>
    /// <param name="indexesSupported">False for an engine with no CREATE INDEX.</param>
    public static bool Indexes(DesiredTable table, ObjectShape? live, TableDdl ddl, bool indexesSupported, IStructureHost host, List<StructureStep> steps, List<StructureProblem> problems)
    {
        var wanted = table.Indexes;
        if (wanted.Count == 0 && live == null) return true;
        var (schema, name) = TableDdl.Split(table.Name);

        if (wanted.Count > 0 && !indexesSupported)
        {
            problems.Add(new StructureProblem(StructureProblemKind.IndexNotSupported,
                $"index.unsupported: {table.Name} declares index(es) {string.Join(", ", wanted.Select(i => $"`{i.Name}`"))}, and {ddl.Engine switch { "fabric" => "Fabric", var e => e }} has no CREATE INDEX."));
            return false;
        }

        var liveIndexes = (live?.Physical ?? []).Where(p => p.Kind is "index" or "constraint_index").ToList();
        foreach (var index in wanted)
        {
            var canonical = IndexText.Canonical(index.Unique, index.Columns, index.Include);
            var existing = liveIndexes.FirstOrDefault(p => string.Equals(p.Name, index.Name, StringComparison.OrdinalIgnoreCase));
            var expect = $"index:{index.Name}={canonical}";
            if (existing == null)
                steps.Add(Ddl(table.Name, $"create index {index.Name}", ddl.CreateIndex(schema, name, index), RiskClass.Safe, ["index.added"], expect: expect));
            else if (existing.Kind == "constraint_index")
            {
                problems.Add(new StructureProblem(StructureProblemKind.IndexNameIsConstraint,
                    $"index.constraint_name: {table.Name} declares index `{index.Name}`, but a PRIMARY KEY or UNIQUE constraint of that name exists on the target. Rename the index in the model."));
                return false;
            }
            else if (!string.Equals(existing.Definition, canonical, StringComparison.OrdinalIgnoreCase))
                steps.Add(Ddl(table.Name, $"rebuild index {index.Name}", ddl.DropIndex(schema, name, index.Name) + "\n" + ddl.CreateIndex(schema, name, index), RiskClass.Risky,
                    ["index.changed", $"live definition {existing.Definition}; declared {canonical}"], expect: expect));
        }

        foreach (var extra in liveIndexes.Where(p => p.Kind == "index" && !wanted.Any(i => string.Equals(i.Name, p.Name, StringComparison.OrdinalIgnoreCase))))
            host.Notice($"Index `{extra.Name}` on {table.Name} exists on the target but is not declared in the model; it was left alone ({extra.Definition}).");
        return true;
    }

    /// <summary>Column diff of a live table against the declared columns. Returns false when a question is open.</summary>
    public static bool Columns(DesiredTable table, ObjectShape live, TableDdl ddl, IStructureHost host, List<StructureStep> steps, List<StructureProblem> problems)
    {
        var declared = table.Columns;
        var (schema, name) = TableDdl.Split(table.Name);
        var current = live.Columns.ToList();                                  // running shape, so every step knows the hash it must leave behind
        var liveByName = live.Columns.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var liveAtStart = liveByName.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var declaredByName = declared.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var open = false;
        var problemsBefore = problems.Count;

        string HashNow() => Hashing.ShapeHash(current);
        void Replace(string oldName, ColumnShape? to)
        {
            var i = current.FindIndex(x => x.Name == oldName);
            if (to == null) current.RemoveAt(i); else current[i] = to;
        }

        void RenameColumn(string from, string to)
        {
            var shape = liveByName[from] with { Name = to };
            Replace(from, shape);
            liveByName.Remove(from);
            liveByName[to] = shape;
        }

        // declared renames first (explicit in the model; never inferred)
        var renamed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in table.Renames)
        {
            if (!liveByName.ContainsKey(r.From) || liveByName.ContainsKey(r.To) || !declaredByName.ContainsKey(r.To)) continue;
            RenameColumn(r.From, r.To);
            steps.Add(Ddl(table.Name, $"rename column {r.From} to {r.To}", ddl.RenameColumn(schema, name, r.From, r.To), RiskClass.Risky, ["col.renamed", "declared in the model"], HashNow()));
            renamed.Add(r.From);
            renamed.Add(r.To);
        }

        var removed = live.Columns.Select(x => x.Name).Where(n => !declaredByName.ContainsKey(n) && !renamed.Contains(n)).ToList();
        var added = declared.Select(x => x.Name).Where(n => !liveByName.ContainsKey(n) && !renamed.Contains(n)).ToList();

        // a missing and a new column might be a rename: ask, never infer
        var renameTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in removed.ToList())
        {
            if (added.Count == 0) break;
            var oldShape = liveAtStart[r];
            var same = added.Where(a => declaredByName[a].Expected with { Name = r } == oldShape).ToList();
            Proposal? proposal = removed.Count == 1 && added.Count == 1 && same.Count == 1
                ? new Proposal("rename_to", same[0], ProposalCertainty.Normal, [$"`{r}` is gone and `{same[0]}` is new, with the identical type, length, nullability and collation."])
                : null;
            var q = new Question(QuestionIds.Rename(table.Name, r),
                $"`{r}` is no longer declared on {table.Name}, and new column(s) {string.Join(", ", added.Select(a => $"`{a}`"))} appear. Is `{r}` renamed?",
                [$"`{r}` is {oldShape.Type}{Size(oldShape)}; undeclared renames plan as a destructive drop plus an add."],
                [new("rename_to", "Rename the column, keeping its data", "A rename step replaces the drop and the add.", TakesValue: true, ValueHint: "the new column name, one of: " + string.Join(", ", added)),
                 new("drop_and_add", "Drop the old column and add the new one", "The old column's data is lost (destructive).")], proposal);
            var a = host.Answer(q);
            if (a == null) { open = true; continue; }
            if (a.Choice != "rename_to") continue;
            if (!added.Contains(a.Value!) || renameTargets.Contains(a.Value!))
            {
                problems.Add(new StructureProblem(StructureProblemKind.AnswerValueMismatch, $"col.rename.invalid: the answer to `{q.Id}` renames `{r}` to `{a.Value}`, which is not a new column of {table.Name} that is still free. New columns: {string.Join(", ", added)}."));
                continue;
            }
            renameTargets.Add(a.Value!);
            RenameColumn(r, a.Value!);
            steps.Add(Ddl(table.Name, $"rename column {r} to {a.Value}", ddl.RenameColumn(schema, name, r, a.Value!), RiskClass.Risky, ["col.renamed", $"answer {q.Id} = rename_to {a.Value}"], HashNow()));
            removed.Remove(r);
            added.Remove(a.Value!);
            // the renamed column may differ in more than its name; the common-column check below handles that
        }
        if (problems.Count > problemsBefore) return false;

        foreach (var r in removed)
        {
            Replace(r, null);
            liveByName.Remove(r);
            steps.Add(Ddl(table.Name, $"drop column {r}", ddl.DropColumn(schema, name, r), RiskClass.Destructive, ["col.removed"], HashNow()));
        }

        foreach (var a in added)
        {
            var col = declaredByName[a];
            current.Add(col.Expected);
            var reasons = new List<string> { col.Nullable ? "col.added" : "col.added.notnull" };
            var q = new Question(QuestionIds.History(table.Name, a),
                $"{table.Name} gains column `{a}`. What about the rows that are already loaded?",
                ["Rows loaded before this change will have NULL (or the column default) in the new column."],
                [new("not_backfilled", "Leave existing rows as they are", "Rows loaded before this change keep NULL in the column; recorded in the plan."),
                 new("backfill_later", "Request a backfill separately", "The plan records the intent; run a backfill plan afterwards to fill the column.")]);
            var answer = host.Answer(q);
            if (answer == null) { open = true; continue; }
            reasons.Add($"answer {q.Id} = {answer.Choice}");
            host.Notice(answer.Choice == "not_backfilled"
                ? $"Rows of {table.Name} loaded before this plan will have NULL in `{a}` (history disposition: not_backfilled{(answer.Note == null ? "" : ", " + answer.Note)})."
                : $"A backfill of `{a}` in {table.Name} was requested but is not part of this plan.");
            steps.Add(Ddl(table.Name, $"add column {a}", ddl.AddColumn(schema, name, col), col.Nullable ? RiskClass.Safe : RiskClass.Risky, reasons, HashNow()));
        }

        // columns present on both sides: type, nullability and collation
        foreach (var col in declared.Where(d => liveByName.ContainsKey(d.Name) && !added.Contains(d.Name)))
        {
            var from = liveByName[col.Name];
            var to = col.Expected;
            if (from == to) continue;
            if (from.Computed != null) { host.Notice($"`{col.Name}` of {table.Name} is a computed column on the target and was not changed."); continue; }
            var change = TableDdl.Classify(from, to);
            var risk = RiskClass.Safe;
            var reasons = new List<string>();
            if (change == TypeChange.Widening) reasons.Add("col.type.widen");
            else if (change == TypeChange.Other) { risk = RiskClass.Destructive; reasons.Add("col.type.other"); }
            if (from.Nullable != to.Nullable) { reasons.Add(to.Nullable ? "col.null.relax" : "col.null.tighten"); if (!to.Nullable && risk == RiskClass.Safe) risk = RiskClass.Risky; }
            if (from.Collation != to.Collation) { reasons.Add("col.collation"); if (risk == RiskClass.Safe) risk = RiskClass.Risky; }
            Replace(col.Name, to);
            steps.Add(Ddl(table.Name, $"alter column {col.Name}", ddl.AlterColumn(schema, name, col), risk, reasons, HashNow()));
        }
        return !open;

        static string Size(ColumnShape s) => s.Length != null ? $"({(s.Length == -1 ? "max" : s.Length.ToString())})" : s.Precision != null ? $"({s.Precision}, {s.Scale})" : "";
    }
}
