import type { EditorDiagnostic } from '@dbdatakit/shell'
import type { Diagnostic } from './api'

/** Where a model's name must fit: what the server accepts (`StoreModels.IsValidName`). The server decides; this only keeps a button off for a name it will refuse. */
export const modelNamePattern = /^[a-z0-9][a-z0-9_-]{0,63}$/
export const isModelName = (name: string) => modelNamePattern.test(name)

/** The text a new model starts from: valid as it stands, with one entity to change. */
export function newModelText(name: string): string {
  return [
    'schemaVersion: 1',
    `name: ${name || 'my_model'}`,
    'title: ',
    'storage: {connection: mdm_sql}',
    'entities:',
    '  item:',
    '    key: [code]',
    '    attributes:',
    '      code: {type: VARCHAR(20), required: true}',
    '      name: {type: VARCHAR(100), required: true}',
    '',
  ].join('\n').replace('title: \n', 'title: New model\n')
}

/** What the code editor marks: the Store's own diagnostics, a problem with no line on the first line. */
export function markersOf(diagnostics: Diagnostic[]): EditorDiagnostic[] {
  return diagnostics.map((d) => ({
    line: d.line > 0 ? d.line : 1,
    column: d.column > 0 ? d.column : 1,
    message: d.fix ? `${d.message} ${d.fix}` : d.message,
    severity: d.severity === 'warning' ? 'warning' : d.severity === 'info' ? 'info' : 'error',
  }))
}

export const errorsOf = (diagnostics: Diagnostic[]) => diagnostics.filter((d) => d.severity === 'error')

export interface SaveInputs {
  isNew: boolean
  name: string
  /** What the Store said this caller may do on this model (false while unknown); a new model is the caller's role in general, passed as `canWrite`. */
  canWrite: boolean
  changed: boolean
  /** The check of the current text has not answered yet. */
  checking: boolean
  /** The text the last answer was about is not the text now. */
  stale: boolean
  diagnostics: Diagnostic[]
  saving: boolean
}

/** Whether Save is offered, and when it is not, the sentence that says why (shown beside the button and as its tooltip). */
export function saveState(i: SaveInputs): { allowed: boolean; reason: string | null } {
  if (!i.canWrite) return { allowed: false, reason: 'Saving a model needs the author or administrator role.' }
  if (i.isNew && i.name === '') return { allowed: false, reason: 'Give the model a name first.' }
  if (i.isNew && !isModelName(i.name)) return { allowed: false, reason: 'A model name is lower-case letters, digits, "-" and "_", starting with a letter or digit, at most 64.' }
  if (!i.isNew && !i.changed) return { allowed: false, reason: 'Nothing has changed since it was saved.' }
  if (i.saving) return { allowed: false, reason: 'Saving...' }
  if (i.checking || i.stale) return { allowed: false, reason: 'The Store is checking the text.' }
  const errors = errorsOf(i.diagnostics).length
  if (errors > 0) return { allowed: false, reason: `The Store refuses this model: ${errors} problem${errors === 1 ? '' : 's'} to fix (listed below).` }
  return { allowed: true, reason: null }
}

/** The entities and attribute counts of a model text, for a one-line summary beside the editor. Reads the text loosely (the Store's check is the authority). */
export function summaryOf(entities: Record<string, { attributes?: Record<string, unknown> }> | undefined): string {
  const names = Object.keys(entities ?? {})
  if (names.length === 0) return 'No entities yet.'
  const attributes = names.reduce((n, e) => n + Object.keys(entities![e]?.attributes ?? {}).length, 0)
  return `${names.length} ${names.length === 1 ? 'entity' : 'entities'}, ${attributes} ${attributes === 1 ? 'attribute' : 'attributes'}`
}

