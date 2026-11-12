import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ErrorBanner, type EditorDiagnostic } from '@dbdatakit/shell'
import { buildApi } from '../api'
import {
  entryValue, isSemanticFile, lineOfEntry, modelNameOf, nameProblem, parameterTypes, parameterValue, readQueries, removeEntry, renameEntry, writeEntry,
  type ParameterDef, type QueryEntry,
} from '../semanticQueries'
import type { PairedProxy, QueryCheckAnswer, TryQueryAnswer } from '../types'

export { isSemanticFile }

/** What the translation check says, as markers on the file's lines (the YAML tab draws them; the form says them under the query). */
export function checkMarkers(text: string, name: string, kind: 'sql' | 'request', answer: QueryCheckAnswer | undefined): EditorDiagnostic[] {
  if (!answer) return []
  const line = lineOfEntry(text, name, kind) ?? 1
  return answer.errors.map((e) => ({ line, column: 1, message: `${e.code}: ${e.message}`, severity: 'error' as const }))
}

/** The sentence under a query's Try button: why it is off, or null. */
export function tryBlock(proxy: string, model: string, invalid: boolean, busy: boolean, check: QueryCheckAnswer | undefined): string | null {
  if (busy) return 'Running…'
  if (proxy === '') return 'Choose the Gateway the model is published to.'
  if (invalid) return 'The request is not valid JSON.'
  if (check && !check.ok) return 'Fix what the check found first.'
  if (model === '') return 'The file names no model.'
  return null
}

interface EntryProps {
  entry: QueryEntry
  others: string[]
  project: string
  workspace: string
  model: string
  text: string
  readOnly: boolean
  proxies: PairedProxy[]
  onChange: (entry: QueryEntry) => void
  onRename: (to: string) => void
  onRemove: () => void
  onMarkers: (name: string, markers: EditorDiagnostic[]) => void
}

function ParameterRow({ index, name, def, found, readOnly, onChange, onRemove }: {
  index: number; name: string; def: ParameterDef; found: boolean; readOnly: boolean; onChange: (def: ParameterDef) => void; onRemove: () => void
}) {
  const id = (field: string) => `build-queries-param-${index}-${field}`
  return (
    <div className="row" style={{ gap: 8, marginBottom: 6, flexWrap: 'wrap', alignItems: 'center' }} data-testid={`build-queries-param-${name}`}>
      <span className="mono" style={{ minWidth: 90 }}>{name}</span>
      <input className="input mono" style={{ width: 150 }} placeholder="for (an attribute)" aria-label={`${name}: for`} value={def.for ?? ''} disabled={readOnly} onChange={(e) => onChange({ ...def, for: e.target.value || undefined })} data-testid={id('for')} />
      <select className="input" aria-label={`${name}: type`} value={def.type ?? ''} disabled={readOnly} onChange={(e) => onChange({ ...def, type: (e.target.value || undefined) as ParameterDef['type'] })} data-testid={id('type')}>
        <option value="">type: from what it is compared with</option>
        {parameterTypes.map((t) => <option key={t} value={t}>{t}</option>)}
      </select>
      <label className="row" style={{ gap: 4 }}><input type="checkbox" checked={def.many === true} disabled={readOnly} onChange={(e) => onChange({ ...def, many: e.target.checked || undefined })} data-testid={id('many')} /> many</label>
      <label className="row" style={{ gap: 4 }}><input type="checkbox" checked={def.required === true} disabled={readOnly} onChange={(e) => onChange({ ...def, required: e.target.checked ? true : undefined })} data-testid={id('required')} /> required</label>
      <input className="input mono" style={{ width: 120 }} placeholder="default" aria-label={`${name}: default`} value={def.default === undefined ? '' : String(def.default)} disabled={readOnly}
        onChange={(e) => onChange({ ...def, default: e.target.value === '' ? undefined : coerce(e.target.value, def.type) })} data-testid={id('default')} />
      <select className="input" aria-label={`${name}: empty`} value={def.empty ?? ''} disabled={readOnly} onChange={(e) => onChange({ ...def, empty: (e.target.value || undefined) as ParameterDef['empty'] })} data-testid={id('empty')}>
        <option value="">an empty list: an error</option>
        <option value="matches_none">an empty list matches nothing</option>
      </select>
      {!found && <span className="hint">not used by the query</span>}
      {!readOnly && <button type="button" className="btn btn-sm" onClick={onRemove} data-testid={id('remove')}>Remove</button>}
    </div>
  )
}

