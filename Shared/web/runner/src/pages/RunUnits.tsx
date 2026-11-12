import { useQuery } from '@tanstack/react-query'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type RunUnits as RunUnitsAnswer } from '../api'
import { durationText, sortUnits, unitStateText, unitsLine } from '../operator'
import { when } from './format'

export interface RunUnitsViewProps {
  units: RunUnitsAnswer | undefined
  loading: boolean
  error: unknown
}

/** The table of units of a run's attempt chain (plan 28a R25): each unit with its state, rows and duration, the failed ones first with their error. A run that keeps no units says so; a run whose chain is not known any more says why. */
export function RunUnitsView({ units, loading, error }: RunUnitsViewProps) {
  const gone = error instanceof ApiError && error.status === 404
  return (
    <div data-testid="runner-run-units-section">
      <div className="card-title" style={{ margin: '12px 0 4px' }}>Units</div>
      {!gone && <ErrorBanner error={error} />}
      {gone && <div className="hint" data-testid="runner-run-units-gone">The units of this run are not shown: it was retried, so the attempt chain has a newer run.</div>}
      {loading && <div className="hint" data-testid="runner-run-units-loading">Reading the units…</div>}
      {units && <div className="hint" data-testid="runner-run-units-summary">{unitsLine(units.summary)}{units.truncated ? ' (the first 500 are listed)' : ''}</div>}
      {units && units.units.length > 0 && (
        <div className="card flush" style={{ maxHeight: 240, overflow: 'auto' }} data-testid="runner-run-units">
          <div className="grid-head" style={{ gridTemplateColumns: '1.2fr 1.4fr 1fr 0.8fr 0.8fr 1.4fr', gap: 10 }}><span>Unit</span><span>Key</span><span>State</span><span>Rows</span><span>Took</span><span>Started</span></div>
          {sortUnits(units.units).map((u) => (
            <div key={`${u.name}/${u.key}`} className="grid-row" style={{ gridTemplateColumns: '1.2fr 1.4fr 1fr 0.8fr 0.8fr 1.4fr', gap: 10 }} data-testid="runner-run-unit" data-state={u.state}>
              <span className="mono">{u.name}</span><span className="mono">{u.key}</span>
              <span className={u.state === 'failed' ? 'field-error' : undefined}>{unitStateText(u.state)}</span>
              <span>{u.rows ?? ''}</span><span>{durationText(u.duration_ms)}</span><span>{when(u.started)}</span>
              {u.error && <span className="field-error" style={{ gridColumn: '1 / -1' }} data-testid="runner-run-unit-error">{u.error}</span>}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

/** A run's units from the runner; read again every few seconds while the run is going. */
export function RunUnits({ runId, live }: { runId: string; live: boolean }) {
  const units = useQuery({ queryKey: ['runner', 'run-units', runId], queryFn: () => runnerApi.runUnits(runId), refetchInterval: live ? 3000 : false, retry: false })
  return <RunUnitsView units={units.data} loading={units.isLoading} error={units.error} />
}
