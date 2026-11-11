import {
  ViewsClientError, draftFromOpened, editAll, errorsOf, moduleClient, newDocumentText, newDraft, validateText,
  type ClientDiagnostic, type DocumentSummary, type EditCommand, type OpenedDocument, type ParameterDefinition, type SaveResult, type ViewKind, type ViewsClient, type ViewsDoc, type WidgetDoc,
} from '@dbdatakit/visuals'
import { nextTile } from '@dbdatakit/views-editor'

/**
 * A person's own dashboards and reports (plan 32 DBR12): the console's side of the `views` module's `/api/v1/my/...` routes, which the Gateway relays with the signed-in person's token (so the person owns what they save, opens what was
 * shared with them, and every widget is read with their own access). `personalClient` is a `ViewsClient`, so the Views page's list, board and editor work on it unchanged (it is a *source* beside the fixtures and a Gateway's published
 * documents); what a source of published documents has no use for (shares, history, restore, promote, the detailed list) are extra calls of the same object.
 *
 * **Names.** A document of another person that was shared with you is `name@owner` (`sales@alice`): the `@` is not part of a document's name (lower case letters, digits, `_`, `-`), so it never collides with your own, and the client
 * turns it into `?owner=alice`. A save, delete, share, history, restore or promote of such a name is refused here in words (the module refuses it too, by identity: it only ever acts on the caller's own).
 */

/** The prefix of a personal source's id: `mine:<server>`. */
export const PERSONAL = 'mine:'
export const personalId = (server: string) => PERSONAL + server
export const isPersonal = (source: string) => source.startsWith(PERSONAL)
export const serverOf = (source: string) => source.slice(PERSONAL.length)

/** `sales@alice` is the document `sales` of `alice`; a plain name is yours. */
export function splitName(name: string): { name: string; owner: string | null } {
  const at = name.indexOf('@')
  return at < 0 ? { name, owner: null } : { name: name.slice(0, at), owner: name.slice(at + 1) }
}
export const sharedName = (name: string, owner: string) => `${name}@${owner}`
export const isShared = (name: string) => name.includes('@')

/** What a person may name their own document: the module's rule, so a bad name is refused before anything is sent. */
export const MINE_NAME = /^[a-z][a-z0-9_-]{0,63}$/

export interface MineItem {
  kind: ViewKind
  /** The owner's id (what `name@owner` and `?owner=` carry). */
  owner: string
  /** The owner's name as of their last save, for people to read. */
  ownerName: string
  name: string
  title: string
  version: string
  updatedAt: string
  own: boolean
  /** Who an own document is shared with (`user:alice`, `group:analyst`, `*`); empty for a document somebody shared with you. */
  sharedWith: string[]
}

export interface VersionInfo { version: string; savedBy: string; savedAt: string; current: boolean }
export interface PromotedFile { path: string; text: string }

export interface PersonalClient extends ViewsClient {
  /** Deletes your document with its history and shares. */
  delete(kind: ViewKind, name: string): Promise<void>
  /** The documents with their owners, versions and shares: yours first, then what others shared with you. */
  mine(kind?: ViewKind): Promise<MineItem[]>
  shares(kind: ViewKind, name: string): Promise<string[]>
  /** Replaces the share list (a grantee not named is unshared). */
  setShares(kind: ViewKind, name: string, grantees: string[]): Promise<string[]>
  /** Every version, newest first. */
  history(kind: ViewKind, name: string): Promise<VersionInfo[]>
  historyText(kind: ViewKind, name: string, version: string): Promise<string>
  /** Brings an earlier version back as a new one; `expectedVersion` is the version you see as current. */
  restore(kind: ViewKind, name: string, version: string, expectedVersion: string): Promise<SaveResult & { restored: string }>
  /** The document as a file (`dashboards/<name>.yaml`) for an app's source or a Build project. */
  promote(kind: ViewKind, name: string): Promise<PromotedFile>
}

const collection = (kind: ViewKind) => (kind === 'dashboard' ? 'dashboards' : 'reports')
const segment = (name: string) => encodeURIComponent(name)

export interface PersonalClientOptions {
  /** The relay of the Gateway (`/relay/<server>`), no trailing slash. */
  baseUrl: string
  fetch?: typeof fetch
}

const own = (what: string, name: string): ViewsClientError =>
  new ViewsClientError('denied', `The ${what} \`${name}\` is another person's, shared with you: only its owner changes it. Make a copy under a name of your own (Make my copy on the list) and change that.`)

