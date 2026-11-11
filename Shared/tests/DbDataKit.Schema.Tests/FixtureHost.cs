using DbDataKit.Schema.Ddl;
using DbDataKit.Schema.Planning;
using DbDataKit.Schema.Questions;
using DbDataKit.Schema.Reading;

namespace DbDataKit.Schema.Tests;

/// <summary>
/// A host that is not Build (plan 19, H05): the smallest thing that keeps a table in the shape it is told to, using only <c>DbDataKit.Schema</c> and the shared catalog readers. It reads the live structure, plans (creating the table, or the column and
/// index changes), refuses anything that needs a person (a question open, a change behind its back, a risky or destructive step that was not allowed) and applies the rest one statement at a time, checking after each that the catalog reports
/// the shape the step promised. What a product adds around this is its own: where the desired table comes from, how a person is asked, what is written down.
/// </summary>
public sealed class FixtureHost(Engines engines, params (string Id, string Choice, string? Value)[] answers) : IStructureHost
{
    private readonly TableDdl _ddl = engines.Engine == Engines.SqlServer ? new TSqlTableDdl("sqlserver") : new PostgresTableDdl();

    public List<string> Applied { get; } = [];
    public List<string> Noticed { get; } = [];
    public List<Question> Open { get; } = [];

    public ResolvedAnswer? Answer(Question question)
    {
        if (answers.FirstOrDefault(a => a.Id == question.Id) is { Id: not null } a) return new ResolvedAnswer(a.Id, a.Choice, a.Value, null, AnswerSource.File);
        Open.Add(question);
        return null;
    }

    public void Notice(string text) => Noticed.Add(text);

    /// <summary>Plans what keeps the table as wanted and applies it. Returns the steps run; throws when it needs a person (the message says what).</summary>
    /// <param name="allow">The highest risk the caller allows without asking.</param>
    public async Task<IReadOnlyList<StructureStep>> EnsureAsync(DesiredTable table, RiskClass allow = RiskClass.Safe)
    {
        var (schema, name) = TableDdl.Split(table.Name);
        var live = (await CatalogShapes.ReadAsync(engines.Reader(), engines.Shape, schema, name)).GetValueOrDefault(table.Name);
        var steps = new List<StructureStep>();
        var problems = new List<StructureProblem>();

        if (live is null)
        {
            steps.AddRange(StructurePlanner.Create(table, _ddl, needsSchema: !await SchemaExistsAsync(schema)));
        }
        else
        {
            // a table nobody recorded is adopted here as it stands: this host keeps no record, so there is nothing to drift from
            var gate = StructurePlanner.Baseline(table.Name, live, null, tracked: false, table.Columns.Count, _ => false, this, steps, [], problems, out _);
            var complete = gate == Gate.Proceed
                && StructurePlanner.Columns(table, live, _ddl, this, steps, problems)
                && StructurePlanner.Indexes(table, live, _ddl, true, this, steps, problems);
            if (!complete || problems.Count > 0 || Open.Count > 0)
                throw new InvalidOperationException(problems.Count > 0 ? problems[0].Message : $"A person is needed: {string.Join("; ", Open.Select(q => q.Prompt))}");
        }

        var refused = steps.FirstOrDefault(s => s.Risk > allow);
        if (refused is not null) throw new InvalidOperationException($"'{refused.Description}' is {refused.Risk.ToString().ToLowerInvariant()} ({string.Join(", ", refused.Reasons)}) and was not allowed.");

        foreach (var step in steps.Where(s => !s.IsTrack))
        {
            await engines.ExecuteAsync(step.Text);
            Applied.Add(step.Description);
            if (step.HashAfter is { } expected)
            {
                var now = (await CatalogShapes.ReadAsync(engines.Reader(), engines.Shape, schema, name))[table.Name];
                if (now.ShapeHash != expected) throw new InvalidOperationException($"After '{step.Description}' the catalog reports {now.ShapeHash}, not the {expected} the plan promised.");
            }
        }
        return steps;
    }

    private async Task<bool> SchemaExistsAsync(string schema)
    {
        await foreach (var s in engines.Reader().ListSchemasAsync())
            if (string.Equals(s.Name, schema, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
