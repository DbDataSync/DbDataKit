import { Field, useIsAdmin } from '@dbdatakit/shell'
import { useReplicationTracking, useResolveTrackingDivergence, useTrackingCatchUp } from '../../api/hooks'
import type { ReplicationTaskConfig, ReplicationTrackingStatus, TrackingCopyStatus } from '../../api/types'

/** What the card says about the mode that applies: one sentence, the same wording whichever side asks. */
export function trackingSummary(status: ReplicationTrackingStatus): string {
  const applies = status.effectiveMode === 'both' ? `both, the ${status.effectivePrimary} primary` : status.effectiveMode
  return status.defaulted ? `${applies} (the default)` : applies
}

/** The card's body without the query: what was asked, what applies and why. Split from `TrackingCard` so it renders on its own. */
export function TrackingStatusView({ status }: { status: ReplicationTrackingStatus }) {
  const asked = status.requestedMode === 'both' ? `both, the ${status.requestedPrimary} primary` : status.requestedMode
  const differs = status.requestedMode !== status.effectiveMode
  return (
    <div className="card-body" style={{ gap: 8 }} data-testid="tracking-status">
      <div className="row" style={{ gap: 8 }}>
        <span className="hint">Applies</span>
        <span className="mono" data-testid="tracking-effective">{trackingSummary(status)}</span>
        {differs && (
          <>
            <span className="hint">asked for</span>
            <span className="mono" data-testid="tracking-requested">{asked}</span>
          </>
        )}
      </div>
      <div className={differs ? 'banner warn' : 'hint'} data-testid="tracking-reason">{status.reason}</div>
      {status.targetDriver && (
        <div className="hint" data-testid="tracking-target">
          Target: {status.targetDriver}
          {status.targetWritersBuilt
            ? ` keeps a state table (dbdatasync.position_log) written in each entry's transaction when the mode is target or both.`
            : status.targetCanHoldState
              ? ' keeps its state on the runner only (mode: state): no writer keeps the position log on MySQL, MariaDB or Oracle.'
              : ' cannot hold a state table, so its state stays on the runner.'}
        </div>
      )}
    </div>
  )
}

/** How the retention reads: what is always kept (the newest entries of a mapping and what is newer than the days). */
export function retentionText(entries: number, days: number): string {
  return `The target's table keeps the newest ${entries} ${entries === 1 ? 'entry' : 'entries'} of each mapping and everything newer than ${days} day${days === 1 ? '' : 's'}; the newest entry of each source is never removed.`
}

/**
 * The state's copy of the entries the target holds (plan 28c E17): how many rows, whether the follower has everything, what went wrong, and an open divergence with the two ways to resolve it.
 * Split from the card so it renders without the query; the buttons are given and shown disabled for anyone but an administrator.
 */
export function TrackingCopyView({ copy, canResolve, busy, onResolve, onCatchUp }: {
  copy: TrackingCopyStatus
  canResolve: boolean
  busy?: boolean
  onResolve: (id: number, resolution: 'archive' | 'reset') => void
  onCatchUp: () => void
}) {
  return (
    <div className="card-body" style={{ gap: 8 }} data-testid="tracking-copy">
      <div className="row" style={{ gap: 8 }}>
        <span className="hint">The state's copy</span>
        <span data-testid="tracking-copy-rows">{`${copy.rows} ${copy.rows === 1 ? 'entry' : 'entries'}`}</span>
        <span className="hint" data-testid="tracking-copy-state">
          {copy.error ? 'the last follow failed' : copy.divergenceId !== null ? 'diverged, not copying' : copy.caughtUp ? 'caught up' : 'catching up'}
        </span>
        <button type="button" className="btn btn-sm" disabled={!canResolve || busy} onClick={onCatchUp} data-testid="tracking-catch-up" title={canResolve ? undefined : 'Following the target now needs the admin role.'}>
          Follow now
        </button>
      </div>
      {copy.error && <div className="banner warn" role="alert" data-testid="tracking-copy-error">{copy.error}</div>}
      {copy.divergenceId !== null && (
        <div className="banner warn" data-testid="tracking-divergence">
          <div data-testid="tracking-divergence-reason">{copy.divergenceReason}</div>
          <div className="row" style={{ gap: 8, marginTop: 6 }}>
            <button type="button" className="btn btn-sm" disabled={!canResolve || busy} onClick={() => onResolve(copy.divergenceId!, 'archive')} data-testid="tracking-resolve-archive"
              title="The copy keeps its later rows apart and copies the target's entries again from the point where the sides part.">
              Archive later rows
            </button>
            <button type="button" className="btn btn-sm" disabled={!canResolve || busy} onClick={() => onResolve(copy.divergenceId!, 'reset')} data-testid="tracking-resolve-reset"
              title="The copy deletes its later rows and copies the target's entries again from the point where the sides part.">
              Reset the copy
            </button>
            {!canResolve && <span className="hint" data-testid="tracking-admin-only">An administrator resolves it.</span>}
          </div>
        </div>
      )}
      {copy.divergenceId === null && copy.divergences > 0 && <div className="hint" data-testid="tracking-resolved-before">{`${copy.divergences} earlier divergence${copy.divergences === 1 ? '' : 's'} resolved.`}</div>}
    </div>
  )
}

