namespace DbDataKit.State;

/// <summary>
/// Words that one of the three engines will not take as an unquoted table, index or column name (SQL Server's reserved keywords, PostgreSQL's reserved words). The state layer writes names unquoted so a statement reads the same everywhere, so a module that names something with one of these is refused
/// when the layout is made, on every engine, not on the one that happens to mind (SQL Server refuses a table called <c>checkpoint</c>; SQLite would not have said a word).
/// </summary>
public static class ReservedWords
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "all", "alter", "and", "any", "as", "asc", "authorization", "backup", "begin", "between", "break", "browse", "bulk", "by", "cascade", "case", "check", "checkpoint",
        "close", "clustered", "coalesce", "collate", "column", "commit", "compute", "constraint", "contains", "continue", "convert", "create", "cross", "current", "current_date",
        "current_time", "current_timestamp", "current_user", "cursor", "database", "dbcc", "deallocate", "declare", "default", "delete", "deny", "desc", "disk", "distinct",
        "distributed", "double", "drop", "dump", "else", "end", "errlvl", "escape", "except", "exec", "execute", "exists", "exit", "external", "fetch", "file", "fillfactor", "for",
        "foreign", "freetext", "from", "full", "function", "goto", "grant", "group", "having", "holdlock", "identity", "if", "in", "index", "inner", "insert", "intersect", "into",
        "is", "join", "key", "kill", "left", "like", "lineno", "load", "merge", "national", "natural", "nocheck", "nonclustered", "not", "null", "nullif", "of", "off", "offset", "on",
        "open", "option", "or", "order", "outer", "over", "percent", "pivot", "plan", "precision", "primary", "print", "proc", "procedure", "public", "raiserror", "read", "readtext",
        "reconfigure", "references", "replication", "restore", "restrict", "return", "revert", "revoke", "right", "rollback", "rowcount", "rowguidcol", "rule", "save", "schema",
        "securityaudit", "select", "session_user", "set", "setuser", "shutdown", "some", "statistics", "system_user", "table", "tablesample", "textsize", "then", "to", "top", "tran",
        "transaction", "trigger", "truncate", "try_convert", "tsequal", "union", "unique", "unpivot", "update", "updatetext", "use", "user", "values", "varying", "view", "waitfor",
        "when", "where", "while", "with", "within", "writetext",
        // PostgreSQL
        "analyse", "analyze", "array", "asymmetric", "binary", "both", "collation", "concurrently", "do", "false", "freeze", "ilike", "initially", "isnull", "lateral",
        "leading", "limit", "localtime", "localtimestamp", "notnull", "only", "overlaps", "placing", "returning", "similar", "symmetric", "tablesample", "trailing", "true",
        "variadic", "verbose", "window",
    };

    public static bool IsReserved(string name) => Words.Contains(name);
}
