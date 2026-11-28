using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Sql;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Provisioning;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// The first rows of a mapping's source, as a pass would read them: the mapped source columns (every column of a query source) of the source table, with the mapping's filter, read with the connection's run login.
/// The rows are the answer of the run and are kept nowhere (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept): the caller hands them to the runner's answer, never to a step or the log.
/// </summary>
/// <param name="Replication">The replication.</param>
/// <param name="Mapping">The mapping.</param>
/// <param name="Connection">The source connection the rows were read on.</param>
/// <param name="Source">The source as <c>database.schema.table</c>, or <c>query</c> for a query source.</param>
/// <param name="Columns">The columns of the rows, in order, as the database named them.</param>
/// <param name="Rows">Each row's values in column order: text, numbers, true or false, or null; a long text is cut and a binary value is shown by its length.</param>
/// <param name="More">Whether the source has more rows than were read.</param>
/// <param name="Limit">How many rows were asked for.</param>
public sealed record MappingPreviewAnswer(string Replication, string Mapping, string Connection, string Source, IReadOnlyList<string> Columns, IReadOnlyList<JsonArray> Rows, bool More, int Limit);

public sealed class MappingPreview(ConfigRepository config, IConnectionFactory connections)
{
    /// <summary>The most rows a preview reads.</summary>
    public const int MaxRows = 100;

    /// <summary>The rows a preview reads when it is not told.</summary>
    public const int DefaultRows = 20;

    /// <summary>The longest text a value is shown with; the rest is cut.</summary>
    public const int MaxText = 500;

