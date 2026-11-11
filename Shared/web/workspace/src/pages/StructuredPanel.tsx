import { useState, type ReactNode } from 'react'
import { CodeEditor, SchemaForm, type SchemaSet } from '@dbdatakit/shell'
import type { CheckDiagnostic } from '../api'
import { liveWord, markersFor, splitFindings, type LiveState } from '../editors'

export type EditorMode = 'form' | 'yaml'

/** The findings of the check about this file, one line each with where it is; those about other files are counted, each with its own button. */
export function FindingsList({ path, diagnostics, state, onOpen }: { path: string; diagnostics: readonly CheckDiagnostic[]; state: LiveState; onOpen?: (path: string) => void }) {
  const { here, elsewhere } = splitFindings(path, diagnostics)
  const errors = here.filter((d) => d.severity === 'error').length
  const warnings = here.filter((d) => d.severity === 'warning').length
  const otherErrors = elsewhere.filter((d) => d.severity === 'error')
  const files = [...new Set(elsewhere.map((d) => d.file).filter((f): f is string => !!f))]
  return (
    <div className="card flush" data-testid="wsp-live" data-state={state} data-errors={errors}>
      <div className="card-head"><span className="card-title">The check of this file</span><span className="hint" data-testid="wsp-live-word">{liveWord(state, errors, warnings)}</span></div>
      {here.map((d, i) => (
        <div key={i} className="row" style={{ gap: 10, padding: '6px 14px', alignItems: 'baseline' }} data-testid="wsp-live-finding" data-severity={d.severity} data-code={d.code}>
          <span className="mono" style={{ whiteSpace: 'nowrap' }}>{d.line ? `line ${d.line}` : 'file'}</span>
          <span><span className="mono">{d.code}</span> {d.message}</span>
        </div>
      ))}
      {elsewhere.length > 0 && (
        <div className="hint" style={{ padding: '6px 14px' }} data-testid="wsp-live-elsewhere">
          {elsewhere.length} more finding{elsewhere.length === 1 ? '' : 's'}{otherErrors.length > 0 ? ` (${otherErrors.length} to fix)` : ''} in other files:{' '}
          {files.map((f) => onOpen ? <button key={f} type="button" className="btn-link mono" style={{ marginRight: 8 }} data-testid="wsp-live-open" onClick={() => onOpen(f)}>{f}</button> : <span key={f} className="mono" style={{ marginRight: 8 }}>{f}</span>)}
        </div>
      )}
    </div>
  )
}

export interface StructuredViewProps {
  schemas: SchemaSet
  schema: string
  path: string
  text: string
  onText: (text: string) => void
  readOnly: boolean
  mode: EditorMode
  onMode: (mode: EditorMode) => void
  diagnostics: readonly CheckDiagnostic[]
  state: LiveState
  /** One line above the editor: what the file is. */
  summary?: ReactNode
  /** Something the editor adds under the check (a note about secrets, for one). */
  notes?: ReactNode
  onOpen?: (path: string) => void
}

/**
 * A file edited as a form made from a schema or as its YAML, two views of one text, with the check of the draft beside it (plan 33 SC21d and SC22d). The schema says which settings exist so the form can offer them; the kind's own
 * validators (the Store's model reader, Sync's strict readers) are the authority and their findings are what is shown. Without the author role the text is read only.
 */
export function StructuredEditorView(p: StructuredViewProps) {
  return (
    <div data-testid="wsp-structured" data-kind-path={p.path} data-mode={p.mode}>
      {p.summary && <div className="hint" style={{ marginBottom: 8 }} data-testid="wsp-structured-summary">{p.summary}</div>}
      <div className="subtabbar" data-testid="wsp-structured-modes" style={{ marginBottom: 10 }}>
        <button type="button" className={`subtab ${p.mode === 'form' ? 'active' : ''}`} data-testid="wsp-mode-form" onClick={() => p.onMode('form')}>Form</button>
        <button type="button" className={`subtab ${p.mode === 'yaml' ? 'active' : ''}`} data-testid="wsp-mode-yaml" onClick={() => p.onMode('yaml')}>YAML</button>
      </div>
      {p.mode === 'form'
        ? <fieldset disabled={p.readOnly} style={{ border: 0, padding: 0, margin: 0 }} data-testid="wsp-form"><SchemaForm schemas={p.schemas} schema={p.schema} text={p.text} onText={p.onText} /></fieldset>
        : <div data-testid="wsp-yaml"><CodeEditor value={p.text} language="yaml" readOnly={p.readOnly} onChange={p.onText} diagnostics={markersFor(p.path, p.diagnostics)} minLines={14} maxLines={30} testId="wsp-code" /></div>}
      {p.notes}
      <div style={{ marginTop: 12 }}><FindingsList path={p.path} diagnostics={p.diagnostics} state={p.state} onOpen={p.onOpen} /></div>
    </div>
  )
}

/** The editor with its own mode (the form first: a person who wants the text clicks YAML). */
export function StructuredEditor(p: Omit<StructuredViewProps, 'mode' | 'onMode'>) {
  const [mode, setMode] = useState<EditorMode>('form')
  return <StructuredEditorView {...p} mode={mode} onMode={setMode} />
}
