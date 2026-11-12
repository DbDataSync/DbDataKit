import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Plan 26 AP30 (and AP27): a portal's home draws blocks for the viewer. The home view has a dashboard block (the mounted app `sales`, drawn as a frame of its own for a person who holds a role on it), a second one for `hr`, a text only
// the portal's `operator` role sees, a table and an image of the portal's package. Two people open the same home: Ada (roles on the portal, `viewer` and `operator`, and on `sales`) sees the sales frame, which really loads and answers from the sales app, the lead
// text, the table and the image, and no `hr` frame; Bob (a role on the portal only) sees the table and the image and neither frame nor the operator line. No PostgreSQL and no Store: the mounted apps are pages of text.
// Needs the built Gateway (the same as `apps-policy.spec.ts`), ports 8681 (identity home), 8682 (apps) and 8683 (management). Written for plan 26 AP30/AP27.

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')

const root = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-home-blocks')
const gatewayDirectory = path.join(root, 'gateway')
const homePort = 8681
const appsPort = 8682
const managementPort = 8683
const homeUrl = `https://localhost:${homePort}`
const appsUrl = `https://localhost:${appsPort}`

const running: ChildProcess[] = []

function start(args: string[]) {
  const child = spawn('dotnet', ['exec', ...args], { env: { ...process.env }, stdio: ['ignore', 'pipe', 'pipe'] })
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

function run(args: string[]): Record<string, any> {
  const text = execFileSync('dotnet', ['exec', gatewayDll, ...args, '--repo', gatewayDirectory, '--format', 'json'], { encoding: 'utf8' })
  return JSON.parse(text).data
}

async function addAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
}

/** Enrols a person at the home with an invitation and signs them in, in this page's own context (a passkey lives in one authenticator). */
async function enrol(context: BrowserContext, page: Page, name: string, email: string) {
  await addAuthenticator(context, page)
  const invitation = run(['identity', 'invite', '--email', email, '--role', 'admin', '--url', homeUrl])
  await page.goto(invitation.url)
  await page.getByTestId('identity-invite-name').fill(name)
  await page.getByTestId('identity-invite-enrol').click()
  await expect(page.getByTestId('identity-invite-done')).toHaveText(`Signed in as ${name}.`)
  return run(['identity', 'user-list']).users.find((u: any) => u.name === name)
}

async function signIn(context: BrowserContext, page: Page, name: string) {
  await context.clearCookies({ domain: 'localhost' })
  await page.goto(`${homeUrl}/identity/sign-in`)
  await page.getByTestId('identity-sign-in-passkey').click()
  await expect(page.getByTestId('identity-signed-in')).toHaveText(`Signed in as ${name}.`)
}

const app = (name: string, text: string) => ({
  manifest: ['schemaVersion: 1', `name: ${name}`, `title: ${name}`, 'version: 1.0.0', 'entry: index.html', 'roles: [viewer]', ''].join('\n'),
  index: `<!doctype html><html><head><meta charset="utf-8"><title>${name}</title></head><body><p id="board">${text}</p></body></html>\n`,
})

const portalManifest = [
  'schemaVersion: 1', 'name: acme-portal', 'title: Acme', 'version: 1.0.0', 'kind: portal', 'roles: [viewer, operator]',
  'portal:', '  mounts: [{app: sales, path: /sales/}, {app: hr, path: /hr/}]', '  home: [views/home.yaml]', '',
].join('\n')
const home = [
  'schemaVersion: 1', 'blocks:',
  '  - text: Welcome to Acme',
  '  - text: Only operators read this line',
  '    roles: [operator]',
  '  - dashboard: {app: sales, height: 200}',
  '  - dashboard: {app: hr, height: 200}',
  '  - table: {columns: [Team, Lead], rows: [[Sales, Ada]]}',
  '  - image: {file: assets/banner.svg, alt: The Acme banner, width: 120}', '',
].join('\n')
const banner = '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="30"><rect width="120" height="30" fill="#36c"/></svg>\n'

test.describe.configure({ mode: 'serial' })
test.setTimeout(240_000)

