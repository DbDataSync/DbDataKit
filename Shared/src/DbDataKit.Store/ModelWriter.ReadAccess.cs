using System.Text;
using DbDataKit.Access;
using DbDataKit.DataModel;

namespace DbDataKit.Store;

/// <summary>
/// The row rules of one caller on one entity, as the reads of a <see cref="ModelWriter"/> apply them (plan 22a GP11, QG2): the predicate the resolver of <c>DbDataKit.Access</c> built from the entity's rules and the caller's names, or
/// the reason the entity is refused to the caller (a rules table past its maximum age, or one that cannot be read: D2).
/// </summary>
public sealed record EntityReadAccess(RowPredicate Predicate, string? Refusal = null);

/// <summary>A read is refused to this caller as a whole (the entity's rules cannot be applied to them: a rules table expired or unreadable). Never thrown for one row the caller may not see: that row is simply not there.</summary>
public sealed class StoreReadRefusedException(string message) : Exception(message);

public sealed partial class ModelWriter
{
    /// <summary>
    /// The caller's row rules per entity, or null for an entity with none (and for every entity when the writer has none: a runner landing rows, a server's own reads). Applied by the reads only (<see cref="GetAsync"/>, <see cref="ListAsync"/>,
    /// <see cref="GetAsOfAsync"/>, <see cref="HistoryAsync"/>, <see cref="CountAsync"/>); a write reads the Store's own rows whatever the rules say (QR6: row rules limit reads, a write keeps the Store's roles and approval).
    /// </summary>
    public Func<string, EntityReadAccess?>? ReadAccess { get; set; }

