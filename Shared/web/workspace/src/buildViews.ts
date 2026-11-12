import { FileRefusal, FOLDERS, semanticModelName, type FilesApi } from '@dbdatakit/build/files'
import { parseYaml } from '@dbdatakit/shell'
import type { CheckDiagnostic } from './api'

/** The calls of a draft's files that the dashboards and reports of a project need; `workspaceApi` answers them, a test answers them in memory. */
export interface DraftFiles {
  list(): Promise<{ path: string; size: number }[]>
  /** The text, or null when there is no such file. */
  read(path: string): Promise<string | null>
  write(path: string, content: string): Promise<void>
  remove(path: string): Promise<void>
}

/** A short digest of a text: the version a document is opened at (the draft's file routes keep no version, so the editor's stale-save check compares what the file holds now with what was opened). */
export async function versionOf(text: string): Promise<string> {
  const bytes = new TextEncoder().encode(text)
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes)
  return [...new Uint8Array(digest)].slice(0, 8).map((b) => b.toString(16).padStart(2, '0')).join('')
}

/**
 * `FilesApi` (what the views client of Build's workspace store asks of a workspace) over a draft of the Workspace server: the version of a file is the digest of its text, a write or a delete states the version it was opened at and is
 * refused with the code Build's file commands use for a stale one (`DDB-471`, a file that exists already is the same), so the editor's conflict handling is the one the standalone screen has.
 */
export function draftFilesApi(draft: DraftFiles): FilesApi {
  const stale = (path: string) => new FileRefusal('DDB-471', `${path} was changed elsewhere (or exists already).`)
  return {
    async list() {
      const all = await draft.list()
      return Promise.all(all.map(async (f) => ({ path: f.path, size: f.size, hash: /^(dashboards|reports)\//.test(f.path) ? await versionOf((await draft.read(f.path)) ?? '') : '' })))
    },
    async read(path) {
      const text = await draft.read(path)
      return text === null ? null : { content: text, hash: await versionOf(text) }
    },
    async create(path, content) {
      if ((await draft.read(path)) !== null) throw stale(path)
      await draft.write(path, content)
      return { hash: await versionOf(content) }
    },
    async write(path, content, hash) {
      const now = await draft.read(path)
      if (now === null || (await versionOf(now)) !== hash) throw stale(path)
      await draft.write(path, content)
      return { hash: await versionOf(content) }
    },
    async remove(path, hash) {
      const now = await draft.read(path)
      if (now !== null && (await versionOf(now)) !== hash) throw stale(path)
      await draft.remove(path)
    },
  }
}

/** The findings of a check about dashboards and reports: the files of those folders, errors and warnings. */
export const viewFindings = (diagnostics: readonly CheckDiagnostic[]): CheckDiagnostic[] =>
  diagnostics.filter((d) => d.severity !== 'info' && d.file !== null && (d.file.startsWith(`${FOLDERS.dashboard}/`) || d.file.startsWith(`${FOLDERS.report}/`)))

/** The semantic files among the paths of a draft (`semantic/*.yml`). */
export const semanticPaths = (paths: readonly string[]): string[] => paths.filter((p) => /^semantic\/.+\.ya?ml$/.test(p))

/**
 * The model a dashboard's wells draw their fields from, read from the draft's own semantic file (plan 33 SC20n): the file `semantic/*.yml` is a catalog without its physical layer, so its `measures` and `dimensions`
 * are the members the editor offers, with the names a query uses (`customers.region`). Offline: nothing is compiled or run, and a model that does not compile is the draft check's to report. Returns the file as an object with its name,
 * the shape `catalogFromModel` reads; throws when no semantic file of the draft is named `model`.
 */
export async function semanticModelOf(draft: Pick<DraftFiles, 'read'>, paths: readonly string[], model: string): Promise<unknown> {
  for (const path of semanticPaths(paths)) {
    const text = await draft.read(path)
    if (text === null || semanticModelName(text) !== model) continue
    const parsed = parseYaml(text)
    if (parsed.errors.length > 0 || typeof parsed.value !== 'object' || parsed.value === null) throw new Error(`${path} is not valid YAML (${parsed.errors[0] ?? 'not a mapping'}): fix it in the Files tab.`)
    return { ...(parsed.value as Record<string, unknown>), name: model }
  }
  throw new Error(`The draft has no semantic model named '${model}' in semantic/.`)
}
