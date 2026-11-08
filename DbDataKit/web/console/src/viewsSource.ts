import { catalogDiagnostics, fixtureClient, moduleClient, retailCatalog, storedDocuments, type FixtureBundle, type RenderedWidgets, type ViewKind, type ViewsClient } from '@dbdatakit/visuals'
import { consoleApi, type InstanceSummary } from './api'
import { isPersonal, personalClient, personalId, serverOf, type PersonalClient } from './userViews'

/**
 * The console's sources of dashboards and reports (plan 32 DBR23 and DBR11b). **Fixtures** (plan 32 DBR23, M1): the retail sample's files and recorded results, no server. **A Gateway's published documents**: the Gateway that runs the `views` module
 * (DBR11) answers the module's own routes on its management API, which the console relays with the signed-in person's token (`/relay/<server>/api/v1/dashboards...`), so every widget is read as that person; `moduleClient` is the same
 * `ViewsClient` over them, with `serverCheck` on (DBR28b: the Gateway relays `POST /api/v1/views/preview`, so the editor's text is also checked by the module as a publish would check it). A Gateway without the module (or one that does not answer) is not a source, and with none the page shows the fixtures. What follows is the fixture source. The documents are the retail template's sample bundle and the recorded results of each
 * widget (`DbDataKit/conformance/views/widget-results`), read into the build as text and JSON, so the page asks no server and no other host for anything. The module client (`moduleClient`, DBR11) is a second
 * implementation of the same `ViewsClient` and replaces this one in `viewsClient()` when it is wired.
 */
const documents = import.meta.glob('../../../../DbDataBuild/templates/retail/{dashboards,reports}/*.yaml', { query: '?raw', import: 'default', eager: true }) as Record<string, string>
const recorded = import.meta.glob('../../../../DbDataKit/conformance/views/widget-results/*/*/*.json', { import: 'default', eager: true }) as Record<string, unknown>

/** The bundle from the files the build collected: `.../dashboards/x.yaml` is `dashboard/x`; `.../widget-results/<kind>/<document>/<widget>.json` is that widget's result. */
export function bundleFrom(files: Record<string, string>, results: Record<string, unknown>): FixtureBundle {
  const bundle: FixtureBundle = { documents: {}, results: {} }
  for (const [path, text] of Object.entries(files)) {
    const m = /\/(dashboards|reports)\/([^/]+)\.yaml$/.exec(path)
    if (m) bundle.documents[`${m[1] === 'dashboards' ? 'dashboard' : 'report'}/${m[2]}`] = text
  }
  for (const [path, result] of Object.entries(results)) {
    const m = /\/widget-results\/([^/]+)\/([^/]+)\/([^/]+)\.json$/.exec(path)
    if (m) ((bundle.results![`${m[1]}/${m[2]}`] ??= {}) as Record<string, unknown>)[m[3]!] = result
  }
  return bundle
}

export interface ResolveCall { kind: ViewKind; name: string; layout: string; widgets: string[] }

/** What the fixture source was asked, in order: shown on the page ("the last request resolved ...") and what a test counts. */
export const resolveLog = (() => {
  let calls: ResolveCall[] = []
  const listeners = new Set<() => void>()
  return {
    snapshot: () => calls,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener) } },
    record(call: ResolveCall) { calls = [...calls, call]; listeners.forEach((l) => l()) },
    clear() { calls = []; listeners.forEach((l) => l()) },
  }
})()

/** The id of the fixture source; any other id is a server this console manages. */
export const FIXTURES = 'fixtures'

export interface ViewsSource {
  id: string
  label: string
  /** True for a server's published documents; false for the fixtures and for a person's own documents. */
  published: boolean
  /** A person's own dashboards and reports on that server (plan 32 DBR12): the server's id, when this is that source. */
  personal?: { server: string }
}

export const fixturesSource: ViewsSource = { id: FIXTURES, label: 'Fixtures (recorded results)', published: false }

/** What a server's status said of the module, or null when it is not a source: no answer, no such call (a Gateway without the module, or an older one) or `enabled: false`. */
async function moduleEnabled(server: InstanceSummary, doFetch: typeof fetch): Promise<boolean> {
  try {
    const response = await doFetch(`/relay/${encodeURIComponent(server.id)}/api/v1/views/status`, { credentials: 'same-origin', headers: { accept: 'application/json' } })
    if (!response.ok) return false
    return ((await response.json()) as { enabled?: unknown }).enabled === true
  } catch {
    return false
  }
}

