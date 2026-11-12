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
// Needs the built Gateway (the same as `apps-policy.spec.ts`), ports 8691 (identity home), 8692 (apps) and 8693 (management). Written for plan 26 AP30/AP27.

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')

const root = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-sessions')
const gatewayDirectory = path.join(root, 'gateway')
const homePort = 8691
const appsPort = 8692
const managementPort = 8693
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

const manifest = ['schemaVersion: 1', 'name: board', 'title: Board', 'version: 1.0.0', 'entry: index.html', 'roles: [viewer]', ''].join('\n')
const index = '<!doctype html><html><head><meta charset="utf-8"><title>Board</title></head><body><p id="board">the board</p></body></html>\n'

let gateway: ReturnType<typeof start>

async function startGateway() {
  gateway = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`])
  await waitFor(`${homeUrl}/health/live`, gateway.output)
  await waitFor(`${appsUrl}/health/live`, gateway.output)
}

async function stopGateway() {
  const child = gateway.child
  const exited = new Promise<void>((resolve) => child.once('exit', () => resolve()))
  child.kill('SIGTERM')
  await exited
  running.splice(running.indexOf(child), 1)
}

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
  const directory = path.join(root, 'board')
  fs.mkdirSync(directory, { recursive: true })
  fs.writeFileSync(path.join(directory, 'app.yaml'), manifest)
  fs.writeFileSync(path.join(directory, 'index.html'), index)
  run(['apps', 'publish', '--dir', directory])
  run(['apps', 'instance-save', '--name', 'board', '--app', 'board'])
  await startGateway()
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

test('a session survives a restart of the Gateway: the same cookie opens the app and the shell still gets its token', async ({ browser }) => {
  const context = await browser.newContext({ ignoreHTTPSErrors: true })
  const page = await context.newPage()
  const ada = await enrol(context, page, 'Ada Lovelace', 'ada@example.com')
  run(['identity', 'assign', '--user', ada.id, '--app', 'board', '--roles', 'viewer'])
  await signIn(context, page, 'Ada Lovelace')

  await page.goto(`${appsUrl}/apps/board/`)
  await expect(page.frameLocator('#app-frame').locator('#board')).toHaveText('the board')
  const cookie = (await context.cookies(appsUrl)).find((c) => c.name.toLowerCase().includes('session') || c.httpOnly)
  expect(cookie, 'the apps host set a session cookie').toBeTruthy()
  const token = await page.request.post(`${appsUrl}/apps/board/_token`, { headers: { 'X-DbData-Shell': '1' } })
  expect(token.status()).toBe(200)

  // the process is stopped and a new one started on the same data directory: nothing of the session is in its memory
  await stopGateway()
  await startGateway()

  await page.goto(`${appsUrl}/apps/board/`)
  expect(page.url().startsWith(appsUrl)).toBe(true)                                   // not sent to the identity home to sign in again
  await expect(page.frameLocator('#app-frame').locator('#board')).toHaveText('the board')
  expect((await context.cookies(appsUrl)).find((c) => c.name === cookie!.name)?.value).toBe(cookie!.value)   // the very same cookie
  const again = await page.request.post(`${appsUrl}/apps/board/_token`, { headers: { 'X-DbData-Shell': '1' } })
  expect(again.status()).toBe(200)

  // a browser that never signed in is sent to the home: the check above is not vacuous
  const stranger = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage()
  await stranger.goto(`${appsUrl}/apps/board/`)
  await expect.poll(() => stranger.url().startsWith(homeUrl)).toBe(true)
  await context.close()
})
