namespace DbData.Dialect;

/// <summary>
/// The "small mechanical differences" between engines: quoting, parameters, row limits, literals, type names.
/// Structural differences (MERGE shape, bulk load) are strategies elsewhere and consume this.
/// </summary>
public interface ISqlDialect
{
    EngineId Id { get; }
    EngineCaps Caps { get; }
    SqlSyntax Syntax { get; }
    IdentifierRules Identifiers { get; }
    ReadOnlyRules ReadOnly { get; }
    ParameterStyle ParameterStyle { get; }
    RowLimitStyle DefaultRowLimitStyle { get; }
    IReadOnlyCollection<RowLimitStyle> SupportedRowLimitStyles { get; }

    /// <summary>Quotes one identifier, always doubling the closing delimiter. Throws ArgumentException for names that cannot be represented (empty, control characters).</summary>
    string Quote(string identifier);
    /// <summary>Quotes each part and joins with '.'.</summary>
    string QuoteQualified(IEnumerable<string> parts);
    /// <summary>Quote-aware split of "a.[b.c].d" into parts (quotes removed, doubled closers undone). Throws FormatException on unterminated quotes or empty parts.</summary>
    IReadOnlyList<string> SplitQualifiedName(string qualified);

    /// <summary>True when the name may be emitted without quotes and still mean the same identifier.</summary>
    bool IsSafeUnquoted(string name);
    /// <summary>Null when the name is acceptable as an identifier, otherwise the reason (control characters, too long, empty).</summary>
    string? ValidateName(string name);

    /// <summary>The marker for the zero-based parameter <paramref name="index"/>; named styles use <paramref name="name"/> or p{index}.</summary>
    string ParameterName(int index, string? name = null);

    RowLimitFragment RowLimit(RowLimitStyle style, long limit, long offset = 0, bool tieSafe = false);

    /// <summary>Renders a value as a SQL literal of the given canonical type. Never concatenates unescaped text.</summary>
    string Literal(object? value, CanonicalType type);

    /// <summary>Canonical to native column type.</summary>
    TypeMapping<string> ColumnType(CanonicalType type);
    /// <summary>Native column type text (as reported by the engine) to canonical.</summary>
    TypeMapping<CanonicalType> ToCanonical(string nativeType);
}
