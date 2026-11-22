using DbDataKit.Dialect;
using System.Data.Common;

namespace DbDataSync.Core.Sql;

/// <summary>
/// DuckDB's answers to the mechanical variations in <see cref="SqlDialect"/>.
/// <para>
/// **Not required by <c>DuckDbQueryReader</c>**, which generates no SQL at all — the operator's query
/// is the statement. It exists because everything *around* a reader asks its driver for a dialect
/// regardless of what the reader does with one: <c>RunExecutor.ResolveDialect</c> builds the
/// watermark key from it, and <c>ProvisioningService</c> translates the *source's* native column types
/// through it to render the target's DDL. A driver that named no dialect would fail on the first pass
/// rather than at configuration time, which is the wrong end of the run to find out.
/// </para>
/// </summary>
public sealed class DuckDbDialect : SqlDialect
{
    public static DuckDbDialect Instance { get; } = new();

    private DuckDbDialect() { }

    private static readonly ISqlDialect Shared = DialectCatalog.Builtin.Get("duckdb");

    public override string QuoteIdentifier(string identifier) => SharedQuoting.Quote(Shared, identifier);

    /// <summary>DuckDB names a prepared parameter with <c>$name</c>; <c>@name</c> is not its syntax.</summary>
    public override string ParameterReference(string name) => $"${name}";

    /// <summary>
    /// Accepts anything and does nothing. <c>ChangeDatabase</c> throws on a DuckDB connection, so the
    /// only question is whether to *validate* the name the way <see cref="PostgresDialect"/> does —
    /// and here the answer is no, for two reasons that both point the same way.
    /// <para>
    /// The first is that <c>connection.Database</c> is not a complete answer to begin with: a DuckDB
    /// connection can <c>ATTACH</c> several databases and a query may legitimately name any of them,
    /// so a mismatch against the one the connection string opened proves nothing.
    /// </para>
    /// <para>
    /// The second is that a query-first source has no database to name in the first place — the rows
    /// come from whatever the query's scanners reach. Config still requires the field
    /// (<c>EndpointResolution.Resolve</c> rejects a mapping whose source database is blank), so it
    /// carries a label rather than a fact, and rejecting a working configuration over the wording of
    /// a label would be this dialect enforcing a rule nothing above it means.
    /// </para>
    /// </summary>
    public override Task UseDatabaseAsync(
        DbConnection connection, string database, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>DuckDB's default schema, which is what an unqualified name resolves to.</summary>
    public override string QualifyTable(string schema, string table) =>
        base.QualifyTable(string.IsNullOrWhiteSpace(schema) ? "main" : schema, table);

    /// <summary>Plain <c>LIMIT n</c> rather than the ANSI base's <c>FETCH FIRST … ROWS ONLY</c> —
    /// DuckDB definitely supports the former; whether it accepts the latter was not confirmed against a
    /// live instance, and a query preview's own cap (phase 193S, the one caller today) isn't worth
    /// guessing on.</summary>
    public override (string Prefix, string Suffix) RenderRowLimit(int n) => ("", $" LIMIT {n}");

    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "tinyint" or "smallint" or "integer" or "bigint" or "hugeint"
            or "utinyint" or "usmallint" or "uinteger" or "ubigint" => BucketableKind.Integral,
        "float" or "real" or "double" => BucketableKind.Numeric,
        "timestamp" => BucketableKind.DateTime,
        "timestamptz" or "timestamp with time zone" => BucketableKind.DateTimeOffset,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>
    /// DuckDB's type names, as <c>information_schema</c> and a result-set schema report them. It has
    /// one string family with no length bound and no narrow/wide distinction, so
    /// <see cref="CanonicalType.IsUnicode"/> is always true and every string is
    /// <see cref="CanonicalType.IsMax"/> unless a length was actually declared. Nested types — LIST,
    /// STRUCT, MAP, UNION — fall through to <see cref="CanonicalTypeKind.Unmappable"/>: there is no
    /// faithful scalar column on any target engine in scope, and guessing one would silently flatten
    /// data.
    /// </summary>
    public override CanonicalType ToCanonicalType(string nativeType) => SharedTypes.ToSyncWideningUnsigned(Shared.ToCanonical(nativeType));

    public override RenderedColumnType RenderColumnType(CanonicalType type) => SharedTypes.Render(Shared, SharedTypes.MicrosecondsByDefault(type));

    private static RenderedColumnType Faithful(string sql, CanonicalType type) => new(sql, type.SourceNote);

    private static string Combine(CanonicalType type, string renderNote) =>
        type.SourceNote is null ? renderNote : $"{type.SourceNote} {renderNote}";
}
