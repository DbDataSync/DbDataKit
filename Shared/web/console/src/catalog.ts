/**
 * The report browser's side of the merged report catalog (plan 32 RC05, section 9e): one list over our own dashboards and reports and the external report servers' (SSRS, Power BI Report Server), each item by its key `provider:id`, as the signed-in person.
 * The calls are the `views` module's `/api/v1/catalog...` routes (RC03), which the Gateway relays with the person's token; `catalogClient` speaks them, `fixtureCatalog` is the same interface over two recorded catalogs (no server needed: the sample source of the page).
 * What a person does here: browse folders or search, open an item (ours: a document the shared visuals draw; external: a frame when the instance approves the report server's origin, else a link), fill the one parameters pane (the same for every catalog) and take an export
 * in a format the item offers. Reading and writing the address are pure, so they are tested without a browser.
 */

export interface CatalogItem {
  key: string
  provider: string
  id: string
  path: string
  name: string
  type: 'folder' | 'dashboard' | 'report' | 'page' | 'external-report' | string
  description?: string | null
  owner?: string | null
  modified?: string | null
}

export interface CatalogOption { value: string; label?: string | null }

export interface CatalogParameter {
  name: string
  type: string
  label?: string | null
  many: boolean
  required: boolean
  default?: string | null
  validValues?: CatalogOption[] | null
  valuesFrom?: string | null
  dependsOn?: string[] | null
}

export interface CatalogExport { format: string; available: boolean; reason?: string | null }

export interface CatalogDetail {
  item: CatalogItem
  parameters: CatalogParameter[]
  exports: CatalogExport[]
  permissions: { known: boolean; principals: string[]; note?: string | null }
}

export interface CatalogProblem { provider: string; code: string; message: string }

export interface CatalogListing { items: CatalogItem[]; unavailable: CatalogProblem[]; creatable: Record<string, string[]> }

/** How an opened item is shown: ours as a `document`, an external one as a `frame` (an approved origin only) or a `link`. */
export interface CatalogOpening {
  key: string
  mode: 'document' | 'frame' | 'link'
  title: string
  url?: string | null
  kind?: 'dashboard' | 'report' | null
  name?: string | null
  version?: string | null
  layouts?: string[] | null
  layout?: string | null
  parameters?: Record<string, string | null> | null
}

export interface CatalogQuery { folder?: string | null; search?: string | null; types?: string[] }

export interface CatalogClient {
  browse(query?: CatalogQuery, signal?: AbortSignal): Promise<CatalogListing>
  item(key: string, signal?: AbortSignal): Promise<CatalogDetail>
  /** `format` asks an export: the answer is a link to the file (an external item) or the document to export from (ours). */
  open(key: string, options?: { parameters?: Record<string, string | null>; layout?: string | null; format?: string | null }, signal?: AbortSignal): Promise<CatalogOpening>
  create?(provider: string, kind: string, name: string, folder?: string | null): Promise<CatalogItem>
}

export class CatalogError extends Error {
  readonly code: string
  readonly status?: number
  constructor(code: string, message: string, status?: number) {
    super(message)
    this.name = 'CatalogError'
    this.code = code
    this.status = status
  }
}

type Wire = Record<string, unknown>
const text = (v: unknown): string | null => (typeof v === 'string' ? v : null)

export const itemOf = (w: Wire): CatalogItem => ({
  key: String(w.key), provider: String(w.provider), id: String(w.id), path: String(w.path), name: String(w.name), type: String(w.type),
  description: text(w.description), owner: text(w.owner), modified: text(w.modified),
})

export const parameterOf = (w: Wire): CatalogParameter => ({
  name: String(w.name), type: String(w.type ?? 'String'), label: text(w.label), many: w.many === true, required: w.required === true, default: text(w.default),
  validValues: Array.isArray(w.valid_values) ? (w.valid_values as Wire[]).map((o) => ({ value: String(o.value), label: text(o.label) })) : null,
  valuesFrom: text(w.values_from), dependsOn: Array.isArray(w.depends_on) ? (w.depends_on as unknown[]).map(String) : null,
})

export interface CatalogClientOptions {
  /** The relay of the Gateway (`/relay/<server>`), no trailing slash. */
  baseUrl: string
  fetch?: typeof fetch
}

