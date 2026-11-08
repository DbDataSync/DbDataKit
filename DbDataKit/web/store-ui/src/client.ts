import { StoreError, type ApplyResult, type InstanceList, type ModelSchema, type RowChange, type RowPage, type RowValues, type StoredRow, type Violation } from './types'

/** How a host reaches the Store: the console sends it through its relay as the signed-in person, an app with its own scoped token. The path is relative to the Store's management API root. */
export type Fetcher = (path: string, init?: RequestInit) => Promise<Response>

/** What the controls need of the Store. A host can supply its own (a cache, a fake in a test) in place of <see cref="createStoreClient"/>. */
export interface StoreClient {
  /** The instances a model has, and how it keeps them apart. */
  instances(model: string): Promise<InstanceList>
  /** The same client for one instance of every model it reads and writes (null or `default` for the model's own copy). */
  withInstance(instance: string | null): StoreClient
  schema(model: string): Promise<ModelSchema>
  rows(model: string, entity: string, page?: { limit?: number; offset?: number }): Promise<RowPage>
  row(model: string, entity: string, key: RowValues): Promise<StoredRow | null>
  history(model: string, entity: string, key: RowValues): Promise<StoredRow[]>
  /** Writes a batch. The answer says when it was not applied but kept for approval (HTTP 202): the rows are not there until someone approves. */
  apply(model: string, changes: RowChange[]): Promise<ApplyResult>
}

const enc = encodeURIComponent

function keyQuery(key: RowValues) {
  return Object.entries(key).map(([name, value]) => `key.${enc(name)}=${enc(String(value))}`).join('&')
}

async function fail(response: Response): Promise<never> {
  let body: { error?: string; violations?: Violation[]; current_version?: number } = {}
  try { body = await response.json() } catch { /* not JSON: the status says enough */ }
  throw new StoreError(body.error ?? `The Store answered ${response.status}.`, response.status, body.violations ?? [], body.current_version ?? null)
}

export function createStoreClient(fetcher: Fetcher, base = '/api/v1', instance: string | null = null): StoreClient {
  // the model's own copy is the path without an instance; an instance of it has its own prefix, with the same routes below
  const under = (model: string) => `/models/${enc(model)}${instance && instance !== 'default' ? `/instances/${enc(instance)}` : ''}`
  const json = async <T,>(path: string, init?: RequestInit): Promise<T> => {
    const response = await fetcher(`${base}${path}`, init)
    return response.ok ? ((await response.json()) as T) : fail(response)
  }
  return {
    schema: (model) => json<ModelSchema>(`${under(model)}/schema`),
    instances: (model) => json<InstanceList>(`/models/${enc(model)}/instances`),
    withInstance: (next) => createStoreClient(fetcher, base, next),
    rows: (model, entity, page = {}) => json<RowPage>(`${under(model)}/entities/${enc(entity)}/rows?limit=${page.limit ?? 25}&offset=${page.offset ?? 0}`),
    row: async (model, entity, key) => {
      const response = await fetcher(`${base}${under(model)}/entities/${enc(entity)}/row?${keyQuery(key)}`)
      if (response.status === 404) return null
      return response.ok ? ((await response.json()) as StoredRow) : fail(response)
    },
    history: async (model, entity, key) => (await json<{ versions: StoredRow[] }>(`${under(model)}/entities/${enc(entity)}/row?${keyQuery(key)}&history=true`)).versions,
    apply: async (model, changes) => {
      const response = await fetcher(`${base}${under(model)}/changes`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ changes }) })
      if (!response.ok) await fail(response)
      if (response.status === 202) {
        const body = (await response.json().catch(() => ({}))) as { id?: string }
        return { waiting: { id: body.id ?? '' } }
      }
      return {}
    },
  }
}
