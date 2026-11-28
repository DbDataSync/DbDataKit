using CanonicalKind = DbDataKit.Dialect.CanonicalKind;
using CanonicalType = DbDataKit.Dialect.CanonicalType;
using System.Data.Common;
using System.Globalization;
using DbDataKit.Sync.Core.Sql;

namespace DbDataKit.Sync.Drivers.Descriptor;

/// <summary>
/// A <see cref="SqlDialect"/> built entirely from a <c>driver.yaml</c>'s <c>dialect</c> and
/// <c>typeMap</c> blocks — data instead of code, per
/// <c>architecture/planning/todo/nuget-loaded-drivers.md</c> §*The descriptor*. Provisioning
/// (<see cref="WriteColumnType"/>) is out of this phase's scope and throws; everything else a
/// descriptor driver's generic Kinds need (quoting, parameter placeholders, row-limit rendering,
/// native → canonical type lookup) is here.
/// </summary>
public sealed class DescriptorDialect : SqlDialect
{
    private readonly char _quoteOpen;
    private readonly char _quoteClose;
    private readonly string _parameterPrefix;
    private readonly bool _parameterNameIsBare;
    private readonly RowLimitStyle _rowLimitStyle;
    private readonly bool _supportsTieSafeRowLimit;
    private readonly bool _supportsChangeDatabase;
    private readonly IReadOnlyDictionary<string, (IReadOnlyList<string> Placeholders, TypeMapEntryYaml Entry)> _typeMap;

    public DescriptorDialect(DescriptorDialectYaml spec, IReadOnlyDictionary<string, TypeMapEntryYaml> typeMap)
    {
        (_quoteOpen, _quoteClose) = spec.QuoteIdentifier switch
        {
            "backtick" => ('`', '`'),
            "doubleQuote" => ('"', '"'),
            "bracket" => ('[', ']'),
            _ => throw new NotSupportedException(
                $"Unknown quoteIdentifier style '{spec.QuoteIdentifier}' (expected backtick, doubleQuote or bracket)."),
        };
        _parameterPrefix = spec.ParameterPrefix;
        _parameterNameIsBare = spec.ParameterNameIsBare;
        _rowLimitStyle = spec.RowLimit switch
        {
            "limitOffset" => RowLimitStyle.LimitOffset,
            "offsetFetch" => RowLimitStyle.OffsetFetch,
            "topN" => RowLimitStyle.TopN,
            _ => throw new NotSupportedException(
                $"Unknown rowLimit style '{spec.RowLimit}' (expected limitOffset, offsetFetch or topN)."),
        };
        // See DescriptorDialectYaml.SupportsTieSafeRowLimit's own doc comment for this default: every
        // style but limitOffset is presumed tie-safe unless the descriptor says otherwise, matching what
        // every dialect in this codebase that speaks that style already does.
        _supportsTieSafeRowLimit = spec.SupportsTieSafeRowLimit ?? _rowLimitStyle != RowLimitStyle.LimitOffset;
        _supportsChangeDatabase = spec.SupportsChangeDatabase;
        _typeMap = BuildTypeMap(typeMap);
    }

    protected override char IdentifierQuoteOpen => _quoteOpen;
    protected override char IdentifierQuoteClose => _quoteClose;

    public override string QuoteIdentifier(string identifier) =>
        $"{_quoteOpen}{identifier.Replace(_quoteClose.ToString(), $"{_quoteClose}{_quoteClose}")}{_quoteClose}";

    public override string ParameterReference(string name) => $"{_parameterPrefix}{name}";

    /// <summary>See <see cref="DescriptorDialectYaml.ParameterNameIsBare"/> — false keeps the base
    /// class's own default (identical to <see cref="ParameterReference"/>, the sigil included), which
    /// every ADO.NET provider so far tolerates.</summary>
    public override string ParameterName(string name) => _parameterNameIsBare ? name : base.ParameterName(name);

