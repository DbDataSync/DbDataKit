import type { WorkspaceEvent } from './api'

const NAME = /^[a-z0-9][a-z0-9_-]{0,31}$/

/** A workspace's name and a project's id are the same shape on the server: lower-case letters, digits, `-` and `_`, at most 32. */
export const isName = (name: string) => NAME.test(name)

/** Why a name would be refused, in a sentence, or null when it is fine (the server says the same; this is for the form as you type). */
export function nameProblem(name: string, what: 'workspace' | 'project'): string | null {
  if (name === '') return `Give the ${what} a name.`
  if (name.length > 32) return 'At most 32 characters.'
  if (!isName(name)) return 'Lower-case letters, digits, "-" and "_", starting with a letter or digit.'
  return null
}

/** What the create button of a form is: whether it may be pressed, and why not when it may not. */
export function createState(input: { name: string; canWrite: boolean; busy: boolean; what: 'workspace' | 'project'; taken?: readonly string[] }): { enabled: boolean; why: string | null } {
  if (!input.canWrite) return { enabled: false, why: input.what === 'project' ? 'Making a project needs the administrator role.' : 'Making a workspace needs the author or administrator role.' }
  const problem = nameProblem(input.name, input.what)
  if (problem) return { enabled: false, why: input.name === '' ? null : problem }
  if (input.taken?.includes(input.name)) return { enabled: false, why: `There is a ${input.what} called "${input.name}" already.` }
  return { enabled: !input.busy, why: null }
}

/** Whether the person may abandon a workspace: only its owner, and only with the author role. */
export function abandonState(input: { mine: boolean; canWrite: boolean }): { enabled: boolean; why: string | null } {
  if (!input.mine) return { enabled: false, why: 'A workspace is abandoned by its owner.' }
  if (!input.canWrite) return { enabled: false, why: 'Abandoning a workspace needs the author or administrator role.' }
  return { enabled: true, why: null }
}

const WORDS: Record<string, string> = { created: 'made', committed: 'committed', pushed: 'pushed', merged: 'merged', abandoned: 'abandoned' }

/** One line of a workspace's history: "Ada committed: Title the grid". */
export function describeEvent(event: WorkspaceEvent): string {
  const verb = WORDS[event.kind] ?? event.kind
  return event.detail ? `${event.actor} ${verb}: ${event.detail}` : `${event.actor} ${verb}`
}

/** A node of the file tree: a folder (with children) or a file (with its size). */
export interface TreeNode { name: string; path: string; folder: boolean; size: number; children: TreeNode[] }

/** The flat list of paths the server sends, as a tree: folders first, each level in name order. */
export function fileTree(files: readonly { path: string; size: number }[]): TreeNode[] {
  const root: TreeNode = { name: '', path: '', folder: true, size: 0, children: [] }
  for (const file of files) {
    let at = root
    const parts = file.path.split('/')
    parts.forEach((part, i) => {
      const path = parts.slice(0, i + 1).join('/')
      const last = i === parts.length - 1
      let next = at.children.find((c) => c.name === part && c.folder === !last)
      if (!next) { next = { name: part, path, folder: !last, size: last ? file.size : 0, children: [] }; at.children.push(next) }
      at = next
    })
  }
  const order = (nodes: TreeNode[]) => {
    nodes.sort((a, b) => (a.folder === b.folder ? a.name.localeCompare(b.name) : a.folder ? -1 : 1))
    nodes.forEach((n) => order(n.children))
  }
  order(root.children)
  return root.children
}

/** The language the editor colours a file in, by its name. */
export function languageOf(path: string): 'yaml' | 'csharp' | 'sql' | 'plaintext' {
  const lower = path.toLowerCase()
  if (lower.endsWith('.yaml') || lower.endsWith('.yml')) return 'yaml'
  if (lower.endsWith('.sql')) return 'sql'
  if (lower.endsWith('.cs')) return 'csharp'
  return 'plaintext'
}

/** Why a path would be refused as you type it (the server checks it again, and says its own words): inside the workspace, no `.git`, not a folder's name. */
export function pathProblem(path: string): string | null {
  if (path === '') return null
  if (path.length > 240) return 'A path is at most 240 characters.'
  if (/[\\:]/.test(path)) return 'A path is written with "/" and has no drive or ":" in it.'
  if (path.startsWith('/')) return 'A path is relative to the workspace: it does not start with "/".'
  const parts = path.split('/')
  if (parts.some((p) => p === '')) return 'A path has no empty part.'
  if (parts.some((p) => p === '.' || p === '..')) return 'A path stays inside the workspace: no "." or ".." parts.'
  if (parts.some((p) => p.replace(/[ .]+$/, '').toLowerCase() === '.git')) return 'Git\'s own files (.git) are not files of the workspace.'
  if (parts.some((p) => /[ .]$/.test(p))) return 'A part of a path does not end with a space or a ".".'
  return null
}

/** What the file buttons are: Save needs a change, and the owner with the author role; the reason is shown when it is not that. */
export function saveState(input: { mine: boolean; canWrite: boolean; dirty: boolean; busy: boolean }): { enabled: boolean; why: string | null } {
  if (!input.mine) return { enabled: false, why: 'A workspace is edited by its owner.' }
  if (!input.canWrite) return { enabled: false, why: 'Editing needs the author or administrator role.' }
  return { enabled: input.dirty && !input.busy, why: null }
}

/** Commit: needs a message and something no commit has taken yet. */
export function commitState(input: { mine: boolean; canWrite: boolean; message: string; uncommitted: boolean; busy: boolean }): { enabled: boolean; why: string | null } {
  if (!input.mine) return { enabled: false, why: 'A workspace is committed by its owner.' }
  if (!input.canWrite) return { enabled: false, why: 'Committing needs the author or administrator role.' }
  if (!input.uncommitted) return { enabled: false, why: 'Nothing has changed since the last commit.' }
  return { enabled: input.message.trim() !== '' && !input.busy, why: null }
}

/** Merge: a commit has to be there and nothing left uncommitted (a merge takes what is committed). The approver role is the server's to judge, and its refusal is shown in its own words. */
export function mergeState(input: { commits: number; uncommitted: boolean; busy: boolean }): { enabled: boolean; why: string | null } {
  if (input.commits === 0) return { enabled: false, why: 'Commit a change first: a merge takes what is committed.' }
  if (input.uncommitted) return { enabled: false, why: 'There are changes no commit has taken; commit them before merging.' }
  return { enabled: !input.busy, why: null }
}

/** The files a refused merge names, when the server sent them with its 409. */
export function conflictsOf(body: unknown): string[] {
  const conflicts = (body as { conflicts?: unknown } | null | undefined)?.conflicts
  return Array.isArray(conflicts) ? conflicts.filter((c): c is string => typeof c === 'string') : []
}

/** A status the server reports for a change, as a word. */
export function statusWord(status: string): string {
  const map: Record<string, string> = { A: 'added', M: 'changed', D: 'deleted', R: 'renamed', added: 'added', modified: 'changed', deleted: 'deleted', renamed: 'renamed' }
  return map[status] ?? status.toLowerCase()
}