export function personalClient(options: PersonalClientOptions): PersonalClient {
  const base = options.baseUrl.replace(/\/+$/, '')
  const doFetch = options.fetch ?? ((input, init) => fetch(input, init))
  // the preview, the live check and the catalog are the module's own and are not about whose the document is
  const inner = moduleClient({ baseUrl: base, fetch: options.fetch, serverCheck: true })

  async function call<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
    let response: Response
    try {
      response = await doFetch(`${base}${path}`, {
        method,
        headers: { accept: 'application/json', ...(body !== undefined ? { 'content-type': 'application/json' } : {}) },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal,
      })
    } catch (e) {
      if (signal?.aborted) throw e
      throw new ViewsClientError('unavailable', `Your dashboards could not be reached: ${e instanceof Error ? e.message : String(e)}`)
    }
    const text = await response.text()
    let data: unknown
    if (text.length > 0) { try { data = JSON.parse(text) } catch { data = undefined } }
    if (!response.ok) {
      const problem = (data ?? {}) as { code?: string; message?: string; diagnostics?: ClientDiagnostic[] }
      const code = response.status === 404 ? 'not_found' : response.status === 409 ? 'version_conflict' : response.status === 400 || response.status === 413 ? 'invalid' : response.status === 401 || response.status === 403 ? 'denied' : 'unavailable'
      throw new ViewsClientError(code, problem.message ?? `The views module answered ${response.status}.`, { status: response.status, diagnostics: problem.diagnostics })
    }
    if (data === undefined) throw new ViewsClientError('bad_response', `The views module answered ${response.status} with a body that is not JSON.`, { status: response.status })
    return data as T
  }

  const ownerQuery = (owner: string | null) => (owner ? `?owner=${encodeURIComponent(owner)}` : '')

  async function mine(kind?: ViewKind): Promise<MineItem[]> {
    const answer = await call<{ items: Array<{ kind: ViewKind; owner: string; owner_name?: string; name: string; title: string; version: string; updated_at: string; own: boolean; shared_with: string[] }> }>('GET', `/api/v1/my/views${kind ? `?kind=${kind}` : ''}`)
    return answer.items.map((i) => ({ kind: i.kind, owner: i.owner, ownerName: i.owner_name ?? i.owner, name: i.name, title: i.title, version: i.version, updatedAt: i.updated_at, own: i.own, sharedWith: i.shared_with ?? [] }))
  }

  /** The module's own calls on the caller's own document; a name that is shared with you has none of them. */
  const mineOnly = (kind: ViewKind, name: string): string => {
    const { owner } = splitName(name)
    if (owner) throw own(kind, name)
    return `/api/v1/my/${collection(kind)}/${segment(name)}`
  }

  return {
    ...inner,

    async list(kind) {
      const items = await mine(kind)
      return items.map((i): DocumentSummary => ({ kind: i.kind, name: i.own ? i.name : sharedName(i.name, i.owner), title: i.own ? i.title : `${i.title} (shared by ${i.ownerName})` }))
        .sort((a, b) => a.name.localeCompare(b.name))
    },

    async open(kind, name): Promise<OpenedDocument> {
      const { name: bare, owner } = splitName(name)
      const body = await call<{ version: string; text: string }>('GET', `/api/v1/my/${collection(kind)}/${segment(bare)}${ownerQuery(owner)}`)
      if (typeof body.text !== 'string' || typeof body.version !== 'string') throw new ViewsClientError('bad_response', 'The views module gave a document with no `text` or `version`.')
      const result = validateText(body.text, { kind, file: `${bare}.yaml` })
      return { name, kind, text: body.text, version: body.version, document: result.document, diagnostics: result.diagnostics }
    },

    resolve(ref, layout, parameters, resolveOptions = {}) {
      const { name, owner } = splitName(ref.name)
      return call('POST', `/api/v1/my/${collection(ref.kind)}/${segment(name)}/render${ownerQuery(owner)}`, { parameters, layout, ...(resolveOptions.only ? { only: resolveOptions.only } : {}) }, resolveOptions.signal)
    },

    async save(kind, name, text, expectedVersion) {
      if (isShared(name)) throw own(kind, name)
      if (!MINE_NAME.test(name)) throw new ViewsClientError('invalid', `A name for your ${kind} is lower case letters, digits, \`_\` and \`-\`, starting with a letter (at most 64 characters); \`${name}\` is not.`)
      const saved = await call<{ name: string; version: string }>('PUT', `/api/v1/my/${collection(kind)}/${segment(name)}`, { text, ...(expectedVersion !== undefined ? { expected_version: expectedVersion } : {}) })
      return { name: saved.name, version: saved.version }
    },

    async delete(kind, name) {
      await call('DELETE', mineOnly(kind, name))
    },

    mine,

    async shares(kind, name) {
      return (await call<{ grantees: string[] }>('GET', `${mineOnly(kind, name)}/shares`)).grantees
    },

    async setShares(kind, name, grantees) {
      return (await call<{ grantees: string[] }>('PUT', `${mineOnly(kind, name)}/shares`, { grantees })).grantees
    },

    async history(kind, name) {
      const answer = await call<{ items: Array<{ version: string; saved_by: string; saved_at: string; current: boolean }> }>('GET', `${mineOnly(kind, name)}/history`)
      return answer.items.map((i) => ({ version: i.version, savedBy: i.saved_by, savedAt: i.saved_at, current: i.current }))
    },

    async historyText(kind, name, version) {
      return (await call<{ text: string }>('GET', `${mineOnly(kind, name)}/history?version=${encodeURIComponent(version)}`)).text
    },

    async restore(kind, name, version, expectedVersion) {
      const answer = await call<{ name: string; version: string; restored: string }>('POST', `${mineOnly(kind, name)}/restore`, { version: Number(version), expected_version: Number(expectedVersion) })
      return { name: answer.name, version: answer.version, restored: answer.restored }
    },

    async promote(kind, name) {
      return call<PromotedFile>('GET', `${mineOnly(kind, name)}/promote`)
    },
  }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------------------------------
