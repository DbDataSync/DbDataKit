import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { loadRuns, took, visible, type RunRow } from '../runs'

const stateText: Record<RunRow['state'], string> = { failed: 'Failed', running: 'Running', queued: 'Queued', stopped: 'Stopped', succeeded: 'Succeeded' }
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '')

/**
 * The newest runs of the replications of every Sync server and of every runner, in one list, newest first, with a switch to show only the ones that failed. Monitoring says which replication or task failed last; this says what each did
 * over the last runs, whichever server it is on, and each row opens the screen that holds the whole history and the actions on a run. Read only: nothing is started, cancelled or resynchronised here. Asked of each server through the relay
 * every thirty seconds; a server that does not answer is named above the list, and the others still show.
 */
export function RunsPage() {
  const [onlyFailures, setOnlyFailures] = useState(false)
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const servers = instances.data ?? []
  const view = useQuery({
    queryKey: ['runs', servers.map((s) => `${s.id}:${s.reachable}`).join(',')],
    queryFn: () => loadRuns(servers),
    enabled: instances.isSuccess,
    refetchInterval: 30_000,
  })
  const all = view.data?.rows ?? []
  const rows = visible(all, onlyFailures)
  const failed = all.filter((r) => r.state === 'failed').length

  return (
    <ConsoleShell crumbs={[{ label: 'Runs', heading: true }]}>
      <div className="pane" data-testid="runs-page">
        <div className="page-head">
          <h1 className="page-title">Runs</h1>
          <span className="page-note">The latest runs of your replications and runner tasks, whichever server they ran on.</span>
        </div>
        <ErrorBanner error={instances.error ?? view.error} />
        {(view.data?.problems ?? []).map((p) => (
          <div key={p.server} className="empty" data-testid="runs-problem">{`${p.server} did not answer: ${p.message}`}</div>
        ))}
        {view.isLoading && <div className="hint" data-testid="runs-loading">Asking the servers…</div>}
        {view.isSuccess && all.length === 0 && <div className="empty" data-testid="runs-none">No run on the Sync and runner servers that answered.</div>}
        {all.length > 0 && (
          <>
            <label className="row" style={{ gap: 8, marginBottom: 10 }}>
              <input type="checkbox" checked={onlyFailures} onChange={(e) => setOnlyFailures(e.target.checked)} data-testid="runs-only-failures" />
              <span>Only failures</span>
              <span className="hint" data-testid="runs-summary">{failed} of the latest {all.length} runs failed.</span>
            </label>
            {rows.length === 0 && <div className="empty" data-testid="runs-no-failures">None of the latest runs failed.</div>}
          </>
        )}
        {rows.length > 0 && (
          <div className="card flush" data-testid="runs-rows" role="table" aria-label="Latest runs">
            <div className="grid-head" role="row" style={{ gridTemplateColumns: '0.8fr 1fr 2.2fr 2.6fr 1.6fr 0.7fr 0.8fr', gap: 14 }}><span>State</span><span>Server</span><span>What</span><span>Detail</span><span>Started</span><span>Took</span><span /></div>
            {rows.map((row) => (
              <div key={row.key} role="row" className="grid-row" style={{ gridTemplateColumns: '0.8fr 1fr 2.2fr 2.6fr 1.6fr 0.7fr 0.8fr', gap: 14 }} data-testid="runs-row" data-state={row.state} data-product={row.product}>
                <span data-testid="runs-state">{stateText[row.state]}</span>
                <span>{row.serverName}</span>
                <span className="mono">{row.subject}</span>
                <span>{row.detail}</span>
                <span>{when(row.started ?? row.queued)}</span>
                <span>{took(row)}</span>
                <Link className="btn" to={row.link} data-testid="runs-open">Open</Link>
              </div>
            ))}
          </div>
        )}
      </div>
    </ConsoleShell>
  )
}
