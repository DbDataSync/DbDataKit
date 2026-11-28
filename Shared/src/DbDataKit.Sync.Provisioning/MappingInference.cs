using DbDataKit.Dialect;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Sql;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Drivers.Generic;

namespace DbDataKit.Sync.Provisioning;

/// <summary>
/// What a mapping's source columns become on the target, and the natural key derived from them, worked out from columns already read. The
/// provisioner answers it after reading the source live; an authoring screen answers it from a catalog cache. Both go through these methods,
/// which are the translation the provisioning DDL uses (<see cref="SqlDialect.ReadColumnType"/>, <see cref="SqlDialect.WriteColumnType"/>) and the
/// derivation a run uses (<see cref="NaturalKeyDerivation"/>), so what an editor shows and what runs cannot disagree.
/// </summary>
public static class MappingInference
{
    /// <summary>Each source column with the type it would get on the target if nobody set one: every column, not only the mapped ones, so a
    /// column mapped later shows its type at once.</summary>
    public static IReadOnlyList<InferredColumnType> TargetTypes(
        Func<string, TypeMapping<CanonicalType>> readSourceType, SqlDialect targetDialect, IReadOnlyList<ColumnMetadata> sourceColumns) =>
        [.. sourceColumns.Select(column => Infer(readSourceType, targetDialect, column))];

    /// <summary>
    /// One column's inference, or a null <c>TargetType</c> with the reason. A type with no cross-engine equivalent is reported, not thrown, so
    /// one column does not stop the editor showing the others, and the person can type a target type of their own.
    /// </summary>
    public static InferredColumnType Infer(Func<string, TypeMapping<CanonicalType>> readSourceType, SqlDialect targetDialect, ColumnMetadata column)
    {
        try
        {
            var read = readSourceType(column.NativeType);
            if (read is not { IsMapped: true, Result: { } canonical })
                return new InferredColumnType(column.Name, column.NativeType, null, null,
                    $"'{column.NativeType}' has no cross-engine equivalent — set a target type by hand.");

            var rendered = targetDialect.WriteColumnType(canonical, read.Fidelity == Fidelity.Exact ? null : read.Note);
            return new InferredColumnType(column.Name, column.NativeType, rendered.Sql, rendered.Fidelity, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException)
        {
            return new InferredColumnType(column.Name, column.NativeType, null, null, DriverFailures.Describe(ex));
        }
    }

    /// <summary>
    /// What the SCD Type 2 writer's natural key would be if nobody stated one: the source's primary key (or the mapping's own
    /// <c>primaryKey</c>) in the target's column names. When nothing can be derived the answer says why: a table with no key and a key the
    /// mapping does not carry across are different problems, and only the person can fix either.
    /// </summary>
    /// <param name="table">The source table as a person names it (<c>schema.table</c>), for the reason.</param>
    public static InferredNaturalKey NaturalKey(IReadOnlyList<ColumnMetadata> sourceColumns, TableMappingConfig mapping, string table)
    {
        var columns = sourceColumns.WithKey(mapping.PrimaryKey);
        var derived = NaturalKeyDerivation.Derive(columns, mapping.ColumnMappings);
        if (derived.Count > 0)
            return new InferredNaturalKey(derived, null);

        var keyColumns = columns.InKeyOrder().Select(c => c.Name).ToList();
        var unmapped = keyColumns
            .Where(k => !mapping.ColumnMappings.Any(m => string.Equals(m.SourceColumn, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new InferredNaturalKey([], keyColumns.Count == 0
            ? $"'{table}' has no primary key, so there is nothing to derive a natural key from. Enter one below."
            : $"'{table}'s primary key includes {string.Join(", ", unmapped.Select(u => $"'{u}'"))}, " +
              "which this mapping does not carry across. Map those columns, or enter a natural key below.");
    }
}
