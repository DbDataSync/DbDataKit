using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Sql;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Drivers.Generic;
using DbDataKit.Sync.Provisioning;
using DbDataSync.Scripting;
using DbDataKit.Sync.Scripting.Abstractions;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>One candidate a strategy proposed: its label, the column and bounds of a range (half-open), or the segment in words for another kind, and whether it is selected without anyone saying so.</summary>
public sealed record StrategyCandidate(string Label, string? Column, string? Start, string? End, string Segment, bool Selected);

/// <summary>What a trial of a segmenting strategy answered: the strategy, its kind, the column it was run over, and every candidate, selected or not.</summary>
public sealed record StrategyTrialAnswer(string Replication, string Mapping, string Strategy, string Kind, string? Column, bool ReadsADatabase, IReadOnlyList<StrategyCandidate> Candidates);

/// <summary>
/// A segmenting strategy tried against a mapping, as a reload would run it: its SQL on the source or the target (a SQL kind), in an in-memory DuckDB (the DuckDB kind), or its script with both connections, each
/// connection opened with its run login. The strategy is the one the replication has by name, or an unsaved one the person is editing, which is tried exactly as it would run once saved. Every candidate is
/// answered, so the person sees the whole proposal. The candidates' bounds are values of a table, so they are the run's answer and are kept nowhere
/// (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept).
/// </summary>
public sealed class StrategyTrial(ConfigRepository config, IConnectionFactory connections, CustomSegmentExpansion segments)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// Tries <paramref name="strategyJson"/> (an unsaved strategy: <c>{"name", "kind", "sql", "scriptName", "parameters"}</c>), or else the strategy <paramref name="strategyName"/> of the replication, over
    /// <paramref name="column"/> of the mapping's source. The mapping is the one of <paramref name="mappingText"/> when it is given (the draft's file, unsaved edits included), else the one the version has. Throws
    /// <see cref="PreviewException"/> naming what is wrong.
    /// </summary>
    public async Task<StrategyTrialAnswer> TryAsync(string replication, string mappingName, string strategyJson, string strategyName, string? column, CancellationToken ct, string? mappingText = null)
    {
        var (task, mapping) = MappingPreview.Load(config, replication, mappingName, mappingText);
        if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
            throw new PreviewException($"The mapping '{mapping.Name}' has {mapping.Sources.Count} source(s) and {mapping.Targets.Count} target(s); a strategy divides a mapping of one source and one target.");
        var strategy = Strategy(task, strategyJson, strategyName);
        column = string.IsNullOrWhiteSpace(column) ? null : column.Trim();

        SourceTableRef source;
        TableRef target;
        try
        {
            source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
            target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);
        }
        catch (InvalidOperationException ex) { throw new PreviewException($"The mapping '{mapping.Name}': {ex.Message}"); }

        IReadOnlyList<SegmentCandidate> proposed;
        try
        {
            if (!strategy.RunsAgainstAConnection)
                proposed = await segments.ProposeAsync(strategy, column, task, mapping, new SegmentingConnections(), ct).ConfigureAwait(false);
            else
            {
                var (sourceConnection, sourceDriver) = await connections.OpenAsync(source.ConnectionName, CredentialRole.Run, ct).ConfigureAwait(false);
                await using (sourceConnection)
                {
                    var (targetConnection, _) = await connections.OpenAsync(target.ConnectionName, CredentialRole.Run, ct).ConfigureAwait(false);
                    await using (targetConnection)
                    {
                        // a script is handed the source's columns as its catalog reports them; the SQL kinds say what they need in their own query
                        IReadOnlyList<ColumnMetadata>? columns = strategy.Kind == SegmentingStrategyKind.Script && source.Table.Length > 0
                            ? await sourceDriver.ListColumnsAsync(sourceConnection, source.Database, source.Schema, source.Table, ct).ConfigureAwait(false)
                            : null;
                        proposed = await segments.ProposeAsync(strategy, column, task, mapping,
                            new SegmentingConnections(sourceConnection, targetConnection, columns, ScriptDialectAdapter.For(sourceDriver)), ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException or ArgumentException)
        {
            // a query that does not compile, returns the wrong columns or cannot reach its side is the strategy being wrong: said in the form, not as a crash
            throw new PreviewException($"The strategy '{strategy.Name}' failed: {DriverFailures.DescribeForThePerson(ex)}",
                $"The strategy '{strategy.Name}' failed. The reason went to the person who asked and is not kept: a database's reason can quote a value.");
        }

        return new StrategyTrialAnswer(task.Name, mapping.Name, strategy.Name, strategy.Kind.ToString(), column, strategy.RunsAgainstAConnection, [.. proposed.Select(Candidate)]);
    }

    private static SegmentingStrategyConfig Strategy(ReplicationTaskConfig task, string strategyJson, string strategyName)
    {
        if (!string.IsNullOrWhiteSpace(strategyJson))
        {
            SegmentingStrategyConfig? given;
            try { given = JsonSerializer.Deserialize<SegmentingStrategyConfig>(strategyJson, Json); }
            catch (JsonException ex) { throw new PreviewException($"The input `strategy` is not a segmenting strategy ({{\"name\", \"kind\", \"sql\", \"scriptName\", \"parameters\"}}): {ex.Message}"); }
            if (given is null) throw new PreviewException("The input `strategy` is empty.");
            if (string.IsNullOrWhiteSpace(given.Name)) given.Name = "unsaved";
            given.Parameters ??= [];
            return given;
        }

        if (string.IsNullOrWhiteSpace(strategyName)) throw new PreviewException("Give the strategy to try: the input `strategy` (an unsaved one) or `name` (one the replication has).");
        return task.SegmentingStrategies.FirstOrDefault(s => string.Equals(s.Name, strategyName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new PreviewException($"The replication '{task.Name}' has no segmenting strategy '{strategyName}' in the published version.");
    }

    private static StrategyCandidate Candidate(SegmentCandidate c) => c.Segment is RangeSegment range
        ? new StrategyCandidate(c.Label, range.Column, range.RangeMin, range.RangeMax, range.Describe(), c.Selected)
        : new StrategyCandidate(c.Label, null, null, null, c.Segment.Describe(), c.Selected);
}
