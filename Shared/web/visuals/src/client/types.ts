import type { Catalog } from '../views/catalog'
import type { ActionForecast, ActionOutcome, ActionRequest, BatchOutcome, BatchRequest } from '../links/actions'
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

/** A viewer's sort of one table, asked of the server (plan 32 DBR45): the column (a catalog name the table shows) and the direction. Never stored. */
export interface ServerSort { by: string; dir: 'asc' | 'desc' }

/** A file the server made of a document (the exporter's PDF, DBR15; DBR27's print preview). */
export interface ExportedFile {
  fileName: string
  contentType: string
  bytes: Uint8Array
  /** The pages of a PDF, as the exporter counted them (`X-DbData-Export-Pages`), or null when the answer did not say. */
  pages: number | null
  /** The browser that drew it, when the answer said. */
  browser: string | null
}

/** What a download is (`format=` of the export route): the PDF of a layout, the xlsx of its tables, matrices and charts, or one widget's rows (`csv`, `xlsx`) or a chart's picture (`svg`), which name the widget. */
export type ExportFormat = 'pdf' | 'csv' | 'xlsx' | 'svg'

export interface ExportOptions {
  /** The layout to draw; absent: the one the print medium selects. */
  layout?: string
  /** The widget a `csv` or `svg` (or a one-sheet `xlsx`) is of. */
  widget?: string
  parameters?: ParameterValues
  signal?: AbortSignal
}

export interface ResolveOptions {
  /** Resolve only these widgets (a parameter changed: the ones that use it). Absent: every widget the layout places. */
  only?: string[]
  /** The viewer's sort of tables, by widget id (DBR45): the server runs those widgets with this `order` instead of the document's. A source that cannot (`sortsOnServer` is not true) ignores it. */
  order?: Record<string, ServerSort>
  /** The rows a viewer expanded in a matrix with a `show: drill` group, by widget id (plan 32 DBR55): each the typed member values of the row's own fields. The server reads the details of each as the viewer; a source that cannot (`expandsOnServer` is not true) ignores it. */
  expand?: Record<string, Array<Array<string | number | boolean | null>>>
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

/** One stored version of a published document: who published it, when, and why (never its text). */
export interface DocumentVersion {
  version: number
  publishedBy: string
  publishedAt: string
  note: string | null
  /** The version the group serves now. */
  active: boolean
  /** The model versions it was published against (`semantic-model:name@version`). */
  dependsOn: string[]
}

/** The versions of a published document, newest first, and which one is served (null once it is retired). */
export interface DocumentVersions {
  kind: ViewKind
  name: string
  active: number | null
  previous: number | null
  versions: DocumentVersion[]
}

/** What a switch, a rollback or a retire did: the version now served (null when retired). */
export interface LifecycleResult { version: number | null; moved: string[] }

/** What a prune answered (plan 30 PB05): the versions removed (or, in a dry run, that would be), the candidates kept with the reason, and the files that go with them. */
export interface PruneResult { dryRun: boolean; remove: number[]; retained: { version: number; reason: string }[]; files: string[] }

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
  /** The stored versions of a published document (who, when, why, which is served). Optional: a source with no history (the fixtures) does not have it. */
  versions?(kind: ViewKind, name: string): Promise<DocumentVersions>
  /** Serves a stored version instead (with the model versions it was published against); `expectedVersion` is the version served when the caller looked (`version_conflict` otherwise). */
  switchTo?(kind: ViewKind, name: string, version: number, expectedVersion?: number | null): Promise<LifecycleResult>
  /** Returns to the version before the one served. */
  rollback?(kind: ViewKind, name: string, expectedVersion?: number | null): Promise<LifecycleResult>
  /** Removes stored versions an administrator names; `dryRun` says what would go and removes nothing. Never the served, staged or previous version, nor one another version pins. Optional like the other lifecycle calls. */
  prune?(kind: ViewKind, name: string, request: { versions: number[]; dryRun: boolean }): Promise<PruneResult>
  /** Stops serving the document (its versions stay and a publish brings it back). */
  retire?(kind: ViewKind, name: string, expectedVersion?: number | null): Promise<void>
  /** True when `resolve` honours `order` (the module does; the fixtures hold recorded results and do not): a table the server cut is then sorted by a new run, not over the rows loaded. */
  sortsOnServer?: boolean
  /** True when `resolve` honours `expand` (the module does; the fixtures hold recorded results and do not): a matrix row that can expand is then expanded by a new run of that widget. */
  expandsOnServer?: boolean
  /** The PDF the exporter draws of the **published** document, as the caller (`GET .../export?format=pdf`; through the console's relay as JSON with the file in base64). Optional: the fixtures have no exporter. A host without a browser refuses with `unavailable` and the module's words, which name the `views.export` setting. */
  exportPdf?(ref: { kind: ViewKind; name: string }, options?: ExportOptions): Promise<ExportedFile>
  /** A download of the **published** document in any format of the export route (DBR17: `pdf`, `xlsx`, a widget's `csv` or `xlsx`, a chart's `svg`); `exportPdf` is `exportFile` with `format: 'pdf'`. Optional: the fixtures have no server. */
  exportFile?(ref: { kind: ViewKind; name: string }, format: ExportFormat, options?: ExportOptions): Promise<ExportedFile>
  /** One widget's rendered form, for the editor's live preview. */
  preview(request: PreviewRequest): Promise<RenderedWidget>
  /** An unsaved document as the caller would see it, in one call (plan 32 DBR28). Optional: a source with no server (the fixture source) leaves the editor to ask `preview` for each widget. A document the server would refuse to publish rejects with `invalid`, its diagnostics placed in the text. */
  previewDocument?(request: DocumentPreviewRequest): Promise<RenderedWidgets>
  /**
   * Runs an action link of the **published** document as the viewer (plan 32 DBR53: `POST .../actions/{link}`): the module derives the change or the task from the document, so the request names the link and carries the clicked row,
   * the parameters and what the person typed. The answer is the outcome, never a rejection for a refusal (a conflict, the Store's broken rules and a denial are outcomes the popup shows). Optional: the fixtures run none unless a bundle gives them.
   */
  runAction?(ref: { kind: ViewKind; name: string }, request: ActionRequest): Promise<ActionOutcome>
  /** What will happen for this viewer if the action is confirmed (`POST .../actions/{link}/forecast`): applies at once, waits for approval, or a task runs; null when the source cannot say. */
  forecastAction?(ref: { kind: ViewKind; name: string }, link: string, signal?: AbortSignal): Promise<ActionForecast | null>
  /** Submits the staged changes of the **published** document as one batch as the viewer (plan 32 DBR53b: `POST .../batches`): one transaction in the Store, or one change request of all of them. Optional like `runAction`. */
  runBatch?(ref: { kind: ViewKind; name: string }, request: BatchRequest): Promise<BatchOutcome>
  /** What will happen to a set for this viewer (`POST .../batches/forecast` with the same lines, which name the model): applies together or waits for approval as one request. */
  forecastBatch?(ref: { kind: ViewKind; name: string }, request: BatchRequest, signal?: AbortSignal): Promise<ActionForecast | null>
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
