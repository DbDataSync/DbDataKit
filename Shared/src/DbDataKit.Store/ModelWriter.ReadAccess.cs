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
}
