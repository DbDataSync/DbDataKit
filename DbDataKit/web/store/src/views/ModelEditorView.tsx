import type { ReactNode } from 'react'
import type { Diagnostic } from '../api'
import { errorsOf } from '../model'

export type EditorMode = 'form' | 'yaml'

/** A button that is off says why, next to itself and as its tooltip: a person is not left guessing which role or step is missing. */
export function Gated({ testId, label, className = 'btn', allowed, reason, onClick, busyLabel }: { testId: string; label: string; className?: string; allowed: boolean; reason: string | null; onClick: () => void; busyLabel?: string }) {
  return (
    <span className="row" style={{ gap: 8, alignItems: 'center' }}>
      <button type="button" className={className} disabled={!allowed} title={reason ?? undefined} onClick={onClick} data-testid={testId}>{busyLabel ?? label}</button>
      {!allowed && reason && <span className="hint" data-testid={`${testId}-why`}>{reason}</span>}
    </span>
  )
}

/** The Store's diagnostics, one line each with where it is, the sentence and the fix it suggests. */
export function DiagnosticList({ diagnostics, checking }: { diagnostics: Diagnostic[]; checking: boolean }) {
  const errors = errorsOf(diagnostics)
  return (
    <div className="card flush" data-testid="model-diagnostics" data-state={checking ? 'checking' : errors.length > 0 ? 'invalid' : 'valid'}>
      <div className="card-head"><span className="card-title">The Store's check</span></div>
      {checking && diagnostics.length === 0 && <div className="empty" data-testid="model-checking">Checking...</div>}
      {!checking && diagnostics.length === 0 && <div className="empty" data-testid="model-valid">The Store accepts this model.</div>}
      {diagnostics.map((d, i) => (
        <div key={i} className="row" style={{ gap: 10, padding: '6px 14px', alignItems: 'baseline' }} data-testid="model-diagnostic" data-severity={d.severity}>
          <span className="mono" style={{ whiteSpace: 'nowrap' }}>
            {d.line > 0 ? `line ${d.line}` : d.file}
          </span>
          <span>{d.message}{d.fix ? <span className="hint"> {d.fix}</span> : null}</span>
        </div>
      ))}
    </div>
  )
}

export interface EditorViewProps {
  isNew: boolean
  name: string
  onName?: (name: string) => void
  mode: EditorMode
  onMode: (mode: EditorMode) => void
  form: ReactNode
  yaml: ReactNode
  diff: ReactNode
  showDiff: boolean
  onShowDiff: (show: boolean) => void
  dirty: boolean
  diagnostics: Diagnostic[]
  checking: boolean
  save: { allowed: boolean; reason: string | null }
  saving: boolean
  onSave: () => void
  /** Offered for a saved model only. */
  remove?: { allowed: boolean; reason: string | null }
  onRemove?: () => void
  /** A refusal of the save in the server's words (and its diagnostics, which are shown in the list). */
  refusal: string | null
  /** The save quoted a version that is no longer the Store's: reload theirs or save over it. */
  conflict: boolean
  onReload: () => void
  onOverwrite: () => void
  savedNote: string | null
}

/** The model editor without its calls: a form and a YAML view of the same text, the Store's check of it, and Save and Remove that say why they are off. */
export function ModelEditorView(p: EditorViewProps) {
  return (
    <div data-testid="model-editor" data-dirty={p.dirty}>
      {p.isNew && (
        <div className="field" style={{ maxWidth: 360, marginBottom: 12 }}>
          <label htmlFor="model-name">Model name</label>
          <input id="model-name" className="input mono" value={p.name} placeholder="sales_territories" onChange={(e) => p.onName?.(e.target.value)} data-testid="model-name-input" />
          <span className="hint">The name is the file's name and must match the model's own <code>name</code>.</span>
        </div>
      )}
      <div className="row" style={{ gap: 8, marginBottom: 10, alignItems: 'center', flexWrap: 'wrap' }}>
        <div className="subtabbar" data-testid="model-modes">
          <button type="button" className={`subtab ${p.mode === 'form' ? 'active' : ''}`} onClick={() => p.onMode('form')} data-testid="model-mode-form">Form</button>
          <button type="button" className={`subtab ${p.mode === 'yaml' ? 'active' : ''}`} onClick={() => p.onMode('yaml')} data-testid="model-mode-yaml">YAML</button>
        </div>
        {!p.isNew && <button type="button" className="btn btn-sm" disabled={!p.dirty} title={p.dirty ? undefined : 'Nothing has changed since it was saved.'} onClick={() => p.onShowDiff(!p.showDiff)} data-testid="model-diff-toggle">{p.showDiff ? 'Hide changes' : 'Show changes'}</button>}
      </div>
      {p.mode === 'form' ? <div data-testid="model-form">{p.form}</div> : <div data-testid="model-yaml">{p.yaml}</div>}
      {p.showDiff && p.dirty && <div style={{ marginTop: 12 }} data-testid="model-diff">{p.diff}</div>}
      <div style={{ marginTop: 14 }}><DiagnosticList diagnostics={p.diagnostics} checking={p.checking} /></div>
      {p.refusal && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="model-refusal"><span className="mark">!</span><span>{p.refusal}</span></div>}
      {p.conflict && (
        <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="model-conflict">
          <span className="mark">!</span>
          <span>Someone saved this model after you opened it.</span>
          <button type="button" className="btn btn-sm" onClick={p.onReload} data-testid="model-conflict-reload">Load theirs (your edits are dropped)</button>
          <button type="button" className="btn btn-sm" onClick={p.onOverwrite} data-testid="model-conflict-overwrite">Save mine over it</button>
        </div>
      )}
      {p.savedNote && <div className="banner" role="status" style={{ marginTop: 10 }} data-testid="model-saved">{p.savedNote}</div>}
      <div className="row" style={{ gap: 14, marginTop: 14, alignItems: 'center', flexWrap: 'wrap' }}>
        <Gated testId="model-save" label={p.isNew ? 'Create model' : 'Save'} className="btn btn-primary" allowed={p.save.allowed} reason={p.save.reason} onClick={p.onSave} busyLabel={p.saving ? 'Saving...' : undefined} />
        {p.remove && p.onRemove && <Gated testId="model-remove" label="Remove model" className="btn btn-danger" allowed={p.remove.allowed} reason={p.remove.reason} onClick={p.onRemove} />}
      </div>
    </div>
  )
}
