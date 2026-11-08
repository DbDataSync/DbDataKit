import { ApiError, apiUrl, request } from '@dbdatakit/shell'
import { createStoreClient, type Fetcher } from '@dbdatakit/store-ui'

export interface ModelSummary { name: string; title: string | null; valid: boolean; entities: string[] }

/** One diagnostic of the Store's own loader: where it is and what it says. The screen shows these and never makes up a second set. */
export interface Diagnostic { code: string; severity: 'error' | 'warning' | 'info' | string; file: string; line: number; column: number; message: string; fix: string | null }

/** A model as the Store holds it: its text, the version a save quotes, and what this caller may do (the server checks every call again). */
export interface ModelDocument { name: string; text: string; version: string; diagnostics: Diagnostic[]; can: { save: boolean; remove: boolean; admin: boolean } }

/** The Store's calls through the console's relay, as the signed-in person. The same controls an app mounts get a fetcher with the app's own token instead. */
export const relayFetcher: Fetcher = (path, init) => fetch(apiUrl(path), { ...init, credentials: 'same-origin' })

export const storeClient = createStoreClient(relayFetcher)

const enc = encodeURIComponent
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
