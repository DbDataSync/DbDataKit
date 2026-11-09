import { useState } from 'react'
import { ErrorBanner, Field, useIsAdmin } from '@dbdatakit/shell'
import { useReplicationOwner, useRunnerLink, useSaveRunnerLink, useSetReplicationOwner } from '../../api/hooks'
import type { OwnerView, ReplicationOwner, RunnerLink } from '../../api/types'
import { OWNER_TEXT, movesFrom } from './runnerOwner'

/** The runner's own row of the replication's task, or what is wrong with asking for it. */
export function RunnerRow({ view }: { view: OwnerView }) {
  if (view.owner === 'old') return <div className="hint" data-testid="runner-row-none">The runner has no part in this replication yet.</div>
  if (view.runnerError) return <div className="hint" data-testid="runner-row-error">The runner could not be asked: {view.runnerError}</div>
  const row = view.runner
  if (!row) return <div className="hint" data-testid="runner-row-missing">The runner lists no task for this replication: publish the replication set to it first.</div>
  return (
    <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '4px 16px', margin: 0 }} data-testid="runner-row">
      <dt>Task</dt><dd className="mono">{row.task}</dd>
      <dt>Runner says owner</dt><dd data-testid="runner-row-owner">{row.owner}</dd>
      <dt>Next due</dt><dd>{row.nextDue ? new Date(row.nextDue).toLocaleString() : 'not scheduled'}</dd>
      <dt>Running / queued</dt><dd data-testid="runner-row-load">{row.running} / {row.queued}</dd>
      <dt>Paused</dt><dd data-testid="runner-row-paused">{row.paused ? `yes${row.pausedBy ? `, by ${row.pausedBy}` : ''}` : 'no'}</dd>
      <dt>Last run</dt>
      <dd data-testid="runner-row-last">{row.lastRun ? `${row.lastRun.outcome}, started ${new Date(row.lastRun.started).toLocaleString()}${row.lastRun.rows != null ? `, ${row.lastRun.rows} rows` : ''}` : 'none yet'}</dd>
    </dl>
  )
}

/**
 * Who schedules the replication, and the runner it can move to (plan 28c E12; the editor of plan 33's UE for this setting). A move is observe (the runner records what it would have done), cutover (the runner's drain takes over after the
 * worker here has finished and the positions are handed over) or rollback (the runner lets go and this server schedules again). A refusal is shown in the server's words and the owner is as it was. The link is the runner the replications
 * are published to (`dbdatasync package publish --to runner` puts them there).
 */
