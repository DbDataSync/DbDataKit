import { isEntryActive, toGroups, type Nav } from '@dbdatakit/shell'

/** A page with no entry of its own: the entry it sits under (lit while it is open) and the page that links to it. */
export interface PageUnder {
  /** The address of the navigation entry it is under. */
  under: string
  /** Where a person finds the link to it. */
  from: string
}

/**
 * Every route of the console (`App.tsx`) and of each server's screens that is not an entry of the navigation, with the entry it is under and the page
 * that links to it. A new route is either an entry of the navigation or a line here; `routes.test.ts` fails, naming the route, when it is neither.
 *
 * Design: DbDataKit/README.md#finding-a-screen-the-navigation
 */
export const pagesUnder: Record<string, Record<string, PageUnder>> = {
  console: {
    '/upgrades': { under: '/instances', from: 'All servers (the Upgrades link)' },
    '/state/:instance': { under: '/instances', from: 'All servers (a server\'s state link)' },
    '/servers/:instance/*': { under: '/instances', from: 'each paired server\'s own entry under Servers, and All servers (Open)' },
    '/docs/:module/*': { under: '/docs', from: 'Docs (a module\'s docs)' },
    '/views/dashboards': { under: '/views', from: 'Views (the Dashboards tab)' },
    '/views/dashboards/:name': { under: '/views', from: 'Views, Dashboards (a dashboard of the list)' },
    '/views/reports': { under: '/views', from: 'Views (the Reports tab)' },
    '/views/reports/:name': { under: '/views', from: 'Views, Reports (a report of the list)' },
    '/views/catalog': { under: '/views', from: 'Views (the Catalog tab)' },
    '*': { under: '/', from: 'any address the console does not know (it shows Home)' },
  },
  gateway: {
    '/data-sources/:name': { under: '/data-sources', from: 'Data sources (a data source of the list)' },
    '/models/:name': { under: '/models', from: 'Models (a model of the list)' },
    '/models/:name/explore': { under: '/models', from: 'a model (Explore)' },
    '/config/:sha': { under: '/config', from: 'Configuration history (a commit)' },
    '*': { under: '/', from: 'any address the Gateway\'s screens do not know (it goes to Overview)' },
  },
  runner: {
    '/packages/:name': { under: '/packages', from: 'Packages (a package of the list)' },
    '*': { under: '/', from: 'any address the runner\'s screens do not know (it goes to Tasks)' },
  },
  workspace: {
    '/:project/:id/*': { under: '/', from: 'Workspaces (a workspace of a project: its Files and its Designer)' },
    '*': { under: '/', from: 'any address the Workspace\'s screens do not know (it goes to Workspaces)' },
  },
  identityHome: {
    '*': { under: '/', from: 'any address the identity home\'s screens do not know' },
  },
}

/** The routes a `<Routes>` source declares, in order: each `<Route path="...">`. */
export function routesIn(source: string): string[] {
  return Array.from(source.matchAll(/<Route\s+path="([^"]+)"/g), (m) => m[1])
}

/**
 * The routes that are neither an entry of the navigation nor listed as a page under one, and the listed pages that are wrong (under an address that is
 * no entry, or a route that no longer exists), each as a sentence naming the route.
 */
export function unplacedRoutes(product: string, routes: string[], nav: Nav, pages: Record<string, PageUnder>): string[] {
  const entries = toGroups(nav).flatMap((g) => g.items).filter((i) => i.to)
  const isEntry = (route: string) => entries.some((e) => e.to === route)
  const problems: string[] = []
  for (const route of routes) {
    if (isEntry(route) || pages[route]) continue
    problems.push(`${product}: the route ${route} is neither an entry of the navigation nor listed in pagesUnder (navigationPages.ts) with the entry it is under and the page that links to it`)
  }
  for (const [route, page] of Object.entries(pages)) {
    if (!routes.includes(route)) problems.push(`${product}: pagesUnder lists ${route}, which is no route`)
    else if (!isEntry(page.under)) problems.push(`${product}: pagesUnder puts ${route} under ${page.under}, which is no entry of the navigation`)
    else if (route !== '*' && !route.startsWith('/:') && !route.startsWith('/servers/') && !isEntryActive(route.replace(/\/\*$/, '/x'), entries.find((e) => e.to === page.under)!)) {
      problems.push(`${product}: ${route} is listed under ${page.under}, which is not lit on it (add it to that entry's pages)`)
    }
  }
  return problems
}