/** The client over the module's routes. A refusal is a `CatalogError` with the module's code and words. */
export function catalogClient(options: CatalogClientOptions): CatalogClient {
  const base = options.baseUrl.replace(/\/+$/, '')
  const doFetch = options.fetch ?? ((input, init) => fetch(input, init))

  async function call<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
    let response: Response
    try {
      response = await doFetch(`${base}${path}`, { method, headers: { accept: 'application/json', ...(body !== undefined ? { 'content-type': 'application/json' } : {}) }, body: body === undefined ? undefined : JSON.stringify(body), signal })
    } catch (e) {
      if (signal?.aborted) throw e
      throw new CatalogError('unavailable', `The report catalog could not be reached: ${e instanceof Error ? e.message : String(e)}`)
    }
    const raw = await response.text()
    let data: unknown
    if (raw.length > 0) { try { data = JSON.parse(raw) } catch { data = undefined } }
    if (!response.ok) {
      const problem = (data ?? {}) as { code?: string; message?: string }
      throw new CatalogError(problem.code ?? (response.status === 404 ? 'not_found' : 'unavailable'), problem.message ?? `The catalog answered ${response.status}.`, response.status)
    }
    if (data === undefined) throw new CatalogError('bad_response', `The catalog answered ${response.status} with a body that is not JSON.`, response.status)
    return data as T
  }

  return {
    async browse(query = {}, signal) {
      const q = new URLSearchParams()
      if (query.folder) q.set('folder', query.folder)
      if (query.search) q.set('search', query.search)
      if (query.types?.length) q.set('types', query.types.join(','))
      const a = await call<{ items: Wire[]; unavailable?: CatalogProblem[]; creatable?: Record<string, string[]> }>('GET', `/api/v1/catalog${q.size ? `?${q}` : ''}`, undefined, signal)
      return { items: a.items.map(itemOf), unavailable: a.unavailable ?? [], creatable: a.creatable ?? {} }
    },
    async item(key, signal) {
      const a = await call<{ item: Wire; parameters: Wire[]; exports: Wire[]; permissions: { known: boolean; principals: string[]; note?: string | null } }>('GET', `/api/v1/catalog/item?key=${encodeURIComponent(key)}`, undefined, signal)
      return { item: itemOf(a.item), parameters: a.parameters.map(parameterOf), exports: a.exports.map((e) => ({ format: String(e.format), available: e.available !== false, reason: text(e.reason) })), permissions: a.permissions }
    },
    async open(key, o = {}, signal) {
      const a = await call<Wire>('POST', '/api/v1/catalog/open', { key, ...(o.parameters ? { parameters: o.parameters } : {}), ...(o.layout ? { layout: o.layout } : {}), ...(o.format ? { format: o.format } : {}) }, signal)
      return {
        key: String(a.key), mode: a.mode as CatalogOpening['mode'], title: String(a.title), url: text(a.url), kind: (text(a.kind) as CatalogOpening['kind']) ?? null, name: text(a.name), version: text(a.version),
        layouts: Array.isArray(a.layouts) ? (a.layouts as unknown[]).map(String) : null, layout: text(a.layout), parameters: (a.parameters as Record<string, string | null> | null) ?? null,
      }
    },
    async create(provider, kind, name, folder) {
      const a = await call<{ item: Wire }>('POST', '/api/v1/catalog/create', { provider, kind, name, ...(folder ? { folder } : {}) })
      return itemOf(a.item)
    },
  }
}

// ---- the address ----

export interface CatalogState {
  folder: string
  search: string
  /** The open item's key, or null. */
  item: string | null
  /** The values typed in the parameters pane, by parameter name (a list is the key repeated). */
  values: Record<string, string[]>
}

const VALUE_PREFIX = 'p.'

export function readCatalogState(search: URLSearchParams): CatalogState {
  const values: Record<string, string[]> = {}
  for (const key of new Set([...search.keys()].filter((k) => k.startsWith(VALUE_PREFIX) && k.length > VALUE_PREFIX.length))) values[key.slice(VALUE_PREFIX.length)] = search.getAll(key)
  return { folder: search.get('folder') ?? '/', search: search.get('q') ?? '', item: search.get('item'), values }
}

/** The address of a state, keeping the other keys (`source`): a root folder and an empty search are not written. */
export function writeCatalogState(base: URLSearchParams, state: Partial<CatalogState>): URLSearchParams {
  const next = new URLSearchParams()
  for (const [k, v] of base) if (!['folder', 'q', 'item'].includes(k) && !k.startsWith(VALUE_PREFIX)) next.append(k, v)
  const merged = { ...readCatalogState(base), ...state }
  if (merged.folder && merged.folder !== '/') next.set('folder', merged.folder)
  if (merged.search) next.set('q', merged.search)
  if (merged.item) {
    next.set('item', merged.item)
    for (const [name, list] of Object.entries(merged.values)) for (const v of list) next.append(VALUE_PREFIX + name, v)
  }
  return next
}