// Pin: a widget (or a query) into a dashboard of your own
// ---------------------------------------------------------------------------------------------------------------------------------------------------------------------

/** A name for the pinned widget that the target document does not have yet: `base`, `base2`, `base3`... The base is the source widget's id, cleaned to the id rule. */
export function freeWidgetId(taken: Iterable<string>, base: string): string {
  const used = new Set(taken)
  const clean = base.replace(/[^A-Za-z0-9_-]/g, '_').replace(/^[^A-Za-z]+/, '') || 'pinned'
  let id = clean
  for (let n = 2; used.has(id); n++) id = `${clean}${n}`
  return id
}

/** The parameters of the document a widget uses: a control's own parameter, and each `{param: name}` in its query. They are copied with it, so the pinned widget works on its own. */
export function parametersUsedBy(widget: WidgetDoc): string[] {
  const names = new Set<string>()
  const control = (widget as { parameter?: unknown }).parameter
  if (typeof control === 'string') names.add(control)
  const walk = (node: unknown) => {
    if (Array.isArray(node)) { node.forEach(walk); return }
    if (typeof node !== 'object' || node === null) return
    for (const [key, value] of Object.entries(node)) {
      if (key === 'param' && typeof value === 'string') names.add(value)
      else walk(value)
    }
  }
  walk((widget as { query?: unknown }).query)
  return [...names]
}

export interface PinArgs {
  /** The target dashboard's name (yours); one that does not exist is made, with `title`. */
  target: string
  title?: string
  widget: WidgetDoc
  /** The id the widget has where it comes from. */
  id: string
  /** The parameters of the document it comes from, to copy the ones the widget uses. */
  parameters?: ParameterDefinition[]
}

export interface PinResult { name: string; version: string; widgetId: string; created: boolean }

/**
 * Pins a widget into a dashboard of the person's own: the target is opened (or made), the widget is added with a free id and **placed in the default layout**, the parameters it uses are copied, and the dashboard is saved with the version
 * it was opened at (a stale save is a conflict that says so). A widget that cannot be pinned (a picture holds a hash of bytes the personal store does not keep) and a dashboard whose layouts are all flow layouts are refused in words,
 * before anything is saved; the module checks what is saved as a publish would, so a widget that needs a model nobody serves is refused there.
 */
