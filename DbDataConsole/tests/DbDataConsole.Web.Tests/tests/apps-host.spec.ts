import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import http from 'node:http'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import pg from 'pg'

// Plan 26, AP05, and plan 20 O08c: a sandboxed code app in a real browser. A person signs in at the identity home, opens the app on the apps host, and the app (a document with an opaque origin) reads and writes rows of a
// master data model through the host: the shell hands it a token, the host checks it and answers from the model the Store published to the group, within the scope of the app's manifest. The app cannot read the host's
// page or its cookies. The Proxy (modules `identity` and `apps`, with its management API for the Store to publish to) is started here; the Store's server is never started: its command line publishes the model.
// Needs a PostgreSQL (`DBDATA_E2E_PG`, an Npgsql connection string) for the model's tables.

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const proxyDll = path.join(repoRoot, 'DbDataProxy/src/DbDataProxy.Server/bin/Debug/net10.0/dbdataproxy.dll')
const storeDll = path.join(repoRoot, 'DbDataStore/src/DbDataStore.Server/bin/Debug/net10.0/dbdatastore.dll')
const warehouse = process.env.DBDATA_E2E_PG

const proxyDirectory = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-proxy')
const storeDirectory = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-store')
const appDirectory = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-app')
const homePort = 8511
const appsPort = 8512
const managementPort = 8513
const homeUrl = `https://localhost:${homePort}`
const appsUrl = `https://localhost:${appsPort}`
const managementUrl = `https://localhost:${managementPort}`
const connectionVariable = 'DBDATAKIT_E2E_APPS_MDM'

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

async function addAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
}

// the origin of an organisation's portal: it may frame `acme`; another origin may not
const portalOrigin = 'http://localhost:8514'
const otherOrigin = 'http://localhost:8515'

// two portals that mount the same instances and look different (declared: data the shell renders, no script)
const portalYaml = (name: string, title: string, accent: string, menu: string, footer: boolean, mounts: string) => [
  'schemaVersion: 1', `name: ${name}`, `title: ${title}`, 'version: 1.0.0', 'kind: portal', 'portal:',
  `  identity: {title: ${title}, footer: "${title} footer"}`, `  theme: {light: {sui-accent: "${accent}"}}`, `  layout: {menu: ${menu}, footer: ${footer}, header: [person]}`,
  mounts, '',
].join('\n')

const manifest = [
  'schemaVersion: 1', 'name: territory-notes', 'title: Territory notes', 'version: 1.0.0', 'entry: index.html', 'roles: [author]',
  'storage: {user: true, instance: true, quota_mb: 5}', 'models:', '  - model: territories', '    entities: {territory: [read, write]}', '',
].join('\n')

// The app: a page and a script (the policy allows no inline script). It asks the shell for a token, writes a row and reads the rows back through the host, and tries to reach what a sandbox must not.
const index = '<!doctype html><html><head><meta charset="utf-8"><title>Territory notes</title></head><body><h1>Territory notes</h1>' +
  '<button id="go">go</button><pre id="route">none</pre><pre id="theme">none</pre><pre id="storage">loading</pre><pre id="rows">loading</pre><pre id="parent-cookie">untested</pre><pre id="own-cookie">untested</pre><pre id="elsewhere">untested</pre><script src="app.js"></script></body></html>\n'
