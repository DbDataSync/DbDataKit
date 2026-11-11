using System.Data.Common;
using System.Globalization;
using System.Text;
using DbDataKit.Connections;
using DbDataKit.Dialect;

namespace DbDataKit.Quality;

/// <summary>
/// Evaluates <c>check/1</c> checks (plan 23a FQ07, D5): <b>one SELECT per check</b>, built with <see cref="ISqlDialect"/> (every identifier quoted, every value a dialect literal) and sent over a connection the caller opened read-only
/// (<see cref="ReadOnlyConnections"/> does it with a provider's recipe). The statement is checked against <see cref="ReadOnlyPolicy"/> before it is sent. Nothing leaves the engine but the measure and a few sample keys.
/// <para>
/// <b>The statements</b> (T is the table, <c>c</c> its columns, all quoted; <c>M</c> the count of failing rows):
/// </para>
/// <list type="bullet">
/// <item><c>not_null</c>: <c>SELECT [TOP n] keys, COUNT(*) OVER () FROM T WHERE c1 IS NULL OR c2 IS NULL ORDER BY keys [LIMIT n]</c>; the measure is the rows with a null in any of the columns.</item>
/// <item><c>unique</c>: the same over <c>(SELECT c, COUNT(*) n FROM T WHERE c IS NOT NULL GROUP BY c HAVING COUNT(*) &gt; 1)</c>, with <c>SUM(n) OVER ()</c>; the measure is the rows that share their values with another row. Rows with a null in a column are the <c>not_null</c> check's business.</item>
/// <item><c>accepted_values</c>: <c>WHERE c IS NOT NULL AND CAST(c AS text) NOT IN (literals)</c>. The comparison is on the <b>text form</b> of the column, so one list works for text, integers and codes whatever the column's type, and a value
/// that does not fit the column never makes the statement fail; the engine's collation decides about case and trailing blanks, as it does for every comparison on it.</item>
/// <item><c>range</c>: <c>WHERE c IS NOT NULL AND (c &lt; min OR c &gt; max)</c>, numbers as decimal literals, dates as date or timestamp literals.</item>
/// <item><c>referential</c>: <c>WHERE c IS NOT NULL AND NOT EXISTS (SELECT 1 FROM R WHERE R.rc = T.c)</c>; a row with a null in any column is not checked.</item>
/// <item><c>freshness</c>: <c>SELECT MAX(c) FROM T</c>, the age is measured against the clock in UTC (a column without a zone is read as UTC); without a column the age of <see cref="EvaluationContext.LastLoadedAt"/> and no statement (QF2). A table with no newest value fails.</item>
/// <item><c>row_count_change</c>: <c>SELECT COUNT(*) FROM T</c> compared with <see cref="EvaluationContext.PreviousRowCount"/>; without a baseline the check passes and only records the count (QF3).</item>
/// </list>
/// <para>
/// <b>Errors.</b> An exception of the connection or the statement never leaves: it becomes an <see cref="EvaluationStatus.Error"/> result (a missing table or column, a login that may not read, a timeout, a connection that is down). Only the caller's own
/// cancellation is rethrown. A limit reached (<see cref="EvaluationLimits"/>) is an error that names the limit.
/// </para>
/// </summary>
public sealed class CheckEvaluator(ISqlDialect dialect, EvaluationLimits? limits = null, TimeProvider? time = null)
{
    private const string TableAlias = "chk_t";
    private const string ReferenceAlias = "chk_r";
    private const string GroupAlias = "chk_g";

    private readonly EvaluationLimits _limits = limits ?? new EvaluationLimits();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ISqlDialect Dialect => dialect;

