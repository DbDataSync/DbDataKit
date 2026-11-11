import { SemanticError, type ModelCatalog, type ModelSummary, type SemanticColumn, type SemanticErrorCode, type SemanticQuery, type SemanticResult, type SemanticValue } from './types'

/** How a host reaches the semantic endpoint: a `fetch`-shaped function that already carries who is asking (an app adds its scoped token, the console its relay). The path is relative to the host's API root. */
export type Fetcher = (path: string, init?: RequestInit) => Promise<Response>

export const SPEC_VERSION = '0.3'

export interface SemanticClient {
  /** The semantic models the caller may query. */
  models(): Promise<ModelSummary[]>
  /** The measures and attributes of one model. A model that is not served rejects with `not_found`. */
  catalog(model: string): Promise<ModelCatalog>
  /** Runs a request as the caller. A request the engine will not run (it needs a question back, or is outside what it supports) rejects with `clarify` or `unsupported` and the engine's reason. */
  query(model: string, request: SemanticQuery, options?: { signal?: AbortSignal }): Promise<SemanticResult>
}

const enc = encodeURIComponent

const fromStatus = (status: number): SemanticErrorCode =>
  status === 404 ? 'not_found' : status === 401 || status === 403 ? 'denied' : status === 400 || status === 422 ? 'invalid' : 'unavailable'

async function fail(response: Response): Promise<never> {
  let body: { error?: string; message?: string; code?: string } = {}
  try { body = await response.json() } catch { /* not JSON: the status says enough */ }
  const code = body.code === 'clarify' || body.code === 'unsupported' ? body.code : fromStatus(response.status)
  throw new SemanticError(body.message ?? body.error ?? `The semantic endpoint answered ${response.status}.`, code, response.status, body)
}

/** The request the engine reads: the envelope of docs/semantic/query.md around the query. */
export function envelope(request: SemanticQuery) {
  if (!Number.isInteger(request.limit) || request.limit < 1 || request.limit > 10_000) throw new SemanticError(`A semantic query states a limit from 1 to 10,000, not ${request.limit}.`, 'invalid')
  if (request.measures.length === 0) throw new SemanticError('A semantic query names at least one measure.', 'invalid')
  return { spec_version: SPEC_VERSION, kind: 'query', query: request }
}

/** Rows as the server sends them (an object per row, or an array in column order) as objects by column name. */
function rowsOf(columns: SemanticColumn[], rows: unknown): Record<string, SemanticValue>[] {
  if (!Array.isArray(rows)) throw new SemanticError('The semantic endpoint answered without rows.', 'bad_response')
  return rows.map((row) => Array.isArray(row) ? Object.fromEntries(columns.map((c, i) => [c.name, (row[i] ?? null) as SemanticValue])) : (row as Record<string, SemanticValue>))
}

export function createSemanticClient(fetcher: Fetcher, base = '/api/v1'): SemanticClient {
  const call = async (path: string, init?: RequestInit): Promise<Response> => {
    let response: Response
    try { response = await fetcher(`${base}${path}`, init) }
    catch (e) {
      if (init?.signal?.aborted) throw e
      throw new SemanticError(`The semantic endpoint could not be reached: ${e instanceof Error ? e.message : String(e)}`, 'unavailable')
    }
    return response.ok ? response : fail(response)
  }
  return {
    models: async () => ((await (await call('/semantic/models')).json()) as { models: ModelSummary[] }).models,
    catalog: async (model) => (await (await call(`/semantic/models/${enc(model)}/catalog`)).json()) as ModelCatalog,
    query: async (model, request, options) => {
      const response = await call(`/semantic/models/${enc(model)}/query`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(envelope(request)), signal: options?.signal })
      const body = (await response.json()) as { status?: string; columns?: SemanticColumn[]; rows?: unknown; warnings?: string[]; request_hash?: string | null; reason?: string }
      if (body.status === 'clarify' || body.status === 'unsupported') throw new SemanticError(body.reason ?? `The engine answered '${body.status}'.`, body.status, response.status, body)
      const columns = body.columns ?? []
      return { status: body.status ?? 'ok', columns, rows: rowsOf(columns, body.rows), warnings: body.warnings ?? [], requestHash: body.request_hash ?? null }
    },
  }
}