const script = `
const out = (id, text) => { document.getElementById(id).textContent = text }
const waiting = new Map()
window.addEventListener('message', (event) => {
  const m = event.data
  if (event.source !== window.parent || !m || m.dbdata !== 'app/1') return
  if (m.type === 'navigate') { out('route', m.path); return }
  if (m.type === 'theme') { out('theme', (m.tokens.light || {})['sui-accent'] || 'none'); return }
  if (!waiting.has(m.id)) return
  waiting.get(m.id)(m); waiting.delete(m.id)
})
const token = () => new Promise((resolve, reject) => {
  const id = String(Math.random())
  waiting.set(id, (m) => (m.type === 'token' ? resolve(m.token) : reject(new Error(m.message))))
  window.parent.postMessage({ dbdata: 'app/1', type: 'token.request', id }, '*')
})
document.getElementById('go').onclick = () => {
  window.parent.postMessage({ dbdata: 'app/1', type: 'route', path: '/territories/NA' }, '*')
  out('route', '/territories/NA')
}
// app storage goes through the shell too: a sandboxed document has no localStorage
const store = (body) => new Promise((resolve, reject) => {
  const id = String(Math.random())
  waiting.set(id, (m) => (m.type === 'storage.result' ? resolve(m.result) : reject(new Error(m.message))))
  window.parent.postMessage({ dbdata: 'app/1', type: 'storage', id, ...body }, '*')
})
async function storage() {
  try { localStorage.setItem('k', 'v'); out('storage', 'localStorage works') } catch (e) { /* expected: opaque origin */ }
  await store({ op: 'set', scope: 'user', key: 'theme', value: 'dark' })
  await store({ op: 'set', scope: 'instance', key: 'motd', value: { text: 'hi' } })
  const theme = await store({ op: 'get', scope: 'user', key: 'theme' })
  const motd = await store({ op: 'get', scope: 'instance', key: 'motd' })
  const big = await store({ op: 'set', scope: 'user', key: 'big', value: 'x'.repeat(300 * 1024) }).then(() => 'stored', (e) => 'refused')
  out('storage', 'theme ' + theme.value + '; motd ' + motd.value.text + '; big ' + big)
}
storage().catch((e) => out('storage', 'failed: ' + e.message))
const data = '/apps/acme/_data/api/v1/models/territories'
async function main() {
  const t = await token()
  const headers = { Authorization: 'Bearer ' + t, 'Content-Type': 'application/json' }
  const write = await fetch(data + '/changes', { method: 'POST', headers, body: JSON.stringify({ changes: [{ action: 'insert', entity: 'territory', values: { code: 'NA', name: 'North America', quota: 10 } }] }) })
  const rows = await (await fetch(data + '/entities/territory/rows', { headers })).json()
  out('rows', 'write ' + write.status + '; rows ' + rows.rows.map((r) => r.values.name).join(','))
  // another entity of the same model is outside the manifest's scope
  const other = await fetch(data + '/entities/region/rows', { headers })
  out('rows', document.getElementById('rows').textContent + '; region ' + other.status)
}
main().catch((e) => out('rows', 'failed: ' + e.message))
try { out('parent-cookie', 'read ' + window.parent.document.cookie) } catch (e) { out('parent-cookie', 'blocked ' + e.name) }
try { out('own-cookie', 'read ' + document.cookie) } catch (e) { out('own-cookie', 'blocked ' + e.name) }
fetch('https://example.com/', { mode: 'no-cors' }).then(() => out('elsewhere', 'reached'), () => out('elsewhere', 'blocked'))
`

test.describe.configure({ mode: 'serial' })

