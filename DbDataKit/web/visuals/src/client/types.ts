import type { Catalog } from '../views/catalog'
import type { ViewKind } from '../views/document'
import type { ParameterValues, ViewsDoc, WidgetDoc } from '../views/model'
import type { RenderedWidget, RenderedWidgets } from '../views/rendered'

/**
 * `ViewsClient` (plan 32 DBR22): the console's one path for boards. Everything a page needs of a document and its rows goes through this interface, so the first milestones run on a **fixture source**
 * (`fixtureClient`: files and recorded results, no server) and the real `views` module (DBR11) is a second implementation of the same interface (`moduleClient`, M4).
 */

/** A problem in a document, placed in its text when the text is at hand. `path` is the key path (`layouts[1].tiles[0].x`); `line` and `column` count from 1. */
export interface ClientDiagnostic {
  code: string
  severity: 'error' | 'warning'
  path: string
  message: string
  file?: string
  line?: number
  column?: number
}

export interface DocumentSummary {
  name: string
  kind: ViewKind
  title: string
}

export interface OpenedDocument {
  name: string
  kind: ViewKind
  /** The file's text, which an editor keeps and a save sends back. */
  text: string
  /** The version a save must quote (the file's hash, or the registry's version): a stale save is refused. */
  version: string
  /** The document read from the text, present when there is no error (a document with errors is never drawn). */
  document: ViewsDoc | null
  diagnostics: ClientDiagnostic[]
}

export interface ValidationResult {
  ok: boolean
  /** The parsed document, present when `ok`. */
  document: ViewsDoc | null
  diagnostics: ClientDiagnostic[]
}

export interface ResolveOptions {
  /** Resolve only these widgets (a parameter changed: the ones that use it). Absent: every widget the layout places. */
  only?: string[]
  signal?: AbortSignal
}

export interface PreviewRequest {
  /** One widget as the editor holds it, drawn without saving. */
  widget: WidgetDoc
  /** The kind of the document the widget is in (default `dashboard`): a report's widget is checked and resolved as a report's. */
  kind?: ViewKind
  /** The document the widget belongs to, as the editor holds it: the module takes its parameters (a widget may use them), a fixture ignores it. */
  draft?: ViewsDoc
  /** The id the widget has (or will have) in its document; a fixture finds its recorded result by it. */
  widgetId?: string
  /** The document the widget belongs to, for its parameters and (in a fixture) its recorded results. */
  document?: string
  parameters?: ParameterValues
  signal?: AbortSignal
}

/** A whole unsaved document, resolved on the server as the caller and stored nowhere (plan 32 DBR28): the text as the editor holds it, and the layout and widgets to resolve (the layout the window selects, every widget, when omitted). */
export interface DocumentPreviewRequest {
  kind: ViewKind
  text: string
  layout?: string
  only?: string[]
  parameters?: ParameterValues
  signal?: AbortSignal
}

export interface SaveResult { name: string; version: string }

export interface ViewsClient {
  list(kind?: ViewKind): Promise<DocumentSummary[]>
  /** Reads a document. A name that does not exist rejects with `ViewsClientError` `not_found`. A document with errors is still returned, with its diagnostics and no `document`. */
  open(kind: ViewKind, name: string): Promise<OpenedDocument>
  /** Resolves the widgets a layout places, as the viewer: one entry per widget, an error entry for one that failed. */
  resolve(ref: { kind: ViewKind; name: string }, layout: string, parameters: ParameterValues, options?: ResolveOptions): Promise<RenderedWidgets>
  /** Validates text as the loaders do; nothing is stored. */
  validate(text: string, options?: { kind?: ViewKind; file?: string }): Promise<ValidationResult>
  /** Saves text under a name when `expectedVersion` is still the stored one (`version_conflict` otherwise); a new name needs none. */
  save(kind: ViewKind, name: string, text: string, expectedVersion?: string): Promise<SaveResult>
  /** Removes a document (the editor's Delete). Optional: a source that cannot says so by not having it. */
  delete?(kind: ViewKind, name: string): Promise<void>
  /** One widget's rendered form, for the editor's live preview. */
  preview(request: PreviewRequest): Promise<RenderedWidget>
  /** An unsaved document as the caller would see it, in one call (plan 32 DBR28). Optional: a source with no server (the fixture source) leaves the editor to ask `preview` for each widget. A document the server would refuse to publish rejects with `invalid`, its diagnostics placed in the text. */
  previewDocument?(request: DocumentPreviewRequest): Promise<RenderedWidgets>
  /** The catalog of a semantic model (the Proxy's `GET /api/v1/semantic-models/{name}`, Build's compile): what the editor's field wells offer. Optional: a source without one leaves the wells to typed names. */
  catalog?(model: string): Promise<Catalog>
}

export type ViewsClientErrorCode = 'not_found' | 'version_conflict' | 'invalid' | 'denied' | 'unavailable' | 'bad_response' | 'unsupported'

/** What a client refuses with: a code a page can act on, and the server's own words when the server said them. */
export class ViewsClientError extends Error {
  readonly code: ViewsClientErrorCode
  readonly status?: number
  readonly diagnostics?: ClientDiagnostic[]
  constructor(code: ViewsClientErrorCode, message: string, extra: { status?: number; diagnostics?: ClientDiagnostic[] } = {}) {
    super(message)
    this.name = 'ViewsClientError'
    this.code = code
    this.status = extra.status
    this.diagnostics = extra.diagnostics
  }
}