    /// <summary>
    /// Evaluates the checks over one connection that is opened when the first check that needs it asks and is shared by the others. A connection that cannot be opened makes every check that needs it an error with the same message (it is
    /// tried once). The connection is disposed at the end.
    /// </summary>
    public async Task<IReadOnlyList<CheckEvaluation>> EvaluateAsync(IReadOnlyList<CheckDefinition> checks, Func<CancellationToken, Task<DbConnection>> open, Func<CheckDefinition, EvaluationContext> contextFor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(contextFor);
        var results = new List<CheckEvaluation>();
        DbConnection? connection = null;
        string? openFailure = null;
        try
        {
            foreach (var check in checks)
            {
                ct.ThrowIfCancellationRequested();
                var context = contextFor(check);
                if (!NeedsConnection(check))
                {
                    results.Add(await EvaluateAsync(check, null, context, ct).ConfigureAwait(false));
                    continue;
                }
                if (connection is null && openFailure is null)
                {
                    try { connection = await open(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { openFailure = DescribeOpen(ex); }
                }
                results.Add(connection is null
                    ? CheckEvaluation.Failed(check.Id, openFailure!, _time.GetUtcNow())
                    : await EvaluateAsync(check, connection, context, ct).ConfigureAwait(false));
            }
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
        return results;
    }

    /// <summary>Evaluates one check over an open (read-only) connection. A freshness check on the last load needs none (null is allowed then).</summary>
    public async Task<CheckEvaluation> EvaluateAsync(CheckDefinition check, DbConnection? connection, EvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(context);
        var now = _time.GetUtcNow();
        string? statement = null;
        try
        {
            if (check.Kind == CheckKind.Freshness && check.Attachment.Columns.Count == 0) return FreshnessFromLoad(check, context, now);
            if (connection is null) return CheckEvaluation.Failed(check.Id, "There is no connection to evaluate the check over.", now);

            var table = context.ResolveTable(check.Attachment.Dataset) ?? throw new RefusedException($"The dataset '{check.Attachment.Dataset}' is not known here, so its table cannot be found.");
            switch (check.Kind)
            {
                case CheckKind.Freshness:
                    return await FreshnessFromColumnAsync(check, table, connection, now, ct, s => statement = s).ConfigureAwait(false);
                case CheckKind.RowCountChange:
                    return await RowCountChangeAsync(check, table, context, connection, now, ct, s => statement = s).ConfigureAwait(false);
                default:
                    return await RowCheckAsync(check, table, context, connection, now, ct, s => statement = s).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RefusedException ex) { return CheckEvaluation.Failed(check.Id, Cut(ex.Message), now, statement); }
        catch (Exception ex) { return CheckEvaluation.Failed(check.Id, Cut(Describe(ex)), now, statement); }
    }

    /// <summary>True when the check sends a statement (every kind but a freshness check that names no column).</summary>
    public static bool NeedsConnection(CheckDefinition check) => !(check.Kind == CheckKind.Freshness && check.Attachment.Columns.Count == 0);

    // ---------------------------------------------------------------- row checks

    private async Task<CheckEvaluation> RowCheckAsync(CheckDefinition check, IReadOnlyList<string> table, EvaluationContext context, DbConnection connection, DateTimeOffset now, CancellationToken ct, Action<string> sent)
    {
        var columns = check.Attachment.Columns;
        if (columns.Count == 0) throw new RefusedException($"The check '{check.Id}' names no column.");
        var maxSamples = Math.Clamp(_limits.MaxSampleKeys, 1, EvaluationLimits.SampleKeyCeiling);

        string sql;
        IReadOnlyList<string> sampleColumns;
        string? noSampleNote = null;
        if (check.Kind == CheckKind.Unique)
        {
            sampleColumns = columns;
            sql = UniqueStatement(table, columns, maxSamples);
        }
        else
        {
            var predicate = Predicate(check, table, context);
            sampleColumns = context.KeyColumns.Count > 0 ? context.KeyColumns : check.Kind == CheckKind.NotNull ? [] : columns;
            if (sampleColumns.Count == 0) noSampleNote = "no key columns are known for the dataset, so no sample keys are kept";
            sql = SamplingStatement(table, predicate, sampleColumns, maxSamples);
        }
        sent(sql);
        Refuse(sql);

        var samples = new List<string>();
        decimal measured = 0;
        await ExecuteAsync(connection, sql, ct, async (reader, token) =>
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var width = reader.FieldCount - 1;
                measured = ToDecimal(reader.GetValue(width));
                if (sampleColumns.Count == 0) continue;
                var parts = new string[width];
                for (var i = 0; i < width; i++) parts[i] = sampleColumns[i] + "=" + Format(reader.GetValue(i));
                if (samples.Count < maxSamples) samples.Add(string.Join(", ", parts));
            }
        }).ConfigureAwait(false);

        var failed = measured > 0;
        return new CheckEvaluation(check.Id, failed ? EvaluationStatus.Fail : EvaluationStatus.Pass, measured, 0, samples, null,
            failed && samples.Count == 0 && noSampleNote is not null ? Cut($"{measured.ToString("0", CultureInfo.InvariantCulture)} rows fail; {noSampleNote}") : null, sql, now);
    }

    private string Predicate(CheckDefinition check, IReadOnlyList<string> table, EvaluationContext context)
    {
        var columns = check.Attachment.Columns;
        switch (check.Kind)
        {
            case CheckKind.NotNull:
                return string.Join(" OR ", columns.Select(c => $"{Col(c)} IS NULL"));
            case CheckKind.AcceptedValues:
            {
                var column = Single(check);
                var values = ((AcceptedValuesParameters)check.Parameters).Values;
                if (values.Count > _limits.MaxAcceptedValues) throw new RefusedException($"limit reached: the check lists {values.Count} accepted values and at most {_limits.MaxAcceptedValues} are evaluated in one statement.");
                var text = dialect.ColumnType(CanonicalType.String()).Result ?? throw new RefusedException($"The engine '{dialect.Id}' has no text type to compare the column with.");
                var list = values.Count == 0 ? "NULL" : string.Join(", ", values.Select(v => dialect.Literal(v, CanonicalType.String())));
                return $"{Col(column)} IS NOT NULL AND CAST({Col(column)} AS {text}) NOT IN ({list})";
            }
            case CheckKind.Range:
            {
                var column = Single(check);
                var range = (RangeParameters)check.Parameters;
                var bounds = new List<string>();
                if (range.Min is { } min) bounds.Add($"{Col(column)} < {Bound(min)}");
                if (range.Max is { } max) bounds.Add($"{Col(column)} > {Bound(max)}");
                return $"{Col(column)} IS NOT NULL AND ({string.Join(" OR ", bounds)})";
            }
            case CheckKind.Referential:
            {
                var target = (ReferentialParameters)check.Parameters;
                if (target.Columns.Count != columns.Count) throw new RefusedException($"The check '{check.Id}' names {columns.Count} columns and its target {target.Columns.Count}.");
                var targetTable = context.ResolveTable(target.Dataset) ?? throw new RefusedException($"The dataset '{target.Dataset}' that the check refers to is not known here, so its table cannot be found.");
                var notNull = string.Join(" AND ", columns.Select(c => $"{Col(c)} IS NOT NULL"));
                var match = string.Join(" AND ", columns.Select((c, i) => $"{Alias(ReferenceAlias)}.{dialect.Quote(target.Columns[i])} = {Col(c)}"));
                return $"{notNull} AND NOT EXISTS (SELECT 1 FROM {dialect.QuoteQualified(targetTable)} {Alias(ReferenceAlias)} WHERE {match})";
            }
            default:
                throw new RefusedException($"The kind {CheckNames.Name(check.Kind)} is not a row check.");
        }
    }

    private string Bound(RangeBound bound)
    {
        if (bound.Number is { } number) return dialect.Literal(number, CanonicalType.Decimal(38, Math.Max(0, (decimal.GetBits(number)[3] >> 16) & 0xFF)));
        var moment = bound.Moment!.Value;
        return bound.Text.Length == 10 ? dialect.Literal(moment, CanonicalType.Of(CanonicalKind.Date)) : dialect.Literal(moment, CanonicalType.Timestamp());
    }

    private static string Single(CheckDefinition check) =>
        check.Attachment.Column ?? throw new RefusedException($"The check '{check.Id}' ({CheckNames.Name(check.Kind)}) takes one column and names {check.Attachment.Columns.Count}.");

    // the window count is computed over the whole filtered set before the row limit takes its few rows, so one statement gives the measure and the samples
    private string SamplingStatement(IReadOnlyList<string> table, string predicate, IReadOnlyList<string> sampleColumns, int maxSamples)
    {
        if (sampleColumns.Count == 0) return $"SELECT {CountAll} FROM {From(table)} WHERE {predicate}";
        var limit = dialect.RowLimit(dialect.DefaultRowLimitStyle, maxSamples);
        var keys = string.Join(", ", sampleColumns.Select(Col));
        return $"SELECT {limit.SelectPrefix}{keys}, {CountAll} OVER () FROM {From(table)} WHERE {predicate} ORDER BY {keys}{limit.Suffix}";
    }

    private string UniqueStatement(IReadOnlyList<string> table, IReadOnlyList<string> columns, int maxSamples)
    {
        var limit = dialect.RowLimit(dialect.DefaultRowLimitStyle, maxSamples);
        var inner = string.Join(", ", columns.Select(Col));
        var notNull = string.Join(" AND ", columns.Select(c => $"{Col(c)} IS NOT NULL"));
        var outer = string.Join(", ", columns.Select(c => $"{Alias(GroupAlias)}.{dialect.Quote(c)}"));
        var count = dialect.Quote("n");
        var big = dialect.ColumnType(CanonicalType.Of(CanonicalKind.Int64)).Result ?? "BIGINT";
        return $"SELECT {limit.SelectPrefix}{outer}, SUM(CAST({Alias(GroupAlias)}.{count} AS {big})) OVER () FROM (SELECT {inner}, {CountAll} AS {count} FROM {From(table)} WHERE {notNull} GROUP BY {inner} HAVING {CountAll} > 1) {Alias(GroupAlias)} ORDER BY {outer}{limit.Suffix}";
    }

    // ---------------------------------------------------------------- freshness and row count

    private CheckEvaluation FreshnessFromLoad(CheckDefinition check, EvaluationContext context, DateTimeOffset now)
    {
        var max = ((FreshnessParameters)check.Parameters).MaxAge;
        if (context.LastLoadedAt is not { } loaded)
            return CheckEvaluation.Failed(check.Id, "The check names no column and no successful load of the dataset is known, so its age cannot be measured.", now);
        return AgeResult(check.Id, now - loaded, max, now, null);
    }

    private async Task<CheckEvaluation> FreshnessFromColumnAsync(CheckDefinition check, IReadOnlyList<string> table, DbConnection connection, DateTimeOffset now, CancellationToken ct, Action<string> sent)
    {
        var max = ((FreshnessParameters)check.Parameters).MaxAge;
        var column = Single(check);
        var sql = $"SELECT MAX({Col(column)}) FROM {From(table)}";
        sent(sql);
        Refuse(sql);
        object? newest = null;
        await ExecuteAsync(connection, sql, ct, async (reader, token) => { if (await reader.ReadAsync(token).ConfigureAwait(false)) newest = reader.GetValue(0); }).ConfigureAwait(false);
        if (newest is null or DBNull)
            return new CheckEvaluation(check.Id, EvaluationStatus.Fail, null, (decimal)max.TotalSeconds, [], null, "The dataset has no value in the column, so it has no newest data.", sql, now);
        var moment = newest switch
        {
            DateTimeOffset o => o,
            DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Local ? DateTimeKind.Local : DateTimeKind.Utc)),
            DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            _ => (DateTimeOffset?)null,
        };
        if (moment is not { } at) return CheckEvaluation.Failed(check.Id, $"The column '{column}' is not a date or time (the engine returned {newest.GetType().Name}).", now, sql);
        return AgeResult(check.Id, now - at, max, now, sql);
    }

