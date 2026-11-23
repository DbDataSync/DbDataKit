import { useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { workspaceApi, type CheckDiagnostic } from '../api'
import { liveState, type EditorKind } from '../editors'
import {
  connectionPath, connectionsOf, newConnectionText, newReplicationFiles, replicationPath, replicationsOf,
  secretFindings, syncNameProblem, syncSchemaOf, syncSchemaSet, withCurrentTaskVersion,
} from '../syncSchemas'
import type { LiveCheck } from '../useLiveCheck'
import type { CacheRead } from '../catalogCache'
import { MappingColumns } from './MappingColumns'
import { findingsKey } from '../draft'
import { roleWords, useSyncEngines, type SyncEngineList } from '../syncEngines'
import { parseYaml } from '@dbdatakit/shell'
import { StructuredEditor } from './StructuredPanel'


/** What stands under a connection's editor: the secrets rule, and a finding for each place a password would reach git (the same ones the check refuses by file and line). */
export function SecretNotes({ text }: { text: string }) {
  const found = useMemo(() => secretFindings(text), [text])
  return (
    <div style={{ marginTop: 10 }} data-testid="wsp-secret-notes" data-problems={found.length}>
      <div className="hint">A connection names its secret by reference (<code>secret:dbdatakit:sync:connection:&lt;name&gt;</code>); the value is set on the server that runs the replication and is never in this draft.</div>
      {found.map((f, i) => <div key={i} className="banner error" role="alert" style={{ marginTop: 6 }} data-testid="wsp-secret-finding"><span className="mark">!</span><span>Line {f.line} {f.message}</span></div>)}
    </div>
  )
}

/** What the registry says of a connection's engine: whether it may be a replication's source and target, or that it is a driver of this project, or that neither knows it. */
export function EngineNote({ text, engines }: { text: string; engines: SyncEngineList }) {
  const doc = parseYaml(text).value as { engine?: unknown } | undefined
  const name = typeof doc?.engine === 'string' ? doc.engine : ''
  if (name === '') return null
  const engine = engines.find(name)
  if (!engine) return <div className="hint" style={{ marginTop: 10 }} data-testid="wsp-engine-note" data-engine={name} data-known="false">The engine <code>{name}</code> is neither in the suite's engine registry nor a driver of this project (<code>drivers/{name}/</code>).</div>
  return (
    <div className="hint" style={{ marginTop: 10 }} data-testid="wsp-engine-note" data-engine={engine.id} data-custom={engine.custom} data-source={engine.source.status} data-target={engine.target.status}>
      <div>{engine.displayName}{engine.custom ? ' (custom: a driver of this project)' : ''}</div>
      <div data-testid="wsp-engine-source">As a source: {roleWords(engine.source)}{engine.source.offered ? '' : ' (not offered)'}</div>
      <div data-testid="wsp-engine-target">As a target: {roleWords(engine.target)}{engine.target.offered ? '' : ' (not offered)'}</div>
    </div>
  )
}

/** The engines a new connection may name: those the registry offers as a source or a target, and the project's own drivers marked custom; a planned role is said and not offered. */
export function EngineOptions({ engines }: { engines: SyncEngineList }) {
  return (
    <>
      {engines.all.filter((e) => e.source.offered || e.target.offered).map((e) => {
        const as = [e.source.offered ? 'source' : null, e.target.offered ? 'target' : null].filter(Boolean).join(' and ')
        const planned = [e.source.status === 'planned' ? 'source' : null, e.target.status === 'planned' ? 'target' : null].filter(Boolean).join(' and ')
        return <option key={e.id} value={e.id} data-custom={e.custom}>{e.id}{e.custom ? ' (custom)' : ''}: {as}{planned ? ` (${planned} planned)` : ''}</option>
      })}
    </>
  )
}

/** A file of a Sync replication set as a form (from the schema of its kind) or as YAML, with Sync's own readers' findings as the draft is checked. A task is written in the current format whatever it was read as. */
export function SyncFileEditor({ kind, path, text, onText, readOnly, live, onOpen, catalog, paths = [] }: { catalog?: CacheRead | null; paths?: readonly string[]; kind: Extract<EditorKind, 'sync-task' | 'sync-mapping' | 'sync-connection'>; path: string; text: string; onText: (text: string) => void; readOnly: boolean; live: LiveCheck; onOpen?: (path: string) => void }) {
  const diagnostics: CheckDiagnostic[] = live.diagnostics
  const engines = useSyncEngines(paths)
  const state = liveState({ text, checkedText: live.checkedText, failed: live.failed, running: live.running })
  const set = kind === 'sync-task' ? (value: string) => onText(withCurrentTaskVersion(value)) : onText
  const notes = (
    <>
      {kind === 'sync-connection' && <EngineNote text={text} engines={engines} />}
      {kind === 'sync-connection' && <SecretNotes text={text} />}
      {kind === 'sync-mapping' && <div className="hint" style={{ marginTop: 10 }}>The column cache (<code>sourceColumns</code>, <code>targetColumns</code>) is what the check and the package are made from. It is written from the catalog cache below (or edited in the YAML tab); no database is asked here.</div>}
      {kind === 'sync-mapping' && <MappingColumns mapping={text} catalog={catalog ?? null} readOnly={readOnly} onApply={onText} />}
      {live.failed && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="wsp-live-failed"><span className="mark">!</span><span>{live.failed}</span></div>}
    </>
  )
  const label = kind === 'sync-task' ? 'A replication' : kind === 'sync-mapping' ? 'A table mapping' : 'A connection'
  return (
    <StructuredEditor schemas={syncSchemaSet} schema={syncSchemaOf(kind)} path={path} text={text} onText={set} readOnly={readOnly} diagnostics={diagnostics} state={state} summary={label} notes={notes} onOpen={onOpen} />
  )
}

/** One name and a button, checked as it is typed. */
function NameForm({ label, placeholder, problem, disabled, testId, button, children, onSubmit, value, onValue }: { label: string; placeholder: string; problem: string | null; disabled: boolean; testId: string; button: string; children?: ReactNode; onSubmit: () => void; value: string; onValue: (v: string) => void }) {
  const ok = value !== '' && problem === null && !disabled
  return (
    <form className="row" style={{ gap: 6, marginTop: 8, flexWrap: 'wrap', alignItems: 'center' }} data-testid={testId} onSubmit={(e) => { e.preventDefault(); if (ok) onSubmit() }}>
      <input className="input mono" style={{ flex: 1, minWidth: 120 }} aria-label={label} placeholder={placeholder} value={value} onChange={(e) => onValue(e.target.value)} data-testid={`${testId}-name`} />
      {children}
      <button type="submit" className="btn" disabled={!ok} data-testid={`${testId}-go`}>{button}</button>
      {problem && <span className="hint" data-testid={`${testId}-why`}>{problem}</span>}
    </form>
  )
}

/** The replication set of a Sync project at a glance: its replications (each with the mappings it holds) and connections, each opening its file, and the forms that make a new replication or connection. */
export function SyncSetPanel({ paths, canWrite, busy, onOpen, onCreate, findings }: { paths: readonly string[]; canWrite: boolean; busy: boolean; onOpen: (path: string) => void; onCreate: (files: { path: string; text: string }[]) => void; findings?: readonly CheckDiagnostic[] }) {
  const replications = replicationsOf(paths)
  const connections = connectionsOf(paths)
  const mappingsOf = (name: string) => paths.filter((p) => p.startsWith(`config/replications/${name}/table-mappings/`))
  const errorsIn = (prefix: string) => (findings ?? []).filter((d) => d.severity === 'error' && d.file !== null && d.file.startsWith(prefix)).length
  const [replication, setReplication] = useState('')
  const [source, setSource] = useState('')
  const [target, setTarget] = useState('')
  const [connection, setConnection] = useState('')
  const engines = useSyncEngines(paths)
  const [engine, setEngine] = useState('postgres')
  const from = source || connections[0] || ''
  const to = target || connections[1] || connections[0] || ''
  return (
    <div className="card" data-testid="wsp-sync-set">
      <div className="card-body">
        <div className="hint" style={{ marginBottom: 6 }}>Replications</div>
        {replications.length === 0 && <div className="hint" data-testid="wsp-sync-none">No replication yet.</div>}
        {replications.map((r) => {
          const bad = errorsIn(`config/replications/${r}/`)
          return (
            <div key={r} className="grid-row" style={{ gap: 8, flexWrap: 'wrap' }} data-testid="wsp-sync-replication" data-name={r} data-errors={bad}>
              <button type="button" className="btn-link mono" data-testid="wsp-sync-open-task" onClick={() => onOpen(replicationPath(r))}>{r}</button>
              {mappingsOf(r).map((m) => <button key={m} type="button" className="btn-link mono hint" data-testid="wsp-sync-open-mapping" onClick={() => onOpen(m)}>{m.split('/').pop()}</button>)}
              {bad > 0 && <span className="error" data-testid="wsp-sync-bad">{bad} to fix</span>}
            </div>
          )
        })}
        <div className="hint" style={{ margin: '10px 0 4px' }}>Connections (by reference: no password is kept here)</div>
        {connections.length === 0 && <div className="hint" data-testid="wsp-sync-no-connections">No connection yet; a replication names two.</div>}
        {connections.map((c) => {
          const bad = errorsIn(connectionPath(c))
          return (
            <div key={c} className="grid-row" style={{ gap: 8 }} data-testid="wsp-sync-connection" data-name={c} data-errors={bad}>
              <button type="button" className="btn-link mono" data-testid="wsp-sync-open-connection" onClick={() => onOpen(connectionPath(c))}>{c}</button>
              {bad > 0 && <span className="error">{bad} to fix</span>}
              <span className="hint" data-testid="wsp-sync-connection-test">Test: on the Runs tab, after publish</span>
            </div>
          )
        })}
        {canWrite && (
          <>
            <NameForm label="New connection name" placeholder="source-db" testId="wsp-new-connection" button="New connection" value={connection} onValue={setConnection} disabled={busy}
              problem={syncNameProblem(connection, paths, connectionPath)} onSubmit={() => { onCreate([{ path: connectionPath(connection), text: newConnectionText(connection, engine) }]); setConnection('') }}>
              <select className="input" aria-label="Engine" value={engine} onChange={(e) => setEngine(e.target.value)} data-testid="wsp-new-connection-engine">
                <EngineOptions engines={engines} />
              </select>
            </NameForm>
            <NameForm label="New replication name" placeholder="orders" testId="wsp-new-replication" button="New replication" value={replication} onValue={setReplication} disabled={busy || connections.length === 0}
              problem={connections.length === 0 && replication !== '' ? 'Make the connections first: a replication names its source and its target.' : syncNameProblem(replication, paths, replicationPath)}
              onSubmit={() => { onCreate(newReplicationFiles(replication, from, to)); setReplication('') }}>
              <select className="input" aria-label="Source connection" value={from} onChange={(e) => setSource(e.target.value)} data-testid="wsp-new-replication-source">{connections.map((c) => <option key={c} value={c}>{c}</option>)}</select>
              <select className="input" aria-label="Target connection" value={to} onChange={(e) => setTarget(e.target.value)} data-testid="wsp-new-replication-target">{connections.map((c) => <option key={c} value={c}>{c}</option>)}</select>
            </NameForm>
            <div className="hint" style={{ marginTop: 6 }}>A new replication starts from the template's drivers (SQL Server changes to PostgreSQL); change its reader, cache and writer to the engines it connects.</div>
          </>
        )}
      </div>
    </div>
  )
}

export { findingsKey }

/** Why the counts are missing, when the check could not run. */
export function CheckFailedNote({ error }: { error: unknown }) {
  if (!error) return null
  return <div className="hint" data-testid="wsp-sync-check-failed">The check could not run, so the counts are not shown: {error instanceof Error ? error.message : 'no answer'}</div>
}

/**
 * The replication set with its counts fed (plan 33 SC22f): the check of the draft (Sync's own readers, through the check route) is asked when the page opens and again after each change to the files, and its diagnostics are
 * counted per replication and per connection by `SyncSetPanel`. A check that cannot run says so under the list and the counts are left out: a count of zero must never mean "not checked".
 */
export function SyncSetOverview({ project, id, ...panel }: { project: string; id: string } & Omit<Parameters<typeof SyncSetPanel>[0], 'findings'>) {
  const check = useQuery({ queryKey: findingsKey(project, id), queryFn: () => workspaceApi.check(project, id), staleTime: 30_000, retry: false })
  return (
    <>
      <SyncSetPanel {...panel} findings={check.data?.diagnostics} />
      <CheckFailedNote error={check.error} />
    </>
  )
}
