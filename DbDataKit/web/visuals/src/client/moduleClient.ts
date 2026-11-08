import type { ViewKind } from '../views/document'
import type { RenderedWidgets } from '../views/rendered'
import { validateText } from './text'
import { ViewsClientError } from './types'
import type { ClientDiagnostic, DocumentSummary, OpenedDocument, ViewsClient, ViewsClientErrorCode } from './types'
import { diagnosticsFromRefusal } from './refusal'

/**
 * The typed client of the `views` serving module (plan 32 DBR11, M4): the same `ViewsClient` as the fixture, over the module's REST (`DbDataKit.Views.Hosting`, `ViewsModule`). The routes of `open`, `resolve` and `list` are the
 * module's own, **settled by DBR11**; `preview` and `previewDocument` are DBR28's (the module's `POST /api/v1/views/preview`; the console's relay does not carry it yet, so through the relay it answers 404), and `save` is PROVISIONAL (it waits on the editor's publish path).
 *
 * | Call | Route | Source |
 * | --- | --- | --- |
 * | `open` | `GET /api/v1/dashboards/{name}`, `GET /api/v1/reports/{name}` giving `{kind, name, title, version, as_of, text, document, layouts: [{name, type, default}]}` (`version` is the registry's number as text, `text` the file as published) | DBR11 |
 * | `resolve` | `POST /api/v1/dashboards/{name}/render`, `POST /api/v1/reports/{name}/render` with `{parameters, layout, only?}` giving `rendered-widgets` (`only` narrows the request to the dependent widgets; a body key the module does not know is refused) | DBR11 |
 * | `preview` | `POST /api/v1/views/preview` with `{kind, widget, widget_id, document?: {kind, parameters}, parameters}` giving `rendered-widgets` with the one entry; `previewDocument` posts `{kind, text, layout?, only?, parameters}` and gives the layout's rendered widgets; both resolve as the caller and store nothing | DBR28 |
 * | `list` | `GET /api/v1/dashboards`, `GET /api/v1/reports` giving `{items: [{name, title, version}]}` | DBR11 |
 * | `save` | `PUT /api/v1/dashboards/{name}`, `PUT /api/v1/reports/{name}` with `{text, expected_version?}` giving `{name, version}` | PROVISIONAL (DBR10 and DBR12 own what is behind it; the module does not take it yet) |
 * | `validate` | local: the TypeScript validator (the loaders' checks); with `serverCheck` a text that passes it is also posted to the preview route with `only: []` (nothing is resolved), so the module's own publish checks (the catalog binding) answer as diagnostics; the server's loader runs again on save | decided here, DBR28 |
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
  /** Also ask the module to check what the local validator passes (`validate`, through the preview route with nothing to resolve): the catalog binding of a publish. Off by default: each check is an audited call, and the console's relay does not carry the route yet. A module that cannot be asked leaves the local answer. */
  serverCheck?: boolean
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
      const local = validateText(text, validateOptions)
      if (!options.serverCheck || !local.ok || !validateOptions?.kind) return local
      try {
        await call('POST', '/api/v1/views/preview', { kind: validateOptions.kind, text, only: [], parameters: {} })
        return local
      } catch (e) {
        if (!(e instanceof ViewsClientError) || e.code !== 'invalid') return local
        const diagnostics = e.diagnostics && e.diagnostics.length > 0 ? e.diagnostics : diagnosticsFromRefusal(e.message, validateOptions.file)
        return { ok: false, document: local.document, diagnostics: [...local.diagnostics, ...diagnostics] }
      }
    },

    async save(kind, name, text, expectedVersion) {
      return call<{ name: string; version: string }>('PUT', `/api/v1/${collection(kind)}/${segment(name)}`, { text, ...(expectedVersion !== undefined ? { expected_version: expectedVersion } : {}) })
    },

    async preview(request) {
      const id = request.widgetId ?? 'preview'
      const kind = request.kind ?? request.draft?.kind ?? 'dashboard'
      const body = {
        kind, widget: request.widget, widget_id: id,
        ...(request.draft ? { document: { kind, parameters: request.draft.parameters ?? [] } } : {}),
        parameters: request.parameters ?? {},
      }
      const answer = await call<RenderedWidgets>('POST', '/api/v1/views/preview', body, request.signal)
      const entry = answer.widgets?.[id]
      if (!entry) throw new ViewsClientError('bad_response', `The views module answered a preview with no entry for the widget \`${id}\`.`)
      return entry
    },

    previewDocument(request) {
      return call<RenderedWidgets>('POST', '/api/v1/views/preview', {
        kind: request.kind, text: request.text, parameters: request.parameters ?? {},
        ...(request.layout !== undefined ? { layout: request.layout } : {}), ...(request.only ? { only: request.only } : {}),
      }, request.signal)
    },
  }
}
