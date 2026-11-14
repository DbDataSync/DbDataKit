using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// What a replication is doing right now, and whether it may: since plan 28c E22 the Runner is Sync's only scheduler, so <see cref="Running"/> is whether the replication's drain is up on the
/// runner (<see cref="Runner"/> has the runner's whole row). This server starts no process, so the process fields of the old worker (<see cref="Pid"/>, <see cref="MemoryBytes"/>,
/// <see cref="CpuMilliseconds"/>, <see cref="StartedAtUtc"/>) are always null; they stay in the shape the screens read until those screens go with the server (plan 33 SC23).
/// </summary>
/// <param name="ShouldRun">
/// Whether anything may be scheduled for this replication — <c>Enabled</c> (config) and not <c>Paused</c> (state), from <see cref="TaskScheduling.ShouldRun"/>. The answer rather than the inputs, so
/// the SPA never re-derives the rule; the inputs come too, because the UI has to say *which* gate is closed and they are different controls with different consequences.
/// </param>
/// <param name="PauseNote">Why it is held, if whoever held it said. Null when not paused, and also when they cleared it — the popup allows both.</param>
/// <param name="Runner">What the runner says of the replication's drain (plan 28c E12g, E22): reachable or not, running, queued, paused, next due, last run.</param>
public sealed record ReplicationStatus(
    bool Running,
    int? Pid = null,
    long? MemoryBytes = null,
    double? CpuMilliseconds = null,
    DateTimeOffset? StartedAtUtc = null,
    bool ShouldRun = true,
    bool Enabled = true,
    bool Paused = false,
    string? PauseNote = null,
    DbDataSync.Api.Runner.RunnerTaskState? Runner = null);