/** A default typed in the form, as the file holds it: a number or a boolean when the parameter's type says so. */
function coerce(text: string, type: string | undefined): unknown {
  if (type === 'integer' || type === 'number') return Number.isFinite(Number(text)) ? Number(text) : text
  if (type === 'boolean') return text === 'true' ? true : text === 'false' ? false : text
  return text
}

function Entry(p: EntryProps) {
  const { entry } = p
  const [check, setCheck] = useState<QueryCheckAnswer | undefined>()
  const [proxy, setProxy] = useState(p.proxies.length === 1 ? p.proxies[0].name : '')
  const [values, setValues] = useState<Record<string, string>>({})
  const [name, setName] = useState(entry.name)
  const sent = useRef('')
  const value = useMemo(() => entryValue(entry), [entry])
  const requestProblem = entry.kind === 'request' && value === null ? 'The request is not valid JSON.' : null

  const checking = useMutation({
    mutationFn: (body: Record<string, unknown>) => buildApi.checkQuery(p.project, p.workspace, p.model, body),
    onSuccess: setCheck,
  })
  // the check runs by itself when the saved text of the entry changes: the form is the editor, the compiler's own translator is the judge
  const key = JSON.stringify(value)
  useEffect(() => {
    if (value === null || key === sent.current) return
    sent.current = key
    const timer = setTimeout(() => checking.mutate(value), 300)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])
  useEffect(() => { p.onMarkers(entry.name, checkMarkers(p.text, entry.name, entry.kind, check)); return () => p.onMarkers(entry.name, []) }, [check, p.text, entry.name, entry.kind]) // eslint-disable-line react-hooks/exhaustive-deps

  const asked = check?.parameters ?? []
  const names = [...new Set([...asked.map((a) => a.name), ...entry.parameters.map((q) => q.name)])]

  const run = useMutation({
    mutationFn: () => {
      const parameters: Record<string, unknown> = {}
      for (const n of names) {
        const shape = asked.find((a) => a.name === n)
        const def = entry.parameters.find((q) => q.name === n)?.def
        const parsed = parameterValue(values[n] ?? '', def?.type ?? shape?.type, def?.many ?? shape?.many ?? false)
        if (parsed && 'error' in parsed) throw new Error(`${n}: ${parsed.error}`)
        if (parsed) parameters[n] = parsed.value
      }
      return buildApi.tryQuery(proxy, { model: p.model, ...(entry.kind === 'sql' ? { sql: entry.body } : { request: JSON.parse(entry.body) }), parameters, limit: 20 })
    },
  })
  const why = tryBlock(proxy, p.model, requestProblem !== null, run.isPending, check)
  const answer: TryQueryAnswer | undefined = run.data
  const line = lineOfEntry(p.text, entry.name, entry.kind)

  const setDef = (pname: string, def: ParameterDef) => p.onChange({ ...entry, parameters: entry.parameters.map((q) => (q.name === pname ? { ...q, def } : q)) })
  const renameProblem = name === entry.name ? null : nameProblem(name, p.others)

  return (
    <div className="card" style={{ marginBottom: 12 }} data-testid={`build-queries-entry-${entry.name}`}>
      <div className="card-head">
        <span className="card-title mono">{entry.name}</span>
        {line !== null && <span className="hint">line {line}</span>}
      </div>
      <div className="card-body">
        <div className="row" style={{ gap: 8, marginBottom: 8, flexWrap: 'wrap' }}>
          <input className="input mono" style={{ width: 200 }} aria-label="Name" value={name} disabled={p.readOnly} onChange={(e) => setName(e.target.value)} data-testid="build-queries-name" />
          <button type="button" className="btn btn-sm" disabled={p.readOnly || name === entry.name || renameProblem !== null} onClick={() => p.onRename(name)} data-testid="build-queries-rename">Rename</button>
          {renameProblem && <span className="hint" role="alert">{renameProblem}</span>}
          <select className="input" aria-label="Kind" value={entry.kind} disabled={p.readOnly} onChange={(e) => p.onChange({ ...entry, kind: e.target.value as 'sql' | 'request', body: e.target.value === 'sql' ? `SELECT  FROM ${p.model}` : '{\n  "kind": "query",\n  "query": {}\n}' })} data-testid="build-queries-kind">
            <option value="sql">Semantic SQL</option>
            <option value="request">Request (JSON)</option>
          </select>
          {!p.readOnly && <button type="button" className="btn btn-sm" onClick={p.onRemove} data-testid="build-queries-remove">Remove the query</button>}
        </div>
        <input className="input" style={{ width: '100%', marginBottom: 8 }} placeholder="description" aria-label="Description" value={entry.description} disabled={p.readOnly} onChange={(e) => p.onChange({ ...entry, description: e.target.value })} data-testid="build-queries-description" />
        <textarea className="input mono" style={{ width: '100%', minHeight: 110 }} spellCheck={false} aria-label={entry.kind === 'sql' ? 'Semantic SQL' : 'Request'} value={entry.body} disabled={p.readOnly} onChange={(e) => p.onChange({ ...entry, body: e.target.value })} data-testid="build-queries-body" />
        {requestProblem && <div className="hint" role="alert" data-testid="build-queries-request-problem">{requestProblem}</div>}
        {check && (
          <div style={{ marginTop: 6 }} data-testid="build-queries-check">
            {check.ok && <div className="hint" data-testid="build-queries-ok">The translation is fine{check.parameters.length > 0 ? `; it asks for ${check.parameters.map((a) => a.name).join(', ')}` : ''}.</div>}
            {check.errors.map((e, i) => <div key={i} role="alert" className="mono" style={{ color: 'var(--danger, #b00020)' }} data-testid="build-queries-error">{line !== null ? `line ${line}: ` : ''}{e.code}: {e.message}</div>)}
            {check.warnings.map((w, i) => <div key={i} className="hint" data-testid="build-queries-warning">{w.code}: {w.message}</div>)}
          </div>
        )}
        <ErrorBanner error={checking.error} />

        <div style={{ marginTop: 10 }}>
          <strong>Parameters</strong>
          <div className="hint">A marker in the text is a parameter. Say what it is for (an attribute of the model), its type, whether it takes many values, a default and whether it is required.</div>
          {entry.parameters.map((q) => (
            <ParameterRow key={q.name} index={entry.parameters.indexOf(q)} name={q.name} def={q.def} found={asked.length === 0 || asked.some((a) => a.name === q.name)} readOnly={p.readOnly}
              onChange={(def) => setDef(q.name, def)} onRemove={() => p.onChange({ ...entry, parameters: entry.parameters.filter((x) => x.name !== q.name) })} />
          ))}
          {!p.readOnly && asked.filter((a) => !entry.parameters.some((q) => q.name === a.name)).map((a) => (
            <button key={a.name} type="button" className="btn btn-sm" style={{ marginRight: 6 }}
              onClick={() => p.onChange({ ...entry, parameters: [...entry.parameters, { name: a.name, def: { ...(a.for ? { for: a.for } : {}), ...(a.type ? { type: a.type as ParameterDef['type'] } : {}), ...(a.many ? { many: true } : {}) } }] })}
              data-testid={`build-queries-define-${a.name}`}>Define {a.name}</button>
          ))}
        </div>

        <div style={{ marginTop: 10 }} data-testid="build-queries-try">
          <strong>Try on a Gateway</strong>
          <div className="hint">Runs on the version the Gateway serves now, as you, with the Gateway’s access rules and limits. That is the published model, not this draft: publish it first to see a change.</div>
          {p.proxies.length === 0 && <div className="empty" data-testid="build-queries-no-gateway">This server is not paired with a Gateway. Link it to one on the console’s Servers page.</div>}
          {p.proxies.length > 0 && (
            <>
              <div className="row" style={{ gap: 8, margin: '6px 0', flexWrap: 'wrap' }}>
                <select className="input" aria-label="Gateway" value={proxy} onChange={(e) => setProxy(e.target.value)} data-testid="build-queries-proxy">
                  <option value="">Gateway…</option>
                  {p.proxies.map((x) => <option key={x.name} value={x.name}>{x.name}</option>)}
                </select>
                {names.map((n) => (
                  <input key={n} className="input mono" style={{ width: 150 }} placeholder={`${n}${(entry.parameters.find((q) => q.name === n)?.def.many ?? asked.find((a) => a.name === n)?.many) ? ' (a, b, c)' : ''}`} aria-label={`Value of ${n}`} value={values[n] ?? ''}
                    onChange={(e) => setValues({ ...values, [n]: e.target.value })} data-testid={`build-queries-value-${n}`} />
                ))}
                <button type="button" className="btn btn-primary" disabled={why !== null} title={why ?? undefined} onClick={() => run.mutate()} data-testid="build-queries-run">{run.isPending ? 'Running…' : 'Try'}</button>
              </div>
              {why && !run.isPending && <div className="hint" data-testid="build-queries-run-why">{why}</div>}
              <ErrorBanner error={run.error} />
              {answer && (
                <div data-testid="build-queries-result">
                  <div className="hint">{answer.status}{answer.model_version !== undefined && answer.model_version !== null ? ` on version ${answer.model_version}` : ''}; {answer.rows?.length ?? 0} row(s)</div>
                  {(answer.warnings ?? []).map((w, i) => <div key={i} className="hint">{w}</div>)}
                  <div className="card flush" style={{ overflowX: 'auto' }}>
                    <table className="mono" style={{ fontSize: 12 }}>
                      <thead><tr>{(answer.columns ?? []).map((c) => <th key={c.name} style={{ textAlign: 'left', padding: '2px 8px' }}>{c.name}</th>)}</tr></thead>
                      <tbody>{(answer.rows ?? []).slice(0, 20).map((row, r) => <tr key={r}>{row.map((cell, c) => <td key={c} style={{ padding: '2px 8px' }}>{cell === null ? '' : String(cell)}</td>)}</tr>)}</tbody>
                    </table>
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  )
}

export interface QueriesEditorProps {
  project: string
  workspace: string
  path: string
  text: string
  onText: (text: string) => void
  readOnly?: boolean
  /** What the translation check found, as markers for the YAML tab's editor. */
  onMarkers?: (markers: EditorDiagnostic[]) => void
}

/** The saved queries of a model file: a form for each entry of `queries:`, the translation check at its line, and a try-run on a Gateway (plan 14a SL20). The form and the YAML tab edit the same text. */
export function QueriesEditor({ project, workspace, path, text, onText, readOnly = false, onMarkers }: QueriesEditorProps) {
  const read = readQueries(text)
  const model = modelNameOf(text, path)
  const proxies = useQuery({ queryKey: ['build', 'proxies'], queryFn: () => buildApi.proxies() })
  const [newName, setNewName] = useState('')
  const [failure, setFailure] = useState<string | null>(null)
  const markers = useRef<Record<string, EditorDiagnostic[]>>({})
  const names = read.entries.map((e) => e.name)
  const addProblem = newName === '' ? null : nameProblem(newName, names)

  const change = (action: () => string) => {
    try { onText(action()); setFailure(null) } catch (e) { setFailure(e instanceof Error ? e.message : String(e)) }
  }
  const report = (name: string, found: EditorDiagnostic[]) => {
    markers.current[name] = found
    onMarkers?.(Object.values(markers.current).flat())
  }

  return (
    <div style={{ marginTop: 14 }} data-testid="build-queries">
      <div className="row" style={{ gap: 12, marginBottom: 8, flexWrap: 'wrap' }}>
        <strong>Saved queries of {model}</strong>
        <span className="hint">Published with the model; people run them as <span className="mono">SELECT * FROM name(…)</span> and from the explorer.</span>
      </div>
      {read.problem && <div className="hint" role="alert" data-testid="build-queries-problem">{read.problem}</div>}
      {failure && <div className="hint" role="alert" data-testid="build-queries-failure">{failure}</div>}
      {!read.problem && read.entries.length === 0 && <div className="empty" data-testid="build-queries-none">This model has no saved query yet.</div>}
      {!read.problem && read.entries.map((entry) => (
        <Entry
          key={entry.name}
          entry={entry}
          others={names.filter((n) => n !== entry.name)}
          project={project}
          workspace={workspace}
          model={model}
          text={text}
          readOnly={readOnly}
          proxies={proxies.data ?? []}
          onChange={(next) => change(() => writeEntry(text, next))}
          onRename={(to) => change(() => renameEntry(text, entry.name, to))}
          onRemove={() => change(() => removeEntry(text, entry.name))}
          onMarkers={report}
        />
      ))}
      {!read.problem && !readOnly && (
        <div className="row" style={{ gap: 8, alignItems: 'center' }}>
          <input className="input mono" style={{ width: 200 }} placeholder="new query name" aria-label="New query name" value={newName} onChange={(e) => setNewName(e.target.value)} data-testid="build-queries-new-name" />
          <button type="button" className="btn" disabled={newName === '' || addProblem !== null}
            onClick={() => { change(() => writeEntry(text, { name: newName, description: '', kind: 'sql', body: `SELECT  FROM ${model}`, parameters: [] })); setNewName('') }} data-testid="build-queries-add">Add a query</button>
          {addProblem && <span className="hint" role="alert">{addProblem}</span>}
        </div>
      )}
    </div>
  )
}
