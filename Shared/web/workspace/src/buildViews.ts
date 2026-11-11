import { FileRefusal, FOLDERS, type FilesApi } from '@dbdatakit/build/files'
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
