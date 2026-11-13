import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Plan 32 DBR13: a portal's home `dashboard` block names a dashboard file of a mounted app of views, and the app's runtime draws it to the frame's whole box as the signed-in person (the shared `DashboardView`, through the app's
// views route): a reflow layout fits the box, a canvas layout scales into it. The portal's home has two blocks over the same app, a reflow dashboard in a frame 220 px high and a canvas dashboard (800 by 400, `overflow: scale`) in a frame 300 px high;
// both have no menu and no title (`_as=block`), and a person with no role on the app sees neither. Text widgets only: the page needs no data source. Needs the built Gateway with the views runtime (`npm run build -w @dbdatakit/view-app`
// first, as the Gateway build copies it), ports 8711 (identity home), 8712 (apps) and 8713 (management).

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')

const root = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-home-dashboard')
const gatewayDirectory = path.join(root, 'gateway')
const homePort = 8711
const appsPort = 8712
const managementPort = 8713
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

const salesManifest = ['schemaVersion: 1', 'name: sales', 'title: Sales', 'version: 1.0.0', 'roles: [viewer]', 'views: [views/reflow.yaml, views/scaled.yaml]', ''].join('\n')
const reflow = [
  'schemaVersion: 1', 'kind: dashboard', 'title: Reflow board',
  'widgets:', '  one: {type: text, text: First tile}', '  two: {type: text, text: Second tile}',
  'layouts:',
  '  - {name: desk, type: reflow, default: true, grid: {columns: 12}, tiles: [{widget: one, x: 0, y: 0, w: 6, h: 2}, {widget: two, x: 6, y: 0, w: 6, h: 2}]}', '',
].join('\n')
const scaled = [
  'schemaVersion: 1', 'kind: dashboard', 'title: Scaled board',
  'widgets:', '  big: {type: text, text: Canvas tile}',
  'layouts:',
  '  - {name: wall, type: canvas, default: true, canvas: {width: 800, height: 400}, overflow: scale, tiles: [{widget: big, x: 20, y: 20, w: 400, h: 100}]}', '',
].join('\n')
const portalManifest = [
  'schemaVersion: 1', 'name: acme-portal', 'title: Acme', 'version: 1.0.0', 'kind: portal', 'roles: [viewer]',
  'portal:', '  mounts: [{app: sales, path: /sales/}]', '  home: [views/home.yaml]', '',
].join('\n')
const home = [
  'schemaVersion: 1', 'blocks:', '  - text: Welcome to Acme',
  '  - dashboard: {app: sales, view: views/reflow.yaml, height: 220}',
  '  - dashboard: {app: sales, view: views/scaled.yaml, height: 300}', '',
].join('\n')

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

  const sales = path.join(root, 'sales')
  fs.mkdirSync(path.join(sales, 'views'), { recursive: true })
  fs.writeFileSync(path.join(sales, 'app.yaml'), salesManifest)
  fs.writeFileSync(path.join(sales, 'views', 'reflow.yaml'), reflow)
  fs.writeFileSync(path.join(sales, 'views', 'scaled.yaml'), scaled)
  run(['apps', 'publish', '--dir', sales])
  run(['apps', 'instance-save', '--name', 'sales', '--app', 'sales'])
  const portal = path.join(root, 'acme-portal')
  fs.mkdirSync(path.join(portal, 'views'), { recursive: true })
  fs.writeFileSync(path.join(portal, 'app.yaml'), portalManifest)
  fs.writeFileSync(path.join(portal, 'views', 'home.yaml'), home)
  run(['apps', 'publish', '--dir', portal])
  run(['apps', 'instance-save', '--name', 'acme', '--app', 'acme-portal'])

  const gateway = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`])
  await waitFor(`${homeUrl}/health/live`, gateway.output)
  await waitFor(`${appsUrl}/health/live`, gateway.output)
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

test('a portal home draws a dashboard file of a mounted app to the block\'s size, a reflow layout fitting the box and a canvas scaling into it, for the person who holds a role on the app', async ({ browser }) => {
  const adaContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const ada = await adaContext.newPage()
  const adaUser = await enrol(adaContext, ada, 'Ada Lovelace', 'ada@example.com')
  run(['identity', 'assign', '--user', adaUser.id, '--app', 'acme', '--roles', 'viewer'])
  run(['identity', 'assign', '--user', adaUser.id, '--app', 'sales', '--roles', 'viewer'])
  await signIn(adaContext, ada, 'Ada Lovelace')

  const bobContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const bob = await bobContext.newPage()
  const bobUser = await enrol(bobContext, bob, 'Bob Barker', 'bob@example.com')
  run(['identity', 'assign', '--user', bobUser.id, '--app', 'acme', '--roles', 'viewer'])
  await signIn(bobContext, bob, 'Bob Barker')

  // a page error in a frame (a script the policy refused, a schema compiled with `eval`) fails the story
  const errors: string[] = []
  ada.on('pageerror', (e) => errors.push(String(e).slice(0, 300)))
  await ada.goto(`${appsUrl}/apps/acme/`)
  await expect(ada.getByText('Welcome to Acme')).toBeVisible()
  const frames = ada.locator('iframe.home-dashboard[data-instance="sales"]')
  await expect(frames).toHaveCount(2)
  expect(await frames.nth(0).getAttribute('data-route')).toBe('/views/reflow.yaml?_as=block')

  // the reflow dashboard: the document's own widgets, in the frame's box (220 px), with no menu and no title of the app
  const first = ada.frameLocator('iframe.home-dashboard[data-instance="sales"]').nth(0)
  await expect(first.getByText('First tile')).toBeVisible()
  await expect(first.getByText('Second tile')).toBeVisible()
  await expect(first.locator('[data-testid="vz-dashboard"]')).toHaveAttribute('data-layout-type', 'reflow')
  await expect(first.locator('.va-title, .va-menu')).toHaveCount(0)
  const reflowBox = await first.locator('[data-testid="vz-dashboard"]').boundingBox()
  expect(reflowBox!.height).toBeLessThanOrEqual(221)
  expect(reflowBox!.height).toBeGreaterThan(100)

  // the canvas dashboard: 800 by 400 drawn scaled (to 0.75) into a box 300 px high and the page's width
  const second = ada.frameLocator('iframe.home-dashboard[data-instance="sales"]').nth(1)
  await expect(second.getByText('Canvas tile')).toBeVisible()
  await expect(second.locator('[data-testid="vz-dashboard"]')).toHaveAttribute('data-layout-type', 'canvas')
  const frameBox = await frames.nth(1).boundingBox()
  const canvas = await second.locator('[data-testid="vz-canvas-sizer"]').boundingBox()
  expect(canvas!.height).toBeLessThanOrEqual(frameBox!.height + 1)
  expect(canvas!.width).toBeLessThanOrEqual(frameBox!.width + 1)
  expect(canvas!.height).toBeGreaterThan(250)   // scaled to the box (400 * 0.75), not shrunk to nothing
  expect(errors).toEqual([])

  // Bob holds no role on the app: his home has neither frame
  await bob.goto(`${appsUrl}/apps/acme/`)
  await expect(bob.getByText('Welcome to Acme')).toBeVisible()
  await expect(bob.locator('iframe.home-dashboard')).toHaveCount(0)
  await adaContext.close()
  await bobContext.close()
})
