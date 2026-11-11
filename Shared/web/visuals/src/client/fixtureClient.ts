import type { ViewKind } from '../views/document'
import type { ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget, RenderedWidgets } from '../views/rendered'
import type { Catalog } from '../views/catalog'
import { textVersion, validateText } from './text'
import { ViewsClientError } from './types'
import type { ClientDiagnostic, DocumentSummary, OpenedDocument, PreviewRequest, ResolveOptions, SaveResult, ValidationResult, ViewsClient } from './types'

/**
 * What the fixture source holds: documents as files (their text), and for each document the recorded result of each widget (`widget-results/<document>/<widget id>.json`, the `rendered-widgets` form), and
 * the bytes of the images a document names by hash (as `data:` URIs). Nothing here knows where it was read from: the Playwright fixture and the tests bundle the files, a page can offer "Open file".
 */
export interface FixtureBundle {
  /** Document text by `kind/name` (`dashboard/retail-sales-today`). */
  documents: Record<string, string>
  /** Recorded results by document key, then widget id. */
  results?: Record<string, Record<string, RenderedWidget>>
  /** Image data URIs by hash (`sha256:...`). */
  images?: Record<string, string>
}

/** What the fixture client keeps its documents in: a `Map` does, and so does a store that outlives the page (the console keeps saved documents in the browser's storage). */
export interface DocumentMap {
  get(key: string): string | undefined
  set(key: string, text: string): unknown
  delete(key: string): unknown
  entries(): Iterable<[string, string]>
}

export interface FixtureClientOptions {
  /** Where documents live; default a `Map` of the bundle's documents. */
  documents?: DocumentMap
  /** The `as_of` every resolution reports (a fixture's rows were read once); fixed so a snapshot is the same on every run. */
  asOf?: string
  /** The catalogs of the models the fixture knows (`ViewsClient.catalog`), by model name. */
  catalogs?: Record<string, Catalog>
  /** What the server's catalog check would add to `validate`: the diagnostics for the parts of a document the browser cannot check (filters, order, totals). A test gives it; the fixture source of the console gives a stand-in. */
  validateHook?: (document: ViewsDoc | null, text: string, options: { kind?: ViewKind; file?: string } | undefined) => ClientDiagnostic[] | Promise<ClientDiagnostic[]>
  /** Called for each resolution with the widgets asked for: a counting fixture (a parameter change resolves only the dependent widgets). */
  onResolve?: (call: { kind: ViewKind; name: string; layout: string; widgets: string[]; parameters: ParameterValues }) => void
}

export const FIXTURE_AS_OF = '2026-11-08T09:00:00Z'
export const NO_RECORDED_RESULT = 'no_recorded_result'
export const NO_RECORDED_IMAGE = 'no_recorded_image'

const key = (kind: ViewKind, name: string) => `${kind}/${name}`
const notFound = (kind: ViewKind, name: string) => new ViewsClientError('not_found', `There is no ${kind} named \`${name}\`.`)
const error = (code: string, message: string): RenderedWidget => ({ type: 'error', code, message })

/** The widget ids a layout places, in document order, once each (a flow layout places them in blocks). */
export function placedWidgets(document: ViewsDoc, layoutName: string): string[] {
  const layout = document.layouts.find((l) => l.name === layoutName)
  if (!layout) return []
  const ids: string[] = []
  const add = (id: unknown) => { if (typeof id === 'string' && !ids.includes(id)) ids.push(id) }
  if (layout.type === 'flow') {
    for (const section of layout.sections as Array<{ blocks?: Array<{ widget?: string; row?: Array<{ widget?: string }> }> }>)
      for (const block of section.blocks ?? []) {
        add(block.widget)
        for (const cell of block.row ?? []) add(cell.widget)
      }
  } else for (const tile of layout.tiles) add(tile.widget)
  return ids
}

/**
 * The client of the fixture source: a `ViewsClient` over a bundle, no server. `resolve` gives each widget its recorded result; a widget with none gets its own error entry (so one missing result never fails the page),
 * a text widget needs none, a control needs one only when it lists values. `save` keeps the text in memory under the file's hash as its version and refuses a stale one, as every real store does.
 */
