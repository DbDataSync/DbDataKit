namespace DbDataBuild.Core;

/// <summary>The statements one binding needs, in the order DuckDB needs them: schema names, then types (a table may have a column of one), then macros with what they call first.</summary>
public sealed record DuckPrelude(IReadOnlyList<string> Schemas, IReadOnlyList<string> Types, IReadOnlyList<string> Macros)
{
    public static DuckPrelude Empty { get; } = new([], [], []);
    public bool IsEmpty => Schemas.Count + Types.Count + Macros.Count == 0;
}
