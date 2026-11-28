using DbDataKit.Dialect;
namespace DbDataKit.Sync.Core.Sql;

/// <summary>
/// Phase 165V's reader spike, generic JDBC over IKVM — scoped to exactly what that spike's one Postgres
/// test table needs, not a general cross-engine JDBC type matrix. Whether a JDBC engine ever gets a
/// richer, engine-agnostic dialect of its own is <c>architecture/planning/todo/jdbc-driver-support.md</c>'s
/// own still-open question (its "Open questions" §1) — this class deliberately does not try to answer it.
/// <para>
/// <see cref="ParameterReference"/> renders a named marker (<c>@name</c>), the same look every other
/// dialect here uses — not JDBC's own bare <c>?</c>. <c>DbDataSync.Drivers.Jdbc</c>'s <c>JdbcCommand</c>
/// is what translates named markers to ordinal <c>?</c> placeholders at execute time (the same shape
/// <c>OdbcCommand</c> uses for the identical ordinal-only-placeholder problem) — see phase 165V.
/// </para>
/// <para>
/// <see cref="ReadColumnType"/>/<see cref="WriteColumnType"/> read the native type names pgJDBC's own <c>DatabaseMetaData</c> reports for
/// Postgres (<c>int4</c>, <c>int8</c>, <c>varchar</c>, <c>numeric</c>, <c>timestamp</c>, <c>bool</c>: the pg_catalog spellings, since
/// pgJDBC's metadata queries pg_catalog directly) through the shared <c>postgres</c> table, not a mapping from every JDBC vendor's own
/// type names.
/// </para>
/// </summary>
public sealed class JdbcDialect : SqlDialect
{
    public static JdbcDialect Instance { get; } = new();

    private JdbcDialect() { }

    private static readonly ISqlDialect Shared = DialectCatalog.Builtin.Get("ansi");

    public override string QuoteIdentifier(string identifier) => SharedQuoting.Quote(Shared, identifier);

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>
    /// Bare, no <c>@</c> — the divergence from <see cref="ParameterReference"/> <see
    /// cref="SqlDialect.ParameterName"/>'s own doc comment names ("some providers want the bare name
    /// without its sigil"). Every other dialect here leaves the two identical because the real ADO.NET
    /// provider underneath (SqlClient, Npgsql, MySqlConnector) tolerates a <c>DbParameter.ParameterName</c>
    /// carrying the <c>@</c>/<c>:</c> sigil too — but <c>JdbcCommand</c>'s own name→position translation
    /// (see its class doc) is the thing doing the matching here, not a real provider, and it matches
    /// <see cref="ParameterReference"/>'s captured bare name against <c>DbParameter.ParameterName</c>
    /// directly. Found by running the real parity tests, not by inspection — the mismatch (parameters
    /// stored as <c>"@name"</c>, looked up as <c>"name"</c>) threw "no matching entry in Parameters" the
    /// first time a bound watermark reached <c>JdbcCommand</c>.
    /// </summary>
    public override string ParameterName(string name) => name;

    /// <summary>pgJDBC reports PostgreSQL's own catalog names, so the types are the shared <c>postgres</c> table's.</summary>
    private static readonly ISqlDialect Types = DialectCatalog.Builtin.Get("postgres");

    public override TypeMapping<CanonicalType> ReadColumnType(string nativeType) => DialectTypes.Read(Types, nativeType);

    public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) =>
        DialectTypes.Write(Types, type, sourceNote);
}