/** The folder above a path (`/Sales/Quarterly` is `/Sales`, `/Sales` is `/`). */
export const parentFolder = (path: string): string => (path.lastIndexOf('/') > 0 ? path.slice(0, path.lastIndexOf('/')) : '/')

/** The folders from the root to a path, for the breadcrumb: `/A/B` is `[{name:'A',path:'/A'},{name:'B',path:'/A/B'}]`. */
export function crumbs(folder: string): Array<{ name: string; path: string }> {
  const parts = folder.split('/').filter(Boolean)
  return parts.map((name, i) => ({ name, path: '/' + parts.slice(0, i + 1).join('/') }))
}

/** What the pane starts with: what the address holds, else the parameter's default, else nothing. A `many` parameter's default is a comma list. */
export function initialValues(parameters: CatalogParameter[], state: Record<string, string[]>): Record<string, string[]> {
  const out: Record<string, string[]> = {}
  for (const p of parameters) out[p.name] = state[p.name] ?? (p.default ? (p.many ? p.default.split(',') : [p.default]) : [])
  return out
}

/** The parameters of an open call: one value as it is, several as the provider's list text; a parameter with nothing is left out (the report server's default applies). */
export function openValues(parameters: CatalogParameter[], values: Record<string, string[]>): Record<string, string | null> {
  const out: Record<string, string | null> = {}
  for (const p of parameters) {
    const v = (values[p.name] ?? []).filter((x) => x !== '')
    if (v.length > 0) out[p.name] = p.many ? v.join(',') : v[0]!
  }
  return out
}

/** The parameters that are required and have no value: named in the pane before anything is sent. */
export const missingRequired = (parameters: CatalogParameter[], values: Record<string, string[]>): string[] =>
  parameters.filter((p) => p.required && (values[p.name] ?? []).every((v) => v === '')).map((p) => p.label || p.name)

/** The values of a parameter that depends on others change when they do: a dependent's chosen values are dropped when one it depends on changes. */
export function afterChange(parameters: CatalogParameter[], values: Record<string, string[]>, changed: string, next: string[]): Record<string, string[]> {
  const out = { ...values, [changed]: next }
  for (const p of parameters) if (p.dependsOn?.includes(changed)) out[p.name] = []
  return out
}

/** Whether an address may be framed in the page: https with no credentials (the server only offers `frame` for an origin the instance approved; this is the second lock). */
export function framable(url: string | null | undefined): url is string {
  if (!url) return false
  try {
    const u = new URL(url)
    return u.protocol === 'https:' && u.username === '' && u.password === ''
  } catch { return false }
}

/** The link to follow for a link (off the domain): http or https only. */
export function followable(url: string | null | undefined): url is string {
  if (!url) return false
  try {
    const u = new URL(url)
    return (u.protocol === 'https:' || u.protocol === 'http:') && u.username === '' && u.password === ''
  } catch { return false }
}

/** The address of one of our own opened documents in the console's Views area. */
export function documentAddress(opening: CatalogOpening, source: string | null, values: Record<string, string[]>): string | null {
  if (opening.mode !== 'document' || !opening.kind || !opening.name) return null
  const q = new URLSearchParams()
  if (source) q.set('source', source)
  if (opening.layout) q.set('layout', opening.layout)
  for (const [name, list] of Object.entries(values)) for (const v of list) if (v !== '') q.append(VALUE_PREFIX + name, v)
  return `/views/${opening.kind === 'report' ? 'reports' : 'dashboards'}/${encodeURIComponent(opening.name)}${q.size ? `?${q}` : ''}`
}

export const TYPE_LABEL: Record<string, string> = { folder: 'folder', dashboard: 'dashboard', report: 'report', 'external-report': 'report server' }

// ---- the sample catalogs (no server) ----

interface FixtureItem extends CatalogItem { parameters?: CatalogParameter[]; exports?: CatalogExport[]; principals?: string[]; open?: Partial<CatalogOpening> }

