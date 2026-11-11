import type { CompiledFile, CompileResult } from './api'

/** The words for a status of a compiled file, as the person would say what compiling would do to the draft. */
export const compiledWord = (status: CompiledFile['status']): string => ({ added: 'would be added', changed: 'would change', unchanged: 'as committed', removed: 'would be removed' })[status]

/** How many files a compile would write (added or changed), remove, and leave as they are. */
export function compileCounts(files: readonly CompiledFile[]): { written: number; removed: number; unchanged: number } {
  return { written: files.filter((f) => f.status === 'added' || f.status === 'changed').length, removed: files.filter((f) => f.status === 'removed').length, unchanged: files.filter((f) => f.status === 'unchanged').length }
}

/** The files the author writes into the draft to take the compile (those with their text) and the files they delete; a file too large to have been sent is not written (it is said). */
export function compileActions(result: CompileResult): { write: { path: string; content: string }[]; remove: string[]; skipped: string[] } {
  const write = result.files.filter((f) => (f.status === 'added' || f.status === 'changed') && f.content !== null).map((f) => ({ path: f.path, content: f.content! }))
  return { write, remove: result.files.filter((f) => f.status === 'removed').map((f) => f.path), skipped: result.files.filter((f) => f.truncated).map((f) => f.path) }
}

/** The connection a rendered file belongs to (`rendered/<connection>/...`), or null for one at the top. */
export const connectionOfRendered = (path: string): string | null => /^rendered\/([^/]+)\/.+/.exec(path)?.[1] ?? null

/** One cell of a sample row as shown: a null is written `null`, a long text is cut. */
export const cellText = (cell: string | null, max = 40): string => (cell === null ? 'null' : cell.length > max ? `${cell.slice(0, max - 1)}…` : cell)
