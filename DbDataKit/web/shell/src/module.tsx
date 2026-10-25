import { createContext, useCallback, useContext, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  Link as RouterLink,
  NavLink as RouterNavLink,
  Navigate as RouterNavigate,
  useNavigate as useRouterNavigate,
  type LinkProps,
  type NavLinkProps,
  type NavigateOptions,
  type NavigateProps,
  type To,
} from 'react-router-dom'
import { setApiBase } from './api/http'

/** Where a product's app is mounted and which server it talks to (plan 13, U08). Standalone it is mounted at the root and talks to its own server. */
export interface ModuleInfo {
  /** The path the app's routes begin at, `''` at the root. Absolute links inside the app are written without it and given it here. */
  basePath: string
  /** What `/api/...` calls are sent to, `''` for the host's own. */
  apiBase: string
  /** A way back to the host the module is mounted in (the console's list of servers), shown first in the rail. Outside the module's base: the link is exactly as given. */
  home?: { to: string; title: string }
}

const ModuleContext = createContext<ModuleInfo>({ basePath: '', apiBase: '' })

export const useModule = () => useContext(ModuleContext)

// One cache per server: what one server answered is never shown as another's.
const caches = new Map<string, QueryClient>()
function cacheFor(key: string) {
  let client = caches.get(key)
  if (!client) {
    client = new QueryClient({ defaultOptions: { queries: { retry: 1, staleTime: 5000 } } })
    caches.set(key, client)
  }
  return client
}

/**
 * Mounts a product's app inside a host (plan 13, U08): its links keep working under `basePath`, its `/api` calls go to `apiBase`, and it has a cache of its own for this server.
 * The API base is set while rendering, before any child can ask for data; only one module is on screen at a time.
 */
export function ModuleProvider({ info, children }: { info: ModuleInfo; children: ReactNode }) {
  setApiBase(info.apiBase)
  return (
    <ModuleContext.Provider value={info}>
      <QueryClientProvider client={cacheFor(info.apiBase)}>{children}</QueryClientProvider>
    </ModuleContext.Provider>
  )
}

function prefixed(to: To, basePath: string): To {
  if (!basePath) return to
  if (typeof to === 'string') return to.startsWith('/') ? basePath + to : to
  return to.pathname?.startsWith('/') ? { ...to, pathname: basePath + to.pathname } : to
}

/** The router's `Link`, for a module: an absolute `to` is inside the module. */
export function Link(props: LinkProps) {
  const { basePath } = useModule()
  return <RouterLink {...props} to={prefixed(props.to, basePath)} />
}

export function NavLink(props: NavLinkProps) {
  const { basePath } = useModule()
  return <RouterNavLink {...props} to={prefixed(props.to, basePath)} />
}

export function Navigate(props: NavigateProps) {
  const { basePath } = useModule()
  return <RouterNavigate {...props} to={prefixed(props.to, basePath)} />
}

export function useNavigate() {
  const { basePath } = useModule()
  const navigate = useRouterNavigate()
  return useCallback(
    (to: To | number, options?: NavigateOptions) => (typeof to === 'number' ? navigate(to) : navigate(prefixed(to, basePath), options)),
    [navigate, basePath],
  ) as { (to: To, options?: NavigateOptions): void; (delta: number): void }
}

/** The location inside the module: the path without the module's base. */
export function usePathInModule(pathname: string): string {
  const { basePath } = useModule()
  return basePath && pathname.startsWith(basePath) ? pathname.slice(basePath.length) || '/' : pathname
}
