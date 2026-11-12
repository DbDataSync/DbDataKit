import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 18 G08 and G11b: the Lineage page shows what the Gateway counted of a served table (reads, last read, readers) and the Gateway's serving nodes in the path from a source table to the model it serves. The console's `GET /api/lineage` is answered in
// the page (the shape `ConsoleLineageTests` and `ConsoleGatewayLineageTests` pin on the .NET side). The file name sorts after `identity-home` and `first-run`, which use the first-run invitation.
const warehouse = 'postgres://pg01/dwh.public.orders'
const source = 'suite://gateway-1/data-source/dwh'
const measure = 'suite://gateway-1/semantic/sales/orders.revenue'
const served = 'suite://gateway-1/semantic-model/sales'

const answer = {
  nodes: [
    { id: warehouse, kind: 'dataset', name: 'public.orders', unknown_upstream: true, properties: { connection: 'dwh', reads: '7', last_read: '2026-11-12T09:30:00Z', reader_count: '2', readers: 'ada, cy' } },
    { id: source, kind: 'serving', name: 'dwh', unknown_upstream: false, properties: { serving: 'data-source', provider: 'postgres', tables: 'used or modelled' } },
    { id: measure, kind: 'semanticmember', name: 'sales: orders.revenue', unknown_upstream: false, properties: { catalog: 'sales', member: 'orders.revenue', member_kind: 'measure' } },
    { id: served, kind: 'serving', name: 'sales', unknown_upstream: false, properties: { serving: 'semantic-model', catalog: 'sales', version: '3' } },
  ],
  edges: [
    { from: warehouse, to: source, kind: 'serves', granularity: 'table', confidence: 'observed' },
    { from: measure, to: served, kind: 'serves', granularity: 'table', confidence: 'derived' },
  ],
  fingerprint: 'f',
  servers: [{ server: 'Gateway one', product: 'dbdatagateway', nodes: 4, edges: 2, problems: [], error: null, at: '2026-11-12T10:00:00+00:00' }],
}

test('a served table shows how often it was read and the Gateway appears as what serves it', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Lineage Usage Tester', `lineage-usage-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`)
  await page.route('**/api/lineage?*', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(answer) }))
  await page.route('**/api/lineage', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(answer) }))
  await page.route('**/api/quality', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ datasets: [], servers: [] }) }))

  await page.goto('/lineage')
  await expect(page.getByTestId('lineage-graph')).toBeVisible()
  await expect(page.getByTestId(`lineage-card-${source}`)).toBeVisible()
  await expect(page.getByTestId(`lineage-card-${served}`)).toBeVisible()

  await page.getByTestId('lineage-search').fill('orders')
  await page.getByTestId(`lineage-found-${warehouse}`).click()
  await expect(page.getByTestId('lineage-node-usage')).toContainText('Read 7 times · last 2026-11-12 09:30 UTC · read by ada, cy')
})