/**
 * Where this replication keeps its tracking state (plan 28c E17): the mode in the file's `tracking:` section (absent is the default), the mode that applies and the reason when they differ.
 * `target` and `both` are offered when a writer keeps the state on this replication's target (SQL Server, PostgreSQL); elsewhere they are listed, disabled.
 */
export function TrackingCard({ replicationName, draft, onChange }: {
  replicationName: string
  draft: ReplicationTaskConfig
  onChange: (next: ReplicationTaskConfig) => void
}) {
  const { data: status, error } = useReplicationTracking(replicationName)
  const isAdmin = useIsAdmin()
  const resolve = useResolveTrackingDivergence(replicationName)
  const catchUp = useTrackingCatchUp(replicationName)
  const writersBuilt = status?.targetWritersBuilt ?? false
  const value = draft.tracking?.mode ?? 'default'
  const keepsOnTarget = draft.tracking?.mode === 'target' || draft.tracking?.mode === 'both'

  const setRetention = (key: 'entries' | 'days', text: string) => {
    if (!draft.tracking) return
    const number = text.trim() === '' ? null : Number(text)
    const next = { ...(draft.tracking.retention ?? {}), [key]: number !== null && Number.isFinite(number) ? number : null }
    const empty = next.entries == null && next.days == null
    onChange({ ...draft, tracking: { ...draft.tracking, retention: empty ? null : next } })
  }

  const setMode = (mode: string) => {
    if (mode === 'default') onChange({ ...draft, tracking: null })
    else if (mode === 'both') onChange({ ...draft, tracking: { mode: 'both', primary: draft.tracking?.primary ?? 'target', retention: draft.tracking?.retention ?? null } })
    else onChange({ ...draft, tracking: { mode: mode as 'target' | 'state', primary: null, retention: mode === 'target' ? draft.tracking?.retention ?? null : null } })
  }

  return (
    <div className="card" data-testid="tracking-card">
      <div className="card-head">
        <span className="card-title">Tracking</span>
        <span className="card-note">where the positions and the record of each entry are kept</span>
      </div>
      <div className="card-body" style={{ gap: 12 }}>
        <div className="row" style={{ gap: 12 }}>
          <Field label="Mode">
            <select className="select sm" value={value} onChange={(e) => setMode(e.target.value)} data-testid="tracking-mode-select">
              <option value="default">Default</option>
              <option value="state">State only (the runner's state)</option>
              <option value="target" disabled={!writersBuilt}>Target (a state table on the target)</option>
              <option value="both" disabled={!writersBuilt}>Both (target and state)</option>
            </select>
          </Field>
          {draft.tracking?.mode === 'both' && (
            <Field label="Primary">
              <select
                className="select sm"
                value={draft.tracking.primary ?? 'target'}
                onChange={(e) => onChange({ ...draft, tracking: { mode: 'both', primary: e.target.value as 'target' | 'state' } })}
                data-testid="tracking-primary-select"
              >
                <option value="target">Target</option>
                <option value="state">State</option>
              </select>
            </Field>
          )}
        </div>
        {keepsOnTarget && (
          <div className="row" style={{ gap: 12 }} data-testid="tracking-retention">
            <Field label="Keep the newest entries per mapping">
              <input className="input sm" type="number" min={1} placeholder="1000" value={draft.tracking?.retention?.entries ?? ''} onChange={(e) => setRetention('entries', e.target.value)} data-testid="tracking-retention-entries" />
            </Field>
            <Field label="and everything newer than (days)">
              <input className="input sm" type="number" min={0} step="any" placeholder="7" value={draft.tracking?.retention?.days ?? ''} onChange={(e) => setRetention('days', e.target.value)} data-testid="tracking-retention-days" />
            </Field>
          </div>
        )}
        {status && keepsOnTarget && <div className="hint" data-testid="tracking-retention-text">{retentionText(status.retentionEntries, status.retentionDays)}</div>}
        {error && <div className="banner warn" role="alert" data-testid="tracking-error">The tracking status could not be read: {String((error as Error).message ?? error)}</div>}
      </div>
      {status && <TrackingStatusView status={status} />}
      {status?.copy && (
        <TrackingCopyView copy={status.copy} canResolve={isAdmin} busy={resolve.isPending || catchUp.isPending}
          onResolve={(id, resolution) => resolve.mutate({ id, resolution })} onCatchUp={() => catchUp.mutate()} />
      )}
      {(resolve.error || catchUp.error) && <div className="banner warn" role="alert" data-testid="tracking-action-error">{String(((resolve.error ?? catchUp.error) as Error).message ?? resolve.error ?? catchUp.error)}</div>}
    </div>
  )
}
