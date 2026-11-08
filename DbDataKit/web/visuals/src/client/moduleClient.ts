import type { ViewKind } from '../views/document'
import type { RenderedWidget, RenderedWidgets } from '../views/rendered'
import { validateText } from './text'
import { ViewsClientError } from './types'
import type { ClientDiagnostic, DocumentSummary, OpenedDocument, ViewsClient, ViewsClientErrorCode } from './types'

/**
 * The typed client of the `views` serving module (plan 32 DBR11, M4): the same `ViewsClient` as the fixture, over the module's REST (`DbDataKit.Views.Hosting`, `ViewsModule`). The routes of `open`, `resolve` and `list` are the
 * module's own, **settled by DBR11**; `save` and `preview` are PROVISIONAL (nothing behind them yet: DBR28 owns `preview`, and a save waits on the editor's publish path), and a call to them reaches a module that answers 404.
 *
 * | Call | Route | Source |
 * | --- | --- | --- |
 * | `open` | `GET /api/v1/dashboards/{name}`, `GET /api/v1/reports/{name}` giving `{kind, name, title, version, as_of, text, document, layouts: [{name, type, default}]}` (`version` is the registry's number as text, `text` the file as published) | DBR11 |
 * | `resolve` | `POST /api/v1/dashboards/{name}/render`, `POST /api/v1/reports/{name}/render` with `{parameters, layout, only?}` giving `rendered-widgets` (`only` narrows the request to the dependent widgets; a body key the module does not know is refused) | DBR11 |
 * | `preview` | `POST /api/v1/views/preview` with `{widget, widget_id?, document?, parameters}` | plan 32 DBR28 (PROVISIONAL: not in the module yet) |
 * | `list` | `GET /api/v1/dashboards`, `GET /api/v1/reports` giving `{items: [{name, title, version}]}` | DBR11 |
 * | `save` | `PUT /api/v1/dashboards/{name}`, `PUT /api/v1/reports/{name}` with `{text, expected_version?}` giving `{name, version}` | PROVISIONAL (DBR10 and DBR12 own what is behind it; the module does not take it yet) |
 * | `validate` | local: the TypeScript validator (the loaders' checks); the server's loader runs again on save | decided here |
 *
 * Errors come as `{code, message, diagnostics?}` with the status: 404 `not_found`, 409 `version_conflict`, 400 and 422 `invalid`, 401 and 403 `denied`; anything else, and a network failure, is `unavailable` (the module's 503 while it drains, and its 501 `export_unavailable`, are among them).
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
      return call<RenderedWidgets>('POST', `/api/v1/${collection(ref.kind)}/${segment(ref.name)}/render`, { parameters, layout, ...(resolveOptions.only ? { only: resolveOptions.only } : {}) }, resolveOptions.signal)
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
