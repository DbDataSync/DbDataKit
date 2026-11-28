using DbDataKit.Dialect;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Drivers.Generic;
using DbDataKit.Sync.Core.Sql;

namespace DbDataKit.Sync.Drivers.Generic.Tests;

/// <summary>SQL Server's dialect, restated here rather than referenced. These tests exist to pin the
/// generic layer's output against more than one dialect, and depending on the MSSQL driver to do it
/// would make a change there silently change what "generic" means.</summary>
internal sealed class BracketDialect : SqlDialect
{
    public static BracketDialect Instance { get; } = new();
    public override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";
    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>SQL Server's <c>TOP (n) WITH TIES</c> rather than the ANSI trailing clause — the
    /// generic tests pin the SQL Server rendering, so this dialect has to spell it SQL Server's way.
    /// <see cref="ColonDialect"/> is what covers the ANSI default.</summary>
    public override (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) =>
        ($"TOP ({ParameterReference(parameterName)}) WITH TIES ", "");

    public override TypeMapping<CanonicalType> ReadColumnType(string nativeType) => throw new NotSupportedException();
    public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) => throw new NotSupportedException();
}

/// <summary>The other common shape: double-quoted identifiers and colon-prefixed placeholders whose
/// *bound* name drops the colon — Oracle's, and the reason
/// <see cref="SqlDialect.ParameterName"/> exists separately from
/// <see cref="SqlDialect.ParameterReference"/>.</summary>
internal sealed class ColonDialect : SqlDialect
{
    public static ColonDialect Instance { get; } = new();
    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    public override string ParameterReference(string name) => $":{name}";
    public override string ParameterName(string name) => name;
    public override TypeMapping<CanonicalType> ReadColumnType(string nativeType) => throw new NotSupportedException();
    public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) => throw new NotSupportedException();
}

/// <summary>Records what it was asked to bind. Segment binding is provider-specific by design, so the
/// generic tests assert the names and raw values handed to the binder, not the resulting DbParameter's
/// provider type.</summary>
internal sealed class RecordingBinder : ISegmentValueBinder
{
    public List<(string Name, string Value, string Column)> Calls { get; } = [];

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        Calls.Add((name, rawValue, column.Name));
        return new FakeDbParameter { ParameterName = name, Value = rawValue };
    }
}

/// <summary>A DbParameter with no provider behind it — enough to satisfy the contract, since these
/// tests never execute anything.</summary>
internal sealed class FakeDbParameter : DbParameter
{
    public override System.Data.DbType DbType { get; set; }
    public override System.Data.ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    // [AllowNull] on the setter matches DbParameter's own base declaration: the getter never returns
    // null, but the setter is annotated to accept it, an asymmetry these two properties inherit rather
    // than introduce.
    [AllowNull]
    public override string ParameterName { get; set; } = "";
    public override int Size { get; set; }
    [AllowNull]
    public override string SourceColumn { get; set; } = "";
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}

/// <summary>A dialect whose parameter cap is Postgres-sized, to prove batching follows the dialect
/// rather than a constant.</summary>
internal sealed class GenerousDialect : SqlDialect
{
    public static GenerousDialect Instance { get; } = new();
    public override string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
    public override string ParameterReference(string name) => $"${name}";
    public override int MaxParametersPerStatement => 65535;
    public override TypeMapping<CanonicalType> ReadColumnType(string nativeType) => throw new NotSupportedException();
    public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) => throw new NotSupportedException();
}

/// <summary>A dialect whose <see cref="WriteColumnType"/> is a simple, deterministic mapping — enough
/// to test <see cref="CreateTargetTablePlanner"/>'s handling of Unmappable columns and fidelity
/// warnings without depending on a real engine's dialect.</summary>
internal sealed class FakeCanonicalDialect : SqlDialect
{
    public static FakeCanonicalDialect Instance { get; } = new();
    public override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";
    public override string ParameterReference(string name) => $"@{name}";
    public override TypeMapping<CanonicalType> ReadColumnType(string nativeType) => throw new NotSupportedException();

    public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) => type.Kind switch
    {
        CanonicalKind.Unmappable => throw new InvalidOperationException("Never called for Unmappable."),
        CanonicalKind.String when type.Length is null => new RenderedColumnType("TEXT", "No length bound at the target."),
        CanonicalKind.Decimal => new RenderedColumnType($"NUM({type.Precision},{type.Scale})", sourceNote),
        _ => new RenderedColumnType(type.Kind.ToString().ToUpperInvariant(), sourceNote),
    };
}
