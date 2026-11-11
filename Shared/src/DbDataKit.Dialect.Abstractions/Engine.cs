namespace DbDataKit.Dialect;

/// <summary>Stable lower-case engine identifier: sqlserver, postgres, mysql, oracle, duckdb, fabric, snowflake, odbc:&lt;name&gt;, ...</summary>
public readonly record struct EngineId
{
    public EngineId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim().ToLowerInvariant();
    }

    public string Value { get; }
    public static implicit operator EngineId(string value) => new(value);
    public override string ToString() => Value ?? string.Empty;

    public static readonly EngineId Ansi = new("ansi");
    public static readonly EngineId SqlServer = new("sqlserver");
    public static readonly EngineId Postgres = new("postgres");
    public static readonly EngineId MySql = new("mysql");
    public static readonly EngineId Oracle = new("oracle");
    public static readonly EngineId DuckDb = new("duckdb");
    public static readonly EngineId Fabric = new("fabric");
}

public sealed record EngineVersion(int Major, int Minor = 0, int Patch = 0) : IComparable<EngineVersion>
{
    public int CompareTo(EngineVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    /// <summary>Parses "16.0.1000" or "15.4 (Debian)" style text; trailing non-digits are ignored.</summary>
    public static bool TryParse(string? text, out EngineVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.', 4);
        var nums = new int[3];
        for (int i = 0; i < parts.Length && i < 3; i++)
        {
            int end = 0;
            while (end < parts[i].Length && char.IsAsciiDigit(parts[i][end])) end++;
            if (end == 0 || !int.TryParse(parts[i].AsSpan(0, end), out nums[i])) { if (i == 0) return false; break; }
            if (end < parts[i].Length) break;
        }
        version = new EngineVersion(nums[0], nums[1], nums[2]);
        return true;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>Capability queries replace <c>if (engine == "...")</c> branches.</summary>
[Flags]
public enum EngineCaps : long
{
    None = 0,
    Merge = 1 << 0,
    UpsertOnConflict = 1 << 1,
    Returning = 1 << 2,
    TransactionalDdl = 1 << 3,
    TempTables = 1 << 4,
    Schemas = 1 << 5,
    BulkCopy = 1 << 6,
    MultiStatement = 1 << 7,
    NamedParameters = 1 << 8,
    PositionalParameters = 1 << 9,
    TruncateTable = 1 << 10,
    IdentityInsert = 1 << 11,
    Sequences = 1 << 12,
}

public enum ParameterStyle
{
    /// <summary>@name (SQL Server).</summary>
    NamedAt,
    /// <summary>:name (Oracle).</summary>
    NamedColon,
    /// <summary>? (ODBC, MySQL, ANSI dynamic SQL).</summary>
    Positional,
    /// <summary>$1 (PostgreSQL, DuckDB).</summary>
    Numbered,
}

public enum RowLimitStyle
{
    /// <summary>LIMIT n OFFSET m.</summary>
    LimitOffset,
    /// <summary>OFFSET m ROWS FETCH NEXT n ROWS ONLY.</summary>
    OffsetFetch,
    /// <summary>SELECT TOP (n).</summary>
    TopN,
}

/// <summary>A rendered row-limit. Splice <see cref="SelectPrefix"/> right after SELECT and append <see cref="Suffix"/> after the ORDER BY.</summary>
/// <param name="TieSafe">True when rows that tie with the last row are also returned (WITH TIES).</param>
/// <param name="RequiresOrderBy">The rendering is only deterministic (or, for OFFSET/FETCH, only legal) with an ORDER BY.</param>
public sealed record RowLimitFragment(string SelectPrefix, string Suffix, bool TieSafe, bool RequiresOrderBy, string? Note = null);

public enum IdentifierCase { Preserve, Upper, Lower }

/// <summary>Identifier rules of an engine.</summary>
public sealed record IdentifierRules(IdentifierCase Fold, int MaxLength, IReadOnlySet<string> ReservedWords)
{
    public bool IsReserved(string name) => ReservedWords.Contains(name.ToUpperInvariant());

    public string FoldCase(string name) => Fold switch
    {
        IdentifierCase.Upper => name.ToUpperInvariant(),
        IdentifierCase.Lower => name.ToLowerInvariant(),
        _ => name,
    };
}

/// <summary>Lexical forms an engine's SQL uses. Drives the lexer; also describes literal escaping.</summary>
[Flags]
public enum SqlSyntax
{
    None = 0,
    /// <summary>"..." is a quoted identifier ("" escapes).</summary>
    DoubleQuoteIdentifier = 1 << 0,
    /// <summary>[...] is a quoted identifier (]] escapes).</summary>
    BracketIdentifier = 1 << 1,
    /// <summary>`...` is a quoted identifier (`` escapes).</summary>
    BacktickIdentifier = 1 << 2,
    /// <summary>"..." is a string literal (MySQL default mode).</summary>
    DoubleQuoteString = 1 << 3,
    /// <summary>Backslash escapes inside '...' strings (MySQL).</summary>
    BackslashEscapes = 1 << 4,
    /// <summary>E'...' strings with backslash escapes (PostgreSQL).</summary>
    EStrings = 1 << 5,
    /// <summary>N'...' national strings.</summary>
    NStrings = 1 << 6,
    /// <summary>B'..', X'..', U&amp;'..' prefixed strings.</summary>
    PrefixedStrings = 1 << 7,
    /// <summary>$tag$...$tag$ strings.</summary>
    DollarQuoting = 1 << 8,
    /// <summary>q'[...]' alternative quoting (Oracle).</summary>
    OracleQQuote = 1 << 9,
    /// <summary>/* ... */ comments nest.</summary>
    NestedBlockComments = 1 << 10,
    /// <summary># starts a line comment (MySQL).</summary>
    HashComments = 1 << 11,
    /// <summary>"--" only starts a comment when followed by whitespace (MySQL).</summary>
    DashDashNeedsSpace = 1 << 12,
    /// <summary>/*! ... */ is executable (MySQL); the lexer flags such comments.</summary>
    ExecutableComments = 1 << 13,
    /// <summary>? is a parameter marker.</summary>
    QuestionParameter = 1 << 14,
    /// <summary>@name is a parameter marker.</summary>
    AtParameter = 1 << 15,
    /// <summary>:name is a parameter marker.</summary>
    ColonParameter = 1 << 16,
    /// <summary>$1 is a parameter marker.</summary>
    DollarNumberedParameter = 1 << 17,
    /// <summary>$name is a parameter marker (DuckDB).</summary>
    DollarNamedParameter = 1 << 18,
    /// <summary># may start an identifier (#temp in T-SQL).</summary>
    HashIdentifiers = 1 << 19,
}

/// <summary>Per-engine deny lists for the read-only policy. All matching is case-insensitive on unquoted words.</summary>
/// <param name="DenyWords">Words refused anywhere (functions, statements, options).</param>
/// <param name="DenyPrefixes">Words refused when they start with one of these (xp_, dbms_).</param>
/// <param name="SelectStarters">Extra leading words that begin a read-only statement (duckdb FROM-first, postgres TABLE).</param>
public sealed record ReadOnlyRules(IReadOnlySet<string> DenyWords, IReadOnlyList<string> DenyPrefixes, IReadOnlySet<string> SelectStarters)
{
    public static readonly ReadOnlyRules Empty = new(new HashSet<string>(), [], new HashSet<string>());
}
