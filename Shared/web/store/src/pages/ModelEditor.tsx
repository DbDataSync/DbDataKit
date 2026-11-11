import { useEffect, useMemo, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { CodeEditor, DiffViewer, SchemaForm, SchemaSet, parseYaml, setAt, useNavigate } from '@dbdatakit/shell'
import { refusalOf, storeApi, type Diagnostic } from '../api'
import { isModelName, markersOf, saveState } from '../model'
import { MODEL_SCHEMA, modelSchema } from '../modelSchema'
import { ModelEditorView, type EditorMode } from '../views/ModelEditorView'

const schemas = new SchemaSet({ [MODEL_SCHEMA]: modelSchema })
const DEBOUNCE_MS = 350

/** The Store's check of a text, asked a moment after the last keystroke. `stale` says the answer is about an older text. */
function useChecked(name: string | null, text: string, initial: { text: string; diagnostics: Diagnostic[] } | null) {
  const [answer, setAnswer] = useState<{ text: string; diagnostics: Diagnostic[]; failed: string | null } | null>(initial ? { ...initial, failed: null } : null)
  const latest = useRef(0)
  useEffect(() => {
    if (name === null || answer?.text === text) return
    const ticket = ++latest.current
    const timer = setTimeout(() => {
      storeApi.validate(name, text).then(
        (r) => { if (ticket === latest.current) setAnswer({ text, diagnostics: r.diagnostics, failed: null }) },
        (e) => { if (ticket === latest.current) setAnswer({ text, diagnostics: [], failed: refusalOf(e).message }) },
      )
    }, DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [name, text, answer?.text])
  return { diagnostics: answer?.diagnostics ?? [], failed: answer?.failed ?? null, stale: name !== null && answer?.text !== text }
}

export interface ModelEditorProps {
  /** The model's name when it exists; null for a new one (the person types the name). */
  existing: { name: string; text: string; version: string; diagnostics: Diagnostic[]; can: { save: boolean; remove: boolean; admin: boolean } } | null
  /** What a new model's caller may do (the Store says what a saved one allows). */
  canWriteNew: boolean
  startText: string
}

/** The model definition editor: the form and the YAML are two views of one text; the Store checks it as it changes and decides on save. */
export function ModelEditor({ existing, canWriteNew, startText }: ModelEditorProps) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const isNew = existing === null
  const [typedName, setTypedName] = useState('')
  const name = existing?.name ?? typedName
  const [saved, setSaved] = useState(existing ? { text: existing.text, version: existing.version } : null)
  const [text, setText] = useState(existing?.text ?? startText)
  const [mode, setMode] = useState<EditorMode>('form')
  const [showDiff, setShowDiff] = useState(false)
  const [saving, setSaving] = useState(false)
  const [refusal, setRefusal] = useState<string | null>(null)
  const [refusedDiagnostics, setRefusedDiagnostics] = useState<Diagnostic[]>([])
  const [conflictVersion, setConflictVersion] = useState<string | null | undefined>(undefined)
  const [note, setNote] = useState<string | null>(null)
  const [confirmRemove, setConfirmRemove] = useState(false)
  const [removeError, setRemoveError] = useState<string | null>(null)

  const checkName = isModelName(name) ? name : null
  const checked = useChecked(checkName, text, existing ? { text: existing.text, diagnostics: existing.diagnostics } : null)
  const diagnostics = refusedDiagnostics.length > 0 && checked.stale ? refusedDiagnostics : checked.diagnostics
  const dirty = saved === null || text !== saved.text
  const canWrite = existing ? existing.can.save : canWriteNew
  const save = saveState({ isNew, name, canWrite, changed: dirty, checking: checkName !== null && checked.stale, stale: checkName === null ? false : checked.stale, diagnostics, saving })
  const parsed = useMemo(() => parseYaml(text), [text])

  const edit = (next: string) => { setText(next); setNote(null); setRefusal(null); setRefusedDiagnostics([]); setConflictVersion(undefined) }
  const rename = (next: string) => {
    setTypedName(next)
    if (parsed.errors.length === 0 && next !== '') edit(setAt(text, ['name'], next))
  }

  const send = async (expected: string | null) => {
    setSaving(true); setRefusal(null); setNote(null)
    try {
      const done = await storeApi.save(name, text, expected)
      setSaved({ text, version: done.version })
      setConflictVersion(undefined)
      await queryClient.invalidateQueries({ queryKey: ['store'] })
      if (isNew) navigate(`/model/${encodeURIComponent(name)}`)
      else setNote(`Saved as version ${done.version.slice(0, 8)}.`)
    } catch (e) {
      const r = refusalOf(e)
      if (r.status === 409) setConflictVersion(r.currentVersion)
      else { setRefusal(r.message); setRefusedDiagnostics(r.diagnostics) }
    } finally {
      setSaving(false)
    }
  }

  const reload = async () => {
    if (!existing) return
    const fresh = await storeApi.model(existing.name)
    setSaved({ text: fresh.text, version: fresh.version })
    setText(fresh.text); setConflictVersion(undefined); setRefusal(null); setNote('Loaded the version someone else saved.')
  }

  const remove = async () => {
    if (!existing) return
    try {
      await storeApi.remove(existing.name)
      await queryClient.invalidateQueries({ queryKey: ['store'] })
      navigate('/')
    } catch (e) { setRemoveError(refusalOf(e).message); setConfirmRemove(false) }
  }

  const removeState = existing
    ? { allowed: existing.can.remove, reason: existing.can.remove ? null : 'Removing a model needs the administrator role.' }
    : undefined

  return (
    <>
      <ModelEditorView
        isNew={isNew} name={name} onName={rename}
        mode={mode} onMode={setMode}
        form={<SchemaForm schemas={schemas} schema={MODEL_SCHEMA} text={text} onText={edit} />}
        yaml={<CodeEditor value={text} language="yaml" onChange={edit} diagnostics={markersOf(diagnostics)} minLines={16} maxLines={40} testId="model-yaml-editor" />}
        diff={<DiffViewer before={saved?.text ?? null} after={text} language="yaml" height={260} testId="model-diff-viewer" />}
        showDiff={showDiff} onShowDiff={setShowDiff}
        dirty={dirty} diagnostics={checked.failed ? [{ code: '', severity: 'error', file: '', line: 0, column: 0, message: checked.failed, fix: null }] : diagnostics} checking={checkName !== null && checked.stale}
        save={save} saving={saving} onSave={() => send(saved?.version ?? null)}
        remove={removeState} onRemove={() => { setRemoveError(null); setConfirmRemove(true) }}
        refusal={refusal ?? removeError} conflict={conflictVersion !== undefined} onReload={reload} onOverwrite={() => send(conflictVersion ?? null)}
        savedNote={note}
      />
      {confirmRemove && existing && (
        <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) setConfirmRemove(false) }}>
          <div className="modal" role="dialog" aria-modal="true" aria-label={`Remove ${existing.name}`} data-testid="model-remove-dialog">
            <div className="card-head"><span className="card-title">Remove {existing.name}</span></div>
            <div className="card-body">
              <p>The model's file is removed from this server. Its tables and rows stay in the database; nothing is dropped. Models published to a serving group stay there until they are unpublished.</p>
              <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
                <button type="button" className="btn" onClick={() => setConfirmRemove(false)} data-testid="model-remove-cancel">Cancel</button>
                <button type="button" className="btn btn-danger" onClick={remove} data-testid="model-remove-confirm">Remove</button>
              </div>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