    /// <summary>
    /// The first <paramref name="limit"/> rows of the source of <paramref name="mappingName"/>: the mapping of <paramref name="mappingText"/> when it is given (the draft's file, unsaved edits included), else the one the version has.
    /// Throws <see cref="PreviewException"/> naming what is wrong (a mapping that is not there or not 1:1, a source the database refuses).
    /// </summary>
    public async Task<MappingPreviewAnswer> ReadAsync(string replication, string mappingName, int limit, CancellationToken ct, string? mappingText = null)
    {
        if (limit is < 1 or > MaxRows) throw new PreviewException($"A preview reads 1 to {MaxRows} rows, not {limit}.");
        var (task, mapping) = Load(config, replication, mappingName, mappingText);
        if (mapping.Sources.Count != 1) throw new PreviewException($"The mapping '{mapping.Name}' has {mapping.Sources.Count} sources; a preview reads a mapping of one source.");
        SourceTableRef source;
        try { source = EndpointResolution.ResolveSource(task, mapping.Sources[0]); }
        catch (InvalidOperationException ex) { throw new PreviewException($"The mapping '{mapping.Name}': {ex.Message}"); }

        var (connection, driver) = await connections.OpenAsync(source.ConnectionName, CredentialRole.Run, ct).ConfigureAwait(false);
        await using (connection)
        {
            var dialect = (driver as IDialectProvider)?.Dialect;
            string sql;
            string label;
            if (!string.IsNullOrWhiteSpace(source.Query) && source.Table.Length == 0)
            {
                sql = $"SELECT * FROM ({source.Query}) preview_query";
                label = "query";
            }
            else
            {
                var mapped = mapping.ColumnMappings.Where(c => string.IsNullOrEmpty(c.Relationship) && c.SourceColumn.Length > 0).Select(c => c.SourceColumn).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var list = mapped.Count > 0 && dialect is not null ? string.Join(", ", mapped.Select(dialect.QuoteIdentifier)) : "*";
                var table = dialect?.QualifyTable(source.Schema, source.Table) ?? $"{source.Schema}.{source.Table}";
                sql = $"SELECT {list} FROM {table}{(string.IsNullOrWhiteSpace(source.Filter) ? "" : $" WHERE ({source.Filter})")}";
                label = string.Join('.', new[] { source.Database, source.Schema, source.Table }.Where(p => p.Length > 0));
                if (dialect is not null && source.Database.Length > 0) await dialect.UseDatabaseAsync(connection, source.Database, ct).ConfigureAwait(false);
            }

            var rows = new List<JsonArray>();
            var columns = new List<string>();
            var more = false;
            try
            {
                await using var command = connection.CreateTimedCommand();
                command.CommandText = sql;
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (rows.Count == limit)
                    {
                        more = true;
                        break;
                    }

                    var row = new JsonArray();
                    for (var i = 0; i < reader.FieldCount; i++) row.Add(Value(reader, i));
                    rows.Add(row);
                }

                // the rest of the result is not read: the database is told to stop sending it
                if (more) command.Cancel();
            }
            catch (DbException ex)
            {
                throw new PreviewException($"The read of {label} for the mapping '{mapping.Name}' fails on '{source.ConnectionName}': {DriverFailures.DescribeForThePerson(ex, driver.DriverType)}",
                    $"The read of {label} for the mapping '{mapping.Name}' fails on '{source.ConnectionName}'. The database's reason went to the person who asked and is not kept.");
            }

            return new MappingPreviewAnswer(task.Name, mapping.Name, source.ConnectionName, label, columns, rows, more, limit);
        }
    }

    /// <summary>
    /// The replication and its mapping, or <see cref="PreviewException"/> naming the one that does not read. The replication is the version's; the mapping is read from <paramref name="mappingText"/> when it is given (the
    /// draft's file, which may hold edits the version does not have, or be a mapping the version has not got yet), else from the version.
    /// </summary>
    internal static (ReplicationTaskConfig Task, TableMappingConfig Mapping) Load(ConfigRepository config, string replication, string mapping, string? mappingText = null)
    {
        if (string.IsNullOrWhiteSpace(replication) || string.IsNullOrWhiteSpace(mapping)) throw new PreviewException("Name the replication and its mapping: the inputs `replication` and `mapping`.");
        var file = $"config/replications/{replication.Trim()}/table-mappings/{mapping.Trim()}.yaml";
        try
        {
            var task = config.LoadReplicationTask(replication.Trim());
            return (task, string.IsNullOrWhiteSpace(mappingText) ? config.LoadTableMapping(replication.Trim(), mapping.Trim()) : ConfigRepository.ReadTableMapping(mappingText, file));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PreviewException(string.IsNullOrWhiteSpace(mappingText)
                ? $"The published version has no mapping '{mapping}' of the replication '{replication}'. Publish the draft that has it first."
                : $"The published version has no replication '{replication}'. Publish the draft that has it first.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FormatException or YamlDotNet.Core.YamlException or ConfigValidationException or MappingLoadingException)
        {
            throw new PreviewException($"The mapping '{mapping}' of '{replication}' does not read: {ex.Message}");
        }
    }

    /// <summary>A value as the answer shows it: JSON numbers, text, true or false, or null; a date as ISO 8601, a binary value by its length, a long text cut at <see cref="MaxText"/>.</summary>
    internal static JsonNode? Value(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => JsonValue.Create(b),
            byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong u => JsonValue.Create(u),
            decimal d => JsonValue.Create(d),
            double d when double.IsFinite(d) => JsonValue.Create(d),
            float f when float.IsFinite(f) => JsonValue.Create(f),
            DateTime at => JsonValue.Create(at.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset at => JsonValue.Create(at.ToString("O", CultureInfo.InvariantCulture)),
            DateOnly day => JsonValue.Create(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            TimeOnly time => JsonValue.Create(time.ToString("O", CultureInfo.InvariantCulture)),
            TimeSpan span => JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture)),
            byte[] bytes => JsonValue.Create($"({bytes.Length} bytes)"),
            _ => JsonValue.Create(Cut(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")),
        };
    }

    private static string Cut(string text) => text.Length <= MaxText ? text : string.Concat(text.AsSpan(0, MaxText), "…");
}

/// <summary>
/// A preview or a trial that cannot be made as asked, in words a person can act on. <paramref name="kept"/> is what the run's record may keep of it: a database's own reason can quote a value of the table
/// (a conversion that failed on a value), so that reason goes only to the person, in the run's answer, and the record says where it went.
/// </summary>
public sealed class PreviewException(string message, string? kept = null) : Exception(message)
{
    /// <summary>What the run's record keeps: the message, or words without the database's reason.</summary>
    public string Kept { get; } = kept ?? message;
}
