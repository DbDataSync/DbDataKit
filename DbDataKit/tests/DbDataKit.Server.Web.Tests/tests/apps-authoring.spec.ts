import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import pg from 'pg'

// Plan 22a GP34: an app made from the template (`apps new`), built from the vendored package store (`apps build`, GP32 and GP33) and served by the apps host, whose screens are three view-spec/1 files drawn by the
// library's components: a grid that lists, a form that edits (a rule violation of the model is shown against its field) and a dashboard. Then the dashboard's spec is replaced by a page of code (a component
// registered in src/pages, the spec file deleted), the app is built and published again, and the other two screens are as they were. A real Gateway (identity home and apps host), the Store's own command line
// publishing the model `territories` to its group, a PostgreSQL (`DBDATAKIT_TEST_E2E_PG`, an Npgsql connection string), Bun and Node (the store is made from the web workspace's packages: `npm ci` in Shared/web).
//
// Ports (a port table: 8601 the identity home, 8602 the apps host, 8603 the management API). The story starts its own servers, so the config has no webServer for it; it needs no console and sorts anywhere.
// `DBDATAKIT_TEST_E2E_GATEWAY_DLL` and `DBDATAKIT_TEST_E2E_STORE_DLL` name builds that are not in the products' own bin folders; `DBDATAKIT_BUN` names Bun when it is not on the PATH.

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const gatewayDll = process.env.DBDATAKIT_TEST_E2E_GATEWAY_DLL ?? path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')
const storeDll = process.env.DBDATAKIT_TEST_E2E_STORE_DLL ?? path.join(repoRoot, 'DbDataStore/src/DbDataStore.Server/bin/Debug/net10.0/dbdatastore.dll')
const warehouse = process.env.DBDATAKIT_TEST_E2E_PG

const root = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-authoring')
const gatewayDirectory = path.join(root, 'gateway')
const storeDirectory = path.join(root, 'store')
const packageStore = path.join(root, 'package-store')
const source = path.join(root, 'sales-source')
const homePort = 8601
const appsPort = 8602
const managementPort = 8603
const homeUrl = `https://localhost:${homePort}`
const appsUrl = `https://localhost:${appsPort}`
const managementUrl = `https://localhost:${managementPort}`
const connectionVariable = 'DBDATAKIT_E2E_AUTHORING_MDM'

const running: ChildProcess[] = []

function start(args: string[], env: Record<string, string> = {}) {
  const child = spawn('dotnet', ['exec', ...args], { env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] })
  let output = ''
  child.stdout!.on('data', (chunk) => { output += chunk })
  child.stderr!.on('data', (chunk) => { output += chunk })
  running.push(child)
  return { child, output: () => output }
}

function answers(url: string) {
  return new Promise<boolean>((resolve) => {
    https.get(url, { rejectUnauthorized: false }, (response) => { response.resume(); resolve(response.statusCode === 200) }).on('error', () => resolve(false))
  })
}

async function waitFor(url: string, what: () => string) {
  for (let i = 0; i < 120; i++) {
    if (await answers(url)) return
    await new Promise((resolve) => setTimeout(resolve, 500))
  }
  throw new Error(`${url} did not answer. Output:\n${what()}`)
}

/** A command of a product's command line on its directory, as an operator runs it; the JSON document's data. */
function run(dll: string, directory: string, args: string[], env: Record<string, string> = {}): Record<string, any> {
  const text = execFileSync('dotnet', ['exec', dll, ...args, '--repo', directory, '--format', 'json'], { encoding: 'utf8', env: { ...process.env, ...env } })
  return JSON.parse(text).data
}

/** A command that takes no data directory (`apps new` makes a folder and nothing else). */
function runPlain(args: string[]): string {
  return execFileSync('dotnet', ['exec', gatewayDll, ...args], { encoding: 'utf8' })
}

function bunPath() {
  if (process.env.DBDATAKIT_BUN) return process.env.DBDATAKIT_BUN
  return execFileSync('which', ['bun'], { encoding: 'utf8' }).trim()
}

async function addAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
}

