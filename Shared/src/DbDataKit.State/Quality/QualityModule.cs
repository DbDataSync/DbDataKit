namespace DbDataKit.State;

/// <summary>
/// The <c>quality</c> module (plan 23a FQ06, plan 23 section 2): what the checks of <c>DbDataKit.Quality</c> found, and which of them block. Nothing here evaluates a check; the evaluator writes through <see cref="QualityStore"/>.
/// <para>
/// <b>Two tables.</b> <c>check_result</c> is the history, one row per evaluation, kept for <see cref="HistoryDays"/> days (QF1) and then removed by the shared retention job: it is for trends and may expire.
/// <c>check_status</c> is the latest result of each check and its current block and since when; it is what blocks, so it has no retention rule and goes only with <see cref="QualityStore.Forget"/> (the check is gone from its definition).
/// A check is named by its dataset's lineage identity (plan 23a D3) and its id; the key holds the identity's hash, because the identity is long text and a key is bounded (the identity itself is in the row).
/// </para>
/// <para>
/// <b>Scope.</b> One copy per install (<see cref="Module"/>); the serving layer keeps one per group (<see cref="GroupModule"/>), so the instances of a group share one result and a group's block is the group's. Both are the
/// module <c>quality</c> with the same migrations: a database is opened with one of them, as its scope says.
/// </para>
/// <para>Migrations are appended, never edited; a program that reads a database whose <c>quality</c> version is newer than its own refuses it by the state module version check, naming the module and both versions.</para>
/// </summary>
public static class QualityModule
{
    public const string Name = "quality";

    /// <summary>How long <c>check_result</c> rows are kept (QF1: 90 days, the module's default; an administrator can change it with the retention settings). The state default of 30 days is too short for trends.</summary>
    public const double HistoryDays = 90;

    /// <summary>The statuses a result has, as stored.</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["pass", "fail", "error"];

    private static readonly string[] Migrations =
    [
        """
        -- One evaluation of one check, kept for the history (retention, 90 days). dataset_hash is the SHA-256 (lower-case hex) of the dataset's lineage identity, dataset is the identity itself.
        -- status is pass, fail or error (error: the check could not run, plan 23a QF10: it is neither a pass nor a fail). measured and threshold are numbers in the unit of the check's kind (failing rows,
        -- seconds of age, percent of change). observed is the raw reading behind a result where the kind has one (the row count of row_count_change, the next evaluation's baseline, QF3). sample_keys is a JSON array of a few keys of the rows that failed. message says why a check is in error; it never holds a secret. run_id is the run that made the evaluation.
        CREATE TABLE {table:check_result} (
            id            {{uuid}} PRIMARY KEY,
            dataset_hash  {{key}} NOT NULL,
            check_id      {{key}} NOT NULL,
            dataset       {{text}} NOT NULL,
            status        {{key}} NOT NULL,
            measured      {{decimal}} NULL,
            threshold     {{decimal}} NULL,
            sample_keys   {{json}} NULL,
            observed      {{decimal}} NULL,
            message       {{text}} NULL,
            run_id        {{key}} NULL,
            evaluated_at  {{time}} NOT NULL
        );
        CREATE INDEX {index:check_result_by_check} ON {table:check_result}(dataset_hash, check_id, evaluated_at);
        CREATE INDEX {index:check_result_by_time} ON {table:check_result}(evaluated_at);

        -- The latest result of each check, and whether the check blocks its dataset's dependents now and since when (blocked_at is null while it does not). severity and on_error are the check's
        -- own, as of its last evaluation. status_changed_at is when the status last changed. A row is kept while the check exists: no retention.
        CREATE TABLE {table:check_status} (
            dataset_hash    {{key}} NOT NULL,
            check_id        {{key}} NOT NULL,
            dataset         {{text}} NOT NULL,
            severity        {{key}} NOT NULL,
            on_error        {{key}} NOT NULL,
            status          {{key}} NOT NULL,
            status_changed_at {{time}} NOT NULL,
            last_result_id  {{uuid}} NOT NULL,
            measured        {{decimal}} NULL,
            threshold       {{decimal}} NULL,
            evaluated_at    {{time}} NOT NULL,
            blocking        {{bool}} NOT NULL,
            blocked_at      {{time}} NULL,
            updated_at      {{time}} NOT NULL,
            PRIMARY KEY (dataset_hash, check_id)
        );
        CREATE INDEX {index:check_status_by_block} ON {table:check_status}(blocking, dataset_hash);
        """,
        """
        -- What a watcher has already told people (plan 23a FQ13): the outcome a check was last alerted at, per evaluator (the server that evaluated it, as the console watches several; empty for the install's own),
        -- so an alert is sent once per failure and once per recovery and a restart of the watcher does not send them again. No retention: it goes with Forget.
        CREATE TABLE {table:check_alert} (
            dataset_hash {{key}} NOT NULL,
            check_id     {{key}} NOT NULL,
            evaluator    {{key}} NOT NULL,
            outcome      {{key}} NOT NULL,
            alerted_at   {{time}} NOT NULL,
            PRIMARY KEY (dataset_hash, check_id, evaluator)
        );
        """,
    ];

    private static StateModule Make(StateScopeKind scope) => new(Name, scope, Migrations, retention: [new RetentionRule("check_result", "evaluated_at", HistoryDays)]);

    /// <summary>The module kept once per install: the console, Build, Sync and DbDataStore on one database.</summary>
    public static StateModule Module { get; } = Make(StateScopeKind.Install);

    /// <summary>The same module kept per group, for the serving layer: the instances of a group share one copy, and two groups on one database each have their own.</summary>
    public static StateModule GroupModule { get; } = Make(StateScopeKind.Group);
}
