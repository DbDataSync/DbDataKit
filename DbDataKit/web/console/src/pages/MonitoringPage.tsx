import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { actions, loadRows, type MonitorRow } from '../monitoring'

const label: Record<MonitorRow['state'], string> = { failed: 'Failed', lagging: 'Lagging', event: 'Notice', paused: 'Paused', running: 'Running', ok: 'OK' }

/**
 * One view of the replications and scheduled plans of every server the console manages (plan 13, M11): what failed or is behind is at the top, whichever server it is on, and a row can be held, released or run now. A Proxy's notices (a login locked out, a statement refused, a limit reached; the last day's, newest first) are listed with them. The figures are asked of each
 * server through the relay every thirty seconds; a server that does not answer is named above the list, and the others still show.
 */
export function MonitoringPage() {
  const client = useQueryClient()
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const servers = instances.data ?? []
  const view = useQuery({
    queryKey: ['monitoring', servers.map((s) => `${s.id}:${s.reachable}`).join(',')],
    queryFn: () => loadRows(servers),
    enabled: instances.isSuccess,
    refetchInterval: 30_000,
  })
  const act = useMutation({
    mutationFn: ({ row, what }: { row: MonitorRow; what: 'pause' | 'resume' | 'run' }) => actions[what](row),
    onSuccess: () => client.invalidateQueries({ queryKey: ['monitoring'] }),
  })
  const rows = view.data?.rows ?? []

  return (
    <ConsoleShell crumbs={[{ label: 'Monitoring', heading: true }]}>
      <div className="pane" data-testid="monitoring-page">
        <div className="page-head">
          <h1 className="page-title">Monitoring</h1>
          <span className="page-note">Replications, scheduled plans and Proxy notices across your servers.</span>
        </div>
        <ErrorBanner error={instances.error ?? view.error ?? act.error} />
        {(view.data?.problems ?? []).map((p) => (
          <div key={p.server} className="empty" data-testid="monitoring-problem">{`${p.server} did not answer: ${p.message}`}</div>
        ))}
        {view.isSuccess && rows.length === 0 && <div className="empty" data-testid="monitoring-none">No replication, scheduled plan or notice on the servers that answered.</div>}
        {rows.length > 0 && (
          <div className="card flush" data-testid="monitoring-rows">
            <div className="grid-head" style={{ gridTemplateColumns: '0.8fr 1fr 2.4fr 2.4fr 1.6fr', gap: 14 }}><span>State</span><span>Server</span><span>What</span><span>Detail</span><span /></div>
            {rows.map((row) => (
              <div key={row.key} className="grid-row" style={{ gridTemplateColumns: '0.8fr 1fr 2.4fr 2.4fr 1.6fr', gap: 14 }} data-testid="monitoring-row" data-state={row.state}>
                <span data-testid="monitoring-state">{label[row.state]}{row.held && row.state !== 'paused' ? ' (held)' : ''}</span>
                <span>{row.serverName}</span>
                <span className="mono">{row.subject}</span>
                <span>{row.detail}</span>
                <span className="row" style={{ gap: 8 }}>
                  {row.target !== null && <>
                  <button type="button" className="btn" disabled={act.isPending} onClick={() => act.mutate({ row, what: row.held ? 'resume' : 'pause' })} data-testid="monitoring-toggle">{row.held ? 'Resume' : 'Pause'}</button>
                  <button type="button" className="btn" disabled={act.isPending} onClick={() => act.mutate({ row, what: 'run' })} data-testid="monitoring-run">Run now</button>
                  </>}
                </span>
              </div>
            ))}
          </div>
        )}
      </div>
    </ConsoleShell>
  )
}