    private static CheckEvaluation AgeResult(string id, TimeSpan age, TimeSpan max, DateTimeOffset now, string? sql)
    {
        var seconds = (decimal)Math.Max(0, Math.Round(age.TotalSeconds, 3));
        return new CheckEvaluation(id, age > max ? EvaluationStatus.Fail : EvaluationStatus.Pass, seconds, (decimal)max.TotalSeconds, [], null, null, sql, now);
    }

    private async Task<CheckEvaluation> RowCountChangeAsync(CheckDefinition check, IReadOnlyList<string> table, EvaluationContext context, DbConnection connection, DateTimeOffset now, CancellationToken ct, Action<string> sent)
    {
        var limits = (RowCountChangeParameters)check.Parameters;
        var sql = $"SELECT {CountAll} FROM {From(table)}";
        sent(sql);
        Refuse(sql);
        decimal count = 0;
        await ExecuteAsync(connection, sql, ct, async (reader, token) => { if (await reader.ReadAsync(token).ConfigureAwait(false)) count = ToDecimal(reader.GetValue(0)); }).ConfigureAwait(false);

        if (context.PreviousRowCount is not { } before)
            return new CheckEvaluation(check.Id, EvaluationStatus.Pass, null, null, [], count, "The first evaluation: the row count is recorded as the baseline.", sql, now);

        // percent of change against the baseline; from an empty table every row is an unbounded increase
        decimal? percent = before == 0 ? (count == 0 ? 0 : null) : Math.Round((count - before) / before * 100, 4);
        var increase = percent is null || percent >= 0;
        var exceeded = (increase, percent) switch
        {
            (true, null) => limits.MaxIncreasePercent is not null,
            (true, { } p) => limits.MaxIncreasePercent is { } up && p > up,
            (false, { } p) => limits.MaxDecreasePercent is { } down && -p > down,
            _ => false,
        };
        var threshold = increase ? limits.MaxIncreasePercent : limits.MaxDecreasePercent;
        var message = percent is null ? $"The table was empty at the previous evaluation and has {count.ToString("0", CultureInfo.InvariantCulture)} rows now." : null;
        return new CheckEvaluation(check.Id, exceeded ? EvaluationStatus.Fail : EvaluationStatus.Pass, percent, threshold ?? (increase ? limits.MaxDecreasePercent : limits.MaxIncreasePercent), [], count, message, sql, now);
    }