/** The sources there are: the fixtures, then each reachable Gateway that runs the views module, in the console's order. A console that cannot list its servers has the fixtures. */
export async function discoverSources(deps: { list?: () => Promise<InstanceSummary[]>; fetch?: typeof fetch } = {}): Promise<ViewsSource[]> {
  let servers: InstanceSummary[] = []
  try { servers = await (deps.list ?? consoleApi.instances.list)() } catch { /* no list: the fixtures */ }
  const doFetch = deps.fetch ?? ((input, init) => fetch(input, init))
  const gateways = servers.filter((s) => s.product === 'dbdatagateway' && s.reachable)
  const enabled = await Promise.all(gateways.map((s) => moduleEnabled(s, doFetch)))
  const served = gateways.filter((_, i) => enabled[i])
  // each server that runs the module also keeps a person's own documents (DBR12): a source beside the published ones, after them, so the automatic choice is still the first published source
  return [
    ...served.map((s): ViewsSource => ({ id: s.id, label: `${s.name} (published)`, published: true })),
    ...served.map((s): ViewsSource => ({ id: personalId(s.id), label: `My dashboards on ${s.name}`, published: false, personal: { server: s.id } })),
    fixturesSource,
  ]
}

/** The source of a person's own documents that goes with a source: a server's published documents and the server's own are one pair; the fixtures have the first personal source there is (null when no server runs the module). */
export function personalFor(sources: ViewsSource[], source: ViewsSource): ViewsSource | null {
  if (source.personal) return source
  const pair = sources.find((s) => s.personal && !source.personal && s.personal.server === source.id)
  return pair ?? (source.published ? null : sources.find((s) => s.personal) ?? null)
}

/** The source an address asks for (`?source=`): a known id, else the first published source (the automatic choice), else the fixtures; `unknown` is the id asked for that is not a source now. */
export function pickSource(sources: ViewsSource[], requested: string | null): { source: ViewsSource; unknown: string | null } {
  const found = requested ? sources.find((s) => s.id === requested) : undefined
  if (found) return { source: found, unknown: null }
  return { source: sources.find((s) => s.published) ?? fixturesSource, unknown: requested }
}

/** A client that tells `resolveLog` what it was asked, as the fixture source does by itself (the page shows the last request). */
function logged(inner: ViewsClient): ViewsClient {
  return {
    ...inner,
    async resolve(ref, layout, parameters, options) {
      const answer: RenderedWidgets = await inner.resolve(ref, layout, parameters, options)
      resolveLog.record({ kind: ref.kind, name: ref.name, layout, widgets: options?.only ?? Object.keys(answer.widgets) })
      return answer
    },
  }
}

const clients = new Map<string, ViewsClient>()

/** The client of a source, created on first use: a server's published documents through the relay, or the fixtures. */
export function viewsClient(source: string = FIXTURES): ViewsClient {
  if (source === FIXTURES) return fixtureViewsClient()
  if (isPersonal(source)) return personalViewsClient(source)
  let found = clients.get(source)
  if (!found) {
    found = logged(moduleClient({ baseUrl: `/relay/${encodeURIComponent(source)}`, serverCheck: true }))
    clients.set(source, found)
  }
  return found
}

const personal = new Map<string, PersonalClient>()

/** The client of a person's own documents on a server (the source `mine:<server>`): the `ViewsClient` of the board and the editor, and the shares, history, restore and promote the Views page adds. */
export function personalViewsClient(source: string): PersonalClient {
  let found = personal.get(source)
  if (!found) {
    const made = personalClient({ baseUrl: `/relay/${encodeURIComponent(serverOf(source))}` })
    // what it was asked is shown on the board as for any source (the spread keeps the extra calls)
    found = logged(made) as PersonalClient
    personal.set(source, found)
  }
  return found
}

let client: ViewsClient | null = null

function fixtureViewsClient(): ViewsClient {
  const bundle = bundleFrom(documents, recorded)
  // what the editor saves stays in the browser's storage (this viewer's convenience, until a real store answers): a reload opens the saved file, and a second tab sees it
  let storage: Storage | null = null
  try { storage = window.localStorage } catch { /* blocked: this visit's memory */ }
  // the fixture source knows the retail model's catalog and stands in for the server's catalog check of filters and order (`ViewsClient.validate`)
  const catalogs = { [retailCatalog.model]: retailCatalog }
  client ??= fixtureClient(bundle, { documents: storedDocuments(bundle.documents, storage), catalogs, validateHook: (document) => catalogDiagnostics(document, catalogs), onResolve: (c) => resolveLog.record({ kind: c.kind, name: c.name, layout: c.layout, widgets: c.widgets }) })
  return client
}
