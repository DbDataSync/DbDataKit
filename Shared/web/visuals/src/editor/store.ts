import type { ViewKind } from '../views/document'
import { textVersion, validateText } from '../client/text'
import { ViewsClientError } from '../client/types'
import type { ClientDiagnostic, DocumentSummary, OpenedDocument, SaveResult, ViewsClient } from '../client/types'
import { draftFromOpened, errorsOf, markSaved, type Draft } from './draft'

/**
 * Where an editor opens and saves (plan 32 DBR24): `list`, `open`, `save` with the version that was opened, `duplicate` and `delete`. The same interface fronts a client (the fixture source, the `views`
 * module), the local file store below (nothing but memory and a download) and, later, a Build workspace and the registry (DBR29).
 */
export interface DocumentStore {
  list(kind?: ViewKind): Promise<DocumentSummary[]>
  open(kind: ViewKind, name: string): Promise<OpenedDocument>
  /** Saves under a name when `expectedVersion` is the stored one (`version_conflict` otherwise); a name that does not exist yet needs none. */
  save(kind: ViewKind, name: string, text: string, expectedVersion?: string): Promise<SaveResult>
  /** A copy under another name; the name must be free. */
  duplicate(kind: ViewKind, name: string, as: string): Promise<SaveResult>
  delete(kind: ViewKind, name: string): Promise<void>
}

/** A client as a store: duplicate is an open and a save under the new name; delete needs a client that can (`unsupported` otherwise). */
export function clientStore(client: ViewsClient): DocumentStore {
  return {
    list: (kind) => client.list(kind),
    open: (kind, name) => client.open(kind, name),
    save: (kind, name, text, expectedVersion) => client.save(kind, name, text, expectedVersion),
    async duplicate(kind, name, as) {
      const opened = await client.open(kind, name)
      return client.save(kind, as, opened.text)
    },
    async delete(kind, name) {
      if (!client.delete) throw new ViewsClientError('unsupported', 'This source cannot delete a document.')
      await client.delete(kind, name)
    },
  }
}

export interface LocalFileStore extends DocumentStore {
  /** The file a save as download hands the browser: its name and text. */
  fileOf(kind: ViewKind, name: string): { filename: string; text: string; mime: string }
}

/**
 * The built-in store with no server (plan 32 DBR24): documents held in memory, opened from a file a person picked, saved as a download. It checks as every real store does: a stale version is a
 * conflict, a document with errors is refused with the loader's text. It is also the fake of the tests.
 */
export function localFileStore(initial: Record<string, string> = {}): LocalFileStore {
  const files = new Map(Object.entries(initial))
  const key = (kind: ViewKind, name: string) => `${kind}/${name}`
  const conflict = (kind: ViewKind, name: string, stored: string, yours?: string) =>
    new ViewsClientError('version_conflict', `The ${kind} \`${name}\` was changed since it was opened (stored version ${textVersion(stored)}, ${yours ? `yours ${yours}` : 'none quoted'}). Reload it.`)
  const store: LocalFileStore = {
    async list(kind) {
      return [...files.entries()].filter(([k]) => kind === undefined || k.startsWith(`${kind}/`)).map(([k, text]) => {
        const [k2, name] = k.split('/') as [ViewKind, string]
        return { kind: k2, name, title: validateText(text, { kind: k2 }).document?.title ?? name }
      }).sort((a, b) => a.name.localeCompare(b.name))
    },
    async open(kind, name) {
      const text = files.get(key(kind, name))
      if (text === undefined) throw new ViewsClientError('not_found', `There is no ${kind} named \`${name}\`.`)
      const result = validateText(text, { kind, file: `${name}.yaml` })
      return { name, kind, text, version: textVersion(text), document: result.document, diagnostics: result.diagnostics }
    },
    async save(kind, name, text, expectedVersion) {
      const stored = files.get(key(kind, name))
      if (stored !== undefined && textVersion(stored) !== expectedVersion) throw conflict(kind, name, stored, expectedVersion)
      const result = validateText(text, { kind, file: `${name}.yaml` })
      if (!result.ok) throw new ViewsClientError('invalid', `The ${kind} \`${name}\` is not saved: ${(result.diagnostics.find((d) => d.severity === 'error') as ClientDiagnostic).message}`, { diagnostics: result.diagnostics })
      files.set(key(kind, name), text)
      return { name, version: textVersion(text) }
    },
    async duplicate(kind, name, as) {
      const text = files.get(key(kind, name))
      if (text === undefined) throw new ViewsClientError('not_found', `There is no ${kind} named \`${name}\`.`)
      if (files.has(key(kind, as))) throw new ViewsClientError('version_conflict', `There is already a ${kind} named \`${as}\`.`)
      return store.save(kind, as, text)
    },
    async delete(kind, name) {
      if (!files.delete(key(kind, name))) throw new ViewsClientError('not_found', `There is no ${kind} named \`${name}\`.`)
    },
    fileOf(kind, name) {
      const text = files.get(key(kind, name))
      if (text === undefined) throw new ViewsClientError('not_found', `There is no ${kind} named \`${name}\`.`)
      return { filename: `${name}.yaml`, text, mime: 'application/yaml' }
    },
  }
  return store
}

export type SaveOutcome =
  | { status: 'saved'; draft: Draft }
  | { status: 'invalid'; draft: Draft; diagnostics: ClientDiagnostic[]; message: string }
  /** The stored document is not the one that was opened: `stored` is what is there now (its text and version), for a reload or a look. */
  | { status: 'conflict'; draft: Draft; message: string; storedVersion: string | undefined; stored: OpenedDocument | null }
  | { status: 'failed'; draft: Draft; message: string }

/**
 * Saves a draft: refused at once, with the validator's text, while the draft has errors (the store is not asked); otherwise the store is asked with the version the draft was opened at, and a
 * stale one comes back as `conflict` with what is stored now. A save of a draft that is not dirty is still sent (a store may want the version), so the page decides to disable the button.
 */
export async function saveDraft(store: DocumentStore, draft: Draft): Promise<SaveOutcome> {
  const errors = errorsOf(draft)
  if (errors.length > 0) {
    const first = errors[0]!
    return { status: 'invalid', draft, diagnostics: errors, message: `${draft.kind === 'dashboard' ? 'The dashboard' : 'The report'} is not saved: ${first.message}` }
  }
  try {
    const result = await store.save(draft.kind, draft.name, draft.text, draft.baseVersion)
    return { status: 'saved', draft: markSaved(draft, result.name, result.version) }
  } catch (e) {
    if (!(e instanceof ViewsClientError)) return { status: 'failed', draft, message: e instanceof Error ? e.message : String(e) }
    if (e.code === 'version_conflict') {
      const stored = await store.open(draft.kind, draft.name).catch(() => null)
      return { status: 'conflict', draft, message: e.message, storedVersion: stored?.version, stored }
    }
    if (e.code === 'invalid') return { status: 'invalid', draft, diagnostics: e.diagnostics ?? [], message: e.message }
    return { status: 'failed', draft, message: e.message }
  }
}

/** The draft started again from what is stored (after a conflict, "Reload": the edits of the draft are given up). */
export async function reloadDraft(store: DocumentStore, draft: Draft): Promise<Draft> {
  return draftFromOpened(await store.open(draft.kind, draft.name))
}
