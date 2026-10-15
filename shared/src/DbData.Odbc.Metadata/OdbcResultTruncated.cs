using System.Collections;

namespace DbData.Odbc.Metadata;

/// <summary>Says that a catalog call had more rows than <see cref="OdbcCatalogOptions.MaxRows"/> and only the first <paramref name="MaxRows"/> were kept.</summary>
public sealed record OdbcResultTruncated(string Function, int MaxRows);

/// <summary>Thrown when <see cref="OdbcRowLimitBehavior.Throw"/> is in effect and a catalog call exceeds the row cap.</summary>
public sealed class OdbcResultTruncatedException(OdbcResultTruncated signal)
    : Exception($"The ODBC catalog call {signal.Function} returned more than {signal.MaxRows} rows; raise OdbcCatalogOptions.MaxRows, narrow the request, or stream it.")
{
    public OdbcResultTruncated Signal { get; } = signal;
}

/// <summary>A list of typed catalog results that remembers whether it was cut short by the row cap.</summary>
public sealed class OdbcResultList<T>(IReadOnlyList<T> items, OdbcResultTruncated? truncation) : IReadOnlyList<T>
{
    /// <summary>Non-null when the underlying catalog call had more rows than were kept.</summary>
    public OdbcResultTruncated? Truncation { get; } = truncation;

    public int Count => items.Count;
    public T this[int index] => items[index];
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Reads the truncation signal off any result returned by <see cref="OdbcCatalog"/>.</summary>
public static class OdbcResultExtensions
{
    /// <summary>The truncation signal, or null when the result is complete.</summary>
    public static OdbcResultTruncated? GetTruncation<T>(this IReadOnlyList<T> result) => (result as OdbcResultList<T>)?.Truncation;
}

/// <summary>
/// Raw rows from a catalog function plus whether the function stopped at the row cap. <see cref="IOdbcCatalogFunctions"/>
/// implementations may return a plain list; <see cref="OdbcCatalog"/> applies the cap to those as well.
/// </summary>
public sealed class OdbcRows(IReadOnlyList<string?[]> rows, bool truncated) : IReadOnlyList<string?[]>
{
    public bool Truncated { get; } = truncated;
    public int Count => rows.Count;
    public string?[] this[int index] => rows[index];
    public IEnumerator<string?[]> GetEnumerator() => rows.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
