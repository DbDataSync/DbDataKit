import { expect, test, type Page, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'
import { GATEWAY, SERVER, mockWorkspace } from './workspace-fake'

// The Rows tab of a Store project: the Gateways are the console's (`/api/instances`, faked by `workspace-fake.ts`), one is offered when its store serves
// the model, and a row is read and written through the console's relay to that Gateway's store (`/relay/<gateway>/api/v1/store/...`, faked below in the
// shapes `GatewayStoreManagement` answers). The Workspace server's own list of paired targets answers 404 here, so the story shows the tab does without it.
// The real store routes are tested in the Gateway's suite. It signs in a new administrator, so its name sorts after `first-run` and `identity-home`
// (ci-lessons).
test.setTimeout(120_000)

const MODEL = 'regions'

/** The store of the Gateway `edge` through the relay: it serves `regions` (one entity `item`, keyed by `code`) with one row, and applies an update. */
async function mockGatewayStore(page: Page) {
  const writes: unknown[] = []
  const rows = [{ values: { code: 'NE', name: 'North East' }, version: 1, valid_from: null, valid_to: null }]
  const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  const attribute = (name: string, type: string, length: number, required: boolean) => ({
    name, title: null, type, kind: 'string', length, precision: null, scale: null, required, unique: false, default: null, pattern: null, min: null, max: null, allowed: null, references: null,
  })
  await page.route(`**/relay/${GATEWAY}/api/v1/store/**`, async (route) => {
    const request = route.request()
    const path = decodeURIComponent(new URL(request.url()).pathname.slice(`/relay/${GATEWAY}/api/v1/store`.length))
    if (path === '/models') return json(route, { models: [{ name: MODEL, title: 'Regions', version: 1, entities: ['item'] }] })
    if (path === `/models/${MODEL}/schema`) {
      return json(route, {
        name: MODEL, title: 'Regions', approval: 'none', instances: 'none',
        entities: [{ name: 'item', title: null, key: ['code'], history: 'none', role: 'table', rules: [], attributes: [attribute('code', 'VARCHAR(20)', 20, true), attribute('name', 'VARCHAR(100)', 100, true)] }],
      })
    }
    if (path === `/models/${MODEL}/entities/item/rows`) return json(route, { rows, limit: 25, offset: 0 })
    if (path === `/models/${MODEL}/changes` && request.method() === 'POST') {
      const body = request.postDataJSON() as { changes: { action: string; values: Record<string, unknown> }[] }
      writes.push(body)
      rows[0] = { ...rows[0]!, values: { ...rows[0]!.values, ...body.changes[0]!.values }, version: rows[0]!.version + 1 }
      return json(route, { applied: body.changes.length })
    }
    return json(route, { error: `There is no store route ${request.method()} ${path}.` }, 404)
  })
  return { writes }
}

/** The Workspace server's list of paired targets, gone: every call answers 404 and is counted. */
async function removeWorkspaceTargets(page: Page) {
  const calls: string[] = []
  await page.route(`**/relay/${SERVER}/api/v1/publish-targets**`, (route) => {
    calls.push(route.request().url())
    return route.fulfill({ status: 404, contentType: 'application/json', body: JSON.stringify({ error: 'There is no such call.' }) })
  })
  return calls
}

test('the Rows tab offers the console\'s Gateway that serves the model and edits a row there, with the Workspace server\'s target list gone', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Rows Editor', `rows-${Date.now()}@example.com`)
  await mockWorkspace(page)
  const store = await mockGatewayStore(page)
  const targetCalls = await removeWorkspaceTargets(page)

  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill(MODEL)
  await page.getByTestId('ws-new-project-kind').selectOption('store')
  await page.getByTestId('ws-new-project-create').click()
  await expect(page.locator(`[data-testid="ws-project"][data-project="${MODEL}"]`)).toBeVisible()
  await page.getByTestId(`ws-new-name-${MODEL}`).fill('rows-edit')
  await page.getByTestId(`ws-new-create-${MODEL}`).click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-rows-edit"]')
  await expect(listed).toBeVisible()
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()

  // from here on the Rows tab is all that asks: it never asks the Workspace server for targets
  targetCalls.length = 0
  await page.getByTestId('wsp-tab-rows').click()
  await expect(page.getByTestId('wsp-rows-model')).toHaveValue(MODEL)
  await expect(page.getByTestId('wsp-rows-gateway')).toHaveValue(GATEWAY)
  await expect(page.locator('[data-testid="wsp-rows-gateway"] option')).toHaveText([GATEWAY])

  // edit the row on the Gateway, as the signed-in person
  await page.locator('[data-testid="sui-row"][data-key="NE"]').click()
  await page.getByTestId('sui-field-name').fill('North-East')
  await page.getByTestId('sui-submit').click()
  await expect(page.locator('[data-testid="sui-row"][data-key="NE"]')).toContainText('North-East')
  expect(store.writes).toEqual([{ changes: [expect.objectContaining({ action: 'update', entity: 'item', key: { code: 'NE' }, expected_version: 1, values: expect.objectContaining({ name: 'North-East' }) })] }])
  expect(targetCalls).toEqual([])
})
