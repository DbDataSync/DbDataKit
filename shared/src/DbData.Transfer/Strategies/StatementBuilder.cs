using System.Text;
using DbData.Dialect;

namespace DbData.Transfer;

/// <summary>
/// Builds one parameterised statement. Identifiers go through the dialect's quoting; values only ever become bound parameters,
/// so statement text contains no data.
/// </summary>
internal sealed class StatementBuilder(ISqlDialect dialect)
{
    private readonly List<SqlParameter> _parameters = [];
    private readonly Dictionary<string, SqlParameter> _byName = new(StringComparer.Ordinal);
    private readonly StringBuilder _sql = new();

    public ISqlDialect Dialect => dialect;

    public StatementBuilder Append(string text) { _sql.Append(text); return this; }

    public string Q(string identifier) => dialect.Quote(identifier);
    public string T(TableName table) => dialect.QuoteQualified(table.Parts);
    public string QList(IEnumerable<string> names) => string.Join(", ", names.Select(Q));
    public string Qualified(string alias, string column) => alias + "." + Q(column);

    /// <summary>Returns the placeholder for a named value, adding the parameter on first use (every use for positional styles).</summary>
    public string Param(string name, CanonicalType type, object? value)
    {
        if (dialect.ParameterStyle != ParameterStyle.Positional && _byName.TryGetValue(name, out var existing)) return existing.Marker;
        var index = dialect.ParameterStyle == ParameterStyle.Positional || dialect.ParameterStyle == ParameterStyle.Numbered ? _parameters.Count : _byName.Count;
        var marker = dialect.ParameterName(index, name);
        var p = new SqlParameter(marker, name, type, value);
        _parameters.Add(p);
        _byName[name] = p;
        return marker;
    }

    public SqlStatement Build(string id, WriteIntent intent, StatementRole role = StatementRole.Apply) =>
        new(id, _sql.ToString(), [.. _parameters], intent, role);
}

internal static class StrategyHelpers
{
    public static IReadOnlyList<TransferColumn> Keys(ApplyRequest r, string strategy)
    {
        var keys = r.Schema.Keys.ToList();
        if (keys.Count == 0) throw new ArgumentException($"strategy `{strategy}` needs at least one key column (TransferColumn.IsKey)");
        return keys;
    }

    public static TransferColumn Column(ApplyRequest r, string? name, string what)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException($"{what} is required");
        var i = r.Schema.IndexOf(name);
        return i < 0 ? throw new ArgumentException($"{what} `{name}` is not a column of the schema") : r.Schema[i];
    }

    public static void RequireColumns(ApplyRequest r)
    {
        if (r.Schema.Count == 0) throw new ArgumentException("the schema has no columns");
    }

    /// <summary><c>INSERT INTO target (cols) SELECT cols FROM stage [WHERE ...]</c>; the optional filter appends to the builder.</summary>
    public static void InsertSelect(StatementBuilder b, ApplyRequest r, Action<StatementBuilder>? where = null)
    {
        var cols = b.QList(r.Schema.Columns.Select(c => c.Name));
        b.Append($"INSERT INTO {b.T(r.Target)} ({cols}) SELECT {cols} FROM {b.T(r.Stage)}");
        if (where is not null) { b.Append(" WHERE "); where(b); }
    }

    public static WriteIntent Intent(string strategy, string step, WriteKind kind, ApplyRequest r, WriteRisk risk = WriteRisk.Safe) =>
        new($"{strategy}:{step}", kind, r.Target.ToString(), risk);

    public static string KeyJoin(StatementBuilder b, IEnumerable<TransferColumn> keys, string left, string right) =>
        string.Join(" AND ", keys.Select(k => $"{b.Qualified(left, k.Name)} = {b.Qualified(right, k.Name)}"));
}