    // ---------------------------------------------------------------- execution

    private async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct, Func<DbDataReader, CancellationToken, Task> read)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_limits.StatementTimeout);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            // the engine's own timeout is a little longer than ours, so the cancellation is normally the one that ends it
            command.CommandTimeout = (int)Math.Min(int.MaxValue, Math.Ceiling(_limits.StatementTimeout.TotalSeconds) + 5);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
            await read(reader, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RefusedException($"limit reached: the statement ran past its limit of {_limits.StatementTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds and was stopped.");
        }
    }

    private void Refuse(string sql)
    {
        var decision = ReadOnlyPolicy.Check(sql, dialect);
        if (!decision.Allowed) throw new RefusedException($"The statement was not sent because it is not a plain read: {decision.Reason}.");
    }

    // ---------------------------------------------------------------- text

    private string CountAll => dialect.Id.Value is "sqlserver" or "fabric" ? "COUNT_BIG(*)" : "COUNT(*)";

    private static string Alias(string name) => name;

    private string From(IReadOnlyList<string> table) => $"{dialect.QuoteQualified(table)} {TableAlias}";

    private string Col(string column) => $"{TableAlias}.{dialect.Quote(column)}";

    // DuckDB sums into a HUGEINT, which its driver hands over as a BigInteger
    private static decimal ToDecimal(object value) => value switch
    {
        DBNull => 0,
        System.Numerics.BigInteger big => (decimal)big,
        Int128 huge => (decimal)huge,
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    private string Format(object? value)
    {
        var text = value switch
        {
            null or DBNull => "NULL",
            DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        var length = Math.Max(1, _limits.MaxValueLength);
        return text.Length <= length ? text : text[..length] + "…";
    }

    private string Cut(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= _limits.MaxMessageLength ? line : line[.._limits.MaxMessageLength] + "…";
    }

    /// <summary>The message of an exception that is safe to keep: the engine's words only for a missing table or column (they hold identifiers), the class of the failure for the rest (an engine's message can hold a data value).</summary>
    internal static string Describe(Exception ex)
    {
        if (ex is DbException db)
        {
            var state = db.SqlState;
            var number = NumberOf(ex);
            var duck = DuckClass(ex.Message);
            if (state is "42P01" or "42703" or "42S02" or "42S22" || number is 207 or 208 or 4902 || duck is "Catalog Error" or "Binder Error")
                return "The table or column is not there: " + ex.Message;
            if (state is "57014" || number is -2 || duck is "Interrupt Error")
                return "limit reached: the engine stopped the statement because it ran too long.";
            if (state is "42501" || number is 229 or 230 or 297 or 300 || duck is "Permission Error")
                return "The login may not read the table (permission denied).";
            var code = state ?? (number is { } n ? n.ToString(CultureInfo.InvariantCulture) : duck ?? "unknown");
            return $"The engine refused the statement ({db.GetType().Name}, {code}); its message is not kept, because it can hold a data value.";
        }
        return $"The check could not be evaluated ({ex.GetType().Name}); the message is not kept, because it can hold a data value.";
    }

    private static string DescribeOpen(Exception ex) => ex switch
    {
        ConnectionFailureException failure => "The connection could not be opened: " + failure.Failure.PublicMessage,
        _ => $"The connection could not be opened ({ex.GetType().Name}).",
    };

    private static int? NumberOf(Exception ex) => ex.GetType().GetProperty("Number")?.GetValue(ex) as int?;

    private static string? DuckClass(string message)
    {
        var colon = message.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon < 40 && message[..colon].EndsWith("Error", StringComparison.Ordinal) ? message[..colon] : null;
    }

    private sealed class RefusedException(string message) : Exception(message);
}
