import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 24a UC10: "Upgrade everything" on the Upgrades screen. The console's list of what is available and the servers' own upgrade calls (`/relay/<id>/api/v1/upgrade`, UC09) are answered in the page, because a real
// upgrade needs a service (the library's tests cover the swap and the rollback). The story: three servers have a newer release; the plan puts the Gateway first, then Build, then the Store; the Store is a container, so it is
// shown with its reason and left out; the Gateway is upgraded; Build is rolled back by its health check, so the run stops there. Sorts after `identity-home` and `first-run`, which use the first-run invitation.
const server = (id: string, name: string, product: string) => ({
  id, name, product, version: '1.0.0', generation: 1, newest: '2.0.0', newerAvailable: true, newestGeneration: 1, notesUrl: null, outsideAfterRelease: null,
})
const available = {
  feed: 'mirror', channel: 'stable', mirror: true, at: '2026-11-11T08:00:00+00:00', consoleGeneration: 1, consoleVersion: '1.0.0',
  products: [], servers: [server('s-build', 'Plan build', 'dbdatabuild'), server('s-gw', 'Plan gateway', 'dbdatagateway'), server('s-store', 'Plan store', 'dbdatastore')],
}
const status = (over: Record<string, unknown>) => ({ product: 'x', version: '1.0.0', generation: 1, install_method: 'Archive', can_apply: true, reason: null, state: 'none', problem: null, draining: false, active_work: 0, ...over })

test('the plan upgrades the serving layer first, leaves a container alone, and stops at the first server that is rolled back', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Plan Tester', `plan-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`)
  const applied: string[] = []
  await page.route('**/api/upgrades/available**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(available) }))
  await page.route('**/relay/*/api/v1/upgrade', async (route) => {
    const id = decodeURIComponent(/relay\/([^/]+)\//.exec(route.request().url())![1])
    const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
    if (route.request().method() === 'GET') {
      if (id === 's-store') return json(status({ can_apply: false, reason: 'it runs in a container' }))
      return json(status({ state: applied.includes(id) ? (id === 's-build' ? 'rolled_back' : 'committed') : 'none', message: id === 's-build' ? 'the new version did not become healthy' : null, plan: { id: 'p', from: '1.0.0', to: '2.0.0', state: 'x', reason: null, steps: [] } }))
    }
    const action = JSON.parse(route.request().postData() ?? '{}').action
    if (action === 'prepare') return json({ state: 'prepared', product: 'x', from: '1.0.0', to: '2.0.0', generation: 1, install_method: 'Archive', feed: 'mirror', can_apply: true, message: 'ready' })
    applied.push(id)
    return json(status({ state: 'applying' }))
  })

  await page.goto('/upgrades')
  await expect(page.getByTestId('upgrade-plan')).toBeVisible()
  await page.getByTestId('upgrade-plan-make').click()
  const entries = page.getByTestId('upgrade-plan-list').getByRole('listitem')
  await expect(entries).toHaveCount(3)
  await expect(entries.nth(0)).toContainText('Plan gateway')
  await expect(entries.nth(1)).toContainText('Plan build')
  await expect(entries.nth(2)).toContainText('Plan store')
  await expect(page.getByTestId('upgrade-plan-entry-Plan store')).toHaveAttribute('data-action', 'instruction')
  await expect(page.getByTestId('upgrade-plan-note-Plan store')).toContainText('it runs in a container')

  await page.getByTestId('upgrade-plan-start').click()
  await expect(page.getByTestId('upgrade-plan-done-Plan gateway')).toContainText('Upgraded', { timeout: 30_000 })
  await expect(page.getByTestId('upgrade-plan-failed-Plan build')).toContainText('did not become healthy', { timeout: 30_000 })
  await expect(page.getByTestId('upgrade-plan-summary')).toHaveAttribute('data-stopped', 'true')
  await expect(page.getByTestId('upgrade-plan-summary')).toContainText('Stopped at Plan build')
  expect(applied).toEqual(['s-gw', 's-build'])           // the Store was never asked
})
