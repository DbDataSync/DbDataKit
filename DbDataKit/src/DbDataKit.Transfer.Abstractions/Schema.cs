using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>One column of the rows being moved. Types are canonical (Plan 05); engine types never appear here.</summary>
public sealed record TransferColumn(string Name, CanonicalType Type, bool Nullable = true, bool IsKey = false);

/// <summary>Ordered column list. Names are unique (case-insensitive) so that mapping by name is unambiguous.</summary>
public sealed class TransferSchema
{
    private readonly Dictionary<string, int> _byName;

    public TransferSchema(IEnumerable<TransferColumn> columns)
    {
        Columns = [.. columns];
        _byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Columns.Count; i++)
            if (!_byName.TryAdd(Columns[i].Name, i))
                throw new ArgumentException($"duplicate column name `{Columns[i].Name}`", nameof(columns));
    }

    public IReadOnlyList<TransferColumn> Columns { get; }
    public int Count => Columns.Count;
    public TransferColumn this[int ordinal] => Columns[ordinal];
    public IEnumerable<TransferColumn> Keys => Columns.Where(c => c.IsKey);
    public IEnumerable<TransferColumn> NonKeys => Columns.Where(c => !c.IsKey);

    public int IndexOf(string name) => _byName.TryGetValue(name, out var i) ? i : -1;
    public bool Contains(string name) => _byName.ContainsKey(name);

    public bool SameShapeAs(TransferSchema other) =>
        other.Count == Count && Columns.Zip(other.Columns).All(p => string.Equals(p.First.Name, p.Second.Name, StringComparison.OrdinalIgnoreCase) && p.First.Type == p.Second.Type);

    public override string ToString() => string.Join(", ", Columns.Select(c => $"{c.Name} {c.Type}"));
}

/// <summary>A schema-qualified table. <see cref="Schema"/> is null for engines or temp tables without one.</summary>
public sealed record TableName(string? Schema, string Table)
{
    public IEnumerable<string> Parts => Schema is null ? [Table] : [Schema, Table];
    public override string ToString() => string.Join('.', Parts);
}