test.beforeAll(async () => {
  fs.rmSync(root, { recursive: true, force: true })
  fs.mkdirSync(path.join(gatewayDirectory, 'config'), { recursive: true })
  const settings = path.join(gatewayDirectory, 'config', 'server.yaml')
  fs.writeFileSync(settings, [
    'schemaVersion: 2', 'tls: { mode: selfSigned, hostnames: [localhost] }', 'modules: [identity, apps]', 'definitionPollSeconds: 1',
    `management: { enabled: true, address: 127.0.0.1, port: ${managementPort} }`,
    `identity: { address: 127.0.0.1, port: ${homePort}, publicUrl: '${homeUrl}' }`,
    `apps: { address: 127.0.0.1, port: ${appsPort}, home: { clientId: '__CLIENT__' } }`, '',
  ].join('\n'))
  const registered = run(['identity', 'client-add', '--name', 'Apps host', '--public', '--redirect', `${appsUrl}/apps/-/callback`])
  fs.writeFileSync(settings, fs.readFileSync(settings, 'utf8').replace('__CLIENT__', registered.client_id))

  for (const name of ['sales', 'hr']) {
    const directory = path.join(root, name)
    fs.mkdirSync(directory, { recursive: true })
    const made = app(name, `the ${name} board`)
    fs.writeFileSync(path.join(directory, 'app.yaml'), made.manifest)
    fs.writeFileSync(path.join(directory, 'index.html'), made.index)
    run(['apps', 'publish', '--dir', directory])
    run(['apps', 'instance-save', '--name', name, '--app', name])
  }
  const portal = path.join(root, 'acme-portal')
  fs.mkdirSync(path.join(portal, 'views'), { recursive: true })
  fs.mkdirSync(path.join(portal, 'assets'), { recursive: true })
  fs.writeFileSync(path.join(portal, 'app.yaml'), portalManifest)
  fs.writeFileSync(path.join(portal, 'views', 'home.yaml'), home)
  fs.writeFileSync(path.join(portal, 'assets', 'banner.svg'), banner)
  run(['apps', 'publish', '--dir', portal])
  run(['apps', 'instance-save', '--name', 'acme', '--app', 'acme-portal'])

  const gateway = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`])
  await waitFor(`${homeUrl}/health/live`, gateway.output)
  await waitFor(`${appsUrl}/health/live`, gateway.output)
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

test('a portal home shows a dashboard block to a viewer with the role and hides it from one without, and shows a role-gated line only to that role', async ({ browser }) => {
  const adaContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const ada = await adaContext.newPage()
  const adaUser = await enrol(adaContext, ada, 'Ada Lovelace', 'ada@example.com')
  run(['identity', 'assign', '--user', adaUser.id, '--app', 'acme', '--roles', 'viewer,operator'])
  run(['identity', 'assign', '--user', adaUser.id, '--app', 'sales', '--roles', 'viewer'])
  await signIn(adaContext, ada, 'Ada Lovelace')

  const bobContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const bob = await bobContext.newPage()
  const bobUser = await enrol(bobContext, bob, 'Bob Barker', 'bob@example.com')
  run(['identity', 'assign', '--user', bobUser.id, '--app', 'acme', '--roles', 'viewer'])
  await signIn(bobContext, bob, 'Bob Barker')

  // Ada: the sales dashboard is a frame that loads the sales app (its text is the sales app's own page), no hr frame, the operator line, the table and the image
  await ada.goto(`${appsUrl}/apps/acme/`)
  await expect(ada.getByText('Welcome to Acme')).toBeVisible()
  await expect(ada.getByText('Only operators read this line')).toBeVisible()
  await expect(ada.locator('iframe.home-dashboard')).toHaveCount(1)
  await expect(ada.locator('iframe.home-dashboard[data-instance="sales"]')).toBeVisible()
  await expect(ada.frameLocator('iframe.home-dashboard[data-instance="sales"]').locator('#board')).toHaveText('the sales board')
  await expect(ada.locator('iframe[data-instance="hr"]')).toHaveCount(0)
  await expect(ada.locator('table.home-table td')).toHaveText(['Sales', 'Ada'])
  const image = ada.getByAltText('The Acme banner')
  await expect(image).toBeVisible()
  await expect.poll(() => image.evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0)).toBe(true)

  // Bob: the same home, with the table and the image, with neither dashboard and without the operator line
  await bob.goto(`${appsUrl}/apps/acme/`)
  await expect(bob.getByText('Welcome to Acme')).toBeVisible()
  await expect(bob.locator('table.home-table')).toBeVisible()
  await expect(bob.getByAltText('The Acme banner')).toBeVisible()
  await expect(bob.getByText('Only operators read this line')).toHaveCount(0)
  await expect(bob.locator('iframe.home-dashboard')).toHaveCount(0)
  await adaContext.close()
  await bobContext.close()
})
