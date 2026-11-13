import type { RunnerTaskState } from '../../api/types'

/**
 * What the runner says of a replication it owns (plan 28c E12g), under the figures of this server's own worker.
 *
 * It is a row of its own and the card's state word is untouched: `running` is this server's worker, which is
 * always false for a replication the runner owns (Sync starts none), so the card cannot tell "idle" from
 * "up on the runner". Nothing is drawn for a replication this server runs itself (`runner` absent).
 * A runner that could not be asked is said as that, never as "nothing is running there".
 */
export function RunnerStatusRow({ runner }: { runner: RunnerTaskState | null | undefined }) {
  if (!runner) return null

  if (!runner.reachable) {
    return (
      <div className="banner warn" role="status" data-testid="runner-status-unreachable">
        <strong>The runner could not be asked.</strong> What it is doing is not known{runner.error ? `: ${runner.error}` : '.'}
      </div>
    )
  }

  const state = runner.running ? 'a drain is running' : runner.queued > 0 ? 'a drain is queued' : 'no drain is up'
  return (
    <div data-testid="runner-status-row" style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 10 }}>
      <span style={{ font: '500 10.5px var(--ui)', color: 'var(--ink-4)', textTransform: 'uppercase', letterSpacing: '.04em' }}>
        On the runner
      </span>
      <span data-testid="runner-status-state">
        {state}
        {runner.running && runner.runningCount > 1 ? ` (${runner.runningCount})` : ''}
        {runner.queued > 0 && runner.running ? `, ${runner.queued} queued` : ''}
        {runner.paused ? `; paused${runner.pausedBy ? ` by ${runner.pausedBy}` : ''}` : ''}
      </span>
      {runner.lastRun && (
        <span className="hint" data-testid="runner-status-last-run">
          Last run {runner.lastRun.outcome}{runner.lastRun.rows != null ? `, ${runner.lastRun.rows} rows` : ''}.
        </span>
      )}
      {runner.nextDueUtc && !runner.paused && (
        <span className="hint" data-testid="runner-status-next-due">Next due {new Date(runner.nextDueUtc).toLocaleString()}.</span>
      )}
    </div>
  )
}
