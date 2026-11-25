using System.Data.Common;
using System.Globalization;
using DbDataKit.Dialect;
using DbDataKit.Runner;
using DbDataKit.Transfer;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Provisioning;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>One part of a set's preflight: a mapping (<c>&lt;replication&gt;/&lt;mapping&gt;</c>) or a replication's state on its target (<c>&lt;replication&gt; tracking</c>), and what was found.</summary>
public sealed record SetPreflightPart(string Part, IReadOnlyList<PreflightFinding> Findings);

/// <summary>
/// The live checks of a replication set before a version of it is served (Design: docs/authoring-metadata.md#a-sync-project): each mapping of each enabled replication read against its
/// source and target with the connections <paramref name="connections"/> opens (on a runner, the run login of each slot), read only. Nothing is written: the catalogs are read, the
/// privileges are asked of the engine's privilege functions, the filter is compiled by a read that returns no row, and the provisioning plans are made as the set's <c>provision</c> task
/// makes them before it applies, without applying them.
/// <para>
/// What weighs how much: a table or a column the mapping names that is not there, a filter that does not compile, a login that may not read the source or write the target, change capture
/// that the reader needs and that is off, and a Kind the driver does not have are errors, since the first pass would fail on them. A type that does not fit, a key that does not agree and a
/// key a reader or writer needs and does not get are warnings, as in the offline check. What is in order, and what the engine has no privilege function for, is information.
/// The target's login is asked only for the changes the mapping's writers make (<see cref="IChangeWriter.NeedsPrivileges"/>): a refill of a scope needs no UPDATE.
/// </para>
/// </summary>
public sealed class SetPreflight(ConfigRepository config, IConnectionFactory connections, MappingProvisioner provisioner)
{
    /// <summary>The parts of the preflight of <paramref name="replication"/>, or of every enabled replication when it is empty. A failure to read a part is a finding of that part, never an exception.</summary>
    public async Task<IReadOnlyList<SetPreflightPart>> CheckAsync(string? replication, CancellationToken ct)
    {
        var parts = new List<SetPreflightPart>();
        IReadOnlyList<string> names;
        if (!string.IsNullOrWhiteSpace(replication)) names = [replication.Trim()];
        else
        {
            try { names = config.ListReplications(); }
            catch (Exception ex) when (IsConfig(ex)) { return [new("set", [new(Preflight.Error, $"The set's replications could not be listed: {ex.Message}")])]; }
        }

        foreach (var name in names)
        {
            ReplicationTaskConfig task;
            try { task = config.LoadReplicationTask(name); }
            catch (Exception ex) when (IsConfig(ex))
            {
                parts.Add(new(name, [new(Preflight.Error, $"The replication '{name}' does not read: {ex.Message}")]));
                continue;
            }

            if (!task.Enabled && string.IsNullOrWhiteSpace(replication)) continue;   // a replication that is off is not published, so nothing of it runs
            IReadOnlyList<string> mappings;
            try { mappings = config.ListTableMappings(task.Name); }
            catch (Exception ex) when (IsConfig(ex))
            {
                parts.Add(new(task.Name, [new(Preflight.Error, $"The mappings of '{task.Name}' could not be listed: {ex.Message}")]));
                continue;
            }

            var targets = new List<TableRef>();
            foreach (var mappingName in mappings)
            {
                var found = new List<PreflightFinding>();
                try { await MappingAsync(task, mappingName, found, targets, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    found.Add(new(Preflight.Error, $"The preflight of the mapping '{mappingName}' stopped: {Describe(ex)}"));
                }
                parts.Add(new($"{task.Name}/{mappingName}", found));
            }

            if (await TrackingAsync(task, targets, ct).ConfigureAwait(false) is { Count: > 0 } tracking) parts.Add(new($"{task.Name} tracking", tracking));
        }

        return parts;
    }

    private static bool IsConfig(Exception ex) =>
        ex is IOException or InvalidDataException or InvalidOperationException or FormatException or YamlDotNet.Core.YamlException or ConfigValidationException or MappingLoadingException or UnauthorizedAccessException;

    private static string Describe(Exception ex) => ex is DbException db ? DriverFailures.Describe(db) : ex.Message;

    private static string Label(TableRef side) => string.Join('.', new[] { side.Database, side.Schema, side.Table }.Where(p => p.Length > 0));

    private static ColumnMetadata? ColumnOf(IReadOnlyList<ColumnMetadata> columns, string name) => columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private sealed record Side(TableRef Ref, DbConnection Connection, IDriver Driver, SqlDialect? Dialect) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private async Task<Side?> OpenAsync(TableRef side, string role, List<PreflightFinding> found, CancellationToken ct)
    {
        try
        {
            var (connection, driver) = await connections.OpenAsync(side.ConnectionName, CredentialRole.Run, ct).ConfigureAwait(false);
            return new Side(side, connection, driver, (driver as IDialectProvider)?.Dialect);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            found.Add(new(Preflight.Error, $"The {role} connection '{side.ConnectionName}' could not be opened with its run login: {Describe(ex)}", side.ConnectionName));
            return null;
        }
    }

    private async Task MappingAsync(ReplicationTaskConfig task, string mappingName, List<PreflightFinding> found, List<TableRef> targets, CancellationToken ct)
    {
        TableMappingConfig mapping;
        try { mapping = config.LoadTableMapping(task.Name, mappingName); }
        catch (Exception ex) when (IsConfig(ex))
        {
            found.Add(new(Preflight.Error, $"The mapping '{mappingName}' of '{task.Name}' does not read: {ex.Message}"));
            return;
        }

        if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
        {
            found.Add(new(Preflight.Info, $"The mapping '{mapping.Name}' has {mapping.Sources.Count} source(s) and {mapping.Targets.Count} target(s); only a mapping of one source and one target is checked."));
            return;
        }

        SourceTableRef source;
        TableRef target;
        try
        {
            source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
            target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);
        }
        catch (InvalidOperationException ex)
        {
            found.Add(new(Preflight.Error, $"The mapping '{mapping.Name}': {ex.Message}"));
            return;
        }

        targets.Add(target);
        await using var sourceSide = await OpenAsync(source, "source", found, ct).ConfigureAwait(false);
        await using var targetSide = await OpenAsync(target, "target", found, ct).ConfigureAwait(false);

        IReadOnlyList<ColumnMetadata>? sourceColumns = null;
        if (sourceSide is not null) sourceColumns = await SourceAsync(task, mapping, source, sourceSide, found, ct).ConfigureAwait(false);
        IReadOnlyList<ColumnMetadata>? targetColumns = null;
        var targetExists = false;
        if (targetSide is not null) (targetExists, targetColumns) = await TargetAsync(task, mapping, target, targetSide, found, ct).ConfigureAwait(false);

        // the plans a person's Apply would run, made from the live catalogs as `provision` makes them, never applied
        if (sourceSide is not null && targetSide is not null && sourceColumns is { Count: > 0 } && (source.Query is null || source.Table.Length > 0))
            await PlansAsync(task, mapping, source, target, targetSide, targetExists, targetColumns, found, ct).ConfigureAwait(false);

        if (sourceSide is not null && targetSide is not null && sourceColumns is not null)
        {
            if (sourceSide.Dialect is { } from && targetSide.Dialect is { } to && targetColumns is not null) TypesAgree(mapping, source, target, from, to, sourceColumns, targetColumns, found);
            Keys(task, mapping, source, target, sourceSide.Driver, targetSide.Driver, sourceColumns, targetExists ? targetColumns : null, found);
        }

        if (!found.Any(f => f.Severity != Preflight.Info))
            found.Insert(0, new(Preflight.Info, $"The mapping '{mapping.Name}' from {Label(source)} on '{source.ConnectionName}' to {Label(target)} on '{target.ConnectionName}' found nothing in its way.", source.ConnectionName));
    }