export async function pinWidget(client: ViewsClient, args: PinArgs): Promise<PinResult> {
  if (args.widget.type === 'image') throw new ViewsClientError('invalid', 'A picture cannot be pinned: it is held by the hash of its bytes, and your own dashboards keep no files. Pin a metric, chart, matrix, table or note.')
  let opened: OpenedDocument | null = null
  try { opened = await client.open('dashboard', args.target) } catch (e) { if (!(e instanceof ViewsClientError) || e.code !== 'not_found') throw e }
  const draft0 = opened ? draftFromOpened(opened) : newDraft('dashboard', args.target, newDocumentText('dashboard', args.title?.trim() || args.target))
  const doc = draft0.value as ViewsDoc | undefined
  if (!doc) throw new ViewsClientError('invalid', `The dashboard \`${args.target}\` is not a document yet, so a widget cannot be added to it. Fix it in the editor first.`)
  const layout = doc.layouts.find((l) => l.default) ?? doc.layouts[0]
  const placeable = layout && layout.type !== 'flow' ? layout : doc.layouts.find((l) => l.type !== 'flow')
  if (!placeable) throw new ViewsClientError('invalid', `The dashboard \`${args.target}\` has no layout that places tiles (only flow layouts), so a widget cannot be pinned to it. Add a layout in the editor first.`)
  const id = freeWidgetId(Object.keys(doc.widgets ?? {}), args.id)
  const known = new Set((doc.parameters ?? []).map((p) => p.name))
  const commands: EditCommand[] = []
  for (const name of parametersUsedBy(args.widget)) {
    const definition = args.parameters?.find((p) => p.name === name)
    if (!known.has(name) && definition) commands.push({ type: 'addParameter', parameter: definition })
  }
  commands.push({ type: 'addWidget', id, widget: args.widget })
  commands.push({ type: 'placeTile', layout: placeable.name, tile: nextTile(placeable, args.widget, id) })
  const edited = editAll(draft0, commands)
  if (edited.error) throw new ViewsClientError('invalid', edited.error.message)
  const problems = errorsOf(edited.draft)
  if (problems.length > 0) throw new ViewsClientError('invalid', `The pinned widget makes the dashboard invalid: ${problems[0]!.message}`, { diagnostics: problems })
  const saved = await client.save('dashboard', args.target, edited.draft.text, opened?.version)
  return { name: saved.name, version: saved.version, widgetId: id, created: opened === null }
}

export type QueryShape = 'table' | 'chart' | 'metric'

const outputName = (entry: unknown): string | null => {
  if (typeof entry === 'string') return entry
  if (typeof entry === 'object' && entry !== null) {
    const o = entry as { name?: unknown; as?: unknown }
    if (typeof o.name === 'string') return o.name
    if (typeof o.as === 'string') return o.as
  }
  return null
}

/**
 * A widget from a semantic request (a query result to keep): the request is written into the widget **as it stands** (`mode: explicit`), and the widget shows its outputs: a table of every attribute and measure, a chart
 * of the first attribute against the first measure, or a metric of the first measure. Refused in words for a request that is not an object, has no measure, names no model, or has an output that is not a name (give
 * ad hoc measures and transforms a `name`, so a widget can say which output it shows).
 */
export function widgetFromRequest(input: { model: string; requestText: string; shape: QueryShape; title: string }): WidgetDoc {
  if (input.model.trim() === '') throw new ViewsClientError('invalid', 'Name the semantic model the request asks of.')
  let request: unknown
  try { request = JSON.parse(input.requestText) } catch (e) { throw new ViewsClientError('invalid', `The request is not JSON: ${e instanceof Error ? e.message : String(e)}`) }
  if (typeof request !== 'object' || request === null || Array.isArray(request)) throw new ViewsClientError('invalid', 'A semantic request is a JSON object.')
  const r = request as { measures?: unknown; attributes?: unknown }
  const measures = Array.isArray(r.measures) ? r.measures.map(outputName) : []
  const attributes = Array.isArray(r.attributes) ? r.attributes.map(outputName) : []
  if (measures.length === 0) throw new ViewsClientError('invalid', 'The request has no `measures`, so there is nothing to show.')
  if ([...measures, ...attributes].some((n) => n === null)) throw new ViewsClientError('invalid', 'Every measure and attribute of the request must be a name (give an ad hoc measure a `name`), so the widget can say which output it shows.')
  const ms = measures as string[]
  const as = attributes as string[]
  const query = { mode: 'explicit', semantic: { model: input.model.trim(), request } }
  const title = input.title.trim() || 'Pinned query'
  switch (input.shape) {
    case 'metric': return { type: 'metric', label: title, encodings: { value: ms[0]! }, query } as WidgetDoc
    case 'chart':
      if (as.length === 0) throw new ViewsClientError('invalid', 'A chart needs an attribute to put on its axis: the request has no `attributes`. Pin it as a table or a metric.')
      return { type: 'chart', title, mark: 'bar', encodings: { x: as[0]!, y: ms[0]! }, query } as WidgetDoc
    case 'table': return { type: 'table', title, encodings: { columns: [...as, ...ms].map((field) => ({ field })) }, query } as WidgetDoc
  }
}