test.beforeAll(async () => {
  test.skip(!warehouse, 'set DBDATA_E2E_PG to run the apps host story')
  for (const directory of [proxyDirectory, storeDirectory, appDirectory]) {
    fs.rmSync(directory, { recursive: true, force: true })
    fs.mkdirSync(directory, { recursive: true })
  }

  // the Store: a model on the PostgreSQL, published to the Proxy's group below
  const client = new pg.Client({ connectionString: toPg(warehouse!) })
  await client.connect()
  await client.query('DROP SCHEMA IF EXISTS territories CASCADE')
  await client.end()
  fs.mkdirSync(path.join(storeDirectory, 'connections'), { recursive: true })
  fs.writeFileSync(path.join(storeDirectory, 'connections', 'mdm.yaml'), `schema: connection/1\nname: mdm\nengine: postgres\nconnection_string_secret: 'env:${connectionVariable}'\ntls:\n  encrypt: false\n`)
  const modelFile = path.join(storeDirectory, 'territories.yaml')
  fs.writeFileSync(modelFile, [
    'schemaVersion: 1', 'name: territories', 'storage: {connection: mdm}', 'entities:', '  territory:', '    key: [code]', '    attributes:',
    '      code: {type: VARCHAR(10), required: true}', '      name: {type: VARCHAR(100), required: true}', '      quota: {type: "DECIMAL(10,2)", min: 0}', '',
  ].join('\n'))
  const env = { [connectionVariable]: warehouse! }
  run(storeDll, storeDirectory, ['model', 'put', '--file', modelFile])

  // the Proxy: the identity home and the apps host, with the apps host's client registered at the home before it starts
  fs.mkdirSync(path.join(proxyDirectory, 'config'), { recursive: true })
  fs.writeFileSync(path.join(proxyDirectory, 'config', 'server.yaml'), [
    'schemaVersion: 2', 'tls: { mode: selfSigned, hostnames: [localhost] }', 'modules: [identity, apps]', 'definitionPollSeconds: 1',
    `management: { enabled: true, address: 127.0.0.1, port: ${managementPort} }`,
    `identity: { address: 127.0.0.1, port: ${homePort}, publicUrl: '${homeUrl}' }`,
    `apps: { address: 127.0.0.1, port: ${appsPort}, home: { clientId: '__CLIENT__' } }`, '',
  ].join('\n'))
  const registered = run(proxyDll, proxyDirectory, ['identity', 'client-add', '--name', 'Apps host', '--public', '--redirect', `${appsUrl}/apps/-/callback`])
  const file = path.join(proxyDirectory, 'config', 'server.yaml')
  fs.writeFileSync(file, fs.readFileSync(file, 'utf8').replace('__CLIENT__', registered.client_id))

  // the app, published and served as the instance `acme`, whose data is the Store's default copy of the model
  fs.writeFileSync(path.join(appDirectory, 'app.yaml'), manifest)
  fs.writeFileSync(path.join(appDirectory, 'index.html'), index)
  fs.writeFileSync(path.join(appDirectory, 'app.js'), script)
  run(proxyDll, proxyDirectory, ['apps', 'publish', '--dir', appDirectory])
  run(proxyDll, proxyDirectory, ['apps', 'instance-save', '--name', 'acme', '--app', 'territory-notes', '--bind', 'territories=default', '--setting', `embed.allowed_origins=${portalOrigin}`])
  // two portals over the same instances; `hidden` is mounted in the first and Ada has no role on it
  for (const [name, text] of [
    ['portal-alpha', portalYaml('portal-alpha', 'Alpha Co', '#ff0000', 'side', true, '  mounts: [{app: acme, path: /sales/}, {app: beta, path: /beta/}, {app: hidden, path: /hidden/}]\n  navigation: [{app: acme, label: Sales}, {app: beta, label: Beta}, {app: hidden, label: Hidden}]\n  slots: {aside: beta}')],
    ['portal-omega', portalYaml('portal-omega', 'Omega Ltd', '#0000ff', 'top', false, '  mounts: [{app: acme, path: /work/}]\n  navigation: [{app: acme, label: Work}]')],
  ]) {
    const directory = path.join(os.tmpdir(), `dbdata-e2e-${name}`)
    fs.rmSync(directory, { recursive: true, force: true })
    fs.mkdirSync(directory, { recursive: true })
    fs.writeFileSync(path.join(directory, 'app.yaml'), text)
    run(proxyDll, proxyDirectory, ['apps', 'publish', '--dir', directory])
    run(proxyDll, proxyDirectory, ['apps', 'instance-save', '--name', name.replace('portal-', ''), '--app', name])
  }
  // two more instances and a menu of three: Ada is given a role on beta but not on hidden
  for (const name of ['beta', 'hidden']) run(proxyDll, proxyDirectory, ['apps', 'instance-save', '--name', name, '--app', 'territory-notes', '--bind', 'territories=default'])
  run(proxyDll, proxyDirectory, ['apps', 'nav-save', '--name', 'main', '--title', 'Main', '--item', 'acme=Acme,beta=Beta,hidden=Hidden'])
  const proxy = start([proxyDll, 'server', 'run', `--Proxy:DataDirectory=${proxyDirectory}`])
  await waitFor(`${homeUrl}/health/live`, proxy.output)
  await waitFor(`${appsUrl}/health/live`, proxy.output)

  // the Store publishes the model to the group: its tables first, then the login, the data source and the model, which every instance of the group serves from then on
  const code = /Pairing code: (\S+)/.exec(execFileSync('dotnet', ['exec', proxyDll, 'server', 'pair', '--repo', proxyDirectory, '--max-role', 'publisher'], { encoding: 'utf8' }))![1]
  run(storeDll, storeDirectory, ['proxy', 'add', 'edge', '--url', managementUrl, '--code', code])
  run(storeDll, storeDirectory, ['model', 'publish', 'territories', '--proxy', 'edge'], env)
  await new Promise((resolve) => setTimeout(resolve, 3000))   // the instance reads the group's definitions every second
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

/** The Npgsql connection string of the CI as a node-postgres one. */
function toPg(npgsql: string) {
  const parts = Object.fromEntries(npgsql.split(';').filter(Boolean).map((p) => p.split('=') as [string, string]))
  return `postgres://${parts.Username}:${parts.Password}@${parts.Host}:${parts.Port ?? 5432}/${parts.Database}`
}

test('a person signs in and the sandboxed app reads and writes the published model through the host, and reaches nothing else', async ({ page, context }) => {
  await addAuthenticator(context, page)

  // Ada enrols at the home with the first-run invitation, and is given a role on the app
  const firstRun = fs.readFileSync(path.join(proxyDirectory, 'identity', 'FIRST-RUN.txt'), 'utf8')
  await page.goto(`${homeUrl}/invite#${firstRun.split('/invite#')[1].split(/\s/)[0]}`)
  await page.getByTestId('identity-invite-name').fill('Ada Lovelace')
  await page.getByTestId('identity-invite-email').fill('ada@example.com')
  await page.getByTestId('identity-invite-enrol').click()
  await expect(page.getByTestId('identity-invite-done')).toHaveText('Signed in as Ada Lovelace.')

  // without a role on the app the host refuses her
  await page.goto(`${appsUrl}/apps/acme/`)
  await expect(page.locator('body')).toContainText('gives you no role on this app')

  const ada = run(proxyDll, proxyDirectory, ['identity', 'user-list']).users.find((u: any) => u.name === 'Ada Lovelace')
  run(proxyDll, proxyDirectory, ['identity', 'assign', '--user', ada.id, '--app', 'acme', '--roles', 'author'])
  run(proxyDll, proxyDirectory, ['identity', 'assign', '--user', ada.id, '--app', 'beta', '--roles', 'author'])

  // a new sign-in at the apps host (the host reads her roles when she arrives); the home already knows her
  await context.clearCookies({ domain: 'localhost' })
  await page.goto(`${homeUrl}/identity/sign-in`)
  await page.getByTestId('identity-sign-in-passkey').click()
  await expect(page.getByTestId('identity-signed-in')).toHaveText('Signed in as Ada Lovelace.')
  await page.goto(`${appsUrl}/apps/acme/`)

  // the shell frames the app with the sandbox attribute and no allow-same-origin
  const frameElement = page.locator('#app-frame')
  await expect(frameElement).toHaveAttribute('sandbox', 'allow-scripts allow-forms allow-popups')
  const app = page.frameLocator('#app-frame')

  // the app wrote a row and read it back through the host, and its scope stops at the entity the manifest names
  await expect(app.locator('#rows')).toHaveText('write 200; rows North America; region 403')

  // it cannot read the host's page or cookies, has no cookies of its own to read, and cannot reach another host
  await expect(app.locator('#parent-cookie')).toHaveText(/^blocked /)
  await expect(app.locator('#own-cookie')).toHaveText(/^blocked /)
  await expect(app.locator('#elsewhere')).toHaveText('blocked')

  // storage through the shell: a user value and an instance value read back, and a value over the limit is refused
  await expect(app.locator('#storage')).toHaveText('theme dark; motd hi; big refused')

  // the shell's header is the host's: the person, and the menu with only the instances she may open
  await expect(page.locator('header .who')).toHaveText('Ada Lovelace')
  await expect(page.locator('header nav a')).toHaveText(['Acme', 'Beta'])
  await expect(page.locator('header nav a[aria-current=page]')).toHaveText('Acme')

  // the app reports a route and the address follows; the menu leads to another instance and the back button returns to the route
  await app.locator('#go').click()
  await expect(page).toHaveURL(`${appsUrl}/apps/acme/?launch#/territories/NA`)   // the sign-in returned to the launch address
  await page.locator('header nav a', { hasText: 'Beta' }).click()
  await expect(page).toHaveURL(`${appsUrl}/apps/beta/`)
  await page.goBack()
  await expect(page).toHaveURL(`${appsUrl}/apps/acme/?launch#/territories/NA`)
  await expect(page.frameLocator('#app-frame').locator('#route')).toHaveText('/territories/NA')

  // a pasted link opens the app at that route
  await page.goto(`${appsUrl}/apps/beta/`)
  await page.goto(`${appsUrl}/apps/acme/#/territories/NA`)
  await expect(page.frameLocator('#app-frame').locator('#route')).toHaveText('/territories/NA')

  // the catalog lists the instances she may open (not `hidden`), each with the address that launches it
  const catalog = await page.evaluate(async () => (await fetch('/apps/catalog')).json())
  expect(catalog.instances.map((i: any) => i.name).sort()).toEqual(['acme', 'beta'])
  expect(catalog.instances.find((i: any) => i.name === 'acme').launch).toBe('/apps/acme/?launch')

  // the portal's page may frame an instance (and sees no header with ?embed); a page on another origin is refused by the browser
  // (two small servers stand in for the portals; a page Playwright fulfils itself is not "local" to Chromium and would be blocked from a localhost frame)
  const portals = [portalOrigin, otherOrigin].map((origin) => {
    const server = http.createServer((_, res) => { res.setHeader('Content-Type', 'text/html'); res.end(`<iframe id="f" src="${appsUrl}/apps/acme/?embed"></iframe>`) })
    server.listen(Number(new URL(origin).port))
    return server
  })
  const framed = await context.newPage()
  await framed.goto(`${portalOrigin}/portal`)
  await expect.poll(() => framed.frames().map((f) => f.url()).join(' | ')).toContain(`${appsUrl}/apps/acme/`)
  const inner = framed.frames().find((f) => f.url().startsWith(appsUrl))!
  await expect(inner.locator('#app-frame')).toHaveAttribute('sandbox', /allow-scripts/)
  await expect(inner.locator('header')).toHaveCount(0)
  const refused = await context.newPage()
  await refused.goto(`${otherOrigin}/portal`)
  await expect.poll(() => refused.frames().some((f) => f.url().startsWith('chrome-error:'))).toBe(true)
  expect(refused.frames().some((f) => f.url().startsWith(appsUrl))).toBe(false)
  for (const server of portals) server.close()

  // a portal draws the same app its own way: its identity, its menu (without `hidden`), the theme handed to the app's frame
  await page.goto(`${appsUrl}/apps/alpha/`)
  await expect(page.locator('.portal-title')).toHaveText('Alpha Co')
  await expect(page.locator('nav.portal.side a')).toHaveText(['Sales', 'Beta'])
  await expect(page.locator('footer.portal')).toContainText('Alpha Co footer')
  // the home lists the mounted instances Ada may open: not `hidden`
  await expect(page.locator('ul.tiles li a')).toHaveText(['Territory notes', 'Territory notes'])
  await expect(page.locator('ul.tiles li a').first()).toHaveAttribute('href', '/apps/alpha/sales/')
  // an app in the aside slot is a sandboxed frame of its own, themed like the page
  await expect(page.locator('iframe.slot-aside')).toHaveAttribute('sandbox', 'allow-scripts allow-forms allow-popups')
  await expect(page.frameLocator('iframe.slot-aside').locator('#theme')).toHaveText('#ff0000')
  await page.locator('nav.portal a', { hasText: 'Sales' }).click()
  await expect(page).toHaveURL(`${appsUrl}/apps/alpha/sales/`)
  await expect(page.locator('nav.portal a[aria-current=page]')).toHaveText('Sales')
  await expect(page.frameLocator('#app-frame').locator('#theme')).toHaveText('#ff0000')
  expect((await page.goto(`${appsUrl}/apps/alpha/hidden/`))!.status()).toBe(403)

  // the about page lists the manifest's data access, and asks for no consent
  await page.goto(`${appsUrl}/apps/acme/_about`)
  await expect(page.locator('ul.access li')).toHaveText(['territories.territory: read and write'])
  await expect(page.locator('dl.about')).toContainText('sandboxed')

  await page.goto(`${appsUrl}/apps/omega/work/`)
  await expect(page.locator('.portal-title')).toHaveText('Omega Ltd')
  await expect(page.locator('nav.portal.side')).toHaveCount(0)
  await expect(page.locator('footer.portal')).toHaveCount(0)
  await expect(page.frameLocator('#app-frame').locator('#theme')).toHaveText('#0000ff')
  await expect(page.frameLocator('#app-frame').locator('#rows')).toContainText('rows North America')   // (the insert is refused the second time: the row exists)

  // the row is in the model's tables
  const client = new pg.Client({ connectionString: toPg(warehouse!) })
  await client.connect()
  const rows = await client.query('SELECT name FROM territories.territory')
  await client.end()
  expect(rows.rows.map((r) => r.name)).toEqual(['North America'])
})
