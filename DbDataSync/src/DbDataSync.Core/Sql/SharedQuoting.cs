using DbDataKit.Dialect;

namespace DbDataSync.Core.Sql;

/// <summary>Quotes through a shared dialect, with the one allowance Sync's callers rely on: a blank name (a query source has no schema or table, a connection may have no database) quotes as an empty identifier, as it always has, instead of being refused. Control characters are refused.</summary>
internal static class SharedQuoting
{
    public static string Quote(ISqlDialect dialect, string identifier)
    {
        if (identifier.Length > 0) return dialect.Quote(identifier);
        var probe = dialect.Quote("x");
        var at = probe.IndexOf('x');
        return probe[..at] + probe[(at + 1)..];
    }
}
