import { expect, test, type Page, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'
import { SERVER, mockWorkspace } from './workspace-fake'

// Refresh of a connection's shape from a Runner channel on the Catalog tab of a sync draft, through the console's catalog read, against a Workspace server
// mocked in the page (`workspace-fake.ts`) and a fake of the console's read (`/api/workspace-catalog/...`, below): the real route is tested with real servers
// in `DbDataKit.Server.Servers.Tests` (`CatalogReadFromRunnerTests`). It signs in a new administrator, so its name sorts after `first-run` and
// `identity-home` (ci-lessons).
test.setTimeout(120_000)

const RUNNER = 'runner-eu-1'

/** The shape the fake Runner reads of `source-db`: `dbo.Orders`, with the column `segment` once `withSegment` is set. */
function shape(channel: string, at: string, withSegment: boolean) {
  const columns = ["          - {name: 'id', type: 'int', nullable: false, key: true}", "          - {name: 'total', type: 'decimal(12,2)', nullable: true}"]
  if (withSegment) columns.push("          - {name: 'segment', type: 'nvarchar(20)', nullable: true}")
  return [
    'schema: catalog-cache/2', `generated_at: '${at}'`, 'connections:', "  'source-db':", "    engine: 'sqlserver'",
    `    read: {server: 'runners-eu', product: 'runner', channel: '${channel}', at: '${at}'}`,
    '    databases:', "      - {name: 'App', schemas: ['dbo', 'staging']}", '    tables:',
    "      - database: 'App'", "        schema: 'dbo'", "        name: 'Orders'", '        kind: table', '        columns:', ...columns, '',
  ].join('\n')
}

/** The console's catalog read, faked in the page: the channels of the package on the Runner, and a read that answers at once or after a follow. */
async function mockCatalogRead(page: Page) {
  const reads: Record<string, unknown>[] = []
  let withSegment = false
  let runs = 0
  const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [
      { id: SERVER, name: 'Workspace server', url: 'https://workspace.invalid', product: 'dbdataworkspace', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null },
      { id: RUNNER, name: 'runners-eu', url: 'https://runner.invalid', product: 'dbdatarunner', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null },
    ])
    : route.fallback())
  await page.route('**/api/workspace-catalog/**', async (route) => {
    const url = new URL(route.request().url())
    const base = { runner: RUNNER, runner_name: 'runners-eu', connection: 'source-db' }
    if (url.pathname.endsWith('/channels')) {
      return json(route, {
        ...base, package: 'plant', slot: 'source_db', default: 'dev',
        channels: [{ channel: 'dev', production: false, bound: true, bound_to: 'source_dev' }, { channel: 'prod', production: true, bound: true, bound_to: 'source' }],
      })
    }
    if (url.pathname.endsWith('/read') && route.request().method() === 'POST') {
      const body = route.request().postDataJSON() as Record<string, unknown>
      reads.push(body)
      const run = `run-${++runs}`
      const link = `/servers/${RUNNER}/runs?run=${run}`
      // the first read takes longer than the console waits: the screen follows the run
      if (runs === 1) return json(route, { ...base, state: 'running', run_id: run, link, channel: body.channel, production: false }, 202)
      return json(route, { ...base, state: 'done', run_id: run, link, channel: body.channel, production: false, cache: shape(String(body.channel), '2026-11-22T10:05:00Z', withSegment), tables: 1, columns: 3 })
    }
    const followed = /\/read\/(run-\d+)$/.exec(url.pathname)
    if (followed) {
      const channel = url.searchParams.get('channel') ?? 'dev'
      return json(route, { ...base, state: 'done', run_id: followed[1], link: `/servers/${RUNNER}/runs?run=${followed[1]}`, channel, production: false, cache: shape(channel, '2026-11-22T10:00:00Z', withSegment), tables: 1, columns: 2 })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return { reads, addSegment: () => { withSegment = true } }
}

async function makeDraft(page: Page, id: string, workspace: string) {
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill(id)
  await page.getByTestId('ws-new-project-kind').selectOption('sync')
  await page.getByTestId('ws-new-project-create').click()
  await expect(page.locator(`[data-testid="ws-project"][data-project="${id}"]`)).toBeVisible()
  await page.getByTestId(`ws-new-name-${id}`).fill(workspace)
  await page.getByTestId(`ws-new-create-${id}`).click()
  const listed = page.locator(`[data-testid="ws-workspace"][data-workspace$="-${workspace}"]`)
  await expect(listed).toBeVisible()
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
}

test('Refresh of a table on the Catalog tab shows "1 column added", and accepted the cache is an unsaved edit recorded as read from dev', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Catalog Refresh', `catalog-refresh-${Date.now()}@example.com`)
  const workspace = await mockWorkspace(page)
  const catalog = await mockCatalogRead(page)
  await makeDraft(page, 'plant', 'catalog-edit')
  await page.getByTestId('wsp-nav-files').click()
  await page.getByTestId('wsp-tab-catalog').click()
  await expect(page.getByTestId('wsp-catalog-none')).toBeVisible()

  // the picker offers dev, the first channel that binds the slot and is not production; prod is offered with its warning
  const picker = page.getByTestId('wsp-channel-picker')
  await expect(picker).toHaveAttribute('data-channel', 'dev')
  await page.getByTestId('wsp-channel-channel').selectOption('prod')
  await expect(page.getByTestId('wsp-channel-production')).toContainText('This reads production')
  await expect(page.getByTestId('wsp-refresh-go')).toBeEnabled()
  await page.getByTestId('wsp-channel-channel').selectOption('dev')

  // the whole connection, the first time: the read takes longer than the console waits, so the screen shows the run's link and follows it
  await page.getByTestId('wsp-runner-refresh-connection').selectOption('source-db')
  await page.getByTestId('wsp-refresh-go').click()
  await expect(page.getByTestId('wsp-refresh-summary')).toHaveText('1 table added')
  await page.getByTestId('wsp-refresh-accept').click()
  await expect(page.getByTestId('wsp-catalog-unsaved')).toBeVisible()
  await page.getByTestId('wsp-save-all').click()
  await expect(page.getByTestId('wsp-catalog-unsaved')).toHaveCount(0)
  expect(workspace.files('plant', 'catalog-edit')?.get('catalog-cache.yaml')).toContain("'Orders'")

  // a DBA adds a column; Refresh of that one table shows the difference before anything is replaced
  catalog.addSegment()
  await page.getByTestId('wsp-runner-refresh-table').selectOption('dbo.Orders')
  await page.getByTestId('wsp-refresh-go').click()
  await expect(page.getByTestId('wsp-refresh-summary')).toHaveText('1 column added')
  await expect(page.locator('[data-testid="wsp-refresh-change"][data-change="column-added"]')).toContainText('App.dbo.Orders.segment')
  expect(catalog.reads.at(-1)).toEqual({ runner: RUNNER, channel: 'dev', connection: 'source-db', schema: 'dbo', table: 'Orders' })
  await page.getByTestId('wsp-refresh-accept').click()

  // accepted: an unsaved edit of the draft, recorded as read from dev; the server's file has not changed
  await expect(page.getByTestId('wsp-catalog-unsaved')).toBeVisible()
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '1')
  await expect(page.locator('[data-testid="wsp-catalog-connection"][data-connection="source-db"] [data-testid="wsp-catalog-read"]')).toContainText('read from runners-eu (runner) in dev at 2026-11-22T10:05:00Z')
  await expect(page.locator('[data-testid="wsp-catalog-table"][data-table="App.dbo.Orders"]')).toContainText('3 columns')
  expect(workspace.files('plant', 'catalog-edit')?.get('catalog-cache.yaml')).not.toContain('segment')
})
