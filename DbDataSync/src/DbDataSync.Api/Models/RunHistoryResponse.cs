using DbDataSync.State;

namespace DbDataSync.Api.Models;

/// <summary>
/// The run-history endpoint's shape since phase 104 added paging — a page of runs, and where the next
/// one starts. <see cref="NextCursor"/> is null once the page reaches the end of the history, and
/// never a cursor that would loop back to the first page.
/// <para>
/// <see cref="RunnerUnavailable"/> (plan 28c E13) is set when the replication is the runner's and the runner could not be asked: the runs listed are this server's own, and the
/// ones the runner holds are missing for the reason it gives, which the screen says instead of showing a history that looks complete.
/// </para>
/// </summary>
public sealed record RunHistoryResponse(IReadOnlyList<TaskRunRecord> Runs, string? NextCursor, string? RunnerUnavailable = null);
