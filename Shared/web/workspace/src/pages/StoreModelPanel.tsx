import { useMemo, useState } from 'react'
import { SchemaSet, parseYaml } from '@dbdatakit/shell'
import { MODEL_SCHEMA, isModelName, modelSchema, newModelText, summaryOf } from '@dbdatakit/store'
import type { CheckDiagnostic } from '../api'
import { liveState } from '../editors'
import type { LiveCheck } from '../useLiveCheck'
import { StructuredEditor } from './StructuredPanel'

/** The Store's own schema of a model (`@dbdatakit/store` owns it; this editor imports it, so the two never describe a model differently). */
const schemas = new SchemaSet({ [MODEL_SCHEMA]: modelSchema })

/** A new model's text at the current format (`model/2`): the Store's starting text with its version raised, since the Workspace publishes the current one. */
export const newWorkspaceModelText = (name: string) => newModelText(name).replace('schemaVersion: 1', 'schemaVersion: 2')

/** Where a new model goes and why a name would be refused: the file is `models/<name>.yaml`, the name is the model's own (the loader checks that they agree). */
export function newModelProblem(name: string, taken: readonly string[]): string | null {
  if (name === '') return null
  if (!isModelName(name)) return 'A model name is lower-case letters, digits, "-" and "_", starting with a letter or digit, at most 64.'
  if (taken.includes(`models/${name}.yaml`) || taken.includes(`models/${name}.yml`)) return `models/${name}.yaml already exists in this draft.`
  return null
}

/** The one-line summary of a model's text: its name, entities and attributes, read loosely (the Store's reader is the authority). */
export function modelSummary(text: string): string {
  const parsed = parseYaml(text)
  if (parsed.errors.length > 0 || typeof parsed.value !== 'object' || parsed.value === null) return 'The text is not a model yet.'
  const v = parsed.value as { name?: unknown; entities?: Record<string, { attributes?: Record<string, unknown> }> }
  return `${typeof v.name === 'string' ? v.name : 'unnamed'}: ${summaryOf(v.entities)}`
}

/** A model file edited as a form (from the Store's schema) or as YAML, with the Store's own diagnostics as the draft is checked. */
export function StoreModelEditor({ path, text, onText, readOnly, live, onOpen }: { path: string; text: string; onText: (text: string) => void; readOnly: boolean; live: LiveCheck; onOpen?: (path: string) => void }) {
  const diagnostics: CheckDiagnostic[] = live.diagnostics
  const state = liveState({ text, checkedText: live.checkedText, failed: live.failed, running: live.running })
  const summary = useMemo(() => modelSummary(text), [text])
  return (
    <StructuredEditor schemas={schemas} schema={MODEL_SCHEMA} path={path} text={text} onText={onText} readOnly={readOnly} diagnostics={diagnostics} state={state} summary={summary} onOpen={onOpen}
      notes={live.failed ? <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="wsp-live-failed"><span className="mark">!</span><span>{live.failed}</span></div> : null} />
  )
}

/** Makes `models/<name>.yaml` from the Store's starting text; the name is checked as it is typed. */
export function NewStoreModel({ taken, busy, onCreate }: { taken: readonly string[]; busy: boolean; onCreate: (path: string, text: string) => void }) {
  const [name, setName] = useState('')
  const problem = newModelProblem(name, taken)
  const ok = name !== '' && problem === null && !busy
  return (
    <form className="row" style={{ gap: 6, marginTop: 10, flexWrap: 'wrap' }} data-testid="wsp-new-model"
      onSubmit={(e) => { e.preventDefault(); if (ok) { onCreate(`models/${name}.yaml`, newWorkspaceModelText(name)); setName('') } }}>
      <input className="input mono" style={{ flex: 1, minWidth: 120 }} aria-label="New model name" placeholder="sales_territories" value={name} onChange={(e) => setName(e.target.value)} data-testid="wsp-new-model-name" />
      <button type="submit" className="btn" disabled={!ok} data-testid="wsp-new-model-go">New model</button>
      {problem && <span className="hint" data-testid="wsp-new-model-why">{problem}</span>}
    </form>
  )
}
