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
