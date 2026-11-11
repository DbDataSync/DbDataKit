import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { actions, loadRows, movePlan, moveBlock, ownerSentence, type MonitorRow, type PlanMove } from '../monitoring'

const label: Record<MonitorRow['state'], string> = { failed: 'Failed', gone: 'Gone', lagging: 'Lagging', expiring: 'Expiring', event: 'Notice', paused: 'Paused', running: 'Running', ok: 'OK' }

/**
 * One view of the replications and scheduled plans of every server the console manages (plan 13, M11): what failed or is behind is at the top, whichever server it is on, and a row can be held, released or run now. A Gateway's notices (a login locked out, a statement refused, a limit reached; the last day's, newest first) are listed with them. The figures are asked of each
 * server through the relay every thirty seconds; a server that does not answer is named above the list, and the others still show.
 */
export function MonitoringPage() {
  const client = useQueryClient()
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const servers = instances.data ?? []
  const view = useQuery({
    queryKey: ['monitoring', servers.map((s) => `${s.id}:${s.reachable}`).join(',')],
    queryFn: () => loadRows(servers, Date.now(), true),
    enabled: instances.isSuccess,
    refetchInterval: 30_000,
  })
  const act = useMutation({
    mutationFn: ({ row, what }: { row: MonitorRow; what: 'pause' | 'resume' | 'run' }) => actions[what](row),
    onSuccess: () => client.invalidateQueries({ queryKey: ['monitoring'] }),
  })
  const [moving, setMoving] = useState<string | null>(null)
  const [move, setMove] = useState<PlanMove>({ owner: 'observe', reason: '' })
  const [moved, setMoved] = useState<string | null>(null)
  const moveIt = useMutation({
    mutationFn: ({ row, plan }: { row: MonitorRow; plan: PlanMove }) => movePlan(row, plan),
    onSuccess: (_, { row, plan }) => { setMoved(`${row.subject}: ${ownerSentence(plan.owner, row.runner ?? (plan.runner ? { server: plan.runner, channel: plan.channel ?? '' } : undefined)).toLowerCase()}.`); setMoving(null); client.invalidateQueries({ queryKey: ['monitoring'] }) },
  })
  const open = (row: MonitorRow) => {
    const owner = row.owner ?? 'old'
    setMove({ owner: owner === 'old' ? 'observe' : owner === 'observe' ? 'runner' : 'old', reason: '', runner: row.runner?.server, channel: row.runner?.channel })
    setMoving(moving === row.key ? null : row.key)
    setMoved(null)
    moveIt.reset()
  }
  const rows = view.data?.rows ?? []

  return (
    <ConsoleShell crumbs={[{ label: 'Monitoring', heading: true }]}>
      <div className="pane" data-testid="monitoring-page">
        <div className="page-head">
          <h1 className="page-title">Monitoring</h1>
          <span className="page-note">Servers that are down, replications, scheduled plans, runner tasks, certificates near their end and Gateway notices across your servers.</span>
        </div>
        <ErrorBanner error={instances.error ?? view.error ?? act.error ?? moveIt.error} />
        {moved && <div className="hint" data-testid="monitoring-moved">{moved}</div>}
        {(view.data?.problems ?? []).map((p) => (
          <div key={p.server} className="empty" data-testid="monitoring-problem">{`${p.server} did not answer: ${p.message}`}</div>
        ))}
        {view.isSuccess && rows.length === 0 && <div className="empty" data-testid="monitoring-none">No replication, scheduled plan, runner task or notice on the servers that answered.</div>}
        {rows.length > 0 && (
          <div className="card flush" data-testid="monitoring-rows">
            <div className="grid-head" style={{ gridTemplateColumns: '0.8fr 1fr 2.4fr 2.4fr 1.6fr', gap: 14 }}><span>State</span><span>Server</span><span>What</span><span>Detail</span><span /></div>
            {rows.map((row) => (
              <div key={row.key} className={moving === row.key ? 'grid-row auto' : 'grid-row'} style={{ gridTemplateColumns: '0.8fr 1fr 2.4fr 2.4fr 1.6fr', gap: 14 }} data-testid="monitoring-row" data-state={row.state}>
                <span data-testid="monitoring-state">{label[row.state]}{row.held && row.state !== 'paused' ? ' (held)' : ''}</span>
                <span>{row.serverName}</span>
                <span className="mono">{row.subject}</span>
                <span>{row.detail}</span>
                <span className="row" style={{ gap: 8 }}>
                  {row.target !== null && <>
                  <button type="button" className="btn" disabled={act.isPending} onClick={() => act.mutate({ row, what: row.held ? 'resume' : 'pause' })} data-testid="monitoring-toggle">{row.held ? 'Resume' : 'Pause'}</button>
                  <button type="button" className="btn" disabled={act.isPending} onClick={() => act.mutate({ row, what: 'run' })} data-testid="monitoring-run">Run now</button>
                  {row.kind === 'plan' && <button type="button" className="btn" onClick={() => open(row)} aria-expanded={moving === row.key} data-testid="monitoring-owner-open">Scheduler…</button>}
                  </>}
                </span>
                {moving === row.key && (() => {
                  const block = moveBlock(row, move)
                  const needsRunner = move.owner !== 'old' && row.runner === undefined
                  return (
                    <div style={{ gridColumn: '1 / -1' }} className="card" data-testid="monitoring-owner-form">
                      <div className="card-body">
                        <div className="hint" style={{ marginBottom: 6 }} data-testid="monitoring-owner-now">{ownerSentence(row.owner ?? 'old', row.runner)}. Moving it changes this server&apos;s settings and the runner&apos;s record together, in the order that never schedules the plan twice; a run going is waited for.</div>
                        <div className="row" style={{ gap: 10, flexWrap: 'wrap' }}>
                          <label className="row" style={{ gap: 6 }}><span className="hint">Scheduled by</span>
                            <select className="input" value={move.owner} onChange={(e) => setMove({ ...move, owner: e.target.value as PlanMove['owner'] })} data-testid="monitoring-owner-select">
                              <option value="old">This server</option><option value="observe">This server, observed by the runner</option><option value="runner">The runner</option>
                            </select></label>
                          <label className="row" style={{ gap: 6 }}><span className="hint">Reason</span>
                            <input className="input" value={move.reason} onChange={(e) => setMove({ ...move, reason: e.target.value })} style={{ width: 220 }} data-testid="monitoring-owner-reason" /></label>
                          {needsRunner && <>
                            <label className="row" style={{ gap: 6 }}><span className="hint">Runner</span>
                              <input className="input mono" value={move.runner ?? ''} placeholder="the name it is paired under" onChange={(e) => setMove({ ...move, runner: e.target.value })} style={{ width: 160 }} data-testid="monitoring-owner-runner" /></label>
                            <label className="row" style={{ gap: 6 }}><span className="hint">Channel</span>
                              <input className="input mono" value={move.channel ?? ''} onChange={(e) => setMove({ ...move, channel: e.target.value })} style={{ width: 110 }} data-testid="monitoring-owner-channel" /></label>
                          </>}
                          <button type="button" className="btn" disabled={block !== null || moveIt.isPending} onClick={() => moveIt.mutate({ row, plan: move })} data-testid="monitoring-owner-move">{moveIt.isPending ? 'Moving…' : 'Move'}</button>
                        </div>
                        {block !== null && <div className="hint" style={{ marginTop: 4 }} data-testid="monitoring-owner-why">{block}</div>}
                      </div>
                    </div>
                  )
                })()}
              </div>
            ))}
          </div>
        )}
      </div>
    </ConsoleShell>
  )
}
