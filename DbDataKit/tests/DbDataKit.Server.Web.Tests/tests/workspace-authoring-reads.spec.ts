import { expect, test, type Page, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'
import { RUNNER, SERVER, mockWorkspace } from './workspace-fake'

// The Designer's shared reads on the Catalog tab of a build draft, through the console: Set up for development where no channel binds the connection's
// slot, which binds it, then Refresh in that channel, and Test with each step. The Workspace server is mocked in the page (`workspace-fake.ts`), and the
// console's routes are faked here (`/api/workspace-catalog/...`, `/api/workspace-dev-logins/...`): the real routes are tested with real servers in
// `DbDataKit.Server.Servers.Tests` and against fakes in `DbDataKit.Server.Tests`. It signs in a new administrator, so its name sorts after `first-run` and
// `identity-home` (ci-lessons).
test.setTimeout(120_000)

const PASSWORD = 'dev-login-7731-long'

/** The shape the Runner reads of `warehouse` in dev: one table. */
const shape = (at: string) => [
  'schema: catalog-cache/2', `generated_at: '${at}'`, 'connections:', "  'warehouse':", "    engine: 'postgres'",
  `    read: {server: 'plant', product: 'runner', channel: 'dev', at: '${at}'}`,
  '    databases:', "      - {name: 'dw', schemas: ['public']}", '    tables:',
  "      - database: 'dw'", "        schema: 'public'", "        name: 'orders'", '        kind: table', '        columns:',
  "          - {name: 'id', type: 'integer', nullable: false, key: true}", "          - {name: 'total', type: 'numeric(12,2)', nullable: true}", '',
].join('\n')

/** The console's reads, faked in the page: dev binds the slot once Set up for development sent the login with the draft. */
async function mockConsoleReads(page: Page) {
  const state = { bound: false }
  const sent: { path: string; body: string }[] = []
  const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  const base = { package: 'sales-dw', connection: 'warehouse', slot: 'warehouse', runner: RUNNER, runner_name: 'plant' }
  await page.route('**/api/workspace-dev-logins/**', (route) => {
    sent.push({ path: new URL(route.request().url()).pathname, body: route.request().postData() ?? '' })
    const body = route.request().postDataJSON() as { channel: string; user?: string; draft?: string }
    // the console binds the slot only when it can copy the draft's connection file
    state.bound = body.draft !== undefined
    return json(route, {
      ...base, channel: body.channel, production: false, ...(body.user ? { user: body.user } : {}), secrets: { password: `dbdatakit:runner:dev-login:sales-dw.${body.channel}.warehouse:password` },
      bound: state.bound, copy: `sales-dw-warehouse-${body.channel}`, steps: [],
    })
  })
  await page.route('**/api/workspace-catalog/**', (route) => {
    const url = new URL(route.request().url())
    sent.push({ path: url.pathname, body: route.request().postData() ?? '' })
    if (url.pathname.endsWith('/channels')) {
      return json(route, {
        ...base, default: state.bound ? 'dev' : null,
        channels: [{ channel: 'dev', production: false, bound: state.bound, bound_to: state.bound ? 'warehouse_dev' : null }, { channel: 'prod', production: true, bound: false, bound_to: null }],
        ...(state.bound ? {} : { note: "No channel of sales-dw on plant binds the slot 'warehouse'. An administrator binds it on the Runner's Packages page (Bindings), to a connection the Runner has, in the channel to read from." }),
      })
    }
    if (url.pathname.endsWith('/read')) {
      return json(route, { ...base, state: 'done', run_id: 'run-1', link: `/servers/${RUNNER}/runs?run=run-1`, channel: 'dev', production: false, cache: shape('2026-11-24T09:00:00Z'), tables: 1, columns: 2 })
    }
    if (url.pathname.endsWith('/binding-test')) {
      return json(route, {
        ...base, channel: 'dev', production: false, role: 'default', ok: true, step: 'connect', connected: true, bound_to: 'warehouse_dev',
        message: "The slot 'warehouse' opened a connection through 'warehouse_dev'.",
        steps: ['binding', 'connection', 'login', 'connect'].map((step) => ({ step, state: 'passed' })),
      })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return { state, sent }
}

test('a build draft with no binding offers Set up for development, which binds dev; Refresh then fills the Catalog tab and Test shows its steps', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Authoring Reads', `authoring-reads-${Date.now()}@example.com`)
  const workspace = await mockWorkspace(page)
  const reads = await mockConsoleReads(page)

  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill('sales-dw')
  await page.getByTestId('ws-new-project-kind').selectOption('build')
  await page.getByTestId('ws-new-project-create').click()
  await expect(page.locator('[data-testid="ws-project"][data-project="sales-dw"]')).toBeVisible()
  await page.getByTestId('ws-new-name-sales-dw').fill('reads')
  await page.getByTestId('ws-new-create-sales-dw').click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-reads"]')
  await expect(listed).toBeVisible()
  // the draft's connection, as the person would have added it on the Files area
  workspace.files('sales-dw', 'reads')!.set('connections/warehouse.yaml', 'schema: connection/1\nname: warehouse\nengine: postgres\naddress: {host: dw.example.com, database: dw}\n')
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
  await page.getByTestId('wsp-nav-files').click()
  await page.getByTestId('wsp-tab-catalog').click()
  await expect(page.getByTestId('wsp-catalog-none')).toBeVisible()

  // no channel binds the slot: Refresh cannot read, and the picker offers Set up for development
  await expect(page.getByTestId('wsp-channel-note')).toContainText("binds the slot 'warehouse'")
  await expect(page.getByTestId('wsp-refresh-go')).toBeDisabled()
  await page.getByTestId('wsp-setup-open').click()
  await expect(page.getByTestId('wsp-setup-warning')).toContainText('It is not saved in the project, its history or the Workspace.')
  await page.getByTestId('wsp-setup-user').fill('dw_dev')
  await page.getByTestId('wsp-setup-password').fill(PASSWORD)
  await page.getByTestId('wsp-setup-save').click()
  // the console bound the slot in dev: asked again, the picker offers dev and Set up is no longer offered
  await expect(page.getByTestId('wsp-channel-picker')).toHaveAttribute('data-channel', 'dev')
  // the login went to the console's own route only, with the draft, and is not on the page
  const logins = reads.sent.filter((s) => s.body.includes(PASSWORD))
  expect(logins.map((s) => s.path)).toEqual(['/api/workspace-dev-logins/ws-1/sales-dw'])
  expect(JSON.parse(logins[0]!.body).draft).toMatch(/-reads$/)
  expect(await page.content()).not.toContain(PASSWORD)
  await expect(page.getByTestId('wsp-setup')).toHaveCount(0)

  // Refresh fills the Catalog tab
  await page.getByTestId('wsp-refresh-go').click()
  await expect(page.getByTestId('wsp-refresh-summary')).toHaveText('1 table added')
  await page.getByTestId('wsp-refresh-accept').click()
  await expect(page.getByTestId('wsp-catalog-unsaved')).toBeVisible()
  await expect(page.locator('[data-testid="wsp-catalog-table"][data-table="dw.public.orders"]')).toContainText('2 columns')

  // Test shows each step of the Runner's binding test
  await page.getByTestId('wsp-test-go').click()
  await expect(page.getByTestId('wsp-test-result')).toHaveAttribute('data-ok', 'true')
  await expect(page.getByTestId('wsp-test-message')).toHaveText("The slot 'warehouse' opened a connection through 'warehouse_dev'.")
  await expect(page.getByTestId('wsp-test-step')).toHaveCount(4)
  await expect(page.locator('[data-testid="wsp-test-step"][data-state="passed"]')).toHaveCount(4)
})
