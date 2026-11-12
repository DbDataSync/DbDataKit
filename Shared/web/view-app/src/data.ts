import { createSemanticClient, type SemanticClient } from '@dbdatakit/semantic-client'
import { moduleClient, type ViewsClient } from '@dbdatakit/visuals'
import { createStoreClient, type Fetcher, type StoreClient } from '@dbdatakit/store-ui'
import { parseShellTheme, type ShellTheme, type StoredRow } from '@dbdatakit/store-ui'

/** The most rows a view reads of one entity. A grid and a dashboard shape what they read (filter, sort, total), so the read is bounded and says when it stopped. */
export const ROW_CAP = 2000
const PAGE = 500

export interface Loaded { rows: StoredRow[]; truncated: boolean }

/** Every row of an entity up to the cap, in the Store's key order, through the app's data route. */
export async function loadAll(client: StoreClient, model: string, entity: string, cap = ROW_CAP): Promise<Loaded> {
  const rows: StoredRow[] = []
  while (rows.length < cap) {
    const want = Math.min(PAGE, cap - rows.length)
    const page = await client.rows(model, entity, { limit: want, offset: rows.length })
    rows.push(...page.rows)
    if (page.rows.length < want) return { rows, truncated: false }
  }
  // the cap was reached: one more row tells whether there is more
  const more = await client.rows(model, entity, { limit: 1, offset: rows.length })
  return { rows, truncated: more.rows.length > 0 }
}

/** What the shell page's token broker gives the frame, and the shell's address messages (`{dbdata: 'app/1'}`), the same protocol a code app speaks. */
export interface Host {
  instance: string
  token(): Promise<string>
  /** Tells the shell the view the person is on, so the address and the back button follow. */
  route(path: string): void
  /** Called when the shell asks for a view (the address changed). Returns how to stop listening. */
  onNavigate(listener: (path: string) => void): () => void
  /**
   * Asks the shell page to open a dialog or a side panel at a route of this instance (plan 32 DBR36): the page owns the surface, because a frame inside this document cannot get a token. `closed` is called once, with whether the form in it
   * saved (`views:saved`), when the surface is closed; a shell that refuses (one is open already, the route is not valid) closes it at once, unsaved.
   */
  openSurface(surface: { as: 'dialog' | 'panel'; label: string; address: string }, closed: (saved: boolean) => void): void
  /** Called with the shell's theme (a portal's look, `{type: 'theme', tokens}`), when it sends one. Returns how to stop listening. */
  onTheme(listener: (theme: ShellTheme) => void): () => void
}

export function connectHost(win: Window = window): Host {
  const waiting = new Map<string, (m: { type: string; token?: string; expiresIn?: number; message?: string; saved?: boolean }) => void>()
  const listeners = new Set<(path: string) => void>()
  const themeListeners = new Set<(theme: ShellTheme) => void>()
  let lastTheme: ShellTheme | null = null   // the shell sends the theme once, when the frame loads: a listener added later still gets it
  win.addEventListener('message', (event: MessageEvent) => {
    const m = event.data
    if (event.source !== win.parent || !m || m.dbdata !== 'app/1') return
    if (m.type === 'theme') {
      const theme = parseShellTheme(m)
      if (theme) { lastTheme = theme; themeListeners.forEach((l) => l(theme)) }
      return
    }
    if (m.type === 'token.request' || m.type === 'route' || m.type === 'surface.open') return   // our own requests, seen again where the page has no parent
    if (m.type === 'navigate' && typeof m.path === 'string') { listeners.forEach((l) => l(m.path)); return }
    waiting.get(m.id)?.(m)
    waiting.delete(m.id)
  })
  let cached: { token: string; until: number } | null = null
  const ask = () => new Promise<{ token: string; expiresIn: number }>((resolve, reject) => {
    const id = String(Math.random())
    waiting.set(id, (m) => (m.type === 'token' ? resolve({ token: m.token!, expiresIn: m.expiresIn ?? 60 }) : reject(new Error(m.message || m.type))))
    win.parent.postMessage({ dbdata: 'app/1', id, type: 'token.request' }, '*')
  })
  return {
    // the instance's own address, taken from where this document was served: /apps/<instance>/_app/
    instance: win.location.pathname.split('/')[2] ?? '',
    token: async () => {
      if (cached && cached.until > Date.now()) return cached.token
      const got = await ask()
      cached = { token: got.token, until: Date.now() + Math.max(0, got.expiresIn - 30) * 1000 }
      return got.token
    },
    route: (path) => win.parent.postMessage({ dbdata: 'app/1', type: 'route', path }, '*'),
    openSurface: (surface, closed) => {
      const id = String(Math.random())
      waiting.set(id, (m) => closed(m.type === 'surface.closed' && m.saved === true))
      win.parent.postMessage({ dbdata: 'app/1', id, type: 'surface.open', as: surface.as, label: surface.label, route: surface.address }, '*')
    },
    onNavigate: (listener) => { listeners.add(listener); return () => { listeners.delete(listener) } },
    onTheme: (listener) => {
      themeListeners.add(listener)
      if (lastTheme) listener(lastTheme)
      return () => { themeListeners.delete(listener) }
    },
  }
}

/** How an app reaches the host's data route for its instance: every call carries the shell's token. Paths are relative to the route (`/api/v1/models/...`). */
export function appFetcher(host: Pick<Host, 'instance' | 'token'>, fetchImpl: typeof fetch = (...a) => fetch(...a)): Fetcher {
  return async (path, init) => {
    const headers = new Headers(init?.headers)
    headers.set('Authorization', `Bearer ${await host.token()}`)
    return fetchImpl(`/apps/${encodeURIComponent(host.instance)}/_data${path}`, { ...init, headers })
  }
}

/** The Store client of an app: every call goes to the host's data route for the instance, with the shell's token. The route answers rows, one row, changes and the schema of the entities the manifest lists. */
export function appStoreClient(host: Pick<Host, 'instance' | 'token'>, fetchImpl?: typeof fetch): StoreClient {
  return createStoreClient(appFetcher(host, fetchImpl))
}

/** The semantic client of an app, on the same route and token. The apps host serves the semantic route (plan 22a GP35c) for the models the manifest lists under `semantic:`, as the signed-in person; a model it does not list is refused (`denied`). */
export function appSemanticClient(host: Pick<Host, 'instance' | 'token'>, fetchImpl?: typeof fetch): SemanticClient {
  return createSemanticClient(appFetcher(host, fetchImpl))
}

/**
 * The views client of an app (plan 32 DBR13): the shared `moduleClient` over the app's data route, with the shell's token. The route reads a dashboard or a report of the app by its file (`api/v1/dashboards/<file>`, the file the manifest
 * lists under `views`) and renders its widgets as the signed-in person (`.../render`), so `DashboardView` draws an app's document as it draws a published one. Exports, versions and the editor's calls are not on the app's route and answer `unavailable`.
 */
export function appViewsClient(host: Pick<Host, 'instance' | 'token'>, fetchImpl?: typeof fetch): ViewsClient {
  return moduleClient({
    baseUrl: `/apps/${encodeURIComponent(host.instance)}/_data`,
    ...(fetchImpl ? { fetch: fetchImpl } : {}),
    trusted: true,   // the host loaded and checked the document; this page may not compile a schema (its policy has no `unsafe-eval`)
    headers: async () => ({ Authorization: `Bearer ${await host.token()}` }),
  })
}