/** The Npgsql connection string of the CI as a node-postgres one. */
function toPg(npgsql: string) {
  const parts = Object.fromEntries(npgsql.split(';').filter(Boolean).map((p) => p.split('=') as [string, string]))
  return `postgres://${parts.Username}:${parts.Password}@${parts.Host}:${parts.Port ?? 5432}/${parts.Database}`
}

/** Builds the source into a folder (the apps build command, from the Gateway's own settings: the package store and the allow-list), publishes it and lets the host pick the version up. */
function buildAndPublish(out: string) {
  fs.rmSync(out, { recursive: true, force: true })
  const built = run(gatewayDll, gatewayDirectory, ['apps', 'build', '--src', source, '--out', out, '--bun', bunPath(), '--isolation', 'auto'])
  run(gatewayDll, gatewayDirectory, ['apps', 'publish', '--dir', out])
  return built
}

test.describe.configure({ mode: 'serial' })
test.setTimeout(240_000)

test.beforeAll(async () => {
  test.skip(!warehouse, 'set DBDATAKIT_TEST_E2E_PG to run the app authoring story')
  fs.rmSync(root, { recursive: true, force: true })
  for (const directory of [gatewayDirectory, storeDirectory]) fs.mkdirSync(directory, { recursive: true })

  // the vendored package store, made from the web workspace's installed packages
  execFileSync('node', ['app-store/build.mjs', '--out', packageStore], { cwd: path.join(repoRoot, 'Shared/web'), stdio: 'inherit' })

  // the Store: a model on the PostgreSQL, published to the Gateway's group below
  const client = new pg.Client({ connectionString: toPg(warehouse!) })
  await client.connect()
  await client.query('DROP SCHEMA IF EXISTS territories CASCADE')
  await client.end()
  fs.mkdirSync(path.join(storeDirectory, 'connections'), { recursive: true })
  fs.writeFileSync(path.join(storeDirectory, 'connections', 'mdm.yaml'), `schema: connection/1\nname: mdm\nengine: postgres\nconnection_string_secret: 'env:${connectionVariable}'\ntls:\n  encrypt: false\n`)
  // the template's model, as the app template's manifest names it
  const modelFile = path.join(repoRoot, 'DbDataGateway/templates/apps/territories.yaml')
  run(storeDll, storeDirectory, ['model', 'put', '--file', modelFile])

  // the Gateway: the identity home and the apps host, the apps host's client registered at the home, and the app build's settings (the store, no further packages)
  fs.mkdirSync(path.join(gatewayDirectory, 'config'), { recursive: true })
  const settings = path.join(gatewayDirectory, 'config', 'server.yaml')
  fs.writeFileSync(settings, [
    'schemaVersion: 2', 'tls: { mode: selfSigned, hostnames: [localhost] }', 'modules: [identity, apps]', 'definitionPollSeconds: 1',
    `management: { enabled: true, address: 127.0.0.1, port: ${managementPort} }`,
    `identity: { address: 127.0.0.1, port: ${homePort}, publicUrl: '${homeUrl}' }`,
    `apps: { address: 127.0.0.1, port: ${appsPort}, home: { clientId: '__CLIENT__' }, packageStore: '${packageStore}', allowedPackages: [] }`, '',
  ].join('\n'))
  const registered = run(gatewayDll, gatewayDirectory, ['identity', 'client-add', '--name', 'Apps host', '--public', '--redirect', `${appsUrl}/apps/-/callback`])
  fs.writeFileSync(settings, fs.readFileSync(settings, 'utf8').replace('__CLIENT__', registered.client_id))

  // the app: made from the template, built from the store and published as the instance `sales` over the Store's default copy of the model
  runPlain(['apps', 'new', '--dir', source, '--name', 'sales'])
  buildAndPublish(path.join(root, 'sales-built-1'))
  run(gatewayDll, gatewayDirectory, ['apps', 'instance-save', '--name', 'sales', '--app', 'sales', '--bind', 'territories=default'])

  const gateway = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`])
  await waitFor(`${homeUrl}/health/live`, gateway.output)
  await waitFor(`${appsUrl}/health/live`, gateway.output)

  // the Store publishes the model to the group
  const code = /Pairing code: (\S+)/.exec(execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--repo', gatewayDirectory, '--max-role', 'publisher'], { encoding: 'utf8' }))![1]
  run(storeDll, storeDirectory, ['proxy', 'add', 'edge', '--url', managementUrl, '--code', code])
  run(storeDll, storeDirectory, ['model', 'publish', 'territories', '--proxy', 'edge'], { [connectionVariable]: warehouse! })
  await new Promise((resolve) => setTimeout(resolve, 3000))   // the instance reads the group's definitions every second
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

test('an app of three spec files lists, edits with a rule violation shown against its field and shows a dashboard; one spec replaced by a page of code leaves the others', async ({ page, context }) => {
  await addAuthenticator(context, page)

  // Ada enrols at the home with the first-run invitation and is given the author role on the app
  const firstRun = fs.readFileSync(path.join(gatewayDirectory, 'identity', 'FIRST-RUN.txt'), 'utf8')
  await page.goto(`${homeUrl}/invite#${firstRun.split('/invite#')[1].split(/\s/)[0]}`)
  await page.getByTestId('identity-invite-name').fill('Ada Lovelace')
  await page.getByTestId('identity-invite-email').fill('ada@example.com')
  await page.getByTestId('identity-invite-enrol').click()
  await expect(page.getByTestId('identity-invite-done')).toHaveText('Signed in as Ada Lovelace.')
  const ada = run(gatewayDll, gatewayDirectory, ['identity', 'user-list']).users.find((u: any) => u.name === 'Ada Lovelace')
  run(gatewayDll, gatewayDirectory, ['identity', 'assign', '--user', ada.id, '--app', 'sales', '--roles', 'author'])
  await context.clearCookies({ domain: 'localhost' })
  await page.goto(`${homeUrl}/identity/sign-in`)
  await page.getByTestId('identity-sign-in-passkey').click()
  await expect(page.getByTestId('identity-signed-in')).toHaveText('Signed in as Ada Lovelace.')

  // three territories, written through the host's data route as the shell does (a token from the session)
  await page.goto(`${appsUrl}/apps/sales/`)
  const seeded = await page.evaluate(async () => {
    const t = await fetch('/apps/sales/_token', { method: 'POST', credentials: 'same-origin', headers: { 'X-DbData-Shell': '1' } })
    const headers = { Authorization: 'Bearer ' + (await t.json()).token, 'Content-Type': 'application/json' }
    const changes = [['NA', 'North America', 100], ['EU', 'Europe', 50], ['AP', 'Asia Pacific', 25]].map(([code, name, quota]) => ({ action: 'insert', entity: 'territory', values: { code, name, quota } }))
    return (await fetch('/apps/sales/_data/api/v1/models/territories/changes', { method: 'POST', headers, body: JSON.stringify({ changes }) })).status
  })
  expect(seeded).toBe(200)
  await page.goto(`${appsUrl}/apps/sales/`)
  const app = page.frameLocator('#app-frame')

  // the app is the three spec files: the menu lists the two that need no parameter (the form is reached by a link), and the first is the home
  const menu = app.locator('nav.va-menu a')
  await expect(menu).toHaveText(['Territories', 'Overview'])
  await expect(app.locator('.va-title')).toHaveText('Territories')

  // 1. the grid lists the three rows, a row has its Edit link
  await expect(app.getByTestId('va-grid').locator('tbody tr')).toHaveCount(3)
  await expect(app.getByTestId('va-grid')).toContainText('North America')
  await expect(app.getByTestId('va-grid')).toContainText('Europe')
  expect(await app.locator('[data-testid=code-report]').count()).toBe(0)

  // 2. the form edits a row; a quota under the model's minimum is refused by the Store and the sentence is shown against the field
  await app.getByTestId('va-grid').locator('tbody tr', { hasText: 'North America' }).getByRole('link', { name: 'Edit' }).click()
  await expect(app.locator('.va-title')).toHaveText('Edit a territory')
  await expect(app.getByTestId('sui-field-quota')).toHaveValue('100')
  await app.getByTestId('sui-field-quota').fill('-5')
  await app.getByRole('button', { name: 'Save' }).click()
  await expect(app.getByTestId('sui-problem-quota')).toBeVisible()
  await expect(app.getByTestId('sui-problem-quota')).not.toHaveText('')
  await expect(app.locator('.va-saved')).toHaveCount(0)
  await app.getByTestId('sui-field-quota').fill('120')
  await app.getByRole('button', { name: 'Save' }).click()
  await expect(app.locator('.va-saved')).toHaveText('Saved.')
  await expect(app.getByTestId('sui-problem-quota')).toHaveCount(0)
  await app.getByRole('link', { name: 'Back to the list' }).click()
  await expect(app.getByTestId('va-grid').locator('tbody tr', { hasText: 'North America' })).toContainText('120')

  // 3. the dashboard counts and totals what the model holds now (3 territories, 120 + 50 + 25)
  await menu.nth(1).click()
  await expect(app.locator('.va-title')).toHaveText('Overview')
  await expect(app.getByTestId('va-metric-0')).toContainText('3')
  await expect(app.getByTestId('va-metric-1')).toContainText('195')
  await expect(app.getByTestId('va-dashboard').locator('.va-w-table')).toContainText('North America')

  // the app writes a page of code to replace the dashboard: a component, registered in src/pages, and the spec file and its line in the manifest removed. The others stay as they were.
  fs.rmSync(path.join(source, 'views', 'dashboard.yaml'))
  const manifestFile = path.join(source, 'app.yaml')
  fs.writeFileSync(manifestFile, fs.readFileSync(manifestFile, 'utf8').replace('views: [views/territories.yaml, views/dashboard.yaml, views/edit.yaml]', 'views: [views/territories.yaml, views/edit.yaml]'))
  fs.writeFileSync(path.join(source, 'src', 'pages', 'QuotaReport.tsx'), [
    "import { loadAll, type PageProps } from '@dbdatakit/view-app'", "import { useEffect, useState } from 'react'", '',
    'export function QuotaReport({ client }: PageProps) {',
    '  const [text, setText] = useState(\'counting\')',
    "  useEffect(() => { loadAll(client, 'territories', 'territory').then(({ rows }) => setText(`${rows.length} territories, total quota ${rows.reduce((sum, r) => sum + Number(r.values.quota ?? 0), 0)}`)) }, [client])",
    '  return <p data-testid="code-report">{text}</p>', '}', '',
  ].join('\n'))
  fs.writeFileSync(path.join(source, 'src', 'pages', 'index.ts'), [
    "import type { CodePages } from '@dbdatakit/view-app'", "import { QuotaReport } from './QuotaReport'", '',
    "export const pages: CodePages = { 'pages/quota-report': { title: 'Quota report (code)', component: QuotaReport } }", '',
  ].join('\n'))
  const built = buildAndPublish(path.join(root, 'sales-built-2'))
  expect(built.files).toBeGreaterThan(0)
  await new Promise((resolve) => setTimeout(resolve, 3000))   // the host reads new versions every second

  await page.goto(`${appsUrl}/apps/sales/`)
  await expect(menu).toHaveText(['Territories', 'Quota report (code)'])
  // the page of code runs on the same client and token as the specs
  await menu.nth(1).click()
  await expect(app.locator('.va-title')).toHaveText('Quota report (code)')
  await expect(app.getByTestId('code-report')).toHaveText('3 territories, total quota 195')
  await expect(app.getByTestId('va-dashboard')).toHaveCount(0)

  // the list and the edit form are as they were: the changed row is listed and can be edited again
  await menu.nth(0).click()
  await expect(app.getByTestId('va-grid').locator('tbody tr')).toHaveCount(3)
  await app.getByTestId('va-grid').locator('tbody tr', { hasText: 'Europe' }).getByRole('link', { name: 'Edit' }).click()
  await app.getByTestId('sui-field-quota').fill('-1')
  await app.getByRole('button', { name: 'Save' }).click()
  await expect(app.getByTestId('sui-problem-quota')).toBeVisible()
  await app.getByTestId('sui-field-quota').fill('60')
  await app.getByRole('button', { name: 'Save' }).click()
  await expect(app.locator('.va-saved')).toHaveText('Saved.')
  await menu.nth(1).click()
  await expect(app.getByTestId('code-report')).toHaveText('3 territories, total quota 205')
})
