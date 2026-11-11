import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ApiError, ErrorBanner, Field, useIsAdmin } from '@dbdatakit/shell'
import { consoleApi, instancesKey } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { engineName, modulesCurrent, offersState, stateApi, type MoveProgress, type Preflight, type ServerState } from '../stateApi'

const MODULE_COLUMNS = '1.4fr 1fr 1fr 1fr 1fr'

const sentenceOf = (error: unknown) => (error instanceof ApiError ? error.message : error instanceof Error ? error.message : 'No answer.')
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/**
 * The STATE panel of one server (plan 29b, C06): where its state is (engine, the connection masked as the server shows it), each module's version in the registry against the binary, what is pending, where each setting came from, the other live instances and the
 * maintenance mode, which an administrator puts on and off here. Every call goes through the relay as the signed-in person, and each server audits the change. The console's own state is read-only here (its move is the command line, plan 29b QM3).
 */
export function ServerStatePage() {
  const { instance = '' } = useParams()
  const isAdmin = useIsAdmin()
  const client = useQueryClient()
  const own = instance === 'console'
  const servers = useQuery({ queryKey: instancesKey, queryFn: consoleApi.instances.list, enabled: !own })
  const server = own ? null : (servers.data ?? []).find((s) => s.id === instance)
  const state = useQuery({ queryKey: ['server-state', instance], queryFn: () => stateApi.server.state(instance), refetchInterval: 4_000, enabled: own || !!server?.reachable, retry: false })
  const [reason, setReason] = useState('')
  const maintenance = useMutation({
    mutationFn: (enabled: boolean) => stateApi.server.maintenance(instance, enabled, enabled ? reason.trim() : null),
    onSuccess: () => client.invalidateQueries({ queryKey: ['server-state', instance] }),
  })
  const name = own ? 'This console' : server?.name ?? instance
  const data = state.data
  const crumbs = [{ label: 'Servers', to: '/instances' }, { label: `${name}: state`, heading: true }]

  if (!own && servers.isSuccess && !server) return <ConsoleShell crumbs={crumbs}><div className="pane"><div className="empty" data-testid="state-no-server">That server is not paired with this console.</div></div></ConsoleShell>
  if (!own && server && !server.reachable) return <ConsoleShell crumbs={crumbs}><div className="pane"><div className="empty" data-testid="state-unreachable">{`${server.name} is not answering: ${server.problem ?? 'no answer'}.`}</div></div></ConsoleShell>
  if (!own && server && !offersState(server.product)) return <ConsoleShell crumbs={crumbs}><div className="pane"><div className="empty" data-testid="state-not-offered">{`${server.name} (${server.product}) has no state panel: it does not offer the state API.`}</div></div></ConsoleShell>

  return (
    <ConsoleShell crumbs={crumbs}>
      <div className="pane" data-testid="server-state-page">
        <div className="page-head">
          <h1 className="page-title">{`${name}: state`}</h1>
          <span className="page-note">Where {own ? 'the console keeps' : 'this server keeps'} its state, and what is applied there. Read from <span className="mono">GET /api/v1/state</span>{own ? '' : ' through the relay'}.</span>
        </div>
        <ErrorBanner error={state.error} />
        {data && (
          <>
            <div className="card" data-testid="state-summary">
              <div className="card-body">
                <dl className="kv" style={{ display: 'grid', gridTemplateColumns: '10rem 1fr', gap: 6 }}>
                  <dt>Engine</dt><dd data-testid="state-engine">{engineName(data.engine)}</dd>
                  <dt>Connection</dt><dd className="mono" data-testid="state-connection">{data.connection ?? data.file ?? 'a file in its directory'}</dd>
                  <dt>Software</dt><dd>{data.product} {data.software_version}</dd>
                  <dt>Pending migrations</dt><dd data-testid="state-pending">{data.read ? data.pending_migrations ?? 0 : 'unknown'}</dd>
                  {data.sources && (<><dt>Settings from</dt><dd data-testid="state-sources">{Object.entries(data.sources).map(([setting, source]) => `${setting}: ${source.kind}${source.name ? ` (${source.name})` : ''}`).join('; ')}</dd></>)}
                </dl>
                {!data.read && <div className="empty" role="alert" data-testid="state-unreadable">{data.error ?? 'The state could not be read.'}</div>}
              </div>
            </div>
            {data.modules && data.modules.length > 0 && (
              <div className="card flush" style={{ marginTop: 16 }} data-testid="state-modules">
                <div className="grid-head" style={{ gridTemplateColumns: MODULE_COLUMNS, gap: 14 }}><span>Module</span><span>Scope</span><span>Registered</span><span>This binary</span><span>Pending</span></div>
                {data.modules.map((m) => (
                  <div key={`${m.module}/${m.scope}`} className="grid-row" style={{ gridTemplateColumns: MODULE_COLUMNS, gap: 14 }} data-testid="state-module" data-module={m.module}>
                    <span className="mono">{m.module}</span><span>{m.scope ?? 'install'}</span><span>{m.registered_version ?? 'not applied'}</span><span>{m.binary_version}</span><span>{m.pending}</span>
                  </div>
                ))}
                <div className="page-note" style={{ padding: 10 }} data-testid="state-modules-note">{modulesCurrent(data.modules) ? 'Every module is at the version of this binary.' : 'A module differs from this binary: the next start applies what is pending.'}</div>
              </div>
            )}
            {(data.instances ?? []).length > 0 && (
              <div className="card" style={{ marginTop: 16 }} data-testid="state-instances">
                <div className="card-head"><span className="card-title">Other instances sharing this state</span></div>
                <div className="card-body">
                  {(data.instances ?? []).map((i) => <div key={i.key} className="mono" data-testid="state-instance">{`${i.self ? '(this one) ' : ''}${i.host ?? '?'} pid ${i.process_id ?? '?'}${i.software_version ? ` ${i.software_version}` : ''}`}</div>)}
                </div>
              </div>
            )}
            <div className="card" style={{ marginTop: 16 }} data-testid="state-maintenance">
              <div className="card-head"><span className="card-title">Maintenance</span></div>
              <div className="card-body">
                <div data-testid="maintenance-status" data-enabled={data.maintenance.enabled} data-idle={data.maintenance.idle}>
                  {data.maintenance.enabled
                    ? `In maintenance since ${data.maintenance.since ?? '?'}: ${data.maintenance.reason ?? ''}. ${data.maintenance.idle ? 'Idle: nothing is in flight.' : `Draining: ${data.maintenance.inflight} still in flight.`}`
                    : 'Not in maintenance: the server takes work.'}
                </div>
                {isAdmin && !own && (
                  <div className="row" style={{ gap: 8, alignItems: 'flex-end', marginTop: 10, flexWrap: 'wrap' }}>
                    <ErrorBanner error={maintenance.error} />
                    {!data.maintenance.enabled ? (
                      <>
                        <Field label="Reason"><input className="input" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="moving the state to PostgreSQL" data-testid="maintenance-reason" /></Field>
                        <button type="button" className="btn btn-primary" disabled={maintenance.isPending || !reason.trim()} onClick={() => maintenance.mutate(true)} data-testid="maintenance-on">Put into maintenance</button>
                      </>
                    ) : (
                      <button type="button" className="btn" disabled={maintenance.isPending} onClick={() => maintenance.mutate(false)} data-testid="maintenance-off">End maintenance</button>
                    )}
                  </div>
                )}
                {own && <div className="page-note">The console&apos;s maintenance and its move are on its own machine: <span className="mono">dbdatakit state move</span> (it cannot stop itself).</div>}
              </div>
            </div>
            {!own && isAdmin && server && <MoveWizard serverId={server.id} serverName={server.name} product={server.product ?? ''} state={data} />}
          </>
        )}
      </div>
    </ConsoleShell>
  )
}