    /// <summary>The number of rows the caller may read in an entity (the open versions, for an entity with history): the total behind <see cref="ListAsync"/>.</summary>
    public async Task<long> CountAsync(string entityName, CancellationToken cancellationToken = default)
    {
        var entity = RequireEntity(entityName);
        return await ViewAsync(async tx =>
        {
            var parameters = new List<object?>();
            var filter = ReadFilter(entity, parameters);
            return Convert.ToInt64(await ScalarAsync(tx, $"SELECT COUNT(*) FROM {Table(entity.Name)} WHERE {OpenOnly(entity)}{filter}", parameters, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c> AND (...)</c> that keeps the rows of the entity the caller's rules let them read, or an empty text when none apply. Every rule is a semi-join (<c>EXISTS</c>) over the rules table, so several matching rows of it never multiply a row,
    /// with the caller's names as parameters and each pair of columns matched as the rules say: a NULL matches only a NULL (QG2). The columns of the entity are named with the table's full name, so a column of the rules table of the same name never shadows them.
    /// </summary>
    private string ReadFilter(ModelEntity entity, List<object?> parameters)
    {
        if (ReadAccess?.Invoke(entity.Name) is not { } access) return "";
        if (access.Refusal is not null) throw new StoreReadRefusedException(access.Refusal);
        return access.Predicate is RowPredicate.Always ? "" : $" AND ({Render(access.Predicate, entity, parameters)})";
    }

    private string Render(RowPredicate predicate, ModelEntity entity, List<object?> parameters) => predicate switch
    {
        RowPredicate.Always => "1 = 1",
        RowPredicate.Never => "1 = 0",
        RowPredicate.Rule rule => Exists(rule.Resolution, entity, parameters),
        RowPredicate.And and => "(" + string.Join(" AND ", and.Items.Select(i => Render(i, entity, parameters))) + ")",
        RowPredicate.Or or => "(" + string.Join(" OR ", or.Items.Select(i => Render(i, entity, parameters))) + ")",
        RowPredicate.Not not => "NOT (" + Render(not.Item, entity, parameters) + ")",
        _ => throw new StoreNotSupportedException("A row predicate of a kind this writer does not know."),
    };

    private string Exists(RuleResolution resolution, ModelEntity entity, List<object?> parameters)
    {
        var rule = resolution.Rule;
        var names = string.Join(", ", resolution.Names.Select(n => "@p" + Add(parameters, n)));
        var text = new StringBuilder($"EXISTS (SELECT 1 FROM {QualifiedTable(rule.Table)} r WHERE r.{Col(rule.Subject)} IN ({names})");
        foreach (var pair in rule.Columns)
        {
            var (mine, theirs) = ($"{Table(entity.Name)}.{Col(pair.DatasetColumn)}", $"r.{Col(pair.RulesColumn)}");
            text.Append($" AND ({mine} = {theirs} OR ({mine} IS NULL AND {theirs} IS NULL))");
        }

        return text.Append(')').ToString();
    }

    /// <summary>A rules table as <c>schema.table</c> (the shape <c>access</c> sections of a model allow), quoted part by part.</summary>
    private string QualifiedTable(string reference) => string.Join(".", reference.Split('.').Select(Col));

    /// <summary>The distinct values of a column of a rules table, read on this writer's own connection (the names the resolver matches a caller against).</summary>
    public IReadOnlyCollection<string> RulesSubjects(string table, string column)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT {Col(column)} FROM {QualifiedTable(table)}";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            if (!reader.IsDBNull(0)) names.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "");
        return names;
    }

    /// <summary>
    /// The rows of the rules table of <paramref name="rule"/> that name one of <paramref name="names"/> (as the table spells them), at most <paramref name="limit"/>, for <c>access explain</c> (plan 22a GP15): the subject and the mapped columns, as text.
    /// </summary>
    public async Task<IReadOnlyList<RulesTableRow>> RulesRowsAsync(RowRule rule, IReadOnlyCollection<string> names, int limit, CancellationToken cancellationToken = default)
    {
        if (names.Count == 0) return [];
        var parameters = new List<object?>();
        var list = string.Join(", ", names.Select(n => "@p" + Add(parameters, n)));
        var columns = rule.Columns.Select(c => c.RulesColumn).Distinct(StringComparer.Ordinal).ToList();
        await using var command = Command(null, $"SELECT r.{Col(rule.Subject)}, {string.Join(", ", columns.Select(c => "r." + Col(c)))} FROM {QualifiedTable(rule.Table)} r WHERE r.{Col(rule.Subject)} IN ({list})", parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<RulesTableRow>();
        while (rows.Count < limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new RulesTableRow(Text(reader.GetValue(0)) ?? "", columns.Select((c, i) => (c, Text(reader.GetValue(i + 1)))).ToDictionary(x => x.c, x => x.Item2)));
        return rows;
    }

    /// <summary>
    /// The combinations of the rule's mapped columns in the entity (its open versions) that no row of the rules table has, NULL counted, for <c>access coverage</c> (plan 22a GP15): one statement, with the same NULL-safe comparison the read filter uses.
    /// All combinations are counted, the first <paramref name="limit"/> (the most rows first) are listed.
    /// </summary>
    public async Task<UncoveredValues> UncoveredAsync(string entityName, RowRule rule, int limit, CancellationToken cancellationToken = default)
    {
        var entity = RequireEntity(entityName);
        var mine = rule.Columns.Select(c => c.DatasetColumn).ToList();
        var match = string.Join(" AND ", rule.Columns.Select(c => $"(d.{Col(c.DatasetColumn)} = r.{Col(c.RulesColumn)} OR (d.{Col(c.DatasetColumn)} IS NULL AND r.{Col(c.RulesColumn)} IS NULL))"));
        var sql = $"SELECT {string.Join(", ", mine.Select(c => "d." + Col(c)))}, COUNT(*) FROM {Table(entity.Name)} d WHERE {OpenOnly(entity, "d")} AND NOT EXISTS (SELECT 1 FROM {QualifiedTable(rule.Table)} r WHERE {match}) "
            + $"GROUP BY {string.Join(", ", mine.Select(c => "d." + Col(c)))} ORDER BY COUNT(*) DESC";
        await using var command = Command(null, sql, []);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<UncoveredValue>();
        var count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            count++;
            if (values.Count < limit) values.Add(new UncoveredValue(mine.Select((c, i) => (c, Text(reader.GetValue(i)))).ToDictionary(x => x.c, x => x.Item2), Convert.ToInt64(reader.GetValue(mine.Count), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new UncoveredValues(count, values, count > values.Count);
    }

    private static string? Text(object? value) => value is null or DBNull ? null : value switch
    {
        DateTime moment => moment.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };
}
