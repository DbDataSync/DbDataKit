import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { behind, holdingLine, lastChange, versionLine } from '../deployments'

/**
 * Where definitions are deployed (plan 28c E24, from the lineage the runners publish): pick a definition to see its channels, the active, previous and staged version, who changed it last and when, and every runner with the
 * version it holds and whether it is ready or active; or pick a runner to see what it holds. A runner that lags the channel is marked.
 */
export function DeploymentsPage() {
  const index = useQuery({ queryKey: ['deployments'], queryFn: () => consoleApi.deployments.index() })
  const [definition, setDefinition] = useState<string | null>(null)
  const [runner, setRunner] = useState<string | null>(null)
  const where = useQuery({ queryKey: ['deployments', 'where', definition], queryFn: () => consoleApi.deployments.where(definition!), enabled: definition !== null })
  const on = useQuery({ queryKey: ['deployments', 'on', runner], queryFn: () => consoleApi.deployments.on(runner!), enabled: runner !== null })
  const data = index.data
  // before any runner is paired the answer is a 404 or an empty list
  const empty = data && data.definitions.length === 0 && data.runners.length === 0

  return (
    <ConsoleShell crumbs={[{ label: 'Deployments', heading: true }]}>
      <div className="pane" data-testid="deployments-page">
        <div className="page-head">
          <h1 className="page-title">Deployments</h1>
          <span className="page-note">Where each package is deployed, on which version, and what each runner holds.</span>
        </div>
        <ErrorBanner error={index.error} />
        {empty && <div className="empty" data-testid="deployments-empty">No paired runner has published a deployment yet.</div>}
        {data && !empty && (
          <div className="row" style={{ gap: 24, alignItems: 'flex-start', flexWrap: 'wrap' }}>
            <div className="card" style={{ minWidth: 240 }}>
              <div className="card-body">
                <strong>Definitions</strong>
                {data.definitions.map((d) => (
                  <div key={`${d.kind}/${d.name}`}>
                    <button type="button" className="btn btn-sm" onClick={() => { setDefinition(`${d.kind}/${d.name}`); setRunner(null) }} data-testid={`deployments-definition-${d.name}`}>{d.name}</button>
                  </div>
                ))}
                <strong style={{ display: 'block', marginTop: 12 }}>Runners</strong>
                {data.runners.map((r) => (
                  <div key={r.instance}>
                    <button type="button" className="btn btn-sm" onClick={() => { setRunner(r.instance); setDefinition(null) }} data-testid={`deployments-runner-${r.instance}`}>{r.host ?? r.instance}</button>
                    {!r.live && <span className="hint"> not live</span>}
                  </div>
                ))}
              </div>
            </div>
            <div style={{ flex: 1, minWidth: 320 }}>
              <ErrorBanner error={where.error ?? on.error} />
              {where.data?.channels.map((c) => (
                <div className="card" key={`${c.group}/${c.channel}`} style={{ marginBottom: 12 }} data-testid="deployments-channel" data-channel={c.channel}>
                  <div className="card-body">
                    <strong>{c.name}</strong> on <strong>{c.channel}</strong>: <span data-testid="deployments-versions">{versionLine(c)}</span>
                    {lastChange(c) && <div className="hint" data-testid="deployments-last">{lastChange(c)}</div>}
                    {behind(c).length > 0 && <div className="hint" data-testid="deployments-behind">{behind(c).length} runner(s) not active on v{c.active}</div>}
                    <table className="table">
                      <thead><tr><th>Runner</th><th>Version</th><th>State</th><th>Runtime</th></tr></thead>
                      <tbody>
                        {c.instances.map((i) => (
                          <tr key={i.instance} data-testid="deployments-instance" data-on-active={i.on_active}>
                            <td>{i.host ?? i.instance}{!i.live && ' (gone)'}</td>
                            <td>v{i.version}</td>
                            <td>{i.state}{i.error ? `: ${i.error}` : ''}</td>
                            <td>{i.runtime ?? ''}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              ))}
              {on.data?.instances.map((r) => (
                <div className="card" key={r.instance} style={{ marginBottom: 12 }} data-testid="deployments-runner-card">
                  <div className="card-body">
                    <strong>{r.host ?? r.instance}</strong> <span className="hint">runtime {r.runtime ?? 'unknown'}{r.live ? '' : ', not live'}</span>
                    {r.holdings.length === 0 && <div className="hint">Nothing is deployed on it.</div>}
                    {r.holdings.map((h) => (
                      <div key={`${h.kind}/${h.name}/${h.channel}`} data-testid="deployments-holding">{h.name} on {h.channel}: {holdingLine(h)}{h.error ? `: ${h.error}` : ''}</div>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </ConsoleShell>
  )
}
