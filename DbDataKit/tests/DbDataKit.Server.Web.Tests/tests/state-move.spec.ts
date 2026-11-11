import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import pg from 'pg'

// Plan 29b, C06: a real DbDataStore server is paired with the console, a PostgreSQL state connection is added and tested, the server is put into maintenance and moved onto it through the wizard, and after this test restarts the server
// the panel shows the new engine. Needs a PostgreSQL to make a fresh database on (`DBDATAKIT_TEST_E2E_PG`, an Npgsql connection string to any database of it with the right to create one; CI starts one). The Store is started and restarted by the test itself
// (a restart is what the move asks the operator for), in a directory of its own. The console is the one of playwright.config.ts (its directory is the same temporary folder).
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')
const storeDll = path.join(repoRoot, 'DbDataStore/src/DbDataStore.Server/bin/Debug/net10.0/dbdatastore.dll')
const scratchDirectory = process.env.DBDATAKIT_E2E_CONSOLE_DIR ?? path.join(os.tmpdir(), 'dbdatakit-console-e2e')
const storeDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-state-store')
const storePort = Number(process.env.DBDATAKIT_E2E_STATE_STORE_PORT ?? 8495)
const storeUrl = `https://localhost:${storePort}`
const admin = process.env.DBDATAKIT_TEST_E2E_PG
const storeName = 'Store state e2e'
const connectionName = 'e2e-state-pg'

/**
 * The password typed into the form is never shown again anywhere on the page. A short password (the CI database's is `e2e`) is also ordinary text (`e2e-state-pg` is the connection's own name), so the check
 * removes the connection's name first and, for a password too short to tell from other words, leaves the page check out: the settings file and the stored reference are checked for it too.
 */
async function expectPasswordNotShown(page: import('@playwright/test').Page, password: string) {
  if (password.length < 8) return
  const text = (await page.locator('body').innerText()).split(connectionName).join('')
  expect(text).not.toContain(password)
}

const parts = (connection: string) => Object.fromEntries(connection.split(';').filter(Boolean).map((p) => [p.slice(0, p.indexOf('=')).trim().toLowerCase(), p.slice(p.indexOf('=') + 1)]))

function runStore(...args: string[]) {
  return execFileSync('dotnet', ['exec', storeDll, ...args, '--repo', storeDirectory], { encoding: 'utf8' })
}

let store: ChildProcess | null = null
async function startStore() {
  store = spawn('dotnet', ['exec', storeDll, 'server', 'run', '--repo', storeDirectory, '--listen', storeUrl], { stdio: 'ignore' })
  const give = Date.now() + 60_000
  while (Date.now() < give) {
    // the server's own certificate is self-signed: this probe accepts it, as the console's tests do
    const up = await new Promise<boolean>((resolve) => {
      const request = https.get(`${storeUrl}/health/live`, { rejectUnauthorized: false, timeout: 2000 }, (response) => { response.resume(); resolve(response.statusCode === 200) })
      request.on('error', () => resolve(false))
      request.on('timeout', () => { request.destroy(); resolve(false) })
    })
    if (up) return
    await new Promise((resolve) => setTimeout(resolve, 500))
  }
  throw new Error('The Store server did not start.')
}

async function stopStore() {
  if (!store) return
  const exited = new Promise((resolve) => store!.once('exit', resolve))
  store.kill('SIGTERM')
  await Promise.race([exited, new Promise((resolve) => setTimeout(resolve, 15_000))])
  store.kill('SIGKILL')
  store = null
}