    /// <summary>
    /// <c>SupportsChangeDatabase: false</c> makes this a config error rather than a runtime one that
    /// only surfaces the first time a mapping actually reads — <c>ConfigValidation</c>-adjacent
    /// but stated here, since only the dialect knows whether its engine has the notion at all. Checked
    /// only when a switch is actually being asked for: an empty <paramref name="database"/> (a
    /// deliberate "nothing to switch to" — see <c>DbDataKit.Sync.Core.Config.TableRef.Database</c>'s own
    /// doc comment) skips straight past it, the same as the base class's own check — this override
    /// exists to refuse a *real* request this engine can't honour, not an absent one.
    /// </summary>
    public override Task UseDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(database))
            return Task.CompletedTask;

        if (!_supportsChangeDatabase)
        {
            throw new InvalidOperationException(
                $"This driver's engine does not support switching database at connect time ('{database}' " +
                "was requested); every mapping must already point at the connection's own database — " +
                "or, for a mapping that doesn't need one, leave TableSpec.Database empty rather than " +
                "naming it.");
        }

        return base.UseDatabaseAsync(connection, database, cancellationToken);
    }

    /// <summary>Purely declarative — see the base class's own doc comment. Reflects exactly what
    /// <see cref="RenderTieSafeRowLimit"/> below actually renders for this descriptor.</summary>
    public override RowLimitStyle RowLimitStyle => _rowLimitStyle;

    /// <inheritdoc/>
    public override bool SupportsTieSafeRowLimit => _supportsTieSafeRowLimit;

    /// <summary>
    /// <c>limitOffset</c> has no tie-safe form at all in this codebase's scope (see the base class's own
    /// doc comment and <c>MySqlDialect</c>'s) — a plain <c>LIMIT n</c> can split a group of rows sharing
    /// the boundary value across two passes, silently skipping the tied-but-unread ones. Accepted for
    /// this phase's scope: an engine that needs both bulk batch reads and guaranteed-no-skip behaviour on
    /// that style is exactly the signal to write a compiled dialect instead of a descriptor one.
    /// <para>
    /// <c>offsetFetch</c>/<c>topN</c> render their real <c>WITH TIES</c> form when
    /// <see cref="SupportsTieSafeRowLimit"/> says the engine actually has one, and fall back to the plain
    /// (non-tie-safe) form otherwise — the same accepted gap as <c>limitOffset</c>, just for an engine
    /// whose descriptor said so explicitly rather than one whose whole style implies it.
    /// </para>
    /// </summary>
    public override (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) => (_rowLimitStyle, _supportsTieSafeRowLimit) switch
    {
        (RowLimitStyle.LimitOffset, _) => ("", $"\nLIMIT {ParameterReference(parameterName)}"),
        (RowLimitStyle.TopN, true) => ($"TOP ({ParameterReference(parameterName)}) WITH TIES ", ""),
        (RowLimitStyle.TopN, false) => ($"TOP ({ParameterReference(parameterName)}) ", ""),
        (RowLimitStyle.OffsetFetch, true) => base.RenderTieSafeRowLimit(parameterName),
        (RowLimitStyle.OffsetFetch, false) => ("", $"\nFETCH FIRST {ParameterReference(parameterName)} ROWS ONLY"),
        _ => throw new ArgumentOutOfRangeException(nameof(_rowLimitStyle), _rowLimitStyle, "Unknown row-limit style."),
    };

    /// <summary>The non-tie-safe sibling of <see cref="RenderTieSafeRowLimit"/> above — see the base
    /// class's own doc comment for what it's for (a query preview's own cap, phase 193S). Not previously
    /// overridden here at all, which meant a <c>limitOffset</c>/<c>topN</c>-style descriptor driver's
    /// preview cap silently rendered the base class's ANSI <c>FETCH FIRST</c> form regardless of what its
    /// engine actually speaks — a real, latent bug this fixes.</summary>
    public override (string Prefix, string Suffix) RenderRowLimit(int n) => _rowLimitStyle switch
    {
        RowLimitStyle.LimitOffset => ("", $"\nLIMIT {n}"),
        RowLimitStyle.TopN => ($"TOP ({n}) ", ""),
        RowLimitStyle.OffsetFetch => base.RenderRowLimit(n),
        _ => throw new ArgumentOutOfRangeException(nameof(_rowLimitStyle), _rowLimitStyle, "Unknown row-limit style."),
    };

    /// <summary>
    /// A name-keyed lookup over <c>typeMap</c>, with <c>(p,s)</c>-style argument substitution — strictly
    /// a table, no branching. An engine whose type mapping genuinely branches on a value
    /// (<c>tinyint(1)</c> as boolean) is the signal to write a compiled driver instead, per the plan
    /// doc; this deliberately does not grow that capability.
    /// </summary>
    public override DbDataKit.Dialect.TypeMapping<CanonicalType> ReadColumnType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        if (!_typeMap.TryGetValue(baseName, out var mapping))
            return DbDataKit.Dialect.TypeMapping<CanonicalType>.Unmappable($"native type '{nativeType.Trim()}' is not in the driver's typeMap");

        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < mapping.Placeholders.Count && i < args.Count; i++)
            bound[mapping.Placeholders[i]] = args[i];

        var entry = mapping.Entry;
        var length = entry.Max ? null : ResolvePlaceholder(entry.Length, bound);
        var precision = ResolvePlaceholder(entry.Precision, bound);
        var scale = ResolvePlaceholder(entry.Scale, bound);
        // The kind names of a typeMap entry; TimestampTz is the shared timestamp with a time zone.
        CanonicalType? type = entry.Kind switch
        {
            "Boolean" => CanonicalType.Of(CanonicalKind.Boolean),
            "Int8" => CanonicalType.Of(CanonicalKind.Int8),
            "Int16" => CanonicalType.Of(CanonicalKind.Int16),
            "Int32" => CanonicalType.Of(CanonicalKind.Int32),
            "Int64" => CanonicalType.Of(CanonicalKind.Int64),
            "Decimal" => new CanonicalType(CanonicalKind.Decimal, Precision: precision, Scale: scale),
            "Float" => CanonicalType.Of(CanonicalKind.Float),
            "Double" => CanonicalType.Of(CanonicalKind.Double),
            "String" => CanonicalType.String(length, entry.Unicode),
            "Binary" => CanonicalType.Binary(length),
            "Date" => CanonicalType.Of(CanonicalKind.Date),
            "Time" => CanonicalType.Time(scale),
            "Timestamp" => CanonicalType.Timestamp(scale),
            "TimestampTz" => CanonicalType.Timestamp(scale, tz: true),
            "Guid" => CanonicalType.Of(CanonicalKind.Guid),
            "Json" => CanonicalType.Of(CanonicalKind.Json),
            "Xml" => CanonicalType.Of(CanonicalKind.Xml),
            "Unmappable" => null,
            _ => throw new ArgumentException($"The typeMap entry for '{baseName}' names the kind '{entry.Kind}', which is not one of {string.Join(", ", KindNames)}."),
        };
        return type is null
            ? DbDataKit.Dialect.TypeMapping<CanonicalType>.Unmappable($"the driver's typeMap says '{baseName}' is unmappable")
            : DbDataKit.Dialect.TypeMapping<CanonicalType>.Exact(type);
    }

    /// <summary>The kind names a <c>typeMap</c> entry may use.</summary>
    public static IReadOnlyList<string> KindNames { get; } =
        ["Boolean", "Int8", "Int16", "Int32", "Int64", "Decimal", "Float", "Double", "String", "Binary", "Date", "Time", "Timestamp", "TimestampTz", "Guid", "Json", "Xml", "Unmappable"];

    /// <summary>Not supported in this phase — a descriptor driver's initial scope is watermark and
    /// batch reload only, neither of which renders a target column. See the plan doc's §*The
    /// descriptor* — provisioning needs the reverse (canonical → native) direction of exactly the table
    /// that stays deliberately minimal here.</summary>
    public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) =>
        throw new NotSupportedException(
            "Provisioning is not supported for a driver defined by a YAML descriptor. " +
            "Write a compiled driver if this engine needs target-table DDL.");

    private static IReadOnlyDictionary<string, (IReadOnlyList<string>, TypeMapEntryYaml)> BuildTypeMap(
        IReadOnlyDictionary<string, TypeMapEntryYaml> typeMap)
    {
        var result = new Dictionary<string, (IReadOnlyList<string>, TypeMapEntryYaml)>(StringComparer.Ordinal);
        foreach (var (key, entry) in typeMap)
        {
            var (baseName, placeholders) = CanonicalTypeSpec.Parse(key);
            result[baseName] = (placeholders, entry);
        }
        return result;
    }

    /// <summary>A field's YAML value is either one of this entry's own placeholder names (bound from
    /// the matched native type's own arguments) or a literal integer (a fixed mapping, e.g. a
    /// hand-written <c>decimal(10,2): { kind: Decimal, precision: "10", scale: "2" }</c>).</summary>
    private static int? ResolvePlaceholder(string? value, IReadOnlyDictionary<string, string> bound)
    {
        if (value is null)
            return null;
        if (bound.TryGetValue(value, out var resolved))
            return int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var boundInt) ? boundInt : null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var literal) ? literal : null;
    }
}
