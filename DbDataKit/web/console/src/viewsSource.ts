import { fixtureClient, storedDocuments, type FixtureBundle, type ViewKind, type ViewsClient } from '@dbdatakit/visuals'

/**
 * The console's source of dashboards and reports until a serving module answers (plan 32 DBR23, M1): the fixture source. The documents are the retail template's sample bundle and the recorded results of each
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

let client: ViewsClient | null = null

/** The one client of the page: created on first use, so a page that is never opened reads nothing. */
export function viewsClient(): ViewsClient {
  const bundle = bundleFrom(documents, recorded)
  // what the editor saves stays in the browser's storage (this viewer's convenience, until a real store answers): a reload opens the saved file, and a second tab sees it
  let storage: Storage | null = null
  try { storage = window.localStorage } catch { /* blocked: this visit's memory */ }
  client ??= fixtureClient(bundle, { documents: storedDocuments(bundle.documents, storage), onResolve: (c) => resolveLog.record({ kind: c.kind, name: c.name, layout: c.layout, widgets: c.widgets }) })
  return client
}
