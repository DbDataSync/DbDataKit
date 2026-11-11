using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>
/// How a caller writes identifiers and parameter references into a statement, as callbacks. The shared builders accept these as an additional way in
/// (plan 13, A16a): a product that has its own dialect type, or serves engines the shared dialect catalog does not cover (JDBC, ODBC, descriptor
/// drivers), supplies its own quoting and its own placeholder text; a caller that has an <see cref="ISqlDialect"/> uses <see cref="From"/>.
/// </summary>
public sealed record StatementSyntax
{
    /// <summary>Quotes one identifier.</summary>
    public required Func<string, string> QuoteIdentifier { get; init; }

    /// <summary>
    /// The text that refers to a parameter inside a statement: the zero-based position of the parameter and its logical name (<c>seg0</c>,
    /// <c>segMin</c>, <c>afterOrdinal</c>, ...). A callback may use either, or neither: Sync's own dialect uses the name only.
    /// </summary>
    public required Func<int, string, string> ParameterReference { get; init; }

    /// <summary>
    /// Whether a parameter used twice in one statement is one parameter (named styles) or two (<see cref="Dialect.ParameterStyle.Positional"/>, where every
    /// occurrence is bound in order). Also the style the ADO executor binds with when this syntax is in use.
    /// </summary>
    public ParameterStyle ParameterStyle { get; init; } = ParameterStyle.NamedAt;

    /// <summary>Quotes a qualified name. Null: each part is quoted by <see cref="QuoteIdentifier"/> and the parts are joined with a dot.</summary>
    public Func<IEnumerable<string>, string>? QuoteQualified { get; init; }

    /// <summary>
    /// The head of an INSERT: <c>(quoted table, quoted column list, overrideGenerated)</c>. Null: <c>INSERT INTO t (cols)</c>, which is also what an engine
    /// that puts nothing in the statement renders. Called with <c>overrideGenerated</c> true only for <see cref="GeneratedColumnOverride.Clause"/>.
    /// </summary>
    public Func<string, string, bool, string>? InsertInto { get; init; }

    /// <summary>
    /// Transact-SQL's spelling of MERGE: <c>;</c> ends every statement, <c>WHEN NOT MATCHED BY TARGET</c> is written out, update targets are qualified
    /// (<c>tgt.[c] = src.[c]</c>) and a CTE can be the target of a MERGE with <c>WHEN NOT MATCHED BY SOURCE</c>. Off is the standard spelling (PostgreSQL 15+).
    /// </summary>
    public bool TransactSql { get; init; }

    /// <summary>
    /// The operation-marker and ordinal columns of a staging table are quoted like any identifier. Sync's SQL Server writers have always written them bare
    /// (<c>__Operation</c>); a caller that wants the same text sets this false.
    /// </summary>
    public bool QuoteBookkeepingColumns { get; init; } = true;

    /// <summary>
    /// How an UPDATE that takes its values from the stage is written (<see cref="UpdateStyle"/>). Engines spell the join differently and no one spelling runs on all
    /// of them; the default is the correlated form, which every engine accepts.
    /// </summary>
    public UpdateStyle UpdateStyle { get; init; } = UpdateStyle.Correlated;

    /// <summary>The UPDATE spelling an engine takes: its own join form where it has one, the correlated subquery otherwise (Oracle and any engine not listed).</summary>
    public static UpdateStyle UpdateStyleOf(EngineId engine) =>
        engine == EngineId.SqlServer || engine == EngineId.Fabric ? UpdateStyle.FromJoin
        : engine == EngineId.Postgres || engine == EngineId.DuckDb ? UpdateStyle.UpdateFrom
        : engine == EngineId.MySql ? UpdateStyle.JoinSet
        : UpdateStyle.Correlated;

    /// <summary>The syntax of a shared dialect. T-SQL spelling for SQL Server and Fabric.</summary>
    public static StatementSyntax From(ISqlDialect dialect) => new()
    {
        UpdateStyle = UpdateStyleOf(dialect.Id),
        QuoteIdentifier = dialect.Quote,
        ParameterReference = dialect.ParameterName,
        ParameterStyle = dialect.ParameterStyle,
        QuoteQualified = dialect.QuoteQualified,
        TransactSql = dialect.Id == EngineId.SqlServer || dialect.Id == EngineId.Fabric,
    };
}

