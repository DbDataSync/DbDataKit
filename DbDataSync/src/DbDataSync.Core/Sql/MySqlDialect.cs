using DbDataKit.Dialect;
namespace DbDataSync.Core.Sql;

/// <summary>
/// MySQL and MariaDB, on MySqlConnector.
/// <para>
/// One real, unresolved gap, worth stating up front: <see cref="RenderTieSafeRowLimit"/> cannot be
/// tie-safe here. Every other dialect in scope expresses "cap this ordered read at n rows without
/// splitting ties" as a prefix or suffix wrapped around an already-fixed <c>SELECT ... WHERE ...
/// ORDER BY</c> shape — SQL Server's <c>TOP (n) WITH TIES</c>, the ANSI <c>FETCH FIRST n ROWS WITH
/// TIES</c> Postgres and Oracle both accept. MySQL has no clause like it at all, tie-safe or otherwise,
/// and the only mechanism that WOULD be tie-safe (a window-function rewrite ranking rows by the order
/// column) needs the query restructured around a derived table — which this hook's shape, two string
/// fragments wrapped around a query it does not otherwise touch, has no room for. This renders a plain
/// <c>LIMIT n</c> instead: correct syntax, not tie-safe. A bounded <c>Watermark</c> pass capped
/// mid-tie can skip a sibling row sharing the exact boundary value until a later pass happens to end
/// somewhere the tie does not recur. Real, not theoretical — named in phase 147's own "Open questions"
/// — and not fixed by this phase.
/// </para>
/// <para>
/// <c>RenderRenameColumn</c> is not overridden: the inherited ANSI <c>RENAME COLUMN ... TO ...</c> form
/// is valid MySQL 8.0+/MariaDB 10.5.2+ syntax. An older server needs <c>CHANGE COLUMN old new type</c>
/// instead, which this does not attempt — naming the floor here rather than discovering it as a syntax
/// error against an old server.
/// </para>
/// </summary>
public sealed class MySqlDialect : SqlDialect
{
    public static MySqlDialect Instance { get; } = new();

    private MySqlDialect() { }

    private static readonly ISqlDialect Shared = DialectCatalog.Builtin.Get("mysql");

    // Backticks, so SplitQualifiedName unwraps `a.b` the way QuoteIdentifier wraps it (the base class assumed double quotes here).
    protected override char IdentifierQuoteOpen => '`';

    protected override char IdentifierQuoteClose => '`';

    public override string QuoteIdentifier(string identifier) => SharedQuoting.Quote(Shared, identifier);

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>MySQL's protocol allows up to 65535 placeholders per prepared statement — the same
    /// ceiling Postgres has; SQL Server's 2100 is the outlier, not this.</summary>
    public override int MaxParametersPerStatement => 65535;

    public override string OperationMarkerColumnType => "char(1)";

    /// <summary>MySQL has no <c>datetime2</c>; <c>timestamp(6)</c> is the closest sub-second-precision
    /// equivalent, and matches the type <see cref="DbDataSync.Drivers.MySql.MySqlTriggerAudit"/>'s own
    /// shadow table DDL already uses for <c>DS_ChangedAt</c>.</summary>
    public override string ChangedAtColumnType => "timestamp(6)";

    // UseDatabaseAsync: not overridden. Unlike Postgres and Oracle, a MySqlConnector connection
    // genuinely can change database on a live connection — MySqlConnection.ChangeDatabase is a real
    // override, not an inherited one that throws, confirmed directly against the MySqlConnector 2.4.0
    // assembly rather than assumed. That resolves phase 147's own open "switch, or validate-and-refuse"
    // question: switch, using the connection's real capability. The base SqlDialect implementation
    // (DbConnection.ChangeDatabase) is correct here unmodified.

    /// <inheritdoc/>
    public override (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) =>
        ("", $" LIMIT {ParameterReference(parameterName)}");

    public override (string Prefix, string Suffix) RenderRowLimit(int n) => ("", $" LIMIT {n}");

