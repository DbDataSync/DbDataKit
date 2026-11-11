import { parseDocument } from 'yaml'
import type { ViewKind } from '../views/document'
import type { ViewsDoc } from '../views/model'
import { textVersion, validateText } from '../client/text'
import type { ClientDiagnostic, OpenedDocument, ValidationResult } from '../client/types'
import { canonicalText } from './canonical'
import { commandOps, EditError, type EditCommand } from './commands'
import { applyOps, YamlEditError } from './yamlEdit'

/**
 * The draft of the editor (plan 32 DBR24): the text of a file and the document read from it kept in step, the validator's answer for the text as it stands, the version the file had when it was opened
 * (what a save quotes), and the undo and redo stacks. A draft is a value: every function here returns a new one, so a page keeps it in a reducer and a test compares two.
 * The text is the truth. A form edit is a command applied to the text (comments and order kept); the YAML view edits the text itself; both end in `validateText`, the TypeScript validator of the loaders.
 */
export interface Draft {
  kind: ViewKind
  name: string
  text: string
  /** The text as last opened or saved: the draft is dirty when `text` differs. */
  baseText: string
  /** The version a save must quote; absent for a document that was never saved (a new one). */
  baseVersion: string | undefined
  validation: ValidationResult
  /** The text read as plain values, absent while it is not YAML. A document with errors still has a value, so a form can show it and the problem beside it. */
  value: ViewsDoc | undefined
  undoStack: string[]
  redoStack: string[]
  /** The group of the last change: changes of one group in a row (typing, one drag) are one undo step. */
  group: string | undefined
}

const fileOf = (name: string) => `${name}.yaml`

function read(text: string): ViewsDoc | undefined {
  const doc = parseDocument(text)
  if (doc.errors.length > 0) return undefined
  const value = doc.toJS() as unknown
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as ViewsDoc) : undefined
}

function withText(draft: Draft, text: string): Draft {
  return { ...draft, text, value: read(text), validation: validateText(text, { kind: draft.kind, file: fileOf(draft.name) }) }
}

export function draftFromOpened(opened: OpenedDocument): Draft {
  return withText({ kind: opened.kind, name: opened.name, text: '', baseText: opened.text, baseVersion: opened.version, validation: { ok: false, document: null, diagnostics: [] }, value: undefined, undoStack: [], redoStack: [], group: undefined }, opened.text)
}

/** A draft of a document that does not exist yet (`baseVersion` absent), dirty from the start. */
export function newDraft(kind: ViewKind, name: string, text: string): Draft {
  return withText({ kind, name, text: '', baseText: '', baseVersion: undefined, validation: { ok: false, document: null, diagnostics: [] }, value: undefined, undoStack: [], redoStack: [], group: undefined }, text)
}

export const isDirty = (draft: Draft) => draft.text !== draft.baseText
export const canUndo = (draft: Draft) => draft.undoStack.length > 0
export const canRedo = (draft: Draft) => draft.redoStack.length > 0
export const errorsOf = (draft: Draft): ClientDiagnostic[] => draft.validation.diagnostics.filter((d) => d.severity === 'error')

/** The text replaced (the YAML view). Changes of one `group` in a row are one undo step; a change to the same text is no change. */
export function editText(draft: Draft, text: string, group = 'text'): Draft {
  if (text === draft.text) return draft
  const push = draft.group === group && draft.undoStack.length > 0 ? draft.undoStack : [...draft.undoStack, draft.text]
  return { ...withText(draft, text), undoStack: push, redoStack: [], group }
}

export interface EditResult { draft: Draft; error?: EditError | YamlEditError }

/**
 * A command applied to the text. A command the document cannot take (a name that is taken, a text that is not YAML) leaves the draft as it was and says why in `error`; it is never thrown at a page.
 * Each command is its own undo step unless it names a `group` (a drag sends one command per pointer move with the same group).
 */
export function edit(draft: Draft, command: EditCommand, group?: string): EditResult {
  try {
    if (!draft.value) throw new YamlEditError('The text is not a document yet, so a form cannot change it. Fix it in the YAML view first.')
    const ops = commandOps(draft.value, command)
    const text = applyOps(draft.text, ops)
    return { draft: editText(draft, text, group ?? `command:${Math.random()}`) }
  } catch (e) {
    if (e instanceof EditError || e instanceof YamlEditError) return { draft, error: e }
    throw e
  }
}

/** Several commands as one undo step (drop a field and the tile that goes with it). The first that cannot be applied stops the rest and leaves the draft as it was. */
export function editAll(draft: Draft, commands: EditCommand[], group = `batch:${Math.random()}`): EditResult {
  let next = draft
  for (const command of commands) {
    const result = edit(next, command, group)
    if (result.error) return { draft, error: result.error }
    next = result.draft
  }
  return { draft: next }
}

export function undo(draft: Draft): Draft {
  const previous = draft.undoStack[draft.undoStack.length - 1]
  if (previous === undefined) return draft
  return { ...withText(draft, previous), undoStack: draft.undoStack.slice(0, -1), redoStack: [...draft.redoStack, draft.text], group: undefined }
}

export function redo(draft: Draft): Draft {
  const next = draft.redoStack[draft.redoStack.length - 1]
  if (next === undefined) return draft
  return { ...withText(draft, next), undoStack: [...draft.undoStack, draft.text], redoStack: draft.redoStack.slice(0, -1), group: undefined }
}

/** The text in canonical order and style (comments are dropped); as an undo step, so a person can take it back. Refused while the text is not a document. */
export function format(draft: Draft): EditResult {
  if (!draft.value) return { draft, error: new YamlEditError('The text is not YAML, so it cannot be formatted.') }
  return { draft: editText(draft, canonicalText(draft.value), `format:${Math.random()}`) }
}

/** The draft after a save: the text is the base, the version the store gave. The stacks stay, so a saved change can still be undone (and the draft is dirty again). */
export function markSaved(draft: Draft, name: string, version: string): Draft {
  return { ...draft, name, baseText: draft.text, baseVersion: version, group: undefined, validation: validateText(draft.text, { kind: draft.kind, file: fileOf(name) }) }
}

/** The file's version for a text, as the fixture and local stores give it. */
export { textVersion }
