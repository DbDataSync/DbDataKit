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
/** What a verified query pins (plan 27a KN21): the model, the questions that are embedded, the request that runs, the SQL kept for reading, and who verified it against which model version. */
export interface VerifiedPreview {
  model: string; questions: string[]; request: string | null; semanticSql: string | null; savedQuery: string | null; savedQueryVersion: string | null; verifiedBy: string; verifiedAt: string; modelVersion: string
}
export interface ChunkPreview { title: string | null; role: string | null; owner: string | null; diagnostics: Diagnostic[]; chunks: ChunkView[]; changedChunks: number; verified?: VerifiedPreview | null }
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

// ---- KN28, KN07, KN08: the index, its embedding model and the search ----

export type SearchMode = 'words' | 'vectors' | 'both'
export interface EmbeddingBinding { provider: string | null; model: string | null; modelId: string | null; usable: boolean; problem: string | null }
export interface VectorState { readyModel: string | null; readyDimension: number | null; buildingModel: string | null; buildingDimension: number | null; chunks: number; embedded: number; buildingEmbedded: number; failed: number }
export interface IndexFailure { itemId: string; seq: number; model: string; attempts: number; message: string; failedAt: string }
export interface IndexRun { at: string; embedded: number; failed: number; remaining: number; switched: boolean; problem: string | null }
export interface KnowledgeIndexState {
  channel: string; opened: boolean; running: boolean; items: number; chunks: number; wordsMode: string; vectorsAvailable: boolean; vectorsProblem: string | null
  embedding: EmbeddingBinding; vectors: VectorState | null; failures: IndexFailure[]; lastRun: IndexRun | null; message: string
}
export interface SearchHit { itemId: string; title: string; headingPath: string[]; excerpt: string; source: string; version: string; score: number; chunks: number }
export interface SearchResult { hits: SearchHit[]; used: SearchMode; note: string | null }

/** The ways of searching the index offers: words always, vectors and both only where the index has vectors that are ready (a model is bound and complete). */
export function modesOffered(state: Pick<KnowledgeIndexState, 'opened' | 'vectorsAvailable' | 'vectors'>): SearchMode[] {
  if (!state.opened) return []
  return state.vectorsAvailable && state.vectors?.readyModel ? ['words', 'vectors', 'both'] : ['words']
}

/** The search mode in a line: what the index can do here, and why it cannot do more. */
export function searchModeLine(state: KnowledgeIndexState): string {
  if (!state.opened) return 'Not built.'
  if (!state.vectorsAvailable) return `Words only. ${state.vectorsProblem ?? 'This engine cannot hold vectors.'}`
  if (!state.embedding.provider) return 'Words only for now: no embedding model is bound.'
  if (!state.embedding.usable) return `Words only for now: ${state.embedding.problem ?? 'the embedding model cannot be used.'}`
  if (!state.vectors?.readyModel) return 'Words only until the vectors are built.'
  return 'Words, vectors, or both together.'
}

/** How much of the chunks have a vector, as a whole percentage (an empty index is whole). */
export const vectorPercent = (embedded: number, chunks: number): number => (chunks <= 0 ? 100 : Math.min(100, Math.floor((embedded / chunks) * 100)))

/** The progress of the vectors in a line: the ready index and the one being built beside it. */
export function vectorProgressLine(v: VectorState | null): string {
  if (!v) return 'No vectors.'
  const ready = v.readyModel ? `${v.readyModel} (${v.readyDimension} dimensions): ${v.embedded} of ${v.chunks} chunks` : 'no index is ready yet'
  const building = v.buildingModel ? `; building ${v.buildingModel} (${v.buildingDimension} dimensions) beside it: ${v.buildingEmbedded} of ${v.chunks} chunks, the first keeps answering until the new one is complete` : ''
  return `${ready}${building}${v.failed > 0 ? `; ${v.failed} chunk${v.failed === 1 ? '' : 's'} failed` : ''}.`
}

/** A hit's place as a line: the heading path, or the title when it has none. */
export const hitPlace = (h: SearchHit): string => (h.headingPath.length > 0 ? h.headingPath.join(' > ') : h.title)

// ---- KN10, KN17: what a publish would check ----

/** What a project adds to one agent's always-given text, against its budget (the same numbers `knowledge check` prints). */
export interface CheckReach { agent: string; adds: number; used: number; budget: number; pushesOver: boolean; items: string[] }
export interface CheckProject { name: 'install' | 'project'; ok: boolean; items: number; diagnostics: Diagnostic[]; reaches: CheckReach[] }
export interface CheckReport { projects: CheckProject[] }

