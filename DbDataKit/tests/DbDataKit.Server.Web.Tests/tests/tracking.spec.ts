import { expect, test, type Page, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 28c, E18: the console's Tracking page. The two servers are mocked in the page (the Build server's and the runner's `GET /api/v1/tracking` and the resolve: the server side is pinned by BuildServerTrackingApiTests, RunnerTrackingApiTests and the
// conformance tests of the copy; what this shows is the screen). A Build server holds the copy of a restored target and shows it diverged with the row where the two sides part; the administrator resolves it by archiving, after which it is in step and
// the divergence is listed as resolved; asking for a switch of the primary is refused with its reason, and refused naming the server when it goes down. Sorts after `first-run` and `identity-home`: it invites an administrator.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const identity = 'buildtracking:mssql://sql01/wh.dbdatabuild.tracking@sales'
const point = (key: string, time: string) => ({ order: 1, key, time })
const BUILD = { id: 'b-track', name: 'Tracking build', url: 'https://build.example', product: 'dbdatabuild', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: '2026-11-11T09:00:00Z' }
const RUNNER = { id: 'r-track', name: 'Tracking runner', url: 'https://runner.example', product: 'dbdatarunner', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: '2026-11-11T09:00:00Z' }

function document(holder: string, divergence: 'open' | 'archived' | 'none') {
  const diverged = divergence === 'open'
  return {
    holder, mode: 'both', primary: 'target',
    copies: [{ identity, streams: [{ stream: 'run_log', rows: 12, checkpoint: point('a|12', '2026-11-11T08:00:00.0000000Z'), diverged }, { stream: 'ddl_log', rows: 4, checkpoint: null, diverged: false }] }],
    divergences: divergence === 'none' ? [] : [{
      id: 7, identity, stream: 'run_log', status: diverged ? 'open' : 'archived', open: diverged, reason: 'the target has fewer rows than the copy (a restored backup)',
      common: point('a|8', '2026-11-10T09:30:00.0000000Z'), primary_high: point('a|9', '2026-11-10T09:31:00.0000000Z'), copy_high: point('a|12', '2026-11-11T08:00:00.0000000Z'),
      diverged_at: '2026-11-11T08:05:00Z', resolved_at: diverged ? null : '2026-11-11T09:00:00Z', resolved_by: diverged ? null : 'tracking-admin',
    }],
  }
}

async function mockServers(page: Page) {
  const state = { divergence: 'open' as 'open' | 'archived', buildUp: true, resolves: [] as unknown[] }
  await page.route('**/api/instances', (route) => route.request().method() === 'GET' ? json(route, [BUILD, { ...RUNNER }]) : route.fallback())
  await page.route(`**/relay/${BUILD.id}/api/v1/tracking`, (route) => state.buildUp ? json(route, document('build-server', state.divergence)) : json(route, { error: 'The server did not answer.' }, 502))
  await page.route(`**/relay/${RUNNER.id}/api/v1/tracking`, (route) => json(route, document('runner', 'none')))
  await page.route(`**/relay/${BUILD.id}/api/v1/tracking/divergences/7/resolve`, (route) => {
    state.resolves.push(route.request().postDataJSON())
    state.divergence = 'archived'
    return json(route, { resolved: true })
  })
  return state
}

test('a restored target shows diverged with the common point, the administrator archives, and the switch of the primary refuses with its reason', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Tracking Tester', `tracking-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`)
  const mock = await mockServers(page)

  await page.goto('/tracking')
  await expect(page.getByTestId('tracking-page')).toBeVisible()
  const rows = page.getByTestId('tracking-row')
  await expect(rows).toHaveCount(2)
  const build = rows.filter({ hasText: 'Tracking build' })
  await expect(build.getByTestId('tracking-state')).toHaveText('Diverged')
  await expect(build.getByTestId('tracking-mode')).toHaveText('both')
  await expect(build.getByTestId('tracking-primary')).toHaveText('target')
  await expect(build.getByTestId('tracking-lag')).toContainText('rows in 2 streams')
  await expect(rows.filter({ hasText: 'Tracking runner' }).getByTestId('tracking-state')).toHaveText('In step')

  // where the two sides part
  const divergence = page.getByTestId('tracking-divergence')
  await expect(divergence).toHaveAttribute('data-open', 'true')
  await expect(divergence.getByTestId('tracking-common')).toContainText('a|8')

  // a switch is refused while the divergence is open, with the reason
  await build.getByTestId('tracking-switch').click()
  await expect(page.getByTestId('tracking-switch-message')).toContainText('open divergence (run_log)')

  // the administrator archives
  await divergence.getByTestId('tracking-resolve-archive').click()
  await expect(page.getByTestId('tracking-resolved-message')).toContainText('Divergence 7 resolved')
  expect(mock.resolves).toEqual([{ resolution: 'archive' }])
  await expect(page.getByTestId('tracking-divergence')).toHaveAttribute('data-open', 'false')
  await expect(page.getByTestId('tracking-resolved-by')).toContainText('tracking-admin')
  await expect(build.getByTestId('tracking-state')).toHaveText('In step')

  // both sides up and in step: the switch still says why nothing changed
  await build.getByTestId('tracking-switch').click()
  await expect(page.getByTestId('tracking-switch-message')).toContainText('target first in this release')

  // the Build server goes down: its copy stays on the page marked old, the server is named, and the switch refuses naming it
  mock.buildUp = false
  await page.getByTestId('rail-runs').click()
  await page.getByTestId('rail-tracking').click()
  await expect(page.getByTestId('tracking-problem')).toContainText('Tracking build did not answer')
  const old = page.getByTestId('tracking-row').filter({ hasText: 'Tracking build' })
  await expect(old.getByTestId('tracking-state')).toHaveText('Server down')
  await old.getByTestId('tracking-switch').click()
  await expect(page.getByTestId('tracking-switch-message')).toContainText('Tracking build does not answer')
})
