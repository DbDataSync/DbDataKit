import { ApiError, apiUrl, request } from '@dbdatakit/shell'
import { createStoreClient, type Fetcher } from '@dbdatakit/store-ui'

export interface ModelSummary { name: string; title: string | null; valid: boolean; entities: string[] }

/** One diagnostic of the Store's own loader: where it is and what it says. The screen shows these and never makes up a second set. */
export interface Diagnostic { code: string; severity: 'error' | 'warning' | 'info' | string; file: string; line: number; column: number; message: string; fix: string | null }

/** A model as the Store holds it: its text, the version a save quotes, and what this caller may do (the server checks every call again). */
export interface ModelDocument { name: string; text: string; version: string; diagnostics: Diagnostic[]; can: { save: boolean; remove: boolean; admin: boolean } }

/** One save, creation or removal of a model's text: who and when; `current` is the text the model holds now. A text older than the journal has no author. */
export interface HistoryEntry { version: string; at: string; by: string | null; by_name: string | null; kind: 'created' | 'saved' | 'removed' | string; current: boolean }
export interface ModelHistory { name: string; exists: boolean; current: string | null; versions: HistoryEntry[] }

export interface Statement { object: string; description: string; text: string }
export interface StoragePlan { engine: string; statements: Statement[]; existing: string[] | null; connection_problem?: string }
export interface MigrationPlan { up_to_date: boolean; applied: boolean; statements: Statement[]; blocked: string[]; notes: string[] }
export interface CopyState { name: string; definition: string | null; applied_at: string | null; status: 'current' | 'behind' | 'unknown' | string }
export interface Migrations { definition: string; instances: CopyState[] }
export interface InstanceRow { name: string; connection: string | null; schema: string | null }
export interface Instances { mode: string; instances: InstanceRow[] }

export interface ChangeRequestRow {
  id: string; model: string; instance: string; state: 'pending' | 'approved' | 'rejected' | string
  proposed_by: string; proposed_by_name: string | null; by_agent: boolean; proposed_at: string; description: string | null; change_count: number
  decided_by: string | null; decided_at: string | null; note: string | null
  /** The proposed row changes, as sent; only on a single request. */
  changes?: ProposedChange[]
}
export interface ProposedChange { action: 'insert' | 'update' | 'delete' | string; entity: string; key?: Record<string, unknown>; expected_version?: number; values?: Record<string, unknown>; effective_from?: string }
export interface RequestList { requests: ChangeRequestRow[]; can_decide: boolean; decision_needs: string; approval: string }

/** The Store's calls through the console's relay, as the signed-in person. The same controls an app mounts get a fetcher with the app's own token instead. */
export const relayFetcher: Fetcher = (path, init) => fetch(apiUrl(path), { ...init, credentials: 'same-origin' })

export const storeClient = createStoreClient(relayFetcher)

const enc = encodeURIComponent
const base = (model: string, instance: string | null) => `/api/v1/models/${enc(model)}${instance && instance !== 'default' ? `/instances/${enc(instance)}` : ''}`
const text = (body: string): RequestInit => ({ method: 'POST', body, headers: { 'Content-Type': 'text/plain' } })

export const storeApi = {
  models: () => request<{ models: ModelSummary[] }>('/api/v1/models').then((r) => r.models),
  model: (name: string) => request<ModelDocument>(`/api/v1/models/${enc(name)}`),
  /** A dry run: the Store's diagnostics for a text as it would be saved under this name. */
  validate: (name: string, body: string) => request<{ valid: boolean; diagnostics: Diagnostic[] }>(`/api/v1/models/${enc(name)}/validate`, text(body)),
  /** Saves, quoting the version that was read (none for a new model). A refusal is an ApiError whose body carries the diagnostics or the current version. */
  save: (name: string, body: string, expectedVersion: string | null) =>
    request<{ name: string; created: boolean; version: string; diagnostics: Diagnostic[] }>(`/api/v1/models/${enc(name)}${expectedVersion ? `?expected_version=${enc(expectedVersion)}` : ''}`, { method: 'PUT', body, headers: { 'Content-Type': 'text/plain' } }),
  remove: (name: string) => request<void>(`/api/v1/models/${enc(name)}`, { method: 'DELETE' }),
  /** The model's earlier texts, newest first (UI07): who saved each and when. */
  history: (name: string) => request<ModelHistory>(`/api/v1/models/${enc(name)}/history`),
  /** One earlier text; restoring it is an ordinary save of that text quoting the version the model holds now. */
  historyText: (name: string, version: string) => request<{ name: string; version: string; text: string }>(`/api/v1/models/${enc(name)}/history/${enc(version)}`),
  instances: (name: string) => request<Instances>(`/api/v1/models/${enc(name)}/instances`),
  migrations: (name: string) => request<Migrations>(`/api/v1/models/${enc(name)}/migrations`),
  storage: (name: string, instance: string | null) => request<StoragePlan>(`${base(name, instance)}/storage`),
  createStorage: (name: string, instance: string | null) => request<{ engine: string; statements: Statement[] }>(`${base(name, instance)}/storage/create`, { method: 'POST' }),
  migrate: (name: string, instance: string | null, apply: boolean) => request<MigrationPlan>(`${base(name, instance)}/migrate${apply ? '?apply=true' : ''}`, { method: 'POST' }),
  requests: (name: string, instance: string | null, state: string | null) => request<RequestList>(`${base(name, instance)}/requests${state ? `?state=${enc(state)}` : ''}`),
  changeRequest: (name: string, instance: string | null, id: string) => request<ChangeRequestRow>(`${base(name, instance)}/requests/${enc(id)}`),
  decide: (name: string, instance: string | null, id: string, approve: boolean, note: string) =>
    request<ChangeRequestRow>(`${base(name, instance)}/requests/${enc(id)}/${approve ? 'approve' : 'reject'}`, { method: 'POST', body: JSON.stringify(note.trim() ? { note: note.trim() } : {}) }),
}

/** What a refusal carried besides its sentence: the Store's diagnostics (a model that does not load), or the current version (a stale save). */
export interface Violation { rule: string; entity: string; attribute: string | null; message: string }

export function refusalOf(error: unknown): { message: string; diagnostics: Diagnostic[]; violations: Violation[]; currentVersion: string | null; status: number | null } {
  if (error instanceof ApiError) {
    const body = (error.body ?? {}) as { diagnostics?: Diagnostic[]; violations?: Violation[]; current_version?: string | null }
    return { message: error.message, diagnostics: body.diagnostics ?? [], violations: body.violations ?? [], currentVersion: body.current_version ?? null, status: error.status }
  }
  return { message: error instanceof Error ? error.message : String(error), diagnostics: [], violations: [], currentVersion: null, status: null }
}
