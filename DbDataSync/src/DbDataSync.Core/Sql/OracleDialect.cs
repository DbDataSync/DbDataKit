using DbDataKit.Dialect;
namespace DbDataSync.Core.Sql;

/// <summary>
/// Oracle, on Oracle.ManagedDataAccess.Core (ODP.NET Core).
/// <para>
/// Two real findings from testing this against a live Oracle 23ai instance, not assumed from the ANSI
/// standard or from Postgres's own precedent — both worth stating up front, since both contradict what
/// a first draft of this dialect would reasonably have assumed:
/// </para>
/// <para>
/// **`OVERRIDING SYSTEM VALUE` does not parse on Oracle at all**, in any form (`INSERT ... VALUES`,
/// `INSERT ... SELECT`, or as a `MERGE`'s `INSERT` clause) — confirmed via `sqlplus`, ruling out an
/// ODP.NET client-side quirk. This is the ANSI SQL:2003 clause Postgres uses for exactly this purpose
/// (see <c>PostgresDialect.RenderInsertInto</c>), and Oracle's own SQL Language Reference describes it
/// for identity columns — but it is not what this server actually accepts. See
/// <see cref="WriteWithGeneratedColumnOverrideAsync"/>.
/// </para>
/// <para>
/// **`UseDatabaseAsync` cannot validate anything here.** Oracle connections are bound to one service
/// for their lifetime, the same as Postgres — but unlike Postgres, where <c>connection.Database</c> is
/// the connected database's real name, <c>OracleConnection.Database</c> is confirmed (by reflection and
/// by a live connection) to be an empty string regardless of what the connection is actually pointed
/// at. The value that *is* populated, <c>OracleConnection.ServiceName</c>, is Oracle-specific and
/// reaching it would mean <c>DbDataSync.Core</c> referencing <c>Oracle.ManagedDataAccess.Core</c> — a
/// package dependency no dialect in this project has, and a real layering line worth keeping. So this
/// override does not validate at all, rather than fabricate a check against a property that carries
/// nothing.
/// </para>
/// </summary>
public sealed class OracleDialect : SqlDialect
{
    public static OracleDialect Instance { get; } = new();

    private OracleDialect() { }

    private static readonly ISqlDialect Shared = DialectCatalog.Builtin.Get("oracle");

    public override string QuoteIdentifier(string identifier) => SharedQuoting.Quote(Shared, identifier);

    public override string ParameterReference(string name) => $":{name}";

    /// <summary>
    /// A real, tested divergence from every other driver here: <c>OracleParameter.ParameterName</c>
    /// rejects a colon baked into it (<c>ORA-01745: invalid host/bind variable name</c>) — confirmed by
    /// running the generic pipeline against a live server, not assumed. SQL Server and Postgres/MySQL's
    /// providers tolerate the sigil either way, which is exactly why <see cref="SqlDialect"/>'s own doc
    /// comment on the base <c>ParameterName</c> already anticipated a provider that would not: "some
    /// providers want the bare name without its sigil, which is why the two are separate." Oracle is
    /// that provider.
    /// </summary>
    public override string ParameterName(string name) => name;

    // MaxParametersPerStatement: not overridden. Oracle's real bind-variable ceiling for one statement
    // is high, but this was not measured against a live server the way phase 147's MySQL/Postgres
    // figures were, and the base class's own doc comment is explicit about the consequence of
    // guessing wrong here: "a dialect that forgets to state its own limit should batch too
    // conservatively rather than emit a statement the server rejects." Inherits SqlDialect's
    // conservative default (2100) until someone measures the real one.

    public override string OperationMarkerColumnType => "char(1)";

    public override string ChangedAtColumnType => "timestamp(6)";

