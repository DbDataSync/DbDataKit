using System.Globalization;
using System.Text;
using Apache.Arrow;
using DbDataKit.Access;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.Store;
using DbDataGateway.Data;
using DbDataGateway.Server.Auth;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The reader of a semantic model's rules tables and filtered tables for <c>access explain</c> and <c>access coverage</c> (plan 22a GP15): the same data source, the same physical names (<see cref="RowAccessPlan.PhysicalColumn"/>) and the same strict,
/// NULL-safe comparison as the row filter of the serving core, run with the Gateway's own login like the resolver's read of a rules table's names. It reads rows; it never decides what applies.
/// </summary>
internal sealed class SemanticRulesReader(GatewayAccess access, SemanticModel model) : IRulesTableReader
{
    public IReadOnlyList<RulesTableRow> Rows(RowRule rule, IReadOnlyCollection<string> names, int limit)
    {
        if (names.Count == 0) return [];
        var rules = GatewayAccess.TableOf(model, rule.Table) ?? throw new InvalidOperationException($"The model has no table '{rule.Table}'.");
        var subject = Physical(rules, rule.Subject, rule.Table);
        var columns = rule.Columns.Select(c => c.RulesColumn).Distinct(StringComparer.Ordinal).Select(c => (Name: c, Physical: Physical(rules, c, rule.Table))).ToList();
        var list = string.Join(", ", names.Select(Literal));
        var sql = $"SELECT r.{subject}, {string.Join(", ", columns.Select(c => "r." + c.Physical))} FROM {rules["source"]?.GetValue<string>() ?? rule.Table} r WHERE r.{subject} IN ({list})";
        var rows = new List<RulesTableRow>();
        Run(Serving(rules), sql, (arrays, row) =>
        {
            rows.Add(new RulesTableRow(ArrowText.Get(arrays[0], row) ?? "", columns.Select((c, i) => (c.Name, Value: ArrowText.Get(arrays[i + 1], row))).ToDictionary(x => x.Name, x => x.Value)));
            return rows.Count < limit;
        });
        return rows;
    }

    public UncoveredValues Uncovered(string dataset, RowRule rule, int limit)
    {
        var filtered = GatewayAccess.TableOf(model, dataset) ?? throw new InvalidOperationException($"The model has no table '{dataset}'.");
        var rules = GatewayAccess.TableOf(model, rule.Table) ?? throw new InvalidOperationException($"The model has no table '{rule.Table}'.");
        var pairs = rule.Columns.Select(c => (Mine: Physical(filtered, c.DatasetColumn, dataset), Theirs: Physical(rules, c.RulesColumn, rule.Table), Name: c.DatasetColumn)).ToList();
        var match = string.Join(" AND ", pairs.Select(p => $"(d.{p.Mine} = r.{p.Theirs} OR (d.{p.Mine} IS NULL AND r.{p.Theirs} IS NULL))"));
        var sql = $"SELECT {string.Join(", ", pairs.Select(p => "d." + p.Mine))}, COUNT(*) FROM {filtered["source"]?.GetValue<string>() ?? dataset} d WHERE NOT EXISTS (SELECT 1 FROM {rules["source"]?.GetValue<string>() ?? rule.Table} r WHERE {match}) "
            + $"GROUP BY {string.Join(", ", pairs.Select(p => "d." + p.Mine))} ORDER BY COUNT(*) DESC";
        var values = new List<UncoveredValue>();
        var count = 0;
        Run(Serving(filtered), sql, (arrays, row) =>
        {
            count++;
            if (values.Count < limit)
                values.Add(new UncoveredValue(pairs.Select((p, i) => (p.Name, Value: ArrowText.Get(arrays[i], row))).ToDictionary(x => x.Name, x => x.Value), long.Parse(ArrowText.Get(arrays[pairs.Count], row) ?? "0", CultureInfo.InvariantCulture)));
            return true;
        });
        return new UncoveredValues(count, values, count > values.Count);
    }

    private static string Physical(System.Text.Json.Nodes.JsonObject table, string column, string tableName) =>
        RowAccessPlan.PhysicalColumn(table, column) ?? throw new InvalidOperationException($"The column '{column}' of '{tableName}' is an expression, not a column name of the source, so a rule cannot be read over it.");

    private string Serving(System.Text.Json.Nodes.JsonObject table) => model.ServingSource(table["x_source"]?.GetValue<string>() ?? "");

    /// <summary>A text literal for a name the resolver found in the rules table; a name no standard literal can carry is refused instead of being guessed at.</summary>
    internal static string Literal(string name)
    {
        if (name.Any(c => c == '\\' || char.IsControl(c))) throw new InvalidOperationException("A name in the rules table holds a backslash or a control character, which the explanation does not put in a statement.");
        return "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private void Run(string serving, string sql, Func<IArrowArray[], int, bool> row)
    {
        if (!access.Options.DataSources.TryGetValue(serving, out var source)) throw new InvalidOperationException($"The data source '{serving}' is not served here.");
        if (source.PassThrough) throw new InvalidOperationException($"The data source '{serving}' answers with each caller's own database login, so the Gateway cannot read a rules table on it");
        var resolved = access.Sources.Resolve(new GatewayPrincipal("access-rules", "service", [serving]), serving);
        Task.Run(async () =>
        {
            await using var result = await resolved.Executor.ExecuteQueryAsync(new QueryRequest(sql, resolved.Options.BatchSize, resolved.Options.CommandTimeoutSeconds, null), CancellationToken.None).ConfigureAwait(false);
            await foreach (var batch in result.ReadBatchesAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var arrays = Enumerable.Range(0, batch.ColumnCount).Select(batch.Column).ToArray();
                for (var i = 0; i < batch.Length; i++)
                    if (!row(arrays, i)) return;
            }
        }).GetAwaiter().GetResult();
    }
}

/// <summary>The same for an entity of a Store model: the writer knows the model's own database, its quoting and the entity's tables (<see cref="ModelWriter.RulesRowsAsync"/>, <see cref="ModelWriter.UncoveredAsync"/>).</summary>
internal sealed class EntityRulesReader(ModelWriter writer) : IRulesTableReader
{
    public IReadOnlyList<RulesTableRow> Rows(RowRule rule, IReadOnlyCollection<string> names, int limit) => writer.RulesRowsAsync(rule, names, limit).GetAwaiter().GetResult();

    public UncoveredValues Uncovered(string dataset, RowRule rule, int limit) => writer.UncoveredAsync(dataset, rule, limit).GetAwaiter().GetResult();
}