export function fixtureClient(bundle: FixtureBundle, options: FixtureClientOptions = {}): ViewsClient {
  const documents: DocumentMap = options.documents ?? new Map(Object.entries(bundle.documents))
  const asOf = options.asOf ?? FIXTURE_AS_OF

  const summary = (k: string, text: string): DocumentSummary | null => {
    const [kind, name] = k.split('/') as [ViewKind, string]
    const result = validateText(text, { kind, file: `${name}.yaml` })
    const title = (result.document?.title ?? name)
    return { name, kind, title }
  }

  const resolveWidget = (kind: ViewKind, name: string, id: string, widget: ViewsDoc['widgets'][string]): RenderedWidget => {
    const recorded = bundle.results?.[key(kind, name)]?.[id]
    switch (widget.type) {
      case 'text': return { type: 'text' }
      case 'embed': return { type: 'embed', url: widget.url, framed: false, ...(widget.link_text ? { link_text: widget.link_text } : {}) }
      case 'custom': return error('custom_not_drawn', `The widget \`${id}\` is code of an app (${widget.component}); only the app's own host draws it.`)
      case 'image': {
        const src = bundle.images?.[widget.hash] ?? (recorded?.type === 'image' ? recorded.src : undefined)
        return src ? { type: 'image', src } : error(NO_RECORDED_IMAGE, `The fixture source has no bytes for the image ${widget.hash}.`)
      }
      case 'control':
        if (recorded) return recorded
        return widget.values ? error(NO_RECORDED_RESULT, `The fixture source has no recorded values for the control \`${id}\`.`) : { type: 'control' }
      default:
        return recorded ?? error(NO_RECORDED_RESULT, `The fixture source has no recorded result for the widget \`${id}\` of ${key(kind, name)}.`)
    }
  }

  return {
    async list(kind) {
      return [...documents.entries()]
        .filter(([k]) => kind === undefined || k.startsWith(`${kind}/`))
        .map(([k, text]) => summary(k, text)!)
        .sort((a, b) => a.name.localeCompare(b.name))
    },

    async open(kind, name) {
      const text = documents.get(key(kind, name))
      if (text === undefined) throw notFound(kind, name)
      const result = validateText(text, { kind, file: `${name}.yaml` })
      return { name, kind, text, version: textVersion(text), document: result.document, diagnostics: result.diagnostics } satisfies OpenedDocument
    },

    async resolve(ref, layout, parameters, resolveOptions: ResolveOptions = {}) {
      const text = documents.get(key(ref.kind, ref.name))
      if (text === undefined) throw notFound(ref.kind, ref.name)
      const { document, diagnostics } = validateText(text, { kind: ref.kind, file: `${ref.name}.yaml` })
      if (!document) throw new ViewsClientError('invalid', `The ${ref.kind} \`${ref.name}\` has errors and is not resolved: ${diagnostics.find((d) => d.severity === 'error')?.message ?? ''}`, { diagnostics })
      const layoutDoc = document.layouts.find((l) => l.name === layout)
      if (!layoutDoc) throw new ViewsClientError('invalid', `The ${ref.kind} \`${ref.name}\` has no layout named \`${layout}\`; its layouts are ${document.layouts.map((l) => `\`${l.name}\``).join(', ')}.`)
      const placed = placedWidgets(document, layout)
      const asked = resolveOptions.only ? placed.filter((id) => resolveOptions.only!.includes(id)) : placed
      options.onResolve?.({ kind: ref.kind, name: ref.name, layout, widgets: asked, parameters })
      const widgets: Record<string, RenderedWidget> = {}
      for (const id of asked) widgets[id] = resolveWidget(ref.kind, ref.name, id, document.widgets[id]!)
      return { kind: 'rendered_widgets', layout, as_of: asOf, widgets } satisfies RenderedWidgets
    },

    async validate(text, validateOptions): Promise<ValidationResult> {
      const result = validateText(text, validateOptions)
      const more = options.validateHook ? await options.validateHook(result.document, text, validateOptions) : []
      if (more.length === 0) return result
      const diagnostics = [...result.diagnostics, ...more]
      return { ...result, diagnostics, ok: result.ok && !more.some((d) => d.severity === 'error') }
    },

    async catalog(model) {
      const found = options.catalogs?.[model]
      if (!found) throw new ViewsClientError('not_found', `The fixture source has no catalog for the model \`${model}\`.`)
      return found
    },

    async save(kind, name, text, expectedVersion): Promise<SaveResult> {
      const stored = documents.get(key(kind, name))
      if (stored !== undefined && expectedVersion !== undefined && textVersion(stored) !== expectedVersion)
        throw new ViewsClientError('version_conflict', `The ${kind} \`${name}\` was changed since it was opened (stored version ${textVersion(stored)}, yours ${expectedVersion}). Reload it.`)
      if (stored !== undefined && expectedVersion === undefined) throw new ViewsClientError('version_conflict', `The ${kind} \`${name}\` exists; saving over it quotes the version that was opened.`)
      const result = validateText(text, { kind, file: `${name}.yaml` })
      if (!result.ok) throw new ViewsClientError('invalid', `The ${kind} \`${name}\` is not saved: ${(result.diagnostics.find((d) => d.severity === 'error') as ClientDiagnostic).message}`, { diagnostics: result.diagnostics })
      documents.set(key(kind, name), text)
      return { name, version: textVersion(text) }
    },

    async delete(kind, name) {
      if (documents.get(key(kind, name)) === undefined) throw notFound(kind, name)
      documents.delete(key(kind, name))
    },

    async preview(request: PreviewRequest) {
      const kinds: ViewKind[] = ['dashboard', 'report']
      if (request.widget.type === 'text') return { type: 'text' } as RenderedWidget
      const found = request.document && request.widgetId
        ? kinds.map((k) => bundle.results?.[key(k, request.document!)]?.[request.widgetId!]).find((r) => r !== undefined)
        : undefined
      return found ?? error(NO_RECORDED_RESULT, `The fixture source has no recorded result for ${request.widgetId ? `the widget \`${request.widgetId}\`` : 'this widget'}${request.document ? ` of \`${request.document}\`` : ''}.`)
    },
  }
}