    /// <summary>Purely declarative — matches the rendering above, doesn't drive it. Also carries the
    /// class doc comment's own admission that <c>RenderTieSafeRowLimit</c> isn't actually tie-safe here,
    /// as a queryable fact instead of only as prose.</summary>
    public override RowLimitStyle RowLimitStyle => RowLimitStyle.LimitOffset;

    /// <inheritdoc/>
    public override bool SupportsTieSafeRowLimit => false;

    /// <summary>MySQL's identity column, the same role Postgres's <c>BIGSERIAL</c>/
    /// <c>GENERATED ALWAYS AS IDENTITY</c> plays.</summary>
    public override string RenderStagingOrdinalColumn(string column) =>
        $"{QuoteIdentifier(column)} BIGINT AUTO_INCREMENT PRIMARY KEY";

    // RenderInsertInto / WriteWithGeneratedColumnOverrideAsync: not overridden. MySQL accepts an
    // explicit value for an AUTO_INCREMENT column through a plain INSERT — no session flag, no
    // statement-level override clause. SqlDialect's own doc comment says so ("MySQL needs nothing"),
    // and the base implementations of both hooks already do exactly nothing extra.

    /// <summary>MySQL's <c>CAST</c> target list does not include <c>VARCHAR</c> — only <c>CHAR</c> (and
    /// a handful of other fixed types). The ANSI default this overrides would be a syntax error here.</summary>
    public override string CastToText(string expression) => $"CAST({expression} AS CHAR(4000))";

    /// <summary>MySQL has no <c>ALTER TABLE ... ALTER COLUMN ... TYPE ...</c>; a type change is a full
    /// column redefinition via <c>MODIFY COLUMN</c>. Explicitly nullable, like every other dialect's
    /// override of this hook: a table with rows cannot gain a NOT NULL column for free, and MODIFY
    /// redefines the whole column, so leaving nullability unstated here would let a bare column
    /// definition's own default decide it instead of this method's own explicit answer.</summary>
    public override string? RenderAlterColumnType(string qualifiedTable, string column, string type) =>
        $"ALTER TABLE {qualifiedTable} MODIFY COLUMN {QuoteIdentifier(column)} {type} NULL";

    /// <summary>MySQL user variables share the exact <c>@name</c> syntax <see cref="ParameterReference"/>
    /// already uses for bind parameters, and are untyped — a plain <c>SET @p = value;</c> per parameter,
    /// no declared type needed.</summary>
    public override string? RenderDeclarations(IReadOnlyList<PreviewParameter> parameters) =>
        parameters.Count == 0 ? null : string.Join(
            "\n", parameters.Select(p => $"SET {ParameterReference(p.Name)} = {p.Literal};"));

    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" => BucketableKind.Integral,
        "decimal" or "numeric" or "float" or "double" => BucketableKind.Numeric,
        "date" or "datetime" or "timestamp" => BucketableKind.DateTime,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>The type tables are the shared <c>mysql</c> dialect's (<c>dialects/mysql.yaml</c>). Sync keeps fixed-length
    /// <c>binary(n)</c> as fixed, where the shared table widens it to <c>varbinary</c> and says so.</summary>
    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        return baseName == "binary"
            ? new CanonicalType(CanonicalTypeKind.Binary, CanonicalTypeSpec.IntAt(args, 0, 1), null, null, false, false, IsFixed: true)
            : SharedTypes.ToSync(Shared.ToCanonical(nativeType));
    }

    /// <summary>A time or timestamp with no stated precision renders at microseconds (6), where the shared table's default is whole seconds.</summary>
    public override RenderedColumnType RenderColumnType(CanonicalType type) =>
        type is { Kind: CanonicalTypeKind.Binary, IsFixed: true, IsMax: false }
            ? new RenderedColumnType($"binary({type.Length ?? 255})", type.SourceNote)
            : SharedTypes.Render(Shared, SharedTypes.MicrosecondsByDefault(type));
}
