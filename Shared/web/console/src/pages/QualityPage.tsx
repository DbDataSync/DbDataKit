import { useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'
import { alertsLine, blockLine, datasetState, measureLine, qualityApi, releaseBlockedReason, runBlockedReason, sortDatasets, stateWord, type QualityCheck, type QualityDataset, type QualityRunAnswer, type QualityServer } from '../quality'

/**
 * The data quality of the suite's datasets (plan 23a FQ17), from the servers that evaluate checks (Sync's replications, Build's models) joined by dataset identity: what is blocked, what is failing, and for each check its latest result,
 * the measure and the history with the keys of the rows that failed. A person with the admin role can evaluate a dataset's checks on a server that does it on request (Sync); Build evaluates after a refresh and says so. A server that
 * did not answer is named, not hidden. The console holds none of this: it is read from the servers and evaluated by them.
 */
export function QualityPage() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const [params, setParams] = useSearchParams()
  const quality = useQuery({ queryKey: ['quality'], queryFn: qualityApi.overview })
  const alerts = useQuery({ queryKey: ['quality', 'alerts'], queryFn: qualityApi.alerts })
  const [open, setOpen] = useState<string | null>(null)
  const [running, setRunning] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<{ ok: boolean; text: string } | null>(null)
  const data = quality.data
  const datasets = useMemo(() => (data ? sortDatasets(data.datasets) : []), [data])
  const selected = datasets.find((d) => d.dataset === params.get('dataset')) ?? null

  const release = async (serverId: string, dataset: string, check: string) => {
    setOutcome(null)
    try {
      await qualityApi.release(serverId, dataset, check, data?.servers.find((x) => x.server_id === serverId)?.product ?? null)
      setOutcome({ ok: true, text: `The block of ${check} is released: it still fails, and what depends on the dataset is no longer held for it. It ends when the check passes.` })
      await client.invalidateQueries({ queryKey: ['quality'] })
    } catch (e) {
      setOutcome({ ok: false, text: e instanceof Error ? e.message : String(e) })
    }
  }
  const choose = (name: string) => { setParams({ dataset: name }); setOpen(null); setOutcome(null) }
  const run = async (serverId: string, serverName: string, dataset: string) => {
    setRunning(serverId)
    setOutcome(null)
    try {
      const answer: QualityRunAnswer = await qualityApi.run(serverId, dataset, data?.servers.find((x) => x.server_id === serverId)?.product ?? null)
      setOutcome({ ok: true, text: `${serverName} evaluated ${answer.evaluated} check${answer.evaluated === 1 ? '' : 's'}: ${answer.passed} passed, ${answer.failed} failed, ${answer.errors} could not run.${answer.notes.length > 0 ? ' ' + answer.notes.join(' ') : ''}` })
      await client.invalidateQueries({ queryKey: ['quality'] })
    } catch (e) {
      // the server's own words: a refusal for a role, an unknown dataset
      setOutcome({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally { setRunning(null) }
  }

  return (
    <ConsoleShell crumbs={[{ label: 'Quality', heading: true }]}>
      <div className="pane" data-testid="quality-page">
        <div className="page-head">
          <h1 className="page-title">Data quality</h1>
          <span className="page-note">The checks stored with your replications and models: what is blocked, what is failing, and what each check last found.</span>
        </div>
        <ErrorBanner error={quality.error} />
        {alerts.data && (
          <div className="card" style={{ marginBottom: 14 }} data-testid="quality-alerts" data-configured={alerts.data.configured && alerts.data.channelKnown && !alerts.data.problem}>
            <div className="card-body">{alertsLine(alerts.data)}</div>
          </div>
        )}
        {quality.isLoading && <div className="hint" data-testid="quality-loading">Asking the servers…</div>}
        {data && (
          <div className="card" style={{ marginBottom: 14 }} data-testid="quality-servers">
            <div className="card-body">
              {data.servers.length === 0 && <span data-testid="quality-no-servers">No paired server evaluates checks yet (Build and Sync servers do).</span>}
              {data.servers.map((s) => (
                <div key={s.server_id} data-testid="quality-server" data-server={s.server}>
                  <strong>{s.server}</strong>{' '}
                  <span className="hint">{s.error ? `did not give its results: ${s.error}` : `${s.checks ?? 0} check${s.checks === 1 ? '' : 's'} with results`}</span>
                </div>
              ))}
              <button type="button" className="btn btn-sm" style={{ marginTop: 8 }} onClick={() => void quality.refetch()} data-testid="quality-refresh">Ask the servers again</button>
            </div>
          </div>
        )}
        {data && datasets.length === 0 && data.servers.length > 0 && (
          <div className="empty" data-testid="quality-empty">No check has a result yet. Add checks to a replication or a model; they are evaluated after a run or a refresh.</div>
        )}
        {datasets.length > 0 && (
          <div className="row" style={{ gap: 24, alignItems: 'flex-start', flexWrap: 'wrap' }}>
            <div className="card" style={{ minWidth: 280, flex: '0 1 380px' }}>
              <div className="card-body">
                <strong>Datasets</strong>
                {datasets.map((d) => {
                  const state = datasetState(d)
                  return (
                    <div key={d.dataset}>
                      <button type="button" className={`btn btn-sm${selected?.dataset === d.dataset ? ' btn-primary' : ''}`} onClick={() => choose(d.dataset)} data-testid={`quality-dataset-${d.dataset}`} data-state={state} style={{ margin: '2px 0', textAlign: 'left' }}>
                        <span className="mono">{d.dataset}</span> · {stateWord(state)}
                      </button>
                    </div>
                  )
                })}
              </div>
            </div>
            <div style={{ flex: 1, minWidth: 320 }}>
              {!selected && <div className="hint" data-testid="quality-choose">Choose a dataset to see its checks.</div>}
              {selected && <DatasetDetail dataset={selected} servers={data?.servers ?? []} open={open} setOpen={setOpen} running={running} isAdmin={isAdmin} onRun={run} onRelease={release} outcome={outcome} />}
            </div>
          </div>
        )}
      </div>
    </ConsoleShell>
  )
}

function DatasetDetail(props: {
  dataset: QualityDataset
  servers: QualityServer[]
  open: string | null
  setOpen: (key: string | null) => void
  running: string | null
  isAdmin: boolean
  onRun: (serverId: string, serverName: string, dataset: string) => Promise<void>
  onRelease: (serverId: string, dataset: string, check: string) => Promise<void>
  outcome: { ok: boolean; text: string } | null
}) {
  const { dataset, servers, open, setOpen, running, isAdmin, onRun, onRelease, outcome } = props
  const evaluating = [...new Map(dataset.checks.map((c) => [c.server_id, c.server])).entries()]
  return (
    <div className="card" data-testid="quality-detail" data-dataset={dataset.dataset}>
      <div className="card-body">
        <strong className="mono">{dataset.dataset}</strong> <span className="hint">{stateWord(datasetState(dataset))}</span>
        <div style={{ margin: '8px 0' }}>
          {evaluating.map(([serverId, serverName]) => {
            const why = runBlockedReason(servers.find((s) => s.server_id === serverId), isAdmin)
            return (
              <span key={serverId} style={{ marginRight: 12 }}>
                <button type="button" className="btn btn-sm" disabled={why !== null || running !== null} title={why ?? undefined} onClick={() => void onRun(serverId, serverName, dataset.dataset)} data-testid={`quality-run-${serverId}`}>
                  {running === serverId ? 'Evaluating…' : `Evaluate on ${serverName}`}
                </button>
                {why && <span className="hint" data-testid={`quality-run-why-${serverId}`}> {why}</span>}
              </span>
            )
          })}
        </div>
        {outcome && <div className={outcome.ok ? 'hint' : 'error'} role={outcome.ok ? 'status' : 'alert'} data-testid="quality-run-result">{outcome.text}</div>}
        <table className="table">
          <thead><tr><th>Check</th><th>From</th><th>Result</th><th>Measure</th><th>Severity</th><th>Last evaluated</th></tr></thead>
          <tbody>
            {dataset.checks.map((c) => {
              const key = `${c.server_id}/${c.check_id}`
              return (
                <CheckRows key={key} check={c} dataset={dataset.dataset} expanded={open === key} onToggle={() => setOpen(open === key ? null : key)} releaseWhy={releaseBlockedReason(servers.find((s) => s.server_id === c.server_id), isAdmin)} onRelease={() => void onRelease(c.server_id, dataset.dataset, c.check_id)} />
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function CheckRows({ check, dataset, expanded, onToggle, releaseWhy, onRelease }: { check: QualityCheck; dataset: string; expanded: boolean; onToggle: () => void; releaseWhy: string | null; onRelease: () => void }) {
  const history = useQuery({ queryKey: ['quality', 'history', check.server_id, dataset, check.check_id], queryFn: () => qualityApi.history(check, dataset), enabled: expanded })
  const block = blockLine(check)
  return (
    <>
      <tr data-testid={`quality-check-${check.check_id}`} data-status={check.status} data-blocking={check.blocking}>
        <td><button type="button" className="btn btn-sm" onClick={onToggle} aria-expanded={expanded} data-testid={`quality-history-${check.check_id}`}>{check.check_id}</button></td>
        <td>{check.server}</td>
        <td data-testid={`quality-status-${check.check_id}`}>{check.status}{check.blocking ? ' · blocking' : ''}</td>
        <td>{measureLine(check)}</td>
        <td>{check.severity}</td>
        <td>{check.evaluated_at}</td>
      </tr>
      {expanded && (
        <tr data-testid={`quality-detail-${check.check_id}`}>
          <td colSpan={6}>
            {block && <div className="hint" data-testid={`quality-block-${check.check_id}`}>{block}</div>}
            {check.blocking && (
              <div style={{ margin: '6px 0' }}>
                <button type="button" className="btn btn-sm" disabled={releaseWhy !== null} title={releaseWhy ?? undefined} onClick={onRelease} data-testid={`quality-release-${check.check_id}`}>Release the block</button>
                {releaseWhy && <span className="hint" data-testid={`quality-release-why-${check.check_id}`}> {releaseWhy}</span>}
              </div>
            )}
            <ErrorBanner error={history.error} />
            {history.isLoading && <div className="hint">Reading the history…</div>}
            {history.data && history.data.results.length === 0 && <div className="hint">No result is kept for this check.</div>}
            {history.data && history.data.results.length > 0 && (
              <table className="table" data-testid={`quality-results-${check.check_id}`}>
                <thead><tr><th>When</th><th>Result</th><th>Measure</th><th>Rows that failed</th><th>Run</th></tr></thead>
                <tbody>
                  {history.data.results.map((r) => (
                    <tr key={r.id} data-testid="quality-result" data-status={r.status}>
                      <td>{r.evaluated_at}</td>
                      <td>{r.status}{r.message ? `: ${r.message}` : ''}</td>
                      <td>{measureLine(r)}</td>
                      <td className="mono" data-testid="quality-sample-keys">{r.sample_keys.join(', ')}</td>
                      <td className="mono">{r.run_id ?? ''}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </td>
        </tr>
      )}
    </>
  )
}