    // ---- the source

    private async Task<IReadOnlyList<ColumnMetadata>?> SourceAsync(ReplicationTaskConfig task, TableMappingConfig mapping, SourceTableRef source, Side side, List<PreflightFinding> found, CancellationToken ct)
    {
        var about = source.ConnectionName;
        if (!string.IsNullOrWhiteSpace(source.Query) && source.Table.Length == 0)
        {
            // a query source: its text compiles and the login may run it, by a read that returns no row
            await ZeroRowReadAsync(side, $"SELECT * FROM ({source.Query}) preflight_query WHERE 1 = 0", $"The query of the mapping '{mapping.Name}'", about, found, ct).ConfigureAwait(false);
            return null;
        }

        var columns = await side.Driver.ListColumnsAsync(side.Connection, source.Database, source.Schema, source.Table, ct).ConfigureAwait(false);
        if (columns.Count == 0)
        {
            found.Add(new(Preflight.Error, $"The source table {Label(source)} is not on the connection '{source.ConnectionName}', or its run login cannot see it.", about));
            return null;
        }

        var mapped = new List<string>();
        foreach (var column in mapping.ColumnMappings.Where(c => string.IsNullOrEmpty(c.Relationship)))
        {
            if (ColumnOf(columns, column.SourceColumn) is not { } live)
            {
                found.Add(new(Preflight.Error, $"The source column '{column.SourceColumn}' of the mapping '{mapping.Name}' is not a column of {Label(source)}.", about));
                continue;
            }
            mapped.Add(live.Name);
            if (side.Dialect is { } dialect && dialect.ReadColumnType(live.NativeType) is { IsMapped: false } read)
                found.Add(new(Preflight.Warning, $"The source column '{live.Name}' of {Label(source)} has the type {live.NativeType}, which the {side.Driver.DriverType} driver does not read{(read.Note is { Length: > 0 } n ? $" ({n})" : "")}.", about));
        }

        // the login may read the table: the engine's privilege function, then a read that returns no row, which also compiles the filter
        var readable = await PrivilegeAsync(side, Privilege.Select, source, ct).ConfigureAwait(false);
        if (readable == false)
            found.Add(new(Preflight.Error, $"The run login of '{source.ConnectionName}' may not SELECT from {Label(source)}.", about));
        else if (readable is null)
            found.Add(new(Preflight.Info, $"The {side.Driver.DriverType} engine's privileges are not read by the preflight; the read below is the check of the login.", about));
        var list = mapped.Count > 0 && side.Dialect is { } d ? string.Join(", ", mapped.Distinct(StringComparer.OrdinalIgnoreCase).Select(d.QuoteIdentifier)) : "1";
        var table = side.Dialect?.QualifyTable(source.Schema, source.Table) ?? $"{source.Schema}.{source.Table}";
        var filter = string.IsNullOrWhiteSpace(source.Filter) ? "" : $"({source.Filter}) AND ";
        var columnsSaid = $" The columns of {Label(source)} are {string.Join(", ", columns.Select(c => c.Name))}.";
        await ZeroRowReadAsync(side, $"SELECT {list} FROM {table} WHERE {filter}1 = 0",
            string.IsNullOrWhiteSpace(source.Filter) ? $"The read of {Label(source)}" : $"The source filter of the mapping '{mapping.Name}' on {Label(source)}", about, found, ct, source.Database,
            string.IsNullOrWhiteSpace(source.Filter) ? "" : columnsSaid).ConfigureAwait(false);

        // change tracking, CDC, a trigger's shadow table or a logical slot and its publication: what the reader needs of the source, as the provisioning plan of the source side
        if (side.Driver is IProvisioner sourceProvisioner)
        {
            var reader = PipelineResolution.Reader(task, mapping);
            try
            {
                var plan = await sourceProvisioner.PlanAsync(side.Connection, new ProvisioningRequest(ProvisioningActions.EnableSourceChangeCapture, source, reader.Kind, reader.Options, []), ct).ConfigureAwait(false);
                switch (plan.State)
                {
                    case ProvisioningState.Missing:
                        found.Add(new(Preflight.Error,
                            $"The reader '{reader.Kind}' needs the source {Label(source)} to be set up first: {string.Join("; ", plan.Steps.Select(s => s.Title))}. "
                            + $"The statements that do it (the source side's provisioning, which the set's `provision` task applies): {string.Join(" ", plan.Steps.Select(s => s.CommandText.TrimEnd(';') + ";"))}", about));
                        break;
                    case ProvisioningState.Unsupported or ProvisioningState.Unknown:
                        found.Add(new(Preflight.Warning, $"What the reader '{reader.Kind}' needs of the source {Label(source)} could not be planned: {string.Join(" ", plan.Warnings)}", about));
                        break;
                    default:
                        found.Add(new(Preflight.Info, $"The source {Label(source)} needs nothing more for the reader '{reader.Kind}'.", about));
                        break;
                }
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                found.Add(new(Preflight.Warning, $"What the reader '{reader.Kind}' needs of the source {Label(source)} could not be read: {Describe(ex)}", about));
            }
        }

        return columns;
    }

