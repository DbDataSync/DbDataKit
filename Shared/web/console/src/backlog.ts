import { apiUrl, ApiError, request } from '@dbdatakit/shell'

/** The workflow of the backlogs and what the signed-in person may do (plan 27a BK01: `GET /api/backlog/workflow`). */
export interface BacklogState { id: string; title: string; category: 'todo' | 'doing' | 'done'; requires: string[] }
export interface BacklogTransition { from: string; to: string; role: 'member' | 'lead' }
export interface BacklogWorkflow {
  initial: string
  role: 'viewer' | 'member' | 'lead'
  states: BacklogState[]
  transitions: BacklogTransition[]
  itemTypes: string[]
  priorities: string[]
  linkKinds: string[]
  objectKinds: string[]
}

export interface BacklogProject { id: string; name: string; description: string | null; createdAt: string }

export interface BacklogItem {
  id: string
  key: string
  project: string
  type: string
  title: string
  description: string | null
  state: string
  priority: string
  estimate: number | null
  assignee: string | null
  reporter: string
  epic: string | null
  epicKey: string | null
  sprint: string | null
  labels: string[]
  resolution: string | null
  createdAt: string
  updatedAt: string
  closedAt: string | null
  version: number
  sortKey: number
}

export interface BacklogSprint { id: string; project: string; name: string; goal: string | null; state: 'planned' | 'active' | 'closed'; plannedStart: string | null; plannedEnd: string | null; startedAt: string | null; closedAt: string | null; version: number }

export interface BacklogComment { id: string; author: string; authorName: string; body: string; createdAt: string; editedAt: string | null }
export interface BacklogLink { id: string; kind: string; targetKind: string; target: string; targetKey: string | null; label: string | null; state: 'ok' | 'dropped'; createdBy: string; createdAt: string }
export interface BacklogAttachment { id: string; fileName: string; mediaType: string; size: number; addedBy: string; addedAt: string }
export interface BacklogHistory { seq: number; actor: string; actorName: string; kind: string; field: string | null; oldValue: string | null; newValue: string | null; at: string }

export interface BacklogDetail {
  item: BacklogItem
  targets: string[]
  comments: BacklogComment[]
  links: BacklogLink[]
  attachments: BacklogAttachment[]
  history: BacklogHistory[]
  children: BacklogItem[]
}

export interface BacklogSummary { done: number; moved: number; pointsDone: number; pointsMoved: number; movedTo: string | null }

export interface ImportProblem { code: string; line: number; column: number; message: string; fix: string | null }

/** The priorities from highest to lowest, for the sort of a list and the mark on a card. */
export const priorityRank = (priority: string) => ['highest', 'high', 'medium', 'low', 'lowest'].indexOf(priority)

export const stateTitle = (workflow: BacklogWorkflow, id: string) => workflow.states.find((s) => s.id === id)?.title ?? id

/** The states a person with this role could move an item in `from` to, by the workflow alone (the server decides, and also checks the required fields): the board's drop targets. */
export function movesFrom(workflow: BacklogWorkflow, from: string, role: BacklogWorkflow['role'] = workflow.role): string[] {
  const held = role === 'lead' ? 2 : role === 'member' ? 1 : 0
  if (held === 0) return []
  const needed = (r: BacklogTransition['role']) => (r === 'lead' ? 2 : 1)
  return workflow.states
    .filter((s) => s.id !== from)
    .filter((s) => {
      const t = workflow.transitions.find((x) => x.from === from && x.to === s.id) ?? workflow.transitions.find((x) => x.from === '*' && x.to === s.id)
      return t !== undefined && held >= needed(t.role)
    })
    .map((s) => s.id)
}

/** Why a card cannot be dropped in a column, or null when the workflow allows it (a hint before the call; a required field is only known to the server). */
export function dropRefusal(workflow: BacklogWorkflow, item: Pick<BacklogItem, 'key' | 'state'>, to: string): string | null {
  if (workflow.role === 'viewer') return 'A viewer cannot move an item.'
  if (item.state === to) return null
  if (movesFrom(workflow, item.state).includes(to)) return null
  const any = movesFrom(workflow, item.state, 'lead').includes(to)
  return any
    ? `${item.key} can only be moved from ${stateTitle(workflow, item.state)} to ${stateTitle(workflow, to)} by a lead.`
    : `The workflow has no move from ${stateTitle(workflow, item.state)} to ${stateTitle(workflow, to)}.`
}

