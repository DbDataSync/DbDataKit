import { Field } from '@dbdatakit/shell'
import type { AfterChangeStrategy, DeleteGuard, ReconcileConfig, ScheduleMode } from '../types'

/** What a replication's `reconcile:` is when the file says nothing (Sync's `ReconcileConfig`): off, no cadence, no after-change trigger, half the scope at most. */
export const DEFAULT_RECONCILE: ReconcileConfig = { enabled: false, every: null, afterChange: { mode: 'none' }, deleteGuard: { mode: 'ratio', maxRatio: 0.5 } }

/** The reconcile settings with Sync's defaults where the file says nothing. */
export const reconcileOf = (value: ReconcileConfig | null | undefined): ReconcileConfig => ({
  ...DEFAULT_RECONCILE, ...value,
  every: value?.every ?? null,
  afterChange: value?.afterChange ?? DEFAULT_RECONCILE.afterChange,
  deleteGuard: value?.deleteGuard ?? DEFAULT_RECONCILE.deleteGuard,
})

/**
 * Delete reconciliation: a sweep for rows the reader's own changes never show deleted (a watermark reader sees none), on a cadence of its own,
 * after changes, or after every N passes, with the guard a sweep runs under. Beside the change pipeline it complements.
 */
export function ReconcileConfigCard({ reconcile, writerKind, onChange, editable = true }: {
  reconcile: ReconcileConfig
  /** The change writer's kind, read only to say which writer a sweep ends with (`KeyReconcileScd2Close` behind `Scd2`, else `KeyReconcileDelete`). */
  writerKind: string
  onChange: (next: ReconcileConfig) => void
  editable?: boolean
}) {
  const every = reconcile.every
  const setEveryMode = (mode: ScheduleMode | 'off') =>
    onChange({
      ...reconcile,
      every: mode === 'off' ? null : mode === 'Continuous'
        ? { mode, frequencySeconds: every?.frequencySeconds ?? 3600, cronExpression: null }
        : { mode, frequencySeconds: null, cronExpression: every?.cronExpression ?? '0 * * * *' },
    })
  const setAfterChange = (strategy: AfterChangeStrategy) => onChange({ ...reconcile, afterChange: strategy })
  const setGuard = (guard: DeleteGuard) => onChange({ ...reconcile, deleteGuard: guard })
  const needsEvery = reconcile.afterChange.mode !== 'none' && every === null

  return (
    <div className={`card ${reconcile.enabled ? 'enabled' : 'disabled'}`} data-testid="reconcile-config-card">
      <div className="card-head">
        <span className="card-title">Delete reconciliation</span>
        <span className="card-note">sweeps for rows the reader's own changes never show deleted</span>
        <button type="button" className={`toggle ${reconcile.enabled ? 'on' : ''}`} disabled={!editable} onClick={() => onChange({ ...reconcile, enabled: !reconcile.enabled })}
          aria-pressed={reconcile.enabled} data-testid="reconcile-enabled-toggle" />
      </div>

      {reconcile.enabled && (
        <fieldset className="card-body" style={{ gap: 12, border: 0, margin: 0 }} disabled={!editable}>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
            <Field label="Cadence">
              <div className="row" style={{ gap: 6 }}>
                <select className="select sm" value={every?.mode ?? 'off'} onChange={(e) => setEveryMode(e.target.value as ScheduleMode | 'off')} data-testid="reconcile-every-mode-select">
                  <option value="off">Off: after a change or on request only</option>
                  <option value="Continuous">Every…</option>
                  <option value="Periodic">Periodic (cron)</option>
                </select>
                {every?.mode === 'Continuous' && (
                  <span className="row" style={{ gap: 4 }}>
                    <input className="input sm mono" style={{ width: 64 }} type="number" min={1} value={every.frequencySeconds ?? 3600}
                      onChange={(e) => onChange({ ...reconcile, every: { ...every, frequencySeconds: Number(e.target.value) } })} data-testid="reconcile-every-frequency-input" />
                    <span className="hint">s</span>
                  </span>
                )}
                {every?.mode === 'Periodic' && (
                  <input className="input sm mono" style={{ width: 140 }} value={every.cronExpression ?? ''} placeholder="cron expression"
                    onChange={(e) => onChange({ ...reconcile, every: { ...every, cronExpression: e.target.value } })} data-testid="reconcile-every-cron-input" />
                )}
              </div>
            </Field>
            <Field label="After a change">
              <select className="select sm" value={reconcile.afterChange.mode} onChange={(e) => setAfterChange(e.target.value === 'any' ? { mode: 'any' } : { mode: 'none' })} data-testid="reconcile-after-change-select">
                <option value="none">Never: on the cadence only</option>
                <option value="any">Any row read since the last sweep</option>
              </select>
            </Field>
          </div>

          <Field label="Sweep after every N passes">
            <div className="row" style={{ gap: 6 }}>
              <input className="input sm mono" style={{ width: 64 }} type="number" min={1} placeholder="off" value={reconcile.afterEveryPasses ?? ''}
                onChange={(e) => onChange({ ...reconcile, afterEveryPasses: e.target.value === '' ? null : Math.max(1, Math.floor(Number(e.target.value))) })}
                data-testid="reconcile-after-every-passes-input" />
              <span className="hint">
                successful change passes since the last sweep; needs no cadence. It bounds how long an old key left by a primary key update stays in the
                target when the reader (<span className="mono">Watermark</span>) cannot see it.
              </span>
            </div>
          </Field>

          {needsEvery && (
            <span className="hint" style={{ color: 'var(--warn)' }} data-testid="reconcile-needs-every-hint">
              An after-change trigger needs a cadence: it never fires more often than the cadence allows.
            </span>
          )}

          <Field label="Guard">
            <div className="row" style={{ gap: 6 }}>
              <select className="select sm" value={reconcile.deleteGuard.mode} onChange={(e) => setGuard(e.target.value === 'none' ? { mode: 'none' } : { mode: 'ratio', maxRatio: 0.5 })} data-testid="reconcile-guard-select">
                <option value="ratio">Refuse over a ratio of the scope</option>
                <option value="none">No guard: always delete what is absent</option>
              </select>
              {reconcile.deleteGuard.mode === 'ratio' && (
                <span className="row" style={{ gap: 4 }}>
                  <input className="input sm mono" style={{ width: 56 }} type="number" min={0} max={100} value={Math.round(reconcile.deleteGuard.maxRatio * 100)}
                    onChange={(e) => setGuard({ mode: 'ratio', maxRatio: Number(e.target.value) / 100 })} data-testid="reconcile-guard-ratio-input" />
                  <span className="hint">% of the scope</span>
                </span>
              )}
            </div>
          </Field>

          <span className="hint" data-testid="reconcile-pair-hint">
            A sweep reads with <span className="mono">KeyReconcile</span> and writes with{' '}
            <span className="mono">{writerKind === 'Scd2' ? 'KeyReconcileScd2Close' : 'KeyReconcileDelete'}</span>; each table mapping's default segmenting scopes
            it, as it does a bulk load.
          </span>
        </fieldset>
      )}
    </div>
  )
}