    /// <summary>See this class's own doc comment — there is genuinely nothing to validate against
    /// using only <see cref="System.Data.Common.DbConnection"/>'s provider-agnostic surface.</summary>
    public override Task UseDatabaseAsync(
        System.Data.Common.DbConnection connection, string database, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// <c>GENERATED ALWAYS AS IDENTITY</c> — matches <see cref="DbDataSync.Drivers.Oracle.OracleTriggerAudit"/>'s
    /// own shadow table sequence column, and correct here too: nothing ever supplies an explicit value
    /// for the staging table's own ordinal, so the fact that Oracle has no working override mechanism
    /// for this generation mode (see <see cref="WriteWithGeneratedColumnOverrideAsync"/>) never matters
    /// for this column.
    /// </summary>
    public override string RenderStagingOrdinalColumn(string column) =>
        $"{QuoteIdentifier(column)} NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY";

    /// <summary>
    /// Oracle has no <c>VALUES (…), (…)</c> multi-row form, and <c>INSERT ALL</c> — the form this once
    /// rendered — turned out to be the wrong choice for exactly the table this hook exists to serve:
    /// confirmed against a live server that <c>INSERT ALL</c>'s multiple <c>INTO</c> branches all see
    /// the *same* generated identity/sequence value, not one each, because Oracle evaluates a sequence
    /// (identity columns included — they're sequence-backed under the hood) at most once per statement,
    /// however many times it's referenced within it. A staging table's own ordinal column
    /// (<see cref="RenderStagingOrdinalColumn"/>) needs a distinct value per row, so a multi-row
    /// <c>INSERT ALL</c> against it reliably raised a unique-constraint violation on the second row.
    /// <para>
    /// <c>INSERT INTO t (cols) SELECT * FROM (SELECT … FROM dual UNION ALL SELECT … FROM dual …)</c> —
    /// an ordinary <c>INSERT ... SELECT</c> whose source happens to be a literal row per branch — does
    /// not have this problem: it inserts one row at a time from the driving query's own result set, the
    /// same shape as any other multi-row <c>INSERT ... SELECT</c>, so identity generation fires once
    /// per row exactly as it does for a real query. Confirmed against a live server to generate distinct
    /// sequential values for every row in one statement, which <c>INSERT ALL</c> never did.
    /// </para>
    /// </summary>
    public override string RenderMultiRowInsert(string qualifiedTable, string columnList, IReadOnlyList<string> rowValueTuples)
    {
        // Each tuple arrives as "(v0, v1, …)"; the outer parens are stripped rather than trimmed
        // character-by-character, since a value itself could legitimately end in ')' (a function call)
        // and Trim('(', ')') would eat that too.
        var rows = rowValueTuples.Select(tuple => $"SELECT {tuple[1..^1]} FROM dual");
        return $"""
            INSERT INTO {qualifiedTable} ({columnList})
            SELECT * FROM (
                {string.Join("\n    UNION ALL ", rows)}
            )
            """;
    }

    /// <summary>
    /// A real, tested constraint, not a design preference: confirmed against a live Oracle 23ai
    /// instance (via <c>sqlplus</c>, so not an ODP.NET client quirk) that <c>OVERRIDING SYSTEM VALUE</c>
    /// does not parse in any statement shape, and that a <c>GENERATED ALWAYS AS IDENTITY</c> column
    /// rejects an explicit value outright (<c>ORA-32795</c>) with no session-level escape hatch the way
    /// SQL Server's <c>SET IDENTITY_INSERT</c> is. The one DDL-based workaround —
    /// <c>ALTER TABLE ... MODIFY id GENERATED BY DEFAULT ON NULL AS IDENTITY</c> around the write, then
    /// back — is not offered here: Oracle DDL commits implicitly, so running it around a write would
    /// silently end whatever transaction the caller is in, which is worse than the write failing.
    /// <para>
    /// The one mechanism that *is* confirmed to work, by contrast, needs no override machinery at all:
    /// a target column declared <c>GENERATED BY DEFAULT ON NULL AS IDENTITY</c> accepts an explicit
    /// value through a plain <c>INSERT</c>, verified directly. An Oracle target table that needs
    /// explicit-value round-tripping (mirroring a source's own identity column) should be created that
    /// way — a provisioning/documentation recommendation, not something this hook can retrofit onto a
    /// table it did not create.
    /// </para>
    /// </summary>
    public override Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string qualifiedTable,
        bool overrideRequired,
        Func<Task<T>> write,
        CancellationToken cancellationToken) => write();

    /// <summary>Oracle's <c>CAST</c> accepts neither bare identifiers as its own target-type keyword
    /// set includes no ambiguity here — <c>VARCHAR2</c> is the faithful choice, unlike MySQL's
    /// <c>CAST</c> restriction to <c>CHAR</c>.</summary>
    public override string CastToText(string expression) => $"CAST({expression} AS VARCHAR2(4000))";

    /// <summary><c>NUMBER(1)</c> with <c>1</c>/<c>0</c> — Oracle has no native boolean column type
    /// before 23c, and this driver's floor (see <c>OracleTriggerAudit</c>'s own identity-column
    /// decision) does not assume 23c+. A deliberate convention, not a default nobody chose.</summary>
    public override string TrueLiteral => "1";

    public override string FalseLiteral => "0";

    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "number" => BucketableKind.Numeric,
        "binary_float" or "binary_double" => BucketableKind.Numeric,
        "date" or "timestamp" => BucketableKind.DateTime,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>The type tables are the shared <c>oracle</c> dialect's (<c>dialects/oracle.yaml</c>): Oracle <c>DATE</c> reads as a
    /// timestamp (it carries a time of day), <c>NUMBER(n,0)</c> as the smallest integer that holds it, a bare <c>NUMBER</c> as
    /// <c>DECIMAL(38,10)</c> with a note, and <c>ROWID</c>/<c>UROWID</c>/<c>BFILE</c>/spatial types are unmappable.</summary>
    public override CanonicalType ToCanonicalType(string nativeType) => SharedTypes.ToSync(Shared.ToCanonical(nativeType));

    /// <summary>Sync keeps a canonical <c>TIME</c> as a <c>TIMESTAMP</c> column, where the shared table says an interval of the day.
    /// Existing Oracle targets were created that way, and the interval reads back as a different canonical type.</summary>
    public override RenderedColumnType RenderColumnType(CanonicalType type) => type.Kind == CanonicalTypeKind.Time
        ? new RenderedColumnType(
            $"timestamp({Math.Min(type.Scale ?? 6, 9)})",
            type.SourceNote is null
                ? "Oracle has no native TIME-only type; stored as TIMESTAMP, whose date portion is meaningless."
                : $"{type.SourceNote} Oracle has no native TIME-only type; stored as TIMESTAMP, whose date portion is meaningless.")
        : SharedTypes.Render(Shared, type);
}