/// <summary>
/// The spellings of an UPDATE whose new values come from another table (the stage). Each engine's own join form where it has one, and the correlated
/// subquery (what Oracle needs, and what runs everywhere) where it does not.
/// </summary>
public enum UpdateStyle
{
    /// <summary><c>UPDATE tgt SET tgt.c = src.c FROM target tgt JOIN stage src ON ...</c> (SQL Server, Fabric).</summary>
    FromJoin,
    /// <summary><c>UPDATE target tgt SET c = src.c FROM stage src WHERE ...</c> (PostgreSQL, DuckDB).</summary>
    UpdateFrom,
    /// <summary><c>UPDATE target tgt JOIN stage src ON ... SET tgt.c = src.c</c> (MySQL, MariaDB).</summary>
    JoinSet,
    /// <summary><c>UPDATE target tgt SET (c1, c2) = (SELECT ... WHERE key) WHERE EXISTS (...)</c> (Oracle; no MERGE and no join form).</summary>
    Correlated,
}

/// <summary>A table or view as a statement refers to it: a name the syntax quotes, or text the caller already rendered (a temp table, a placeholder in a preview).</summary>
public readonly record struct SqlRelation
{
    private SqlRelation(TableName? name, string? text) { Name = name; Text = text; }

    public TableName? Name { get; }
    public string? Text { get; }

    public static SqlRelation Of(TableName name) => new(name, null);
    /// <summary>Text that goes into the statement as it is. It must be a table reference the caller has already quoted; it is never a value.</summary>
    public static SqlRelation Raw(string text) => new(null, text);
    public static implicit operator SqlRelation(TableName name) => Of(name);

    public string Render(StatementSyntax syntax) =>
        Text ?? (Name is { } n
            ? syntax.QuoteQualified is { } q ? q(n.Parts) : string.Join('.', n.Parts.Select(syntax.QuoteIdentifier))
            : throw new InvalidOperationException("the relation has neither a name nor text"));
}

/// <summary>
/// Which rows of the target a statement may touch: all, a list of values, or a half-open range, on one named column. The shared builder owns the
/// <i>shape</i> of the predicate and the placeholder text; the caller binds each value, typed as its engine needs (a <c>varchar</c> against a
/// <c>varchar</c> column so the index is still seeked), from the <see cref="ParameterSlot"/>s the builder returns.
/// </summary>
public abstract record ScopeFilter
{
    private ScopeFilter() { }

    /// <summary>Every row; renders <c>1 = 1</c> so a statement can always carry a predicate.</summary>
    public static ScopeFilter All { get; } = new AllRows();
    /// <summary><c>column IN (v0, v1, ...)</c>. An empty list is refused: it matches nothing, which for a reconciling write would delete a whole scope.</summary>
    public static ScopeFilter List(string column, IReadOnlyList<object?> values, CanonicalType? type = null) => new ValueList(column, values, type);
    /// <summary><c>column &gt;= min AND column &lt; max</c>: consecutive ranges tile a value space with no gap and no row twice.</summary>
    public static ScopeFilter Range(string column, object? min, object? max, CanonicalType? type = null) => new ValueRange(column, min, max, type);

    public sealed record AllRows : ScopeFilter;
    public sealed record ValueList(string Column, IReadOnlyList<object?> Values, CanonicalType? Type) : ScopeFilter;
    public sealed record ValueRange(string Column, object? Min, object? Max, CanonicalType? Type) : ScopeFilter;
}

/// <summary>
/// The staged column that says what a row is for. Rows whose marker equals <see cref="DeleteValue"/> delete the target row on a MERGE and are
/// left out of a refill (the scope was already emptied, so a delete row would only re-add a row to say it is not there).
/// </summary>
public sealed record OperationMarker(string Column = "__Operation", string DeleteValue = "D");

/// <summary>
/// A slice of the staging table by its per-pass row ordinal: rows with <c>Column &gt; After AND Column &lt;= UpTo</c>. A chunked apply issues the same
/// statement once per slice, each a seek on the ordinal rather than another scan of the stage.
/// </summary>
public sealed record OrdinalRange(long After, long UpTo, string Column = "__Ordinal");

/// <summary>How an INSERT that supplies a generated (identity) column's value is allowed to.</summary>
public enum GeneratedColumnOverride
{
    /// <summary>The write supplies no generated column.</summary>
    None,
    /// <summary>The override is part of the statement: the builder asks <see cref="StatementSyntax.InsertInto"/> for the head with the flag set (PostgreSQL <c>OVERRIDING SYSTEM VALUE</c>).</summary>
    Clause,
    /// <summary>The override brackets the write (SQL Server <c>SET IDENTITY_INSERT ... ON/OFF</c>, switched off again on failure). The builder returns the statement with <see cref="BuiltStatement.NeedsOverrideBracket"/>; the caller wraps it.</summary>
    Bracketed,
}