const SAMPLE: FixtureItem[] = [
  { key: 'views:folder/dashboards', provider: 'views', id: 'folder/dashboards', path: '/Dashboards', name: 'Dashboards', type: 'folder' },
  { key: 'views:dashboard/sales', provider: 'views', id: 'dashboard/sales', path: '/Dashboards/sales', name: 'Sales', type: 'dashboard', description: 'Revenue by region',
    parameters: [{ name: 'region', type: 'string', label: 'Region', many: true, required: false, default: null }], exports: [{ format: 'pdf', available: true }, { format: 'xlsx', available: true }],
    open: { mode: 'document', kind: 'dashboard', name: 'sales', layouts: ['desk', 'kpi'], layout: null } },
  { key: 'ssrs:f-sales', provider: 'ssrs', id: 'f-sales', path: '/Sales', name: 'Sales', type: 'folder' },
  { key: 'ssrs:r-quarterly', provider: 'ssrs', id: 'r-quarterly', path: '/Sales/Quarterly revenue', name: 'Quarterly revenue', type: 'external-report', description: 'Revenue by quarter and region',
    parameters: [
      { name: 'Country', type: 'String', label: 'Country', many: false, required: true, validValues: [{ value: 'US', label: 'United States' }, { value: 'DE', label: 'Germany' }] },
      { name: 'Region', type: 'String', label: 'Region', many: true, required: false, dependsOn: ['Country'], validValues: [{ value: 'East' }, { value: 'West', label: 'Western' }] },
    ],
    exports: [{ format: 'PDF', available: true }, { format: 'EXCELOPENXML', available: true }, { format: 'CSV', available: true }],
    open: { mode: 'frame', url: 'https://reports.example.com/ReportServer?/Sales/Quarterly%20revenue&rs:Command=Render&rc:Toolbar=false' } },
  { key: 'ssrs:r-board', provider: 'ssrs', id: 'r-board', path: '/Sales/Board pack', name: 'Board pack', type: 'external-report', description: 'Opens in the report portal',
    parameters: [], exports: [], open: { mode: 'link', url: 'https://portal.example.org/powerbi/Sales/Board%20pack' } },
]

const parentOf = (path: string) => (path.lastIndexOf('/') > 0 ? path.slice(0, path.lastIndexOf('/')) : '/')

/** The sample source of the page: two catalogs (ours, and a report server's) with a folder each, a report with parameters that depend on each other and several export formats, a framed item and a link. The recorded `frame` address is not asked: it only shows what the page does with one. */
export function fixtureCatalog(): CatalogClient {
  return {
    async browse(query = {}) {
      const folder = !query.folder || query.folder === '/' ? '/' : '/' + query.folder.replace(/^\/+|\/+$/g, '')
      const found = query.search ? SAMPLE.filter((i) => i.type !== 'folder' && [i.name, i.path, i.description ?? ''].some((t) => t.toLowerCase().includes(query.search!.toLowerCase()))) : SAMPLE.filter((i) => parentOf(i.path) === folder)
      return { items: found.map(({ parameters: _p, exports: _e, principals: _x, open: _o, ...item }) => item), unavailable: [], creatable: {} }
    },
    async item(key) {
      const f = SAMPLE.find((i) => i.key === key)
      if (!f) throw new CatalogError('not_found', `There is no item '${key}'.`, 404)
      const { parameters, exports, principals, open: _o, ...item } = f
      return { item, parameters: parameters ?? [], exports: exports ?? [], permissions: { known: f.provider !== 'views', principals: principals ?? [], note: null } }
    },
    async open(key, o = {}) {
      const f = SAMPLE.find((i) => i.key === key)
      if (!f || f.type === 'folder') throw new CatalogError('not_found', `There is no item '${key}' to open.`, 404)
      if (o.format) {
        if (!(f.exports ?? []).some((e) => e.available && e.format.toLowerCase() === o.format!.toLowerCase())) throw new CatalogError('invalid', `The item '${f.path}' is not offered in the format '${o.format}'.`, 400)
        return { key, mode: f.open?.mode === 'document' ? 'document' : 'link', title: f.name, url: f.open?.mode === 'document' ? null : `https://reports.example.com/ReportServer?${encodeURI(f.path)}&rs:Command=Render&rs:Format=${encodeURIComponent(o.format)}`, kind: f.open?.kind ?? null, name: f.open?.name ?? null, parameters: o.parameters ?? null }
      }
      return { key, mode: 'link', title: f.name, ...f.open, parameters: o.parameters ?? null, layout: o.layout ?? f.open?.layout ?? null } as CatalogOpening
    },
  }
}