async function signInNewAdmin(page: Page, context: BrowserContext, baseURL: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `state-${Date.now()}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('State Tester')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

test.describe.serial('moving a server\'s state from the console', () => {
  test.skip(!admin, 'set DBDATAKIT_TEST_E2E_PG to a PostgreSQL connection string that may create a database')
  test.setTimeout(240_000)

  const database = `c06_e2e_${Math.random().toString(36).slice(2, 10)}`
  let password = ''
  let connectionString = ''

  test.beforeAll(async () => {
    const a = parts(admin!)
    password = a.password
    connectionString = `Host=${a.host};Port=${a.port ?? 5432};Database=${database};Username=${a.username}`
    const client = new pg.Client({ host: a.host, port: Number(a.port ?? 5432), user: a.username, password: a.password, database: a.database })
    await client.connect()
    await client.query(`CREATE DATABASE ${database}`)
    await client.end()
    fs.rmSync(storeDirectory, { recursive: true, force: true })
    fs.mkdirSync(storeDirectory, { recursive: true })
    await startStore()
  })

  test.afterAll(async () => {
    await stopStore()
    const a = parts(admin!)
    const client = new pg.Client({ host: a.host, port: Number(a.port ?? 5432), user: a.username, password: a.password, database: a.database })
    await client.connect()
    await client.query(`DROP DATABASE IF EXISTS ${database} WITH (FORCE)`)
    await client.end()
  })

  test('add a PostgreSQL state connection, test it, move the Store onto it and see the new engine after its restart', async ({ page, context, baseURL }) => {
    await signInNewAdmin(page, context, baseURL!)

    // pair the Store: a code from its own command line, the pairing form of the Servers page
    const code = /Pairing code: (\S+)/.exec(runStore('server', 'pair', '--max-role', 'admin'))![1]
    await page.goto('/instances')
    await page.getByTestId('pair-url-input').fill(storeUrl)
    await page.getByTestId('pair-code-input').fill(code)
    await page.getByTestId('pair-name-input').fill(storeName)
    await page.getByTestId('pair-button').click()
    await expect(page.getByTestId(`instance-row-${storeName}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })

    // add the state connection: the password is typed once and never shown again
    await page.goto('/connections')
    await page.getByTestId('state-connection-add').click()
    await page.getByTestId('state-connection-name').fill(connectionName)
    await page.getByTestId('state-connection-engine').selectOption('postgres')
    await page.getByTestId('state-connection-string').fill(connectionString)
    await page.getByTestId('state-connection-password').fill(password)
    await page.getByTestId('state-connection-save').click()
    const row = page.getByTestId('state-connection-row').filter({ has: page.getByText(connectionName, { exact: true }) })
    await expect(row).toBeVisible()
    await expect(row).toContainText(`secret:dbdatakit:server:state:${connectionName}`)
    await expectPasswordNotShown(page, password)

    // test: one sentence
    await row.getByTestId('state-connection-test').click()
    await expect(page.getByTestId('state-connection-test-result')).toContainText('database is empty')
    await expect(row.getByTestId('state-connection-used-by')).toHaveText('no server')

    // the server's panel: SQLite, not in maintenance
    await page.goto('/instances')
    await page.getByTestId(`instance-row-${storeName}`).getByTestId('instance-state-link').click()
    await expect(page.getByTestId('state-engine')).toHaveText('SQLite')
    await expect(page.getByTestId('maintenance-status')).toHaveAttribute('data-enabled', 'false')
    await expect(page.getByTestId('state-module').first()).toBeVisible()

    // put it into maintenance: it reports idle
    await page.getByTestId('maintenance-reason').fill('moving the state to PostgreSQL')
    await page.getByTestId('maintenance-on').click()
    await expect(page.getByTestId('maintenance-status')).toHaveAttribute('data-idle', 'true', { timeout: 30_000 })

    // the wizard: the server cannot yet resolve the password, and says which command sets it on that machine
    await page.getByTestId('move-target').selectOption(connectionName)
    await page.getByTestId('move-check').click()
    await expect(page.getByTestId('move-checks')).toContainText(`dbdatastore secret set dbdatakit:server:state:${connectionName}`)
    await expect(page.getByTestId('move-start')).toHaveCount(0)

    // the operator sets it there, then the checks pass
    execFileSync('dotnet', ['exec', storeDll, 'secret', 'set', `dbdatakit:server:state:${connectionName}`, '--stdin', '--repo', storeDirectory], { input: password + '\n', stdio: ['pipe', 'ignore', 'inherit'] })
    await page.getByTestId('move-check').click()
    await expect(page.getByTestId('move-start')).toBeVisible()
    await expect(page.getByTestId('move-check-item').filter({ hasText: /Not ready/ })).toHaveCount(0)

    // the move, with its steps
    await page.getByTestId('move-start').click()
    await expect(page.getByTestId('move-done')).toBeVisible({ timeout: 120_000 })
    await expect(page.getByTestId('move-restart')).toContainText('Restart the service')
    await expect(page.getByTestId('move-way-back')).toContainText('To go back')
    await expect(page.getByTestId('move-step-moving')).toHaveAttribute('data-status', 'done')
    await expectPasswordNotShown(page, password)

    // the settings file names PostgreSQL with the reference, never the password
    const settings = fs.readFileSync(path.join(storeDirectory, 'server.yaml'), 'utf8')
    expect(settings).toContain('postgres')
    expect(settings).toContain(`secret:dbdatakit:server:state:${connectionName}`)
    expect(settings).toMatch(/password=secret:/i)                      // the password is a reference
    if (password.length >= 8) expect(settings).not.toContain(password)  // a short one (CI's `e2e`) is also inside the database name

    // the operator restarts it: the panel shows the new engine and the server is out of maintenance
    await stopStore()
    await startStore()
    await page.reload()
    await expect(page.getByTestId('state-engine')).toHaveText('PostgreSQL', { timeout: 30_000 })
    await expect(page.getByTestId('state-connection')).toContainText(`secret:dbdatakit:server:state:${connectionName}`)
    await expect(page.getByTestId('maintenance-status')).toHaveAttribute('data-enabled', 'false')
    await expect(page.getByTestId('state-pending')).toHaveText('0')

    // and the connection's list now says who uses it, with the module versions found there
    await page.goto('/connections')
    const used = page.getByTestId('state-connection-row').filter({ has: page.getByText(connectionName, { exact: true }) })
    await expect(used.getByTestId('state-connection-used-by')).toHaveText(storeName)
    await expect(used.getByTestId('state-connection-modules')).toContainText(/\w+ \d+/)
  })
})
