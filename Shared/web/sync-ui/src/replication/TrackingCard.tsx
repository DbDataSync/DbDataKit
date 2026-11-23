import { Field } from '@dbdatakit/shell'
import type { ReplicationTaskConfig, TrackingConfig } from '../types'

/** What the target's state table keeps when `tracking.retention` says nothing (Sync's `TrackingRetention`). */
export const DEFAULT_RETENTION = { entries: 1000, days: 7 }

/** How the retention reads: what is always kept (the newest entries of a mapping and what is newer than the days). */
export function retentionText(entries: number, days: number): string {
  return `The target's table keeps the newest ${entries} ${entries === 1 ? 'entry' : 'entries'} of each mapping and everything newer than ${days} day${days === 1 ? '' : 's'}; the newest entry of each source is never removed.`
}

/** The mode the select shows: `default` for a file without the section. */
export const modeOf = (draft: ReplicationTaskConfig): string => draft.tracking?.mode ?? 'default'

/** The task after choosing a mode: no section for the default, a primary only for both, a retention only where a table is kept on the target. */
export function withMode(draft: ReplicationTaskConfig, mode: string): ReplicationTaskConfig {
  if (mode === 'default') return { ...draft, tracking: null }
  const retention = draft.tracking?.retention ?? null
  if (mode === 'both') return { ...draft, tracking: { mode: 'both', primary: draft.tracking?.primary ?? 'target', retention } }
  return { ...draft, tracking: { mode: mode as 'target' | 'state', primary: null, retention: mode === 'target' ? retention : null } }
}

/** The task after a retention field changed: an empty field is removed, and a retention with neither left goes. */
export function withRetention(draft: ReplicationTaskConfig, key: 'entries' | 'days', text: string): ReplicationTaskConfig {
  if (!draft.tracking) return draft
  const number = text.trim() === '' ? null : Number(text)
  const next = { ...draft.tracking.retention, [key]: number !== null && Number.isFinite(number) ? number : null }
  const empty = next.entries == null && next.days == null
  return { ...draft, tracking: { ...draft.tracking, retention: empty ? null : next } }
}

/**
 * Where the replication keeps its tracking state: `tracking:` of `task.yaml` (absent is the default), the primary when both are kept, and how much
 * of the state table on the target is kept. Whether the target's engine can keep a state table is said by the draft's check, which refuses
 * `target` and `both` on an engine whose writers do not keep one; what applies on a Runner and the state's copy are the Runner's to show.
 */
export function TrackingCard({ draft, onChange, editable = true }: {
  draft: ReplicationTaskConfig
  onChange: (next: ReplicationTaskConfig) => void
  editable?: boolean
}) {
  const tracking: TrackingConfig | null | undefined = draft.tracking
  const keepsOnTarget = tracking?.mode === 'target' || tracking?.mode === 'both'
  const entries = tracking?.retention?.entries ?? DEFAULT_RETENTION.entries
  const days = tracking?.retention?.days ?? DEFAULT_RETENTION.days

  return (
    <div className="card" data-testid="tracking-card">
      <div className="card-head">
        <span className="card-title">Tracking</span>
        <span className="card-note">where the positions and the record of each entry are kept</span>
      </div>
      <fieldset className="card-body" style={{ gap: 12, border: 0, margin: 0 }} disabled={!editable}>
        <div className="row" style={{ gap: 12 }}>
          <Field label="Mode">
            <select className="select sm" value={modeOf(draft)} onChange={(e) => onChange(withMode(draft, e.target.value))} data-testid="tracking-mode-select">
              <option value="default">Default</option>
              <option value="state">State only (the Runner's state)</option>
              <option value="target">Target (a state table on the target)</option>
              <option value="both">Both (target and state)</option>
            </select>
          </Field>
          {tracking?.mode === 'both' && (
            <Field label="Primary">
              <select
                className="select sm"
                value={tracking.primary ?? 'target'}
                onChange={(e) => onChange({ ...draft, tracking: { ...tracking, mode: 'both', primary: e.target.value as 'target' | 'state' } })}
                data-testid="tracking-primary-select"
              >
                <option value="target">Target</option>
                <option value="state">State</option>
              </select>
            </Field>
          )}
        </div>
        {keepsOnTarget && (
          <>
            <div className="row" style={{ gap: 12 }} data-testid="tracking-retention">
              <Field label="Keep the newest entries per mapping">
                <input className="input sm" type="number" min={1} placeholder={String(DEFAULT_RETENTION.entries)} value={tracking?.retention?.entries ?? ''}
                  onChange={(e) => onChange(withRetention(draft, 'entries', e.target.value))} data-testid="tracking-retention-entries" />
              </Field>
              <Field label="and everything newer than (days)">
                <input className="input sm" type="number" min={0} step="any" placeholder={String(DEFAULT_RETENTION.days)} value={tracking?.retention?.days ?? ''}
                  onChange={(e) => onChange(withRetention(draft, 'days', e.target.value))} data-testid="tracking-retention-days" />
              </Field>
            </div>
            <div className="hint" data-testid="tracking-retention-text">{retentionText(entries, days)}</div>
            <div className="hint" data-testid="tracking-target-note">
              The draft's check refuses this mode when the target's engine keeps no state table.
            </div>
          </>
        )}
        {modeOf(draft) === 'default' && (
          <div className="hint" data-testid="tracking-default-note">No tracking section: the replication keeps its state as the Runner's default says.</div>
        )}
      </fieldset>
    </div>
  )
}
