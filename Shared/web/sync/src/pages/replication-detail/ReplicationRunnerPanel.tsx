import { ErrorBanner } from '@dbdatakit/shell'
import { useReplicationRunner } from '../../api/hooks'
import type { ReplicationRunnerView } from '../../api/types'
import { RunnerLinkCard } from '../RunnerLinkCard'

/** What the Runner tab says of who runs a replication: since plan 28c E22 the Runner is Sync's only scheduler, so there is nothing to move. */
export const RUNNER_TEXT = 'The runner schedules this replication and runs it as a drain. This server starts no process of its own; Run now, reloads, reconciles, verifications, resyncs, pause and resume go to the runner, and a replication runs only once its replication set is published to the runner.'

/** The runner's own row of the replication's task, or what is wrong with asking for it. */
export function RunnerRow({ view }: { view: ReplicationRunnerView }) {
  if (view.runnerError) return <div className="hint" data-testid="runner-row-error">The runner could not be asked: {view.runnerError}</div>
  const row = view.runner
  if (!row) return <div className="hint" data-testid="runner-row-missing">The runner lists no task for this replication: publish the replication set to it first.</div>
  return (
    <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '4px 16px', margin: 0 }} data-testid="runner-row">
      <dt>Task</dt><dd className="mono">{row.task}</dd>
      <dt>Next due</dt><dd>{row.nextDue ? new Date(row.nextDue).toLocaleString() : 'not scheduled'}</dd>
      <dt>Running / queued</dt><dd data-testid="runner-row-load">{row.running} / {row.queued}</dd>
      <dt>Paused</dt><dd data-testid="runner-row-paused">{row.paused ? `yes${row.pausedBy ? `, by ${row.pausedBy}` : ''}` : 'no'}</dd>
      <dt>Last run</dt>
      <dd data-testid="runner-row-last">{row.lastRun ? `${row.lastRun.outcome}, started ${new Date(row.lastRun.started).toLocaleString()}${row.lastRun.rows != null ? `, ${row.lastRun.rows} rows` : ''}` : 'none yet'}</dd>
    </dl>
  )
}

/**
 * The runner a replication runs on (plan 28c E12; E22: the only scheduler): its row of the replication's task, and the link the replications are published to (`dbdatasync package publish --to runner`
 * puts them there). The owner moves (observe, cutover, rollback) went with the Sync server's own scheduler.
 */
export function ReplicationRunnerPanel({ replicationName }: { replicationName: string }) {
  const runner = useReplicationRunner(replicationName)
  return (
    <div className="pane" data-testid="replication-runner">
      <div className="card">
        <div className="card-head tight">
          <span className="card-title sm">Who runs this replication</span>
          <span className="badge spacer" data-testid="runner-owner">runner</span>
        </div>
        <p className="hint" data-testid="runner-owner-text">{RUNNER_TEXT}</p>
        {runner.data && <RunnerRow view={runner.data} />}
        {runner.error && <ErrorBanner error={runner.error} />}
      </div>

      <RunnerLinkCard />
    </div>
  )
}
