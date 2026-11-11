import { useState } from 'react'
import { CONNECTION_ENGINES, ConnectionGrantsEditor, ConnectionPolicyEditor, MODEL_KINDS, connectionPath, connectionProblem, connectionYaml, emptyConnection, modelNameProblem, type ConnectionForm } from '@dbdatakit/build/files'
import type { CheckDiagnostic } from '../api'
import { buildSchemaOf, buildSchemaSet, modelLayoutOf, newModelClash, newModelFiles } from '../buildSchemas'
import { liveState } from '../editors'
import type { LiveCheck } from '../useLiveCheck'
import { StructuredEditor } from './StructuredPanel'

/** The `.yml` of a model, a connection, a folder, a semantic model, the project and the rest of Build's files as a form (from Build's own schema of the file) or as YAML, with the findings of Build's reader as the draft is checked. */
export function BuildFileEditor({ path, text, onText, readOnly, live, onOpen }: { path: string; text: string; onText: (text: string) => void; readOnly: boolean; live: LiveCheck; onOpen?: (path: string) => void }) {
  const association = buildSchemaOf(path)
  const diagnostics: CheckDiagnostic[] = live.diagnostics
  const state = liveState({ text, checkedText: live.checkedText, failed: live.failed, running: live.running })
  if (!association) return null
  const connection = association.schema === 'connection.schema.json'
  const notes = (
    <>
      {connection && <div className="hint" style={{ marginTop: 10 }} data-testid="wsp-build-connection-note">The file holds no address and no login: the server that runs the project holds them (<code>DBDATAKIT_BUILD_&lt;NAME&gt;_READ</code> and <code>_WRITE</code>), so the file can be committed and reviewed.</div>}
      {connection && <div style={{ marginTop: 10 }}><ConnectionGrantsEditor text={text} onText={onText} readOnly={readOnly} /></div>}
      {connection && <div style={{ marginTop: 10 }}><ConnectionPolicyEditor text={text} onText={onText} readOnly={readOnly} /></div>}
      {live.failed && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="wsp-live-failed"><span className="mark">!</span><span>{live.failed}</span></div>}
    </>
  )
  return <StructuredEditor schemas={buildSchemaSet} schema={association.schema} path={path} text={text} onText={onText} readOnly={readOnly} diagnostics={diagnostics} state={state} summary={`A Build ${association.kind}`} notes={notes} onOpen={onOpen} />
}

/** The kinds in the form's order; the project's connections are the ones its draft has. */
export const connectionsOfDraft = (paths: readonly string[]): string[] => paths.map((p) => /^connections\/([^/]+)\.yaml$/.exec(p)?.[1]).filter((n): n is string => !!n)

/** **New model** (the kind, the name, where it lives): the two files `project model create` makes, written into the draft. The name is checked as it is typed, against the files already there. */
export function NewBuildModel({ paths, busy, onCreate }: { paths: readonly string[]; busy: boolean; onCreate: (files: { path: string; text: string }[]) => void }) {
  const [name, setName] = useState('')
  const [kind, setKind] = useState('view')
  const [on, setOn] = useState<string[]>([])
  const layout = modelLayoutOf(paths)
  const connections = connectionsOfDraft(paths)
  const problem = modelNameProblem(name) ?? newModelClash(name, layout, paths)
  const ok = name !== '' && problem === null && !busy
  const about = MODEL_KINDS.find((k) => k.key === kind)?.about
  return (
    <form style={{ marginTop: 10 }} data-testid="wsp-build-new-model" onSubmit={(e) => { e.preventDefault(); if (ok) { onCreate(newModelFiles(name, kind, on, layout)); setName('') } }}>
      <div className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
        <input className="input mono" style={{ flex: 1, minWidth: 140 }} aria-label="New model name" placeholder="marts.fct_returns" value={name} onChange={(e) => setName(e.target.value)} data-testid="wsp-build-new-model-name" />
        <select className="input" aria-label="Model kind" value={kind} onChange={(e) => setKind(e.target.value)} data-testid="wsp-build-new-model-kind">
          {MODEL_KINDS.map((k) => <option key={k.key} value={k.key}>{k.label}</option>)}
        </select>
        <button type="submit" className="btn" disabled={!ok} data-testid="wsp-build-new-model-go">New model</button>
      </div>
      {about && <div className="hint" style={{ marginTop: 4 }}>{about}</div>}
      {connections.length > 1 && (
        <div className="row" style={{ gap: 10, marginTop: 4, flexWrap: 'wrap' }} role="group" aria-label="Connections">
          <span className="hint">Lives on (none chosen: the default):</span>
          {connections.map((c) => <label key={c} className="row" style={{ gap: 4 }}><input type="checkbox" checked={on.includes(c)} onChange={(e) => setOn((now) => (e.target.checked ? [...now, c] : now.filter((x) => x !== c)))} data-testid={`wsp-build-new-model-on-${c}`} /><span className="mono">{c}</span></label>)}
        </div>
      )}
      {problem && <div className="hint" style={{ marginTop: 4 }} data-testid="wsp-build-new-model-why">{problem}</div>}
    </form>
  )
}

/** **New connection** (name, engine, how it is secured): `connections/<name>.yaml` as Build's own form writes it; the grants and the serving policy are then edited in the file. */
export function NewBuildConnection({ paths, busy, onCreate }: { paths: readonly string[]; busy: boolean; onCreate: (path: string, text: string) => void }) {
  const [form, setForm] = useState<ConnectionForm>(emptyConnection)
  const taken = paths.includes(connectionPath(form))
  const problem = connectionProblem(form) ?? (taken && form.name !== '' ? `${connectionPath(form)} exists already.` : null)
  const origin = CONNECTION_ENGINES.find((e) => e.key === form.engine)?.origin === true
  const ok = form.name !== '' && problem === null && !busy
  const set = (patch: Partial<ConnectionForm>) => setForm({ ...form, ...patch })
  return (
    <form style={{ marginTop: 10 }} data-testid="wsp-build-new-connection" onSubmit={(e) => { e.preventDefault(); if (ok) { onCreate(connectionPath(form), connectionYaml(form)); setForm(emptyConnection) } }}>
      <div className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
        <input className="input mono" style={{ flex: 1, minWidth: 120 }} aria-label="New connection name" placeholder="warehouse" value={form.name} onChange={(e) => set({ name: e.target.value })} data-testid="wsp-build-new-connection-name" />
        <select className="input" aria-label="Engine" value={form.engine} onChange={(e) => set({ engine: e.target.value })} data-testid="wsp-build-new-connection-engine">
          {CONNECTION_ENGINES.map((e) => <option key={e.key} value={e.key}>{e.label}</option>)}
        </select>
        <button type="submit" className="btn" disabled={!ok} data-testid="wsp-build-new-connection-go">New connection</button>
      </div>
      {!origin && (
        <div className="row" style={{ gap: 14, marginTop: 4, flexWrap: 'wrap' }}>
          <label className="row" style={{ gap: 4 }}><input type="checkbox" checked={form.encrypt} onChange={(e) => set({ encrypt: e.target.checked })} data-testid="wsp-build-new-connection-encrypt" /> Encrypt</label>
          <label className="row" style={{ gap: 4 }}><input type="checkbox" checked={form.trustCertificate} onChange={(e) => set({ trustCertificate: e.target.checked })} data-testid="wsp-build-new-connection-trust" /> Trust the certificate without checking it</label>
        </div>
      )}
      {problem && <div className="hint" style={{ marginTop: 4 }} data-testid="wsp-build-new-connection-why">{problem}</div>}
    </form>
  )
}
