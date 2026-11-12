import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 18 G10b and plan 23a FQ16: the Lineage page marks a failed run on its edge and shows what a block stops. The console's `GET /api/lineage` is answered in the page (the shape `LineageController` sends, which
// `ConsoleLineageTests` pins on the .NET side), because the page is read from a browser with no servers behind it (a Sync server and a Gateway send `runs` now, pinned by their own tests). The file name sorts after `identity-home` and `first-run`, which use the first-run invitation.
const crm = 'sqlserver://sql01/crm.dbo.customers'
const warehouse = 'postgres://pg01/dwh.public.customers'
const model = 'postgres://pg01/dwh.marts.dim_customer'

const answer = {
  nodes: [
    { id: crm, kind: 'dataset', name: 'dbo.customers', unknown_upstream: true },
    { id: warehouse, kind: 'dataset', name: 'public.customers', unknown_upstream: false, properties: { check_status: 'blocked', check_count: '1', check_failing: '1', check_blocking: '1', check_blocking_ids: 'customers_fresh' } },
    { id: model, kind: 'dataset', name: 'marts.dim_customer', unknown_upstream: false },
  ],
  edges: [
    { from: crm, to: warehouse, kind: 'replicates', granularity: 'table', confidence: 'declared' },
    { from: warehouse, to: model, kind: 'derives', granularity: 'table', confidence: 'derived' },
  ],
  fingerprint: 'f',
  servers: [{ server: 'Sync one', product: 'dbdatasync', nodes: 3, edges: 2, problems: [], error: null, at: '2026-11-10T08:00:00+00:00' }],
  runs: [
    { from: crm, to: warehouse, kind: 'replicates', at: '2026-11-10T08:00:00+00:00', outcome: 'failed', rows: 12 },
    { from: warehouse, to: model, kind: 'derives', at: '2026-11-10T09:00:00+00:00', outcome: 'succeeded', rows: null, late: true },
  ],
  blocks: [{ node: warehouse, name: 'public.customers', checks: ['customers_fresh'], since: '2026-11-08T12:00:00+00:00', stops: [{ id: model, name: 'marts.dim_customer', kind: 'dataset', depth: 1 }] }],
}

test('a failed run is marked on its edge, an overdue one is amber and a blocked node says what its block stops', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Lineage Tester', `lineage-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`)
  await page.route('**/api/lineage?*', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(answer) }))
  await page.route('**/api/lineage', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(answer) }))
  await page.route('**/api/quality', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ datasets: [], servers: [] }) }))

  await page.goto('/lineage')
  await expect(page.getByTestId('lineage-graph')).toBeVisible()
  // the failed replication is red on its line and says when; the derivation that ran is not
  await expect(page.locator('.lineage-run-failed')).toHaveCount(1)
  await expect(page.locator('.lineage-run-succeeded')).toHaveCount(1)
  await expect(page.getByText('replicates · failed 2026-11-10 08:00')).toBeVisible()
  // a derivation whose last run was fine but is overdue by its server's expectation is amber and says so (G10b, the late colour)
  await expect(page.locator('.lineage-run-late')).toHaveCount(1)
  await expect(page.getByText('derives · late, last ok 2026-11-10 09:00')).toBeVisible()

  // choosing the blocked table says what its block stops
  await page.getByTestId('lineage-search').fill('customers')
  await page.getByTestId(`lineage-found-${warehouse}`).click()
  await expect(page.getByTestId('lineage-node-checks')).toHaveAttribute('data-status', 'blocked')
  await expect(page.getByTestId('lineage-node-checks')).toContainText('blocked by customers_fresh; stops 1: marts.dim_customer')
})