export function ReplicationRunnerPanel({ replicationName }: { replicationName: string }) {
  const isAdmin = useIsAdmin()
  const owner = useReplicationOwner(replicationName)
  const link = useRunnerLink()
  const saveLink = useSaveRunnerLink()
  const move = useSetReplicationOwner(replicationName)
  // null is "what the server has"; a link is an unsaved edit of it
  const [edit, setEdit] = useState<RunnerLink | null>(null)
  const draft: RunnerLink = edit ?? (link.data ? { ...link.data, clientDirectory: link.data.clientDirectory ?? '' } : { server: '', channel: '', package: '', clientDirectory: '' })
  const setDraft = setEdit
  const [wait, setWait] = useState('')
  const [done, setDone] = useState<string | null>(null)

  const view = owner.data
  const current: ReplicationOwner = view?.owner ?? 'old'
  const allowed = movesFrom(current)
  const linked = !!link.data
  const reason = !isAdmin ? 'Moving a replication needs the admin role.' : !linked ? 'Set the runner first.' : move.isPending ? 'A move is in progress.' : undefined
  const complete = draft.server.trim() !== '' && draft.channel.trim() !== '' && draft.package.trim() !== ''

  const go = async (to: ReplicationOwner) => {
    setDone(null)
    const seconds = wait.trim() === '' ? undefined : Number(wait)
    try {
      const change = await move.mutateAsync({ owner: to, waitSeconds: Number.isFinite(seconds) ? seconds : undefined })
      setDone(`${change.from} to ${change.to}${change.positionsHandedOver > 0 ? `; ${change.positionsHandedOver} position(s) handed over` : ''}${change.waited ? '; waited for work in flight' : ''}.`)
    } catch { /* shown below, in the server's words */ }
  }

  return (
    <div className="pane" data-testid="replication-runner">
      <div className="card">
        <div className="card-head tight">
          <span className="card-title sm">Who runs this replication</span>
          <span className="badge spacer" data-testid="runner-owner" data-owner={current}>{current}</span>
        </div>
        <p className="hint" data-testid="runner-owner-text">{OWNER_TEXT[current]}</p>
        {view && <RunnerRow view={view} />}
        {owner.error && <ErrorBanner error={owner.error} />}
        <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 12 }}>
          <button type="button" className="btn btn-sm" disabled={!!reason || !allowed.observe} title={reason} onClick={() => go('observe')} data-testid="runner-observe">Observe on the runner</button>
          <button type="button" className="btn btn-sm btn-primary" disabled={!!reason || !allowed.cutover} title={reason} onClick={() => go('runner')} data-testid="runner-cutover">Cut over to the runner</button>
          <button type="button" className="btn btn-sm" disabled={!!reason || !allowed.rollback} title={reason} onClick={() => go('old')} data-testid="runner-rollback">Roll back to this server</button>
          <Field label="Wait for work in flight (seconds)">
            <input className="input" style={{ width: 90 }} inputMode="numeric" value={wait} placeholder="1800" onChange={(e) => setWait(e.target.value)} data-testid="runner-wait" />
          </Field>
        </div>
        {done && <div className="hint" data-testid="runner-done">{done}</div>}
        {move.error && <ErrorBanner error={move.error} />}
        <p className="hint" style={{ marginTop: 12 }}>
          A cutover waits for the worker here and its queue, hands the mappings' positions to the runner's first drain, and then lets the runner schedule. A rollback waits for the runner's drain, copies its pause back and lets this server
          schedule again; the positions the drain advanced on the runner are not copied back, so the next worker here reads again from the positions of the cutover.
        </p>
      </div>

      <div className="card">
        <div className="card-head tight"><span className="card-title sm">The runner</span></div>
        <p className="hint">The paired runner the replications are published to, the channel that serves them and the package they are in.</p>
        <div style={{ display: 'grid', gap: 8, gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))' }}>
          <Field label="Server (name or address)"><input className="input" value={draft.server} onChange={(e) => setDraft({ ...draft, server: e.target.value })} data-testid="runner-link-server" /></Field>
          <Field label="Channel"><input className="input" value={draft.channel} onChange={(e) => setDraft({ ...draft, channel: e.target.value })} data-testid="runner-link-channel" /></Field>
          <Field label="Package"><input className="input" value={draft.package} onChange={(e) => setDraft({ ...draft, package: e.target.value })} data-testid="runner-link-package" /></Field>
          <Field label="Client directory (optional)"><input className="input" value={draft.clientDirectory ?? ''} onChange={(e) => setDraft({ ...draft, clientDirectory: e.target.value })} data-testid="runner-link-client-dir" /></Field>
        </div>
        <div style={{ marginTop: 12 }}>
          <button type="button" className="btn btn-sm" disabled={!isAdmin || !complete || saveLink.isPending} title={!isAdmin ? 'Setting the runner needs the admin role.' : !complete ? 'The server, channel and package are all needed.' : undefined}
            onClick={() => saveLink.mutate({ ...draft, clientDirectory: draft.clientDirectory?.trim() ? draft.clientDirectory : null }, { onSuccess: () => setEdit(null) })} data-testid="runner-link-save">
            Save the runner
          </button>
        </div>
        {saveLink.error && <ErrorBanner error={saveLink.error} />}
      </div>
    </div>
  )
}
