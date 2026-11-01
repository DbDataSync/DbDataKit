import { useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { LineageGraphView, projectLineage, reach } from '@dbdatakit/visuals'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { findCards, serverNote } from '../lineage'

/**
 * What feeds what, across the servers (plan 18, G10): the graphs of the Build and Sync servers joined by identity, drawn at the table level with a dataset opening to its columns. Pick a card (click it, or search) and what feeds
 * it is marked on the left of it and what it feeds on the right; the rest dims. Each server is named with what it said, and a server that did not answer is named, not hidden.
 */
export function LineagePage() {
  const client = useQueryClient()
  const lineage = useQuery({ queryKey: ['lineage'], queryFn: () => consoleApi.lineage(false) })
  const [selected, setSelected] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const graph = lineage.data
  const found = useMemo(() => (graph ? findCards(graph, search) : []), [graph, search])
  const chosen = graph?.nodes.find((n) => n.id === selected)
  const counts = useMemo(() => {
    if (!graph || !selected) return null
    const r = reach(projectLineage(graph), selected)
    return { upstream: r.upstream.size, downstream: r.downstream.size }
  }, [graph, selected])
  const refresh = async () => { client.setQueryData(['lineage'], await consoleApi.lineage(true)) }

  return (
    <ConsoleShell crumbs={[{ label: 'Lineage', heading: true }]}>
      <div className="pane" data-testid="lineage-page">
        <div className="page-head">
          <h1 className="page-title">Lineage</h1>
          <span className="page-note">What feeds what across your Build and Sync servers. Choose a table to see where it comes from and where it goes.</span>
        </div>
        <ErrorBanner error={lineage.error} />
        {graph?.error && <div className="empty" data-testid="lineage-join-error">{graph.error}</div>}
        {graph && (
          <div className="card" style={{ marginBottom: 14 }} data-testid="lineage-servers">
            <div className="card-body">
              {graph.servers.length === 0 && <span data-testid="lineage-no-servers">No paired server keeps a lineage yet (Build and Sync servers do).</span>}
              {graph.servers.map((s) => (
                <div key={s.server} data-testid="lineage-server" data-server={s.server}>
                  <strong>{s.server}</strong> <span className="hint">{serverNote(s)}</span>
                  {s.problems.map((p, i) => <div key={i} className="hint" data-testid="lineage-problem">{p}</div>)}
                </div>
              ))}
              <button type="button" className="btn btn-sm" style={{ marginTop: 8 }} onClick={() => void refresh()} data-testid="lineage-refresh">Ask the servers again</button>
            </div>
          </div>
        )}
        {graph && graph.nodes.length > 0 && (
          <>
            <div className="row" style={{ gap: 12, marginBottom: 10, alignItems: 'baseline', flexWrap: 'wrap' }}>
              <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="find a table, a model or a measure" aria-label="Find" data-testid="lineage-search" style={{ minWidth: 280 }} />
              {found.map((n) => (
                <button key={n.id} type="button" className="btn btn-sm" onClick={() => { setSelected(n.id); setSearch('') }} data-testid={`lineage-found-${n.id}`}>{n.name}</button>
              ))}
            </div>
            {chosen && counts && (
              <div className="hint" style={{ marginBottom: 8 }} data-testid="lineage-selected">
                <strong>{chosen.name}</strong> · {counts.upstream} upstream, {counts.downstream} downstream
                {chosen.unknown_upstream && ' · what feeds it is not known'} · <span className="mono">{chosen.id}</span>
              </div>
            )}
            <LineageGraphView graph={graph} selected={selected} onSelect={setSelected} height={640} />
          </>
        )}
        {graph && graph.nodes.length === 0 && graph.servers.length > 0 && !graph.error && <div className="empty" data-testid="lineage-empty">The servers that answered have no lineage to show yet.</div>}
      </div>
    </ConsoleShell>
  )
}
