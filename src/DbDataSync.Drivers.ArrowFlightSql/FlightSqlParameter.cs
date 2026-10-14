using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>A command parameter: a name and a value. Input only — a source-only driver has nothing to
/// return through one.</summary>
public sealed class FlightSqlParameter : DbParameter
{
    public override DbType DbType { get; set; } = DbType.Object;

    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
                throw new NotSupportedException("Flight SQL parameters are input only.");
        }
    }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = "";

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = "";

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.Object;
}

public sealed class FlightSqlParameterCollection : DbParameterCollection
{
    private readonly List<FlightSqlParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    internal IReadOnlyList<FlightSqlParameter> Items => _items;

    public override int Add(object value)
    {
        _items.Add(Cast(value));
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
            Add(value);
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => value is FlightSqlParameter p && _items.Contains(p);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => value is FlightSqlParameter p ? _items.IndexOf(p) : -1;

    public override int IndexOf(string parameterName) =>
        _items.FindIndex(p => Matches(p.ParameterName, parameterName));

    public override void Insert(int index, object value) => _items.Insert(index, Cast(value));

    public override void Remove(object value) => _items.Remove(Cast(value));

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => RemoveAt(Require(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[Require(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _items[index] = Cast(value);

    protected override void SetParameter(string parameterName, DbParameter value) => _items[Require(parameterName)] = Cast(value);

    /// <summary>Names are compared without their sigil: a dialect may name a parameter <c>@id</c>,
    /// <c>:id</c> or <c>id</c>, and the statement text may reference it by any of the spellings.</summary>
    internal static bool Matches(string a, string b) =>
        string.Equals(a.TrimStart('@', ':', '$'), b.TrimStart('@', ':', '$'), StringComparison.OrdinalIgnoreCase);

    private int Require(string name)
    {
        var index = IndexOf(name);
        return index >= 0 ? index : throw new IndexOutOfRangeException($"No parameter named '{name}'.");
    }

    private static FlightSqlParameter Cast(object value) =>
        value as FlightSqlParameter
        ?? throw new InvalidCastException($"Expected a {nameof(FlightSqlParameter)}, got {value?.GetType().Name ?? "null"}.");
}