    private static async Task ZeroRowReadAsync(Side side, string sql, string what, string about, List<PreflightFinding> found, CancellationToken ct, string? database = null, string hint = "")
    {
        try
        {
            if (side.Dialect is { } dialect && database is not null) await dialect.UseDatabaseAsync(side.Connection, database, ct).ConfigureAwait(false);
            await using var command = side.Connection.CreateTimedCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) { }   // no row comes back: the condition is false for every row
        }
        catch (DbException ex)
        {
            found.Add(new(Preflight.Error, $"{what} fails on the database: {DriverFailures.Describe(ex, side.Driver.DriverType)} The statement, which reads no row: {sql}.{hint}", about));
        }
    }

    // ---- the target

    private async Task<(bool Exists, IReadOnlyList<ColumnMetadata>? Columns)> TargetAsync(ReplicationTaskConfig task, TableMappingConfig mapping, TableRef target, Side side, List<PreflightFinding> found, CancellationToken ct)
    {
        var about = target.ConnectionName;
        var columns = await side.Driver.ListColumnsAsync(side.Connection, target.Database, target.Schema, target.Table, ct).ConfigureAwait(false);
        if (columns.Count == 0) return (false, columns);   // the provisioning plan says whether it can be created and how

        foreach (var column in mapping.ColumnMappings)
            if (ColumnOf(columns, column.TargetColumn) is null)
                found.Add(new(ProvisioningResolution.AlterTargetTableColumns(task, mapping) ? Preflight.Warning : Preflight.Error,
                    $"The target column '{column.TargetColumn}' of the mapping '{mapping.Name}' is not a column of {Label(target)}.", about));

        // only what the mapping's writers do to the table: a writer that refills a scope (DeleteInsert) never updates, so its login needs no UPDATE
        var writers = Stages(task, mapping).Where(s => s.Role == "writer" && !string.IsNullOrEmpty(s.Kind)).Select(s => s.Kind!).Distinct(StringComparer.Ordinal).ToList();
        var unknown = false;
        foreach (var (privilege, change) in new[] { (Privilege.Insert, WritePrivileges.Insert), (Privilege.Update, WritePrivileges.Update), (Privilege.Delete, WritePrivileges.Delete) })
        {
            // a Kind the driver does not have needs nothing here: the key check reports it as an error
            var needing = writers.Where(kind => side.Driver.Writers.FirstOrDefault(w => w.Kind == kind) is { } writer && writer.NeedsPrivileges.HasFlag(change)).ToList();
            if (needing.Count == 0) continue;
            var may = await PrivilegeAsync(side, privilege, target, ct).ConfigureAwait(false);
            if (may is null) { unknown = true; break; }
            if (may == false)
                found.Add(new(Preflight.Error, $"The run login of '{target.ConnectionName}' may not {Word(privilege)} {Label(target)}, which {(needing.Count == 1 ? $"the writer '{needing[0]}' does" : $"the writers {string.Join(" and ", needing.Select(k => $"'{k}'"))} do")}.", about));
        }
        if (unknown) found.Add(new(Preflight.Info, $"The {side.Driver.DriverType} engine's privileges are not read by the preflight, so whether the run login may write {Label(target)} is not checked.", about));

        foreach (var key in columns.Where(c => c.IsPrimaryKey && c.IsNullable))
            found.Add(new(Preflight.Warning, $"The key column '{key.Name}' of {Label(target)} is nullable: a key column must not be.", about));
        return (true, columns);
    }

    private async Task PlansAsync(ReplicationTaskConfig task, TableMappingConfig mapping, SourceTableRef source, TableRef target, Side targetSide, bool targetExists,
        IReadOnlyList<ColumnMetadata>? targetColumns, List<PreflightFinding> found, CancellationToken ct)
    {
        var about = target.ConnectionName;
        ProvisioningPlan plan;
        try { plan = (await provisioner.GetPlansAsync(task.Name, mapping.Name, ct).ConfigureAwait(false)).Target; }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ConfigValidationException or NotSupportedException)
        {
            found.Add(new(targetExists ? Preflight.Warning : Preflight.Error, $"The target side's provisioning plan of the mapping '{mapping.Name}' could not be made: {Describe(ex)}", about));
            return;
        }

        var statements = string.Join(" ", plan.Steps.Select(s => s.CommandText.TrimEnd().TrimEnd(';') + ";"));
        if (!targetExists)
        {
            if (plan.State is not ProvisioningState.Missing)
            {
                found.Add(new(Preflight.Error, $"The target table {Label(target)} is not there, and provisioning cannot create it: {string.Join(" ", plan.Warnings)}", about));
                return;
            }
            var mayCreate = await MayCreateTableAsync(targetSide, target, ct).ConfigureAwait(false);
            var automatic = ProvisioningResolution.CreateTargetTableIfMissing(task, mapping);
            var login = mayCreate switch
            {
                true => $"The run login of '{target.ConnectionName}' may create it.",
                false => $"The run login of '{target.ConnectionName}' may not create it, so an administrator runs the statement.",
                null => $"Whether the run login of '{target.ConnectionName}' may create it is not read on the {targetSide.Driver.DriverType} engine.",
            };
            var severity = automatic && mayCreate != false ? Preflight.Warning : Preflight.Error;
            found.Add(new(severity, automatic
                ? $"The target table {Label(target)} is not there; the first pass creates it (createTargetTableIfMissing). {login} The statement: {statements}"
                : $"The target table {Label(target)} is not there: apply the target side's provisioning (the set's `provision` task, createTargetTable) before the first pass. {login} The statement: {statements}", about));
            return;
        }

        if (plan.State is ProvisioningState.Missing)
        {
            var mayAlter = await PrivilegeAsync(targetSide, Privilege.Alter, target, ct).ConfigureAwait(false);
            var automatic = ProvisioningResolution.AlterTargetTableColumns(task, mapping);
            found.Add(new(automatic && mayAlter != false ? Preflight.Info : Preflight.Warning,
                $"The target table {Label(target)} differs from the mapping; {(automatic ? "each pass alters it (alterTargetTableColumnsIfMissingOrChanged)" : "the set's `provision` task (alterTargetTable) brings it in line")}"
                + $"{(mayAlter == false ? $", and the run login of '{target.ConnectionName}' may not alter it" : "")}. The statements: {statements}", about));
        }
        else if (plan.State is ProvisioningState.Unsupported or ProvisioningState.Unknown && plan.Warnings.Count > 0)
            found.Add(new(Preflight.Warning, $"The target side's provisioning plan of {Label(target)}: {string.Join(" ", plan.Warnings)}", about));
    }

    // ---- types and keys

    private static void TypesAgree(TableMappingConfig mapping, SourceTableRef source, TableRef target, SqlDialect from, SqlDialect to,
        IReadOnlyList<ColumnMetadata> sourceColumns, IReadOnlyList<ColumnMetadata> targetColumns, List<PreflightFinding> found)
    {
        foreach (var column in mapping.ColumnMappings)
        {
            // a transform's result, or a column of a joined table, has a type the catalog does not give
            if (!string.IsNullOrEmpty(column.Transform) || !string.IsNullOrEmpty(column.Relationship)) continue;
            if (ColumnOf(sourceColumns, column.SourceColumn) is not { } s || ColumnOf(targetColumns, column.TargetColumn) is not { } t) continue;
            var sourceType = from.ReadColumnType(s.NativeType);
            var targetType = to.ReadColumnType(t.NativeType);
            if (sourceType is not { IsMapped: true, Result: { } a } || targetType is not { IsMapped: true, Result: { } b }) continue;
            var fit = FidelityRules.Assess(a, b);
            var pair = $"'{s.Name}' ({s.NativeType} on {Label(source)}) into '{t.Name}' ({t.NativeType} on {Label(target)})";
            if (!fit.IsMapped)
                found.Add(new(Preflight.Warning, $"The column {pair} is not a type the target column can hold{(fit.Note is { Length: > 0 } n ? $": {n}" : "")}.", target.ConnectionName));
            else if (fit.Fidelity == Fidelity.Lossy)
                found.Add(new(Preflight.Warning, $"The column {pair} may not fit{(fit.Note is { Length: > 0 } n ? $": {n}" : "")}.", target.ConnectionName));
        }
    }

    /// <summary>
    /// The mapping's key against the target's (no nullable key column, the target's primary key when it has one), and each reader and writer the mapping runs asked of its own driver
    /// whether it matches rows on a key, and whether the live tables give it one. A Kind the driver does not have is an error: the pass would refuse it.
    /// </summary>
    private static void Keys(ReplicationTaskConfig task, TableMappingConfig mapping, SourceTableRef source, TableRef target, IDriver sourceDriver, IDriver targetDriver,
        IReadOnlyList<ColumnMetadata> sourceColumns, IReadOnlyList<ColumnMetadata>? targetColumns, List<PreflightFinding> found)
    {
        var sourceKey = mapping.PrimaryKey.Count > 0 ? [.. mapping.PrimaryKey] : sourceColumns.Where(c => c.IsPrimaryKey).OrderBy(c => c.KeyOrdinal).Select(c => c.Name).ToList();
        foreach (var name in sourceKey)
            if (ColumnOf(sourceColumns, name) is { IsNullable: true } c)
                found.Add(new(Preflight.Warning, $"The key column '{c.Name}' is nullable on the source {Label(source)}: a row whose key is null cannot be matched on the target.", source.ConnectionName));

        var targetKey = sourceKey.Select(k => mapping.ColumnMappings.FirstOrDefault(m => string.IsNullOrEmpty(m.Relationship) && string.Equals(m.SourceColumn, k, StringComparison.OrdinalIgnoreCase))?.TargetColumn).ToList();
        var targetPrimary = targetColumns?.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList() ?? [];
        if (targetColumns is not null && sourceKey.Count > 0 && targetKey.All(k => k is not null) && targetPrimary.Count > 0
            && !new HashSet<string>(targetKey!, StringComparer.OrdinalIgnoreCase).SetEquals(targetPrimary))
            found.Add(new(Preflight.Warning, $"The mapping's key ({string.Join(", ", targetKey)}) is not the primary key ({string.Join(", ", targetPrimary)}) of the target {Label(target)}.", target.ConnectionName));

        foreach (var (role, kind, options) in Stages(task, mapping).DistinctBy(s => (s.Role, s.Kind)))
        {
            if (string.IsNullOrEmpty(kind)) continue;
            var driver = role == "reader" ? sourceDriver : targetDriver;
            var side = role == "reader" ? source.ConnectionName : target.ConnectionName;
            KeyNeed? need = role == "reader" ? driver.Readers.FirstOrDefault(r => r.Kind == kind)?.NeedsKey : driver.Writers.FirstOrDefault(w => w.Kind == kind)?.NeedsKey;
            if (need is null)
            {
                found.Add(new(Preflight.Error, $"The {role} '{kind}' is not one the {driver.DriverType} driver of '{side}' has.", side));
                continue;
            }

            var problem = need switch
            {
                KeyNeed.SourceKey when sourceKey.Count == 0 => $"the source table {Label(source)} has no primary key, and the {role} reports rows by it",
                KeyNeed.TargetKey when targetColumns is not null && targetPrimary.Count == 0 => $"the target table {Label(target)} has no primary key, and the {role} matches rows on it",
                KeyNeed.TargetKey when targetColumns is not null && targetPrimary.Where(k => !mapping.ColumnMappings.Any(m => string.Equals(m.TargetColumn, k, StringComparison.OrdinalIgnoreCase))).ToList() is { Count: > 0 } missing =>
                    $"the column mappings do not include the target's key column(s) {string.Join(", ", missing)}, which the {role} matches rows on",
                KeyNeed.NaturalKey when sourceKey.Count == 0 && !(options.TryGetValue(Scd2Writer.NaturalKeyOption, out var natural) && !string.IsNullOrWhiteSpace(natural)) =>
                    $"it needs a natural key (its `{Scd2Writer.NaturalKeyOption}` option, or the source's primary key), and the source {Label(source)} has none",
                _ => null,
            };
            if (problem is not null) found.Add(new(Preflight.Warning, $"The {role} '{kind}' would refuse the mapping '{mapping.Name}': {problem}.", side));
        }
    }

    /// <summary>Each reader and writer a pass of the mapping runs: the change processing's, the bulk load's, and the reconcile's when it is on.</summary>
    private static List<(string Role, string? Kind, IReadOnlyDictionary<string, string> Options)> Stages(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        var stages = new List<(string Role, string? Kind, IReadOnlyDictionary<string, string> Options)>
        {
            ("reader", PipelineResolution.Reader(task, mapping).Kind, PipelineResolution.Reader(task, mapping).Options),
            ("writer", PipelineResolution.Writer(task, mapping).Kind, PipelineResolution.Writer(task, mapping).Options),
        };
        if (PipelineResolution.BulkLoadReader(task, mapping) is { } bulkReader) stages.Add(("reader", bulkReader.Kind, bulkReader.Options));
        if (PipelineResolution.BulkLoadWriter(task, mapping) is { } bulkWriter) stages.Add(("writer", bulkWriter.Kind, bulkWriter.Options));
        if (PipelineResolution.Reconcile(task, mapping).Enabled)
        {
            stages.Add(("reader", PipelineResolution.ReconcileReaderKind(task, mapping), new Dictionary<string, string>()));
            stages.Add(("writer", PipelineResolution.ReconcileWriterKind(task, mapping), PipelineResolution.Writer(task, mapping).Options));
        }
        return stages;
    }

    // ---- the state on the target

    private async Task<List<PreflightFinding>> TrackingAsync(ReplicationTaskConfig task, List<TableRef> targets, CancellationToken ct)
    {
        var found = new List<PreflightFinding>();
        if (task.Tracking is null) return found;
        foreach (var target in targets.DistinctBy(t => (t.ConnectionName, t.Database)))
        {
            Side? side = null;
            try
            {
                side = await OpenAsync(target, "target", found, ct).ConfigureAwait(false);
                if (side is null) continue;
                var resolved = TrackingResolution.Resolve(task.Tracking, side.Driver.DriverType);
                if (resolved.EffectiveMode == "state" || TargetPositionLog.EngineOf(side.Driver.DriverType) is not { } engine) continue;
                var log = new TargetPositionLog(engine);
                if (side.Dialect is { } dialect) await dialect.UseDatabaseAsync(side.Connection, target.Database, ct).ConfigureAwait(false);
                if (await log.ExistsAsync(side.Connection, ct).ConfigureAwait(false))
                {
                    found.Add(new(Preflight.Info, $"The state table {log.Qualified} is on '{target.ConnectionName}'{(target.Database.Length > 0 ? $" ({target.Database})" : "")}.", target.ConnectionName));
                    continue;
                }
                var may = await MayCreateTableAsync(side, new TableRef { ConnectionName = target.ConnectionName, Database = target.Database, Schema = TargetPositionLog.Schema, Table = TargetPositionLog.Table }, ct).ConfigureAwait(false);
                found.Add(may switch
                {
                    false => new(Preflight.Error, $"The replication keeps its state on the target (tracking mode {resolved.EffectiveMode}), and {log.Qualified} is not on '{target.ConnectionName}': its run login may not create the schema '{TargetPositionLog.Schema}' and its table, so an administrator creates them before the first pass.", target.ConnectionName),
                    true => new(Preflight.Info, $"The state table {log.Qualified} is not on '{target.ConnectionName}' yet; the first pass creates it, which its run login may.", target.ConnectionName),
                    null => new(Preflight.Warning, $"The state table {log.Qualified} is not on '{target.ConnectionName}' yet; the first pass creates it, and whether its run login may is not read on the {side.Driver.DriverType} engine.", target.ConnectionName),
                });
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                found.Add(new(Preflight.Warning, $"Where the replication keeps its state on '{target.ConnectionName}' could not be read: {Describe(ex)}", target.ConnectionName));
            }
            finally
            {
                if (side is not null) await side.DisposeAsync().ConfigureAwait(false);
            }
        }
        return found;
    }

    // ---- the engines' privilege functions

    private enum Privilege { Select, Insert, Update, Delete, Alter }

    private static string Word(Privilege privilege) => privilege switch
    {
        Privilege.Insert => "INSERT into",
        Privilege.Update => "UPDATE",
        Privilege.Delete => "DELETE from",
        Privilege.Alter => "ALTER",
        _ => "SELECT from",
    };

    private static bool IsSqlServer(IDriver driver) => string.Equals(driver.DriverType, DriverIds.MsSql, StringComparison.OrdinalIgnoreCase);

    private static bool IsPostgres(IDriver driver) => string.Equals(driver.DriverType, DriverIds.Postgres, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the login holds <paramref name="privilege"/> on the table, by the engine's own function (<c>HAS_PERMS_BY_NAME</c>, <c>has_table_privilege</c>, the table's owner for an alter on PostgreSQL); null on an engine whose function is not read here.</summary>
    private static async Task<bool?> PrivilegeAsync(Side side, Privilege privilege, TableRef table, CancellationToken ct)
    {
        if (side.Dialect is not { } dialect) return null;
        var name = dialect.QualifyTable(table.Schema, table.Table);
        string sql;
        if (IsSqlServer(side.Driver))
            sql = $"SELECT HAS_PERMS_BY_NAME(@name, 'OBJECT', '{privilege.ToString().ToUpperInvariant()}')";
        else if (IsPostgres(side.Driver))
            sql = privilege == Privilege.Alter
                ? "SELECT pg_has_role(c.relowner, 'USAGE') FROM pg_class c WHERE c.oid = to_regclass(@name)"
                : $"SELECT has_table_privilege(to_regclass(@name), '{privilege.ToString().ToUpperInvariant()}')";
        else return null;
        await dialect.UseDatabaseAsync(side.Connection, table.Database, ct).ConfigureAwait(false);
        return await ScalarAsync(side.Connection, sql, ct, ("name", name)).ConfigureAwait(false);
    }

    /// <summary>Whether the login may create the table in its schema: the schema's and the database's rights on SQL Server, the schema's (or the database's, for a schema that is not there) on PostgreSQL; null elsewhere.</summary>
    private static async Task<bool?> MayCreateTableAsync(Side side, TableRef table, CancellationToken ct)
    {
        string sql;
        if (IsSqlServer(side.Driver))
            sql = "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1 AND (CASE WHEN SCHEMA_ID(@schema) IS NULL THEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE SCHEMA') "
                + "ELSE HAS_PERMS_BY_NAME(@schema, 'SCHEMA', 'ALTER') END) = 1 THEN 1 ELSE 0 END";
        else if (IsPostgres(side.Driver))
            sql = "SELECT CASE WHEN to_regnamespace(@schema) IS NULL THEN has_database_privilege(current_database(), 'CREATE') ELSE has_schema_privilege(@schema, 'CREATE') END";
        else return null;
        if (side.Dialect is { } dialect) await dialect.UseDatabaseAsync(side.Connection, table.Database, ct).ConfigureAwait(false);
        return await ScalarAsync(side.Connection, sql, ct, ("schema", table.Schema)).ConfigureAwait(false);
    }

    /// <summary>A yes-or-no answer of the engine; a NULL (an object the login cannot see) is a no.</summary>
    private static async Task<bool?> ScalarAsync(DbConnection connection, string sql, CancellationToken ct, params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateTimedCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        var answer = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return answer switch
        {
            null or DBNull => false,
            bool b => b,
            _ => Convert.ToInt32(answer, CultureInfo.InvariantCulture) == 1,
        };
    }
}

/// <summary>What the preflight's provisioner records of an apply: nothing, since the preflight plans and never applies.</summary>
internal sealed class NothingAfterApply : IMappingAfterApply
{
    public void RenamesApplied(string replicationName, TableMappingConfig mapping, IReadOnlyList<RenameStep> applied) { }

    public void TargetColumnsRead(string replicationName, string mappingName, IReadOnlyList<CachedColumn> read) { }
}