/// <summary>What a parameter slot is for, so the caller knows which value to bind and how to type it.</summary>
public enum ParameterRole
{
    /// <summary>A value of a <see cref="ScopeFilter.ValueList"/>, to bind typed like <see cref="ParameterSlot.Column"/>.</summary>
    ScopeValue,
    /// <summary>The inclusive lower bound of a <see cref="ScopeFilter.ValueRange"/>.</summary>
    ScopeMin,
    /// <summary>The exclusive upper bound of a <see cref="ScopeFilter.ValueRange"/>.</summary>
    ScopeMax,
    /// <summary>The exclusive lower bound of an <see cref="OrdinalRange"/>; always a 64-bit integer.</summary>
    OrdinalAfter,
    /// <summary>The inclusive upper bound of an <see cref="OrdinalRange"/>; always a 64-bit integer.</summary>
    OrdinalUpTo,
}

/// <summary>
/// One parameter a built statement refers to. <see cref="Position"/> is the order of first reference in the text, <see cref="Name"/> the logical name
/// (<c>seg0</c>, <c>segMin</c>, <c>afterOrdinal</c>; the caller turns it into whatever its provider wants as a parameter name), <see cref="Marker"/> the
/// text in the statement. <see cref="Value"/> is the scope value or the ordinal bound; <see cref="Column"/> the scope column a value is compared with.
/// </summary>
public sealed record ParameterSlot(int Position, string Name, string Marker, ParameterRole Role, string? Column, object? Value);

/// <summary>A statement as text plus the parameters it refers to, in order. The caller creates its own typed parameters from the slots.</summary>
public sealed record BuiltStatement(string Text, IReadOnlyList<ParameterSlot> Slots, bool NeedsOverrideBracket = false);

/// <summary>
/// What the scope-shaped builders need, free of any dialect: relation names, the written columns, the key, the scope, the marker, the ordinal slice and the
/// override style. Sync's writers build this from their own metadata; the strategies build it from an <see cref="ApplyRequest"/>.
/// </summary>
public sealed record ScopedApplySpec
{
    public required SqlRelation Target { get; init; }
    /// <summary>The staging table. Not needed by a scope delete.</summary>
    public SqlRelation Stage { get; init; }
    /// <summary>The columns written, in order (the insert list, the select list, the merge's insert and update columns). Not needed by a scope delete.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// <summary>The key columns, a subset of <see cref="Columns"/>; needed by a merge.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
    public ScopeFilter Scope { get; init; } = ScopeFilter.All;
    public OperationMarker? Marker { get; init; }
    public OrdinalRange? Ordinal { get; init; }
    public GeneratedColumnOverride Override { get; init; }
    /// <summary>
    /// The staged column that holds a row's arrival order (the per-pass ordinal), for the collapse of a staged change set (<c>ScopedStatements.CollapseStage</c>): of several staged rows
    /// with one key the highest is the last change and the others are dropped. Null: the <see cref="Ordinal"/> column, else <c>__Ordinal</c>.
    /// </summary>
    public string? SequenceColumn { get; init; }
    /// <summary>Merge only: target rows in <see cref="Scope"/> that the stage does not mention are deleted (SQL Server: the scope is a CTE that is the MERGE target).</summary>
    public bool DeleteUnmatchedInScope { get; init; }
}

/// <summary>
/// The options of the scope-shaped strategies (<c>delete_insert_by_scope</c>, <c>merge_staged_changes</c>) on an <see cref="ApplyRequest"/>. Everything is optional:
/// the defaults are every row, no marker, the whole stage and no override.
/// </summary>
public sealed record ScopedOptions
{
    public ScopeFilter Scope { get; init; } = ScopeFilter.All;
    public OperationMarker? Marker { get; init; }
    public OrdinalRange? Ordinal { get; init; }
    public GeneratedColumnOverride Override { get; init; }
    public bool DeleteUnmatchedInScope { get; init; }
    /// <summary>The staged column holding the arrival order; see <see cref="ScopedApplySpec.SequenceColumn"/>.</summary>
    public string? SequenceColumn { get; init; }
    /// <summary>The caller's quoting and placeholder text. Null: the dialect's (<see cref="StatementSyntax.From"/>).</summary>
    public StatementSyntax? Syntax { get; init; }
}
