using DbDataKit.Dialect;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Core.Sql;

namespace DbDataKit.Sync.Drivers.Generic;

/// <summary>
/// Turns a mapping's <see cref="ColumnMapping"/> list plus the source's introspected
/// <see cref="ColumnMetadata"/> into the <see cref="ProvisioningColumn"/> list a
/// <see cref="ProvisioningActions.CreateTargetTable"/> request carries — shared by
/// the provisioning plan (<c>dbdatasync provisioning plan</c>, the Workspace's preview) and
/// <c>DbDataSync.TaskRunner.RunExecutor</c> (the automatic <c>CreateTargetTableIfMissing</c> path), so the
/// column a run creates unattended is never computed by a second code path than the one an operator
/// previewed.
/// </summary>
public static class ProvisioningColumnBuilder
{
    public static (IReadOnlyList<ProvisioningColumn> Columns, IReadOnlyList<string> Warnings) Build(
        SqlDialect sourceDialect, IReadOnlyList<ColumnMetadata> sourceColumns, IReadOnlyList<ColumnMapping> columnMappings)
    {
        var byName = sourceColumns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var columns = new List<ProvisioningColumn>();

        foreach (var mapping in columnMappings)
        {
            if (!byName.TryGetValue(mapping.SourceColumn, out var sourceColumn))
                throw new InvalidOperationException(
                    $"Source column '{mapping.SourceColumn}' was not found while planning provisioning.");

            // Never recreated on the target: DbDataSync writes explicit values into every column it
            // writes, and an identity column there would need every write to bracket itself with
            // SET IDENTITY_INSERT or OVERRIDING SYSTEM VALUE. The column still gets created — just
            // plain — so the warning is the only place this decision is visible.
            if (sourceColumn.IsIdentity)
                warnings.Add(
                    $"Column '{mapping.TargetColumn}': the source's identity/generated column is created " +
                    "plain — DbDataSync writes explicit values into the target.");

            var read = sourceDialect.ReadColumnType(sourceColumn.NativeType);
            var canonical = read is { IsMapped: true, Result: { } type } ? type : CanonicalType.Unmappable;
            columns.Add(new ProvisioningColumn(
                mapping.TargetColumn, canonical, sourceColumn.IsNullable, sourceColumn.IsPrimaryKey,
                mapping.TargetType, mapping.Renames, sourceColumn.KeyOrdinal, read.Fidelity == Fidelity.Exact ? null : read.Note));
        }

        return (columns, warnings);
    }
}
