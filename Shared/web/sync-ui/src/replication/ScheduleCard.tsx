import type { ReplicationTaskConfig, ScheduleMode, SchedulingConfig } from '../types'

/**
 * When this replication runs, on one line above the Overview's tabs: the mode, its interval or cron expression, the idle timeout of a continuous
 * worker, and how many mappings and bulk loads run at once (two lanes, so a large reload never takes a slot an incremental pass needs). Enabled is
 * not here: it is in the page's chrome, because it is a different kind of setting (whether the replication runs at all).
 * Changing the mode clears the other mode's fields, since Sync reads exactly one of `frequencySeconds` and `cronExpression` for a mode.
 */
export function ScheduleCard({ draft, onChange }: {
  draft: ReplicationTaskConfig
  onChange: (next: ReplicationTaskConfig) => void
}) {
  const setScheduling = (patch: Partial<SchedulingConfig>) =>
    onChange({ ...draft, scheduling: { ...draft.scheduling, ...patch } })

  const setMode = (mode: ScheduleMode) => setScheduling(mode === 'Periodic'
    ? { mode, frequencySeconds: null, idleTimeoutSeconds: null, cronExpression: draft.scheduling.cronExpression ?? '*/15 * * * *' }
    : { mode, frequencySeconds: draft.scheduling.frequencySeconds ?? 60, cronExpression: null })

  const setParallelism = (patch: Partial<Pick<ReplicationTaskConfig['changeProcessing'], 'degreeOfParallelism' | 'bulkLoadDegreeOfParallelism'>>) =>
    onChange({ ...draft, changeProcessing: { ...draft.changeProcessing, ...patch } })

  const mode = draft.scheduling.mode

  return (
    <div className={`card ${draft.enabled ? 'enabled' : 'disabled'}`} data-testid="replication-schedule-card">
      <div className="card-head" style={{ flexWrap: 'wrap', rowGap: 8 }}>
        <span className="card-title">Schedule</span>

        <select
          className="select sm"
          style={{ width: 'auto', flex: 'none' }}
          value={mode}
          onChange={(e) => setMode(e.target.value as ScheduleMode)}
          data-testid="schedule-mode-select"
        >
          <option value="Continuous">Continuous</option>
          <option value="Periodic">Periodic (cron)</option>
        </select>

        {mode === 'Continuous' ? (
          <span className="row" style={{ gap: 6, flex: 'none' }}>
            <span className="hint">every</span>
            <input
              className="input sm mono"
              style={{ width: 56 }}
              type="number"
              min={1}
              value={draft.scheduling.frequencySeconds ?? 60}
              onChange={(e) => setScheduling({ frequencySeconds: Number(e.target.value) })}
              data-testid="schedule-frequency-input"
            />
            <span className="hint">s · idle timeout</span>
            <input
              className="input sm mono"
              style={{ width: 56 }}
              type="number"
              min={1}
              // blank rather than 60 when unset: "nobody said" and "somebody chose 60" are different answers, and only one is written down
              placeholder="60"
              value={draft.scheduling.idleTimeoutSeconds ?? ''}
              onChange={(e) => setScheduling({ idleTimeoutSeconds: e.target.value ? Number(e.target.value) : null })}
              data-testid="schedule-idle-timeout-input"
            />
            <span className="hint">s</span>
          </span>
        ) : (
          <input
            className="input sm mono"
            style={{ width: 200, flex: 'none' }}
            value={draft.scheduling.cronExpression ?? ''}
            onChange={(e) => setScheduling({ cronExpression: e.target.value })}
            placeholder="cron expression"
            data-testid="schedule-cron-input"
          />
        )}

        <span className="row" style={{ gap: 6, flex: 'none' }}>
          <span className="hint">· up to</span>
          <input
            className="input sm mono"
            style={{ width: 44 }}
            type="number"
            min={1}
            value={draft.changeProcessing.degreeOfParallelism}
            onChange={(e) => setParallelism({ degreeOfParallelism: Number(e.target.value) || 1 })}
            data-testid="schedule-degree-of-parallelism-input"
          />
          <span className="hint">mapping(s), and</span>
          <input
            className="input sm mono"
            style={{ width: 44 }}
            type="number"
            min={1}
            value={draft.changeProcessing.bulkLoadDegreeOfParallelism}
            onChange={(e) => setParallelism({ bulkLoadDegreeOfParallelism: Number(e.target.value) || 1 })}
            data-testid="schedule-bulk-load-parallelism-input"
          />
          <span className="hint" title="Bulk Load segments and verifications run on their own lane, so a large reload never takes a slot an incremental pass needs.">bulk load(s), at once</span>
        </span>

        <span
          className="hint spacer"
          style={{ textAlign: 'right' }}
          title={mode === 'Continuous'
            ? 'Continuous mode reads changes on every interval. The worker stays running between intervals and exits once it has gone the idle timeout without finding a changed row.'
            : 'Periodic mode runs on the cron expression.'}
        >
          {mode === 'Continuous' ? 'worker exits idle, restarts on schedule' : 'runs on the cron expression'}
        </span>
      </div>
    </div>
  )
}
