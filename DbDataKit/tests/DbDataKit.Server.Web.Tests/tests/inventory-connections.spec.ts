import { expect, test, type APIRequestContext, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { inventoryBuild, inventorySync, scratchDirectory } from '../playwright.config'

// The file name matters: the specs run one at a time in the order of their names, and first-run.spec.ts and identity-home.spec.ts need a console that has no user yet. This story invites its own administrator, so it must sort after them (as monitoring.spec.ts does).
// Plan 28c, E23 (ED17, the condition of ED16): the Connections page shows one table over the paired servers with where each connection goes, the reference of its secret and the processes that use it. A Build server hosts a project with a connection
// file and a scheduled plan; a Sync server is given a connection and a replication that uses it. The table lists both with their users, the filters answer "where is this connection used" and "what does this task connect to", and removing the Sync
// connection while the replication uses it is refused naming the replication. No password reaches the page. (A runner's part, the task and instances that use a bound slot, is covered by DbDataRunner.Tests' ConnectionInventoryTests and the web unit tests.)
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')
const buildName = 'Inventory build'
const syncName = 'Inventory sync'
const password = 'inventory-password-91c3'

async function signInAsAdmin(page: Page, context: BrowserContext, baseURL: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `inventory-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Inventory Tester')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

async function pair(page: Page, url: string, codeFile: string, name: string) {
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(url)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, codeFile), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill(name)
  await page.getByTestId('pair-button').click()
  await expect(page.getByTestId(`instance-row-${name}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
}

async function instanceId(request: APIRequestContext, name: string): Promise<string> {
  const list = await (await request.get('/api/instances')).json() as { id: string; name: string }[]
  return list.find((i) => i.name === name)!.id
}

const row = (page: Page, server: string, name: string) => page.locator(`[data-testid="inventory-row"][data-server="${server}"][data-name="${name}"]`)

test('the Connections page shows where each connection is used, answers the two questions and refuses to remove a used one', async ({ page, context, baseURL }) => {
  test.setTimeout(180_000)
  await signInAsAdmin(page, context, baseURL!)
  await pair(page, `https://localhost:${inventoryBuild.port}`, 'inventory-build-pairing-code.txt', buildName)
  await pair(page, `https://localhost:${inventorySync.port}`, 'inventory-sync-pairing-code.txt', syncName)
  const sync = await instanceId(page.request, syncName)

  // a connection with a password (stored by the Sync server, never shown back) and a replication that reads from it
  const put = async (url: string, body: unknown) => { const r = await page.request.put(`/relay/${sync}/api${url}`, { data: body }); expect(r.ok(), `${url}: ${r.status()} ${await r.text()}`).toBe(true) }
  await put('/connections/erp', { name: 'erp', driverType: 'MsSql', host: 'sql01', port: 1433, database: 'Erp', authMode: 'SqlAuth', userId: 'reader', password })
  await put('/replications/erp-orders', {
    name: 'erp-orders', enabled: false,
    endpoints: { source: { connectionName: 'erp', database: 'Erp' } },
    scheduling: { mode: 'Continuous', frequencySeconds: 3600 },
    changeProcessing: { reader: { kind: 'MsSqlChangeTracking', options: {} }, cache: { kind: 'MsSqlStagingTable' }, writer: { kind: 'MsSqlMerge' } },
  })

  await page.goto('/connections')
  await expect(page.getByTestId('connection-inventory')).toBeVisible()

  // Build: the connection file's engine, the reference of the read login, the server, the scheduled plan and the flows that write
  const build = row(page, buildName, inventoryBuild.connection)
  await expect(build).toBeVisible({ timeout: 30_000 })
  await expect(build.getByTestId('inventory-secret')).toHaveText('secret:dbdata:build:connection:sqlserver:read')
  await expect(build.locator(`[data-testid="inventory-use"][data-process="plan"]`)).toContainText(`${inventoryBuild.project}/${inventoryBuild.connection}/${inventoryBuild.plan}`)
  await expect(build.locator('[data-testid="inventory-use"][data-process="flow"]').first()).toContainText('write login')
  await expect(build.locator('[data-testid="inventory-use"][data-process="server"]')).toContainText('read login')

  // Sync: the address, the reference, the server and the replication
  const erp = row(page, syncName, 'erp')
  await expect(erp.getByTestId('inventory-address')).toHaveText('sql01:1433/Erp')
  await expect(erp.getByTestId('inventory-secret')).toContainText('secret:')
  await expect(erp.locator('[data-testid="inventory-use"][data-process="replication"]')).toContainText('replication erp-orders, run login')

  // "where is this connection used?" and "what does this task connect to?"
  await page.getByTestId('inventory-filter-connection').fill('erp')
  await expect(page.getByTestId('inventory-row')).toHaveCount(1)
  await page.getByTestId('inventory-filter-connection').fill('')
  await page.getByTestId('inventory-filter-user').fill(`${inventoryBuild.plan}`)
  await expect(page.getByTestId('inventory-row')).toHaveCount(1)
  await expect(page.getByTestId('inventory-row')).toHaveAttribute('data-name', inventoryBuild.connection)
  await page.getByTestId('inventory-filter-user').fill('erp-orders')
  await expect(page.getByTestId('inventory-row')).toHaveAttribute('data-name', 'erp')
  await page.getByTestId('inventory-filter-user').fill('')

  // removing a connection a replication uses is refused, naming the replication; it is still there
  await erp.getByTestId('inventory-remove').click()
  await expect(page.getByTestId('inventory-refused')).toContainText('replication erp-orders')
  await expect(erp).toBeVisible()

  // no secret value reached the page or any answer the console relayed
  await expect(page.locator('body')).not.toContainText(password)
  const answer = await (await page.request.get(`/relay/${sync}/api/v1/connections`)).text()
  expect(answer).not.toContain(password)
})
