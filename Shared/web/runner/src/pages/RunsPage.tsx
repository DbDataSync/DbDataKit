import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { OUTCOMES, runnerApi, type RunFilters, type RunRecord } from '../api'
import { isBad, outcomeText } from '../model'
import { RunnerShell } from '../RunnerShell'
import { took, when } from './format'
import { RunLog } from './RunLog'
import { RunUnits } from './RunUnits'

function RunDetail({ run, onClose }: { run: RunRecord; onClose: () => void }) {
  const rows: [string, string][] = [
    ['Run', run.id], ['Package', `${run.package}${run.package_version === null ? '' : ` v${run.package_version}`}`], ['Channel', run.channel], ['Task', run.task], ['Attempt', String(run.attempt)],
    ['Outcome', outcomeText(run.outcome)], ['Started', when(run.started)], ['Finished', when(run.finished)], ['Took', took(run.started, run.finished)],
    ['Items processed', run.rows === null ? '' : String(run.rows)], ['First failed item', run.failed_item ?? ''],
  ]
  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Run detail" data-testid="runner-run-detail">
        <div className="card-head"><span className="card-title">Run of {run.task}</span></div>
        <div className="card-body">
          {rows.filter(([, value]) => value !== '').map(([name, value]) => (
            <div key={name} className="row" style={{ gap: 12 }}><span style={{ minWidth: 130 }} className="hint">{name}</span><span className="mono" data-testid={`runner-run-${name.toLowerCase().replace(/ /g, '-')}`}>{value}</span></div>
          ))}
          {run.failure && <div className="banner error" data-testid="runner-run-failure"><span>{run.failure.kind}: {run.failure.message}</span></div>}
          <RunUnits runId={run.id} live={run.outcome.toLowerCase() === 'running'} />
          <RunLog runId={run.id} live={run.outcome.toLowerCase() === 'running'} />
          <div className="row" style={{ justifyContent: 'flex-end' }}><button type="button" className="btn" onClick={onClose} data-testid="runner-run-close">Close</button></div>
        </div>
      </div>
    </div>
  )
}

/** The run history, newest first, with filters for the package, the channel and the outcome, and a run's detail. */
export function RunsPage() {
  const [filters, setFilters] = useState<RunFilters>({})
  const [open, setOpen] = useState<RunRecord | null>(null)
  const runs = useQuery({ queryKey: ['runner', 'runs', filters], queryFn: () => runnerApi.runs(filters), refetchInterval: 5000 })
  const status = useQuery({ queryKey: ['runner', 'status'], queryFn: runnerApi.status })
  const packages = [...new Set((status.data?.tasks ?? []).map((t) => t.package))].sort()
  const set = (key: keyof RunFilters) => (e: { target: { value: string } }) => setFilters((f) => ({ ...f, [key]: e.target.value || undefined }))

  return (
    <RunnerShell crumbs={[{ label: 'Runs', heading: true }]}>
      <div className="pane" data-testid="runner-runs-page">
        <div className="page-head">
          <h1 className="page-title">Runs</h1>
          <span className="row" style={{ gap: 8 }}>
            <select className="input" value={filters.package ?? ''} onChange={set('package')} data-testid="runner-filter-package" aria-label="Package">
              <option value="">Every package</option>
              {packages.map((p) => <option key={p} value={p}>{p}</option>)}
            </select>
            <select className="input" value={filters.channel ?? ''} onChange={set('channel')} data-testid="runner-filter-channel" aria-label="Channel">
              <option value="">Every channel</option>
              {(status.data?.channels ?? []).map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
            <select className="input" value={filters.outcome ?? ''} onChange={set('outcome')} data-testid="runner-filter-outcome" aria-label="Outcome">
              <option value="">Every outcome</option>
              {OUTCOMES.map((o) => <option key={o} value={o}>{outcomeText(o)}</option>)}
            </select>
          </span>
        </div>
        <ErrorBanner error={runs.error} />
        {runs.isSuccess && runs.data.length === 0 && <div className="empty" data-testid="runner-no-runs">No run matches.</div>}
        {(runs.data?.length ?? 0) > 0 && (
          <div className="card flush" data-testid="runner-runs">
            <div className="grid-head" style={{ gridTemplateColumns: '1.4fr 0.6fr 1fr 0.5fr 1.4fr 1.5fr 0.8fr', gap: 14 }}>
              <span>Package</span><span>Channel</span><span>Task</span><span>Attempt</span><span>Outcome</span><span>Started</span><span>Took</span>
            </div>
            {runs.data!.map((run) => (
              <button key={run.id} type="button" className="grid-row" style={{ gridTemplateColumns: '1.4fr 0.6fr 1fr 0.5fr 1.4fr 1.5fr 0.8fr', gap: 14, textAlign: 'left', background: 'none', border: 0, width: '100%', cursor: 'pointer' }}
                onClick={() => setOpen(run)} data-testid="runner-run" data-outcome={run.outcome}>
                <span className="mono">{run.package}{run.package_version === null ? '' : ` v${run.package_version}`}</span>
                <span>{run.channel}</span>
                <span className="mono">{run.task}</span>
                <span>{run.attempt}</span>
                <span className={isBad(run.outcome) ? 'field-error' : undefined}>{outcomeText(run.outcome)}</span>
                <span>{when(run.started)}</span>
                <span>{took(run.started, run.finished)}</span>
              </button>
            ))}
          </div>
        )}
        {open && <RunDetail run={open} onClose={() => setOpen(null)} />}
      </div>
    </RunnerShell>
  )
}