/** The items in each state of the workflow, in the order the backlog has them. */
export function columns(workflow: BacklogWorkflow, items: BacklogItem[]): { state: BacklogState; items: BacklogItem[] }[] {
  return workflow.states.map((state) => ({ state, items: items.filter((i) => i.state === state.id).sort((a, b) => a.sortKey - b.sortKey) }))
}

/** What a column or a sprint holds, in points: the items that have an estimate, and how many have none. */
export function points(items: Pick<BacklogItem, 'estimate'>[]): { total: number; unestimated: number } {
  return { total: items.reduce((sum, i) => sum + (i.estimate ?? 0), 0), unestimated: items.filter((i) => i.estimate === null).length }
}

export function pointsLine(items: Pick<BacklogItem, 'estimate'>[]): string {
  const p = points(items)
  const n = `${items.length} item${items.length === 1 ? '' : 's'}`
  return `${n}, ${p.total} point${p.total === 1 ? '' : 's'}${p.unestimated > 0 ? `, ${p.unestimated} not estimated` : ''}`
}

/** The item a card goes before when it moves up (or down) one place among `ordered` (the list as shown), `null` for last; `undefined` when it cannot move that way. */
export function neighbourFor(ordered: Pick<BacklogItem, 'id'>[], id: string, direction: 'up' | 'down'): string | null | undefined {
  const at = ordered.findIndex((i) => i.id === id)
  if (at < 0) return undefined
  if (direction === 'up') return at === 0 ? undefined : ordered[at - 1].id
  if (at === ordered.length - 1) return undefined
  return at + 2 >= ordered.length ? null : ordered[at + 2].id
}

const FIELD_WORDS: Record<string, string> = { title: 'the title', description: 'the description', priority: 'the priority', estimate: 'the estimate', assignee: 'the assignee', epic: 'the epic', labels: 'the labels', resolution: 'the resolution', state: 'the state' }

/** One line of an item's history, for a person: who did what. */
export function historyLine(h: Pick<BacklogHistory, 'actorName' | 'kind' | 'field' | 'oldValue' | 'newValue'>, titles: (state: string) => string = (s) => s): string {
  const who = h.actorName
  const show = (v: string | null) => (v === null || v === '' ? 'nothing' : v.length > 60 ? v.slice(0, 57) + '...' : v)
  switch (h.kind) {
    case 'created': return `${who} made ${h.newValue ?? 'the item'}`
    case 'transition': return `${who} moved it from ${titles(h.oldValue ?? '')} to ${titles(h.newValue ?? '')}`
    case 'field': return `${who} changed ${FIELD_WORDS[h.field ?? ''] ?? h.field ?? 'a field'} from ${show(h.oldValue)} to ${show(h.newValue)}`
    case 'comment': return `${who} commented`
    case 'link': return h.newValue ? `${who} linked ${h.newValue}` : `${who} removed the link to ${h.oldValue ?? 'something'}`
    case 'attachment': return h.newValue ? `${who} attached ${h.newValue}` : `${who} removed the attachment ${h.oldValue ?? ''}`.trim()
    case 'sprint': return h.newValue ? `${who} planned it in ${h.newValue}` : `${who} took it out of ${h.oldValue ?? 'its sprint'}`
    case 'rank': return `${who} changed its place in the backlog`
    default: return `${who}: ${h.kind}`
  }
}

/** What closing a sprint did, as the screen says it. */
export function summaryLine(s: BacklogSummary, carriedTo: string | null): string {
  const done = `${s.done} finished item${s.done === 1 ? '' : 's'} (${s.pointsDone} points) stay in the sprint`
  if (s.moved === 0) return `${done}; nothing was left open.`
  return `${done}; ${s.moved} open item${s.moved === 1 ? '' : 's'} (${s.pointsMoved} points) went ${carriedTo ? `to ${carriedTo}` : 'back to the backlog'}.`
}

/** The refusal of a rule as the server worded it, or the error's own words. */
export const problemText = (e: unknown) => (e instanceof Error ? e.message : String(e))