type Phase = 'idle' | 'maintenance' | 'draining' | 'moving' | 'done' | 'failed'

const PHASES: { phase: Phase; label: string }[] = [
  { phase: 'maintenance', label: 'Put the server into maintenance' },
  { phase: 'draining', label: 'Wait until it reports idle' },
  { phase: 'moving', label: 'Run the move on the server' },
]

/**
 * The Move wizard (plan 29b, C06): choose the state connection to move onto, check it (the target reachable and empty, the rights, the engine and its features, not the server's own database, and the server's own view: it can resolve the password and write its settings
 * file), then the steps with their progress: maintenance on, wait for idle, and the move, which the server runs on its own machine (it needs the settings file and a stop of the service, which only code there can do). At the end it says how to restart and how to go back.
 * Nothing is restarted from here. The console&apos;s own state is not moved from this screen (it cannot stop itself).
 */
function MoveWizard({ serverId, serverName, product, state }: { serverId: string; serverName: string; product: string; state: ServerState }) {
  const client = useQueryClient()
  const connections = useQuery({ queryKey: ['state-connections'], queryFn: stateApi.connections.list })
  const [target, setTarget] = useState('')
  const [preflight, setPreflight] = useState<Preflight | null>(null)
  const [phase, setPhase] = useState<Phase>('idle')
  const [progress, setProgress] = useState<MoveProgress | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const running = useRef(false)

  useEffect(() => () => { running.current = false }, [])

  const check = useMutation({
    mutationFn: () => stateApi.move.preflight(serverId, target),
    onSuccess: (answer) => { setPreflight(answer); setProblem(null) },
  })

  const run = async () => {
    running.current = true
    setProblem(null)
    setProgress(null)
    try {
      if (!state.maintenance.enabled) {
        setPhase('maintenance')
        await stateApi.server.maintenance(serverId, true, `moving the state to ${target}`)
      }
      setPhase('draining')
      for (let i = 0; i < 180; i++) {
        const status = await stateApi.server.maintenanceStatus(serverId)
        if (status.enabled && status.idle) break
        if (i === 179) throw new Error('The server did not become idle within three minutes: something is still in flight. Maintenance stays on; check what it is doing, or end maintenance.')
        await sleep(1000)
      }
      setPhase('moving')
      let last = await stateApi.move.start(serverId, target)
      setProgress(last)
      while (running.current && (last.state === 'running' || last.state === 'none')) {
        await sleep(1000)
        last = await stateApi.move.progress(serverId)
        setProgress(last)
      }
      setPhase(last.state === 'succeeded' ? 'done' : 'failed')
    } catch (error) {
      setProblem(sentenceOf(error))
      setPhase('failed')
    } finally {
      running.current = false
      client.invalidateQueries({ queryKey: ['server-state', serverId] })
    }
  }

  const busy = phase === 'maintenance' || phase === 'draining' || phase === 'moving'
  const tables = (progress?.tables ?? []).filter((t) => t.step === progress?.step)
  const old = preflight?.old === true

  return (
    <div className="card" style={{ marginTop: 16 }} data-testid="move-wizard">
      <div className="card-head"><span className="card-title">Move the state of {serverName}</span></div>
      <div className="card-body">
        <p className="page-note">
          Moves {serverName}&apos;s state ({engineName(state.engine)}) to another database. The server stops taking work, finishes what it is doing, and copies its state itself, on its own machine; it verifies the copy and only then writes its settings. The old database is not touched.
        </p>
        <div className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <Field label="Target state connection">
            <select className="input" value={target} disabled={busy} onChange={(e) => { setTarget(e.target.value); setPreflight(null); setPhase('idle') }} data-testid="move-target">
              <option value="">Choose a state connection</option>
              {(connections.data ?? []).filter((c) => c.engine !== 'sqlite').map((c) => <option key={c.name} value={c.name}>{`${c.name} (${engineName(c.engine)})`}</option>)}
            </select>
          </Field>
          <button type="button" className="btn" disabled={!target || check.isPending || busy} onClick={() => check.mutate()} data-testid="move-check">Check</button>
        </div>
        {connections.isSuccess && (connections.data ?? []).length === 0 && <div className="page-note" data-testid="move-no-connections">No state connection yet: add one under <Link to="/connections">Connections</Link>.</div>}
        <ErrorBanner error={check.error} />
        {preflight && (
          <ul data-testid="move-checks" style={{ marginTop: 10 }}>
            {preflight.checks.map((c) => <li key={c.name} data-testid="move-check-item" data-ok={c.ok} data-name={c.name}>{`${c.ok ? 'OK' : 'Not ready'}: ${c.sentence}`}</li>)}
          </ul>
        )}
        {old && preflight?.program && (
          <div className="empty" data-testid="move-old-server">
            This server cannot move its state from the console. On its machine, stop the service and run <span className="mono">{preflight.program} state move --to-engine &lt;engine&gt; --to-connection &quot;&lt;connection with Password=secret:name&gt;&quot; --yes</span>, then start it.
          </div>
        )}
        {preflight?.ok && phase === 'idle' && (
          <div style={{ marginTop: 10 }}>
            <button type="button" className="btn btn-primary" onClick={() => void run()} data-testid="move-start">Start the move</button>
            <span className="page-note"> The server goes into maintenance first and refuses work until the move is over and it has been restarted.</span>
          </div>
        )}
        {phase !== 'idle' && (
          <ol style={{ marginTop: 12 }} data-testid="move-steps">
            {PHASES.map((p, index) => {
              const at = PHASES.findIndex((x) => x.phase === phase)
              const status = phase === 'done' ? 'done' : phase === 'failed' && at === -1 ? 'done' : index < at ? 'done' : index === at ? (phase === 'failed' ? 'failed' : 'running') : 'waiting'
              return <li key={p.phase} data-testid={`move-step-${p.phase}`} data-status={status}>{`${p.label}: ${status}`}</li>
            })}
          </ol>
        )}
        {progress && progress.state !== 'none' && (
          <div data-testid="move-progress" data-state={progress.state} data-step={progress.step}>
            <div>{`Step ${progress.step}: ${progress.message ?? ''}`}</div>
            {(progress.tables_total ?? 0) > 0 && <div>{`${progress.tables_done} of ${progress.tables_total} tables compared`}</div>}
            {tables.length > 0 && <div className="page-note" data-testid="move-tables">{tables.map((t) => `${t.module}.${t.table}${t.rows !== undefined ? ` (${t.rows} rows)` : ''}${t.done ? ' done' : ''}`).join(', ')}</div>}
          </div>
        )}
        {problem && <div className="empty" role="alert" data-testid="move-problem">{problem}</div>}
        {phase === 'done' && progress && (
          <div className="empty" role="status" data-testid="move-done">
            <div>{progress.summary?.split('\n')[0] ?? 'The state was moved.'}</div>
            <div data-testid="move-restart"><strong>Next:</strong> {progress.restart}</div>
            <div data-testid="move-way-back"><strong>Way back:</strong> {progress.way_back}</div>
            <div className="page-note">{`${serverName} stays in maintenance until it is restarted (a restart leaves maintenance). The product is ${product}; restart it the way you start it.`}</div>
          </div>
        )}
        {phase === 'failed' && progress?.state === 'failed' && (
          <div className="empty" role="alert" data-testid="move-failed">
            <div>{progress.error}</div>
            <div data-testid="move-kept">{progress.kept}</div>
            <div className="page-note">Maintenance is still on: end it from the panel above when you are ready, or fix the cause and start again.</div>
          </div>
        )}
      </div>
    </div>
  )
}