/** The verdict of a check in a line: refused when there is an error, naming how many, and otherwise how many items passed and how many warnings were said. */
export function checkLine(p: Pick<CheckProject, 'ok' | 'items' | 'diagnostics'>): string {
  const errors = p.diagnostics.filter((d) => d.severity === 'error').length
  const warnings = p.diagnostics.filter((d) => d.severity === 'warning').length
  const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`
  if (!p.ok) return `A publish would be refused: ${plural(errors, 'error')}${warnings > 0 ? ` and ${plural(warnings, 'warning')}` : ''}.`
  return `${plural(p.items, 'item')} would be published${warnings > 0 ? `, with ${plural(warnings, 'warning')}` : ''}.`
}

/** What an agent would be given by a project, in a line: the tokens it adds to what it has, against its budget. */
export const reachLine = (r: CheckReach): string =>
  `${r.agent}: adds ${r.adds} tokens to ${r.used} of a budget of ${r.budget}${r.pushesOver ? ', which is over' : ''} (${r.items.join(', ')}).`

/** Where a diagnostic is, as `file line n` (a diagnostic with no line is about the whole file or project). */
export const placeOf = (d: Diagnostic): string => (d.line > 0 ? `${d.file} line ${d.line}` : d.file)

export const knowledgeApi = {
  check: () => request<CheckReport>('/api/knowledge/check'),
  index: () => request<KnowledgeIndexState>('/api/knowledge/index'),
  build: () => request<{ sync: { added: number; unchanged: number; removed: number }; view: KnowledgeIndexState }>('/api/knowledge/index/build', json('POST')),
  retry: () => request<KnowledgeIndexState>('/api/knowledge/index/retry', json('POST')),
  rebuild: () => request<KnowledgeIndexState>('/api/knowledge/index/rebuild', json('POST')),
  bindEmbedding: (provider: string | null, model: string | null) => refused(request<KnowledgeIndexState>('/api/knowledge/index/embedding', json('PUT', { provider, model }))),
  search: (question: string, mode: SearchMode) => request<SearchResult>('/api/knowledge/search', json('POST', { question, mode })),
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

/** A verified query in a line, as an answer from it would say it: "Verified by Maria on 2026-11-06 against version 4 of Sales." */
export const verifiedLine = (v: VerifiedPreview): string => `Verified by ${v.verifiedBy} on ${v.verifiedAt} against version ${v.modelVersion} of ${v.model}.`

/** What the verified query runs: the pinned request, or the saved query of the model it names (with its version when pinned). */
export const verifiedRuns = (v: VerifiedPreview): string =>
  v.savedQuery ? `the saved query ${v.model}/${v.savedQuery}${v.savedQueryVersion ? `@${v.savedQueryVersion}` : ''}` : 'the pinned request'

/** The path of a new verified query from its first question: `queries/<words>.md`. */
export const verifiedQueryId = (question: string): string => {
  const slug = question.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 60).replace(/-+$/, '')
  return `queries/${slug || 'question'}.md`
}

/** A new verified query as the form writes it: the question and model the person gave, the request they pasted (JSON text; a starting one when empty) and who verified it today. The strict reader's diagnostics say what is still wrong. */
export function verifiedQueryText(input: { question: string; model: string; request: string; by: string; today: string; owner?: string }): string {
  const question = input.question.trim().replace(/\s+/g, ' ')
  const request = input.request.trim().length > 0 ? input.request.trim() : JSON.stringify({ kind: 'query', query: { measures: ['measure'], limit: 100 } })
  return `---\nknowledge: 1\nkind: verified-query\ntitle: ${JSON.stringify(question)}\n${input.owner ? `owner: ${input.owner}\n` : ''}model: ${input.model.trim()}\nquestions:\n  - ${JSON.stringify(question)}\nrequest: ${JSON.stringify(request)}\nverified: { by: ${JSON.stringify(input.by)}, at: ${input.today}, model-version: "1" }\n---\nWhy this is the answer, and what it leaves out.\n`
}

/** The context report as lines a person reads: each part with its tokens and its share of the budget. */
export const contextLine = (share: ContextShare): string => `${share.layer}: ${share.id} (${share.tokens} tokens, ${Math.round(share.shareOfBudget * 100)}% of the budget)`
