import type { ViewKind } from '../views/document'
import type { RenderedWidget, RenderedWidgets } from '../views/rendered'
import { validateText } from './text'
import { ViewsClientError } from './types'
import type { ClientDiagnostic, DocumentSummary, OpenedDocument, ViewsClient, ViewsClientErrorCode } from './types'

/**
 * The typed client of the `views` serving module (plan 32 DBR11, M4): the same `ViewsClient` as the fixture, over the module's REST. **Not wired until DBR11 exists.** Of the routes, the plan fixes three and DBR28 one;
 * the rest are this file's proposal, marked PROVISIONAL, and DBR11 settles them (the done-when there says the types are checked against its schema):
 *
 * | Call | Route | Source |
 * | --- | --- | --- |
 * | `open` | `GET /api/v1/dashboards/{name}`, `GET /api/v1/reports/{name}` | plan 32 DBR11 (the body, `{name, kind, version, text}`, is PROVISIONAL) |
 * | `resolve` | `POST /api/v1/dashboards/{name}/render`, `POST /api/v1/reports/{name}/render` with `{parameters, layout, widgets?}` | plan 32 DBR11 (`widgets` narrows the request to the dependent widgets: PROVISIONAL) |
 * | `preview` | `POST /api/v1/views/preview` with `{widget, widget_id?, document?, parameters}` | plan 32 DBR28 |
 * | `list` | `GET /api/v1/dashboards`, `GET /api/v1/reports` giving `{items: [{name, title}]}` | PROVISIONAL |
 * | `save` | `PUT /api/v1/dashboards/{name}`, `PUT /api/v1/reports/{name}` with `{text, expected_version?}` giving `{name, version}` | PROVISIONAL (DBR10 and DBR12 own what is behind it) |
 * | `validate` | local: the TypeScript validator (the loaders' checks); the server's loader runs again on save | decided here |
 *
 * Errors come as `{code, message, diagnostics?}` with the status: 404 `not_found`, 409 `version_conflict`, 400 and 422 `invalid`, 401 and 403 `denied`; anything else, and a network failure, is `unavailable`.
 */

export interface ModuleClientOptions {
  /** The module's address, with no trailing slash (the console's relay, or the serving layer). */
  baseUrl: string
  /** The fetch to use; the global one by default. A test passes a fake. */
  fetch?: typeof fetch
  /** Headers to add to every request, such as the caller's token. Asked for each request. */
  headers?: () => HeadersInit | Promise<HeadersInit>
}

const collection = (kind: ViewKind) => (kind === 'dashboard' ? 'dashboards' : 'reports')
const fromStatus = (status: number): ViewsClientErrorCode =>
  status === 404 ? 'not_found' : status === 409 ? 'version_conflict' : status === 400 || status === 422 ? 'invalid' : status === 401 || status === 403 ? 'denied' : 'unavailable'

export function moduleClient(options: ModuleClientOptions): ViewsClient {
  const base = options.baseUrl.replace(/\/+$/, '')
  const doFetch = options.fetch ?? ((input, init) => fetch(input, init))

  async function call<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
    const extra = options.headers ? await options.headers() : {}
    let response: Response
    try {
      response = await doFetch(`${base}${path}`, {
        method,
        headers: { accept: 'application/json', ...(body !== undefined ? { 'content-type': 'application/json' } : {}), ...extra },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal,
      })
    } catch (e) {
      if (signal?.aborted) throw e
      throw new ViewsClientError('unavailable', `The views module could not be reached: ${e instanceof Error ? e.message : String(e)}`)
    }
    const text = await response.text()
    let data: unknown
    if (text.length > 0) {
      try { data = JSON.parse(text) } catch { data = undefined }
    }
    if (!response.ok) {
      const problem = (data ?? {}) as { code?: string; message?: string; diagnostics?: ClientDiagnostic[] }
      throw new ViewsClientError(fromStatus(response.status), problem.message ?? `The views module answered ${response.status}.`, { status: response.status, diagnostics: problem.diagnostics })
    }
    if (data === undefined) throw new ViewsClientError('bad_response', `The views module answered ${response.status} with a body that is not JSON.`, { status: response.status })
    return data as T
  }

  const segment = (name: string) => encodeURIComponent(name)

  return {
    async list(kind) {
      const kinds: ViewKind[] = kind ? [kind] : ['dashboard', 'report']
      const all = await Promise.all(kinds.map(async (k) => (await call<{ items: Array<{ name: string; title: string }> }>('GET', `/api/v1/${collection(k)}`)).items.map((i): DocumentSummary => ({ name: i.name, title: i.title, kind: k }))))
      return all.flat().sort((a, b) => a.name.localeCompare(b.name))
    },

    async open(kind, name): Promise<OpenedDocument> {
      const body = await call<{ name: string; version: string; text: string }>('GET', `/api/v1/${collection(kind)}/${segment(name)}`)
      if (typeof body.text !== 'string' || typeof body.version !== 'string') throw new ViewsClientError('bad_response', 'The views module gave a document with no `text` or `version`.')
      const result = validateText(body.text, { kind, file: `${name}.yaml` })
      return { name, kind, text: body.text, version: body.version, document: result.document, diagnostics: result.diagnostics }
    },

    resolve(ref, layout, parameters, resolveOptions = {}) {
      return call<RenderedWidgets>('POST', `/api/v1/${collection(ref.kind)}/${segment(ref.name)}/render`, { parameters, layout, ...(resolveOptions.only ? { widgets: resolveOptions.only } : {}) }, resolveOptions.signal)
    },

    async validate(text, validateOptions) {
      return validateText(text, validateOptions)
    },

    async save(kind, name, text, expectedVersion) {
      return call<{ name: string; version: string }>('PUT', `/api/v1/${collection(kind)}/${segment(name)}`, { text, ...(expectedVersion !== undefined ? { expected_version: expectedVersion } : {}) })
    },

    preview(request) {
      return call<RenderedWidget>('POST', '/api/v1/views/preview', { widget: request.widget, widget_id: request.widgetId, document: request.document, parameters: request.parameters ?? {} }, request.signal)
    },
  }
}
