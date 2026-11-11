import { ApiError, request } from '@dbdatakit/shell'
import type { Diagnostic } from './agents'

export interface KnowledgeFile { id: string; title: string; role: string; owner: string | null }
export interface KnowledgeProjectSummary {
  name: 'install' | 'project'; exists: boolean; items: number; chunks: number; instructions: number; guardrails: number; references: number; failed: Diagnostic[]; orphans: string[]; files: KnowledgeFile[]
}
export interface SubjectEntry { type: string; name: string; narrowingKind: string; narrowingName: string; status: string; source?: string }

/** What reading the subjects from the lineage graph found (plan 27a KN02): how many it shows, how many it showed before and no longer does, and whether every server answered. */
export interface SubjectRefresh { found: number; nowGone: number; complete: boolean; total: number; problems: string[]; overview: KnowledgeOverview }

/** The line that says what a reading of the lineage graph did, and why nothing was marked gone when it could not be sure. */
export function refreshLine(r: Pick<SubjectRefresh, 'found' | 'nowGone' | 'complete' | 'problems'>): string {
  const gone = r.nowGone === 1 ? '1 object is now gone' : `${r.nowGone} objects are now gone`
  if (r.complete) return `The graph shows ${r.found} subject${r.found === 1 ? '' : 's'}; ${gone}.`
  return `The graph shows ${r.found} subject${r.found === 1 ? '' : 's'}. Nothing was marked gone: ${r.problems.length > 0 ? `${r.problems[0]}${r.problems.length > 1 ? ` (and ${r.problems.length - 1} more)` : ''}` : 'no server has a lineage to answer with'}.`
}

/** Where a subject came from, for the list: the graph's own, or an administrator's word. */
export const subjectSource = (s: SubjectEntry): string => (s.source === 'lineage' ? 'lineage graph' : 'said by an administrator')
export interface WorkItem { kind: 'orphaned' | 'over-budget' | 'needs-look'; itemId: string; title: string; message: string; subject: string | null }
export interface OwnerWorklist { owner: string; work: WorkItem[] }
export interface KnowledgeOverview {
  projects: KnowledgeProjectSummary[]; hasSubjectCatalog: boolean; subjects: SubjectEntry[]; index: { built: boolean; message: string }; worklists: OwnerWorklist[]
}

export interface ChunkView { id: string; headingPath: string[]; tokens: number; startLine: number; changed: boolean; text: string }
export interface ChunkPreview { title: string | null; role: string | null; owner: string | null; diagnostics: Diagnostic[]; chunks: ChunkView[]; changedChunks: number }
export interface Reach { agent: string; adds: number; used: number | null; budget: number | null; pushesOver: boolean }
export interface ScopedItem { id: string; title: string; role: string; rank: number; scope: string; owner: string | null }
export interface SkippedItem { id: string; title: string; role: string; reason: string }
export interface ScopePreview { instructions: ScopedItem[]; guardrails: ScopedItem[]; skipped: SkippedItem[] }
export interface ContextPart { layer: string; kind: string; id: string; version: string; tokens: number }
export interface ContextShare { layer: string; id: string; version: string; tokens: number; shareOfBudget: number }
export interface ContextReport { agent: string; parts: ContextPart[]; totalTokens: number; window: number; budgetTokens: number; overBudget: boolean; basis: string; inPlay: string[]; shares: ContextShare[] }

/** A refused write carries the diagnostics that say why (file, line, message): show those, not the status text. */
async function refused<T>(call: Promise<T>): Promise<T> {
  try { return await call } catch (e) {
    const body = e instanceof ApiError ? (e.body as { diagnostics?: Diagnostic[] } | undefined) : undefined
    if (body?.diagnostics?.length) throw new Error(body.diagnostics.map((d) => `${d.file} line ${d.line}: ${d.message}${d.fix ? ` ${d.fix}` : ''}`).join(' '))
    throw e
  }
}

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })
const path = (project: string, id: string) => `${project}/${id.split('/').map(encodeURIComponent).join('/')}`

export const knowledgeApi = {
  overview: () => request<KnowledgeOverview>('/api/knowledge/overview'),
  createInstall: () => request<KnowledgeOverview>('/api/knowledge/install', json('POST')),
  item: (project: string, id: string) => request<{ project: string; id: string; text: string }>(`/api/knowledge/items/${path(project, id)}`),
  save: (project: string, id: string, text: string) => refused(request<{ saved: boolean; diagnostics: Diagnostic[] }>(`/api/knowledge/items/${path(project, id)}`, json('PUT', { text }))),
  retire: (project: string, id: string) => request<void>(`/api/knowledge/items/${path(project, id)}`, json('DELETE')),
  repoint: (project: string, id: string, subject: string, newName: string) => request<KnowledgeOverview>(`/api/knowledge/repoint/${path(project, id)}`, json('POST', { subject, newName })),
  refreshSubjects: () => request<SubjectRefresh>('/api/knowledge/subjects/refresh', json('POST')),
  setSubject: (type: string, name: string, status: 'exists' | 'gone') => request<KnowledgeOverview>('/api/knowledge/subjects', json('PUT', { type, name, status })),
  previewChunks: (project: string, id: string, text: string) => request<{ preview: ChunkPreview; reaches: Reach[] }>('/api/knowledge/preview/chunks', json('POST', { project, id, text })),
  previewScope: (inPlay: string[], audience: string, agent: string) => request<{ preview: ScopePreview; scopes: string[] }>('/api/knowledge/preview/scope', json('POST', { inPlay, audience, agent })),
  context: (agent: string, inPlay: string[]) => request<ContextReport>('/api/knowledge/context', json('POST', { agent, inPlay })),
}

/** The subjects a person types in the box for what is in play, one per line or comma: `semantic-model:Sales/measure:revenue`. Blank lines go. */
export const parseInPlay = (text: string): string[] => text.split(/[\n,]/).map((s) => s.trim()).filter((s) => s.length > 0)

/** What the worklist entry offers to do about it. */
export function workActions(item: WorkItem): ('repoint' | 'retire' | 'open')[] {
  return item.kind === 'orphaned' ? ['repoint', 'retire', 'open'] : ['open']
}

/** The words for a kind of work. */
export const workWord = (kind: WorkItem['kind']): string => ({ orphaned: 'Orphaned', 'over-budget': 'Over a budget', 'needs-look': 'Needs another look' })[kind]

/** Where an item lives for the id: policy items are the administrators' and sit in the install project's `policy/` folder. */
export const isPolicyItem = (project: string, id: string): boolean => project === 'install' && id.startsWith('policy/')

/** A new policy item, as the form writes it: always a guardrail, always owned by the administrators; the text is the person's. */
export const policyItemText = (body: string, audience: 'everyone' | 'back-end' | 'front-end'): string =>
  `---\nknowledge: 1\nrole: guardrail\nowner: administrators\n${audience === 'everyone' ? '' : `for: { audience: ${audience} }\n`}---\n${body.trim()}\n`

/** The path of a new policy item from its title: lower case words joined by hyphens, in the `policy/` folder. */
export const policyItemId = (title: string): string => {
  const slug = title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')
  return `policy/${slug || 'item'}.md`
}

/** The context report as lines a person reads: each part with its tokens and its share of the budget. */
export const contextLine = (share: ContextShare): string => `${share.layer}: ${share.id} (${share.tokens} tokens, ${Math.round(share.shareOfBudget * 100)}% of the budget)`
