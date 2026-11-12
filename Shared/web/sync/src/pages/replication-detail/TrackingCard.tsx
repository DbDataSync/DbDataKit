import { Field } from '@dbdatakit/shell'
import { useReplicationTracking } from '../../api/hooks'
import type { ReplicationTaskConfig, ReplicationTrackingStatus } from '../../api/types'

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
          {status.targetCanHoldState ? ' can hold a state table once its writer keeps one.' : ' cannot hold a state table, so its state stays on the runner.'}
        </div>
      )}
    </div>
  )
}

/**
 * Where this replication keeps its tracking state (plan 28c E17): the mode in the file's `tracking:` section (absent is the default), the mode that applies and the reason when they differ.
 * Only `state` can be chosen while no writer keeps the state on the target; `target` and `both` are listed, disabled, so a person sees what is coming and why it is not offered.
 */
export function TrackingCard({ replicationName, draft, onChange }: {
  replicationName: string
  draft: ReplicationTaskConfig
  onChange: (next: ReplicationTaskConfig) => void
}) {
  const { data: status, error } = useReplicationTracking(replicationName)
  const writersBuilt = status?.targetWritersBuilt ?? false
  const value = draft.tracking?.mode ?? 'default'

  const setMode = (mode: string) => {
    if (mode === 'default') onChange({ ...draft, tracking: null })
    else if (mode === 'both') onChange({ ...draft, tracking: { mode: 'both', primary: draft.tracking?.primary ?? 'target' } })
    else onChange({ ...draft, tracking: { mode: mode as 'target' | 'state', primary: null } })
  }

  return (
    <div className="card" data-testid="tracking-card">
      <div className="card-head">
        <span className="card-title">Tracking</span>
        <span className="card-note">where the positions and the record of each unit are kept</span>
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
        {error && <div className="banner warn" role="alert" data-testid="tracking-error">The tracking status could not be read: {String((error as Error).message ?? error)}</div>}
      </div>
      {status && <TrackingStatusView status={status} />}
    </div>
  )
}