/** The lines of an import that the file's reader refused, as `line 4: message`. */
export function importProblems(e: unknown): string[] {
  if (e instanceof ApiError && typeof e.body === 'object' && e.body !== null && Array.isArray((e.body as { diagnostics?: unknown }).diagnostics))
    return ((e.body as { diagnostics: ImportProblem[] }).diagnostics).map((d) => `line ${d.line}: ${d.message}${d.fix ? ` (${d.fix})` : ''}`)
  return [problemText(e)]
}

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })
const p = encodeURIComponent

export const backlogApi = {
  workflow: () => request<BacklogWorkflow>('/api/backlog/workflow'),
  projects: () => request<BacklogProject[]>('/api/backlog/projects'),
  createProject: (key: string, name: string, description: string | null) => request<BacklogProject>('/api/backlog/projects', json('POST', { key, name, description })),
  items: (project: string) => request<BacklogItem[]>(`/api/backlog/projects/${p(project)}/items`),
  createItem: (project: string, body: { type: string; title: string; description?: string | null; priority?: string; estimate?: number | null; assignee?: string | null; epic?: string | null; labels?: string[] }) =>
    request<BacklogItem>(`/api/backlog/projects/${p(project)}/items`, json('POST', body)),
  sprints: (project: string) => request<BacklogSprint[]>(`/api/backlog/projects/${p(project)}/sprints`),
  createSprint: (project: string, name: string, goal: string | null) => request<BacklogSprint>(`/api/backlog/projects/${p(project)}/sprints`, json('POST', { name, goal })),
  startSprint: (id: string) => request<BacklogSprint>(`/api/backlog/sprints/${p(id)}/start`, json('POST')),
  closeSprint: (id: string, carryTo: string | null) => request<{ sprint: BacklogSprint; summary: BacklogSummary }>(`/api/backlog/sprints/${p(id)}/close`, json('POST', { carryTo })),
  detail: (id: string) => request<BacklogDetail>(`/api/backlog/items/${p(id)}`),
  change: (id: string, fields: Record<string, unknown>) => request<BacklogItem>(`/api/backlog/items/${p(id)}`, json('PATCH', fields)),
  transition: (id: string, to: string, resolution: string | null, version: number) => request<BacklogItem>(`/api/backlog/items/${p(id)}/transition`, json('POST', { to, resolution, version })),
  rank: (id: string, before: string | null) => request<BacklogItem>(`/api/backlog/items/${p(id)}/rank`, json('POST', { before })),
  setSprint: (id: string, sprint: string | null) => request<BacklogItem>(`/api/backlog/items/${p(id)}/sprint`, json('PUT', { sprint })),
  remove: (id: string) => request<void>(`/api/backlog/items/${p(id)}`, json('DELETE')),
  comment: (id: string, body: string) => request<{ id: string }>(`/api/backlog/items/${p(id)}/comments`, json('POST', { body })),
  link: (id: string, kind: string, targetKind: string | null, target: string, label: string | null) => request<{ id: string }>(`/api/backlog/items/${p(id)}/links`, json('POST', { kind, targetKind, target, label })),
  unlink: (id: string) => request<void>(`/api/backlog/links/${p(id)}`, json('DELETE')),
  removeAttachment: (id: string) => request<void>(`/api/backlog/attachments/${p(id)}`, json('DELETE')),
  attachmentUrl: (id: string) => apiUrl(`/api/backlog/attachments/${p(id)}`),
  /** A file is sent as a form, not as JSON: the content type is the browser's (it names the boundary). */
  attach: async (id: string, file: File) => {
    const form = new FormData()
    form.append('file', file)
    const response = await fetch(apiUrl(`/api/backlog/items/${p(id)}/attachments`), { method: 'POST', body: form, credentials: 'same-origin' })
    if (!response.ok) {
      let message = response.statusText
      try { message = (await response.json()).error ?? message } catch { /* the status text stands */ }
      throw new ApiError(response.status, message)
    }
  },
  importItems: (project: string, text: string) => request<{ made: number; items: { id: string; key: string; title: string }[] }>(`/api/backlog/projects/${p(project)}/import`, json('POST', { text })),
  exportUrl: (project: string) => apiUrl(`/api/backlog/projects/${p(project)}/export`),
}
