import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { consoleApi, type InstanceSummary } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { commonText, keepStale, lagText, loadTracking, resolutionText, resolveDivergence, switchPrimaryOf, type CopyRow, type Resolution, type TrackingDivergence } from '../tracking'

/** The copies the last answers showed, kept while the page is left and come back to: a server that stops answering keeps its copies on the page, marked old. */
let previous: CopyRow[] = []
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '')

/**
 * The copy of Build's tracking tables and of the units Sync keeps on its targets (plan 28c E18, E17): for every connection of every Build server and runner that keeps one, the mode and primary of the setting, how old the newest copied row is, and a divergence with the point where the two sides part
 * (the last row both hold). An administrator resolves an open divergence: archive keeps the copy's later rows apart, reset deletes them, and the copy then follows the target again from the common point; the server audits it. Switching the primary of a Build server's copy is asked for here (an administrator; the server brings its copy level with the target first)
 * and is refused in a sentence while a side is down or a divergence is open; a Sync replication's and a runner's primary are changed where they are set, and the page says where. Read from each server through the relay every thirty seconds; a server that does not answer is named, and its
 * copies stay on the page marked as old.
 */
export function TrackingPage() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const servers = instances.data ?? []
  const view = useQuery({
    queryKey: ['tracking', servers.map((s) => `${s.id}:${s.reachable}`).join(',')],
    queryFn: async () => {
      const merged = keepStale(previous, await loadTracking(servers))
      previous = merged.rows
      return merged
    },
    enabled: instances.isSuccess,
    refetchInterval: 30_000,
    staleTime: 0,   // what the servers answer is asked again on every visit (the console's default of five seconds would show a server that went down as up when the page is left and come back to)
  })
  const data = view.data
  const rows = data?.rows ?? []
  const divergences = data?.divergences ?? []
  const [switched, setSwitched] = useState<{ key: string; message: string; done?: boolean } | null>(null)
  const [switching, setSwitching] = useState<string | null>(null)
  const servers_ = new Map(servers.map((s) => [s.id, s]))
  const switchOf = async (row: CopyRow) => {
    const server = servers_.get(row.server)
    if (!server) return
    setSwitching(row.key)
    setSwitched(null)
    const result = await switchPrimaryOf(server, row, data?.problems ?? [])
    setSwitching(null)
    setSwitched({ key: row.key, message: result.message, done: result.done })
    if (result.done) client.invalidateQueries({ queryKey: ['tracking'] })
  }
  const [resolved, setResolved] = useState<string | null>(null)
  const resolve = useMutation({
    mutationFn: ({ server, id, how }: { server: InstanceSummary; id: number; how: Resolution }) => resolveDivergence(server, id, how),
    onSuccess: (_, { id, how }) => { setResolved(`Divergence ${id} resolved: ${how === 'archive' ? 'the later rows were archived' : 'the later rows were deleted'}.`); client.invalidateQueries({ queryKey: ['tracking'] }) },
    onError: () => setResolved(null),
  })

  return (
    <ConsoleShell crumbs={[{ label: 'Tracking', heading: true }]}>
      <div className="pane" data-testid="tracking-page">
        <div className="page-head">
          <h1 className="page-title">Tracking copies</h1>
          <span className="page-note">The copy of what Build recorded about each connection, on the Build servers and the runners: mode, primary, how old the newest copied row is, and where a copy has parted from its target.</span>
        </div>
        <ErrorBanner error={instances.error ?? view.error ?? resolve.error} />
        {(data?.problems ?? []).map((p) => (
          <div key={p.server} className="empty" data-testid="tracking-problem">{`${p.server} did not answer: ${p.message}`}</div>
        ))}
        {view.isLoading && <div className="hint" data-testid="tracking-loading">Asking the servers…</div>}
        {view.isSuccess && rows.length === 0 && <div className="empty" data-testid="tracking-none">No Build server, runner or Sync server that answered holds a copy of tracking rows yet.</div>}
        {rows.length > 0 && (
          <div className="card flush" data-testid="tracking-rows" role="table" aria-label="Tracking copies">
            <div className="grid-head" role="row" style={{ gridTemplateColumns: '1fr 1fr 1fr 0.8fr 0.8fr 2fr 1.4fr', gap: 14 }}><span>Server</span><span>Connection</span><span>State</span><span>Mode</span><span>Primary</span><span>Copy</span><span /></div>
            {rows.map((row) => (
              <div key={row.key} data-testid="tracking-row" data-connection={row.connection} data-diverged={row.open.length > 0} data-stale={row.stale === true}>
                <div role="row" className="grid-row" style={{ gridTemplateColumns: '1fr 1fr 1fr 0.8fr 0.8fr 2fr 1.4fr', gap: 14 }}>
                  <span>{row.serverName}</span>
                  <span className="mono" title={row.identity}>{row.connection}</span>
                  <span data-testid="tracking-state">{row.stale ? 'Server down' : row.open.length > 0 ? 'Diverged' : 'In step'}</span>
                  <span data-testid="tracking-mode">{row.mode}</span>
                  <span data-testid="tracking-primary">{row.primary ?? 'the only side'}</span>
                  <span data-testid="tracking-lag">{`${lagText(row.newest, Date.now())}; ${row.rows} rows in ${row.streams} streams`}</span>
                  <button className="btn" data-testid="tracking-switch" disabled={switching === row.key} onClick={() => void switchOf(row)}>Switch primary</button>
                </div>
                {switched?.key === row.key && <div className="hint" role="status" data-testid="tracking-switch-message" data-switched={switched.done === true} style={{ padding: '0 14px 10px' }}>{switched.message}</div>}
              </div>
            ))}
          </div>
        )}
        {divergences.length > 0 && <h2 className="section-title" style={{ marginTop: 18 }}>Divergences</h2>}
        {resolved && <div className="hint" role="status" data-testid="tracking-resolved-message">{resolved}</div>}
        {divergences.map(({ server, divergence }) => <DivergenceCard key={`${server.id}/${divergence.id}`} server={server} divergence={divergence} isAdmin={isAdmin} busy={resolve.isPending} onResolve={(how) => { setResolved(null); resolve.mutate({ server, id: divergence.id, how }) }} />)}
      </div>
    </ConsoleShell>
  )
}

