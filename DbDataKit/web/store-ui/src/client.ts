import { StoreError, type ModelSchema, type RowChange, type RowPage, type RowValues, type StoredRow, type Violation } from './types'

/** How a host reaches the Store: the console sends it through its relay as the signed-in person, an app with its own scoped token. The path is relative to the Store's management API root. */
export type Fetcher = (path: string, init?: RequestInit) => Promise<Response>

/** What the controls need of the Store. A host can supply its own (a cache, a fake in a test) in place of <see cref="createStoreClient"/>. */
export interface StoreClient {
  schema(model: string): Promise<ModelSchema>
  rows(model: string, entity: string, page?: { limit?: number; offset?: number }): Promise<RowPage>
  row(model: string, entity: string, key: RowValues): Promise<StoredRow | null>
  history(model: string, entity: string, key: RowValues): Promise<StoredRow[]>
  apply(model: string, changes: RowChange[]): Promise<void>
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

export function createStoreClient(fetcher: Fetcher, base = '/api/v1'): StoreClient {
  const json = async <T,>(path: string, init?: RequestInit): Promise<T> => {
    const response = await fetcher(`${base}${path}`, init)
    return response.ok ? ((await response.json()) as T) : fail(response)
  }
  return {
    schema: (model) => json<ModelSchema>(`/models/${enc(model)}/schema`),
    rows: (model, entity, page = {}) => json<RowPage>(`/models/${enc(model)}/entities/${enc(entity)}/rows?limit=${page.limit ?? 25}&offset=${page.offset ?? 0}`),
    row: async (model, entity, key) => {
      const response = await fetcher(`${base}/models/${enc(model)}/entities/${enc(entity)}/row?${keyQuery(key)}`)
      if (response.status === 404) return null
      return response.ok ? ((await response.json()) as StoredRow) : fail(response)
    },
    history: async (model, entity, key) => (await json<{ versions: StoredRow[] }>(`/models/${enc(model)}/entities/${enc(entity)}/row?${keyQuery(key)}&history=true`)).versions,
    apply: async (model, changes) => {
      const response = await fetcher(`${base}/models/${enc(model)}/changes`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ changes }) })
      if (!response.ok) await fail(response)
    },
  }
}
