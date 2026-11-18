import { describe, expect, it } from 'vitest'
import { gatewayNav, identityHomeNav } from '@dbdatakit/gateway'
import { runnerNav } from '@dbdatakit/runner'
import { workspaceNav } from '@dbdatakit/workspace'
import { consoleNav } from './ConsoleShell'
import { pagesUnder, routesIn, unplacedRoutes } from './navigationPages'
import consoleRoutes from './App.tsx?raw'
import gatewayRoutes from '../../gateway/src/GatewayModule.tsx?raw'
import runnerRoutes from '../../runner/src/RunnerModule.tsx?raw'
import workspaceRoutes from '../../workspace/src/WorkspaceModule.tsx?raw'
import identityHomeRoutes from '../../gateway/src/identity/IdentityHomeModule.tsx?raw'

const sources: Record<string, string> = {
  'console/src/App.tsx': consoleRoutes,
  'gateway/src/GatewayModule.tsx': gatewayRoutes,
  'runner/src/RunnerModule.tsx': runnerRoutes,
  'workspace/src/WorkspaceModule.tsx': workspaceRoutes,
  'gateway/src/identity/IdentityHomeModule.tsx': identityHomeRoutes,
}
const source = (file: string) => sources[file]

/** Each product's screens: where its routes are declared and its navigation. */
const products = [
  { name: 'console', file: 'console/src/App.tsx', nav: consoleNav },
  { name: 'gateway', file: 'gateway/src/GatewayModule.tsx', nav: gatewayNav },
  { name: 'runner', file: 'runner/src/RunnerModule.tsx', nav: runnerNav },
  { name: 'workspace', file: 'workspace/src/WorkspaceModule.tsx', nav: workspaceNav },
  { name: 'identityHome', file: 'gateway/src/identity/IdentityHomeModule.tsx', nav: identityHomeNav },
]

describe('every route has a place in the navigation', () => {
  for (const product of products) {
    it(`${product.name}: each route is an entry or a page listed under one`, () => {
      const routes = routesIn(source(product.file))
      expect(routes.length).toBeGreaterThan(0)
      expect(unplacedRoutes(product.name, routes, product.nav, pagesUnder[product.name])).toEqual([])
    })
  }

  it('fails, naming the route, when a route is added with neither an entry nor a parent', () => {
    const app = source('console/src/App.tsx').replace('<Route path="/runs"', '<Route path="/new-page" element={null} />\n      <Route path="/runs"')
    expect(unplacedRoutes('console', routesIn(app), consoleNav, pagesUnder.console)).toEqual([
      'console: the route /new-page is neither an entry of the navigation nor listed in pagesUnder (navigationPages.ts) with the entry it is under and the page that links to it',
    ])
    const gateway = source('gateway/src/GatewayModule.tsx').replace('<Route path="/cache"', '<Route path="/cache/:name" element={null} />\n      <Route path="/cache"')
    expect(unplacedRoutes('gateway', routesIn(gateway), gatewayNav, pagesUnder.gateway)).toEqual([expect.stringContaining('the route /cache/:name is neither')])
  })

  it('fails on a listed page that is no route, or is under no entry', () => {
    const routes = routesIn(source('runner/src/RunnerModule.tsx'))
    expect(unplacedRoutes('runner', routes, runnerNav, { ...pagesUnder.runner, '/gone': { under: '/runs', from: 'nowhere' } })).toEqual(['runner: pagesUnder lists /gone, which is no route'])
    expect(unplacedRoutes('runner', routes, runnerNav, { ...pagesUnder.runner, '/packages/:name': { under: '/nowhere', from: 'nowhere' } })).toEqual(['runner: pagesUnder puts /packages/:name under /nowhere, which is no entry of the navigation'])
  })
})