function DivergenceCard(props: { server: InstanceSummary; divergence: TrackingDivergence; isAdmin: boolean; busy: boolean; onResolve: (how: Resolution) => void }) {
  const { server, divergence: d } = props
  return (
    <div className="card" data-testid="tracking-divergence" data-open={d.open} data-status={d.status} style={{ marginBottom: 10 }}>
      <div><strong>{d.open ? 'Open' : `Resolved (${d.status})`}</strong> on {server.name}: <span className="mono">{d.stream}</span> of <span className="mono">{d.identity.slice(d.identity.lastIndexOf('@') + 1)}</span>, since {when(d.diverged_at)}</div>
      <div data-testid="tracking-reason">{d.reason}</div>
      <div data-testid="tracking-common">{commonText(d)}</div>
      {d.copy_high && <div className="hint">{`The copy holds rows up to ${d.copy_high.key}${d.primary_high ? `, the target up to ${d.primary_high.key}` : ''}.`}</div>}
      {!d.open && d.resolved_by && <div className="hint" data-testid="tracking-resolved-by">{`Resolved by ${d.resolved_by} at ${when(d.resolved_at)}.`}</div>}
      {d.open && props.isAdmin && (
        <div className="row" style={{ gap: 8, marginTop: 8 }}>
          <button className="btn" disabled={props.busy} onClick={() => props.onResolve('archive')} data-testid="tracking-resolve-archive" title={resolutionText.archive}>Archive later rows</button>
          <button className="btn" disabled={props.busy} onClick={() => props.onResolve('reset')} data-testid="tracking-resolve-reset" title={resolutionText.reset}>Reset the copy</button>
          <span className="hint">{resolutionText.archive} {resolutionText.reset}</span>
        </div>
      )}
      {d.open && !props.isAdmin && <div className="hint" data-testid="tracking-admin-only">An administrator resolves it.</div>}
    </div>
  )
}
