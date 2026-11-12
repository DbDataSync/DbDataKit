import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import net from 'node:net'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Plan 26 AP17 (and AP27): a report server embedded in an app of views. The app's dashboard has three `embed` widgets: one at an origin the manifest asked for and an administrator approved (a frame), one at an origin the
// manifest asked for and nobody approved (only the link, and the document's `frame-src` does not list it), and one at an approved origin that never answers (a server that accepts the connection and says nothing, as a report
// server that wants a Windows sign-in this browser cannot give does: after 15 seconds the frame is replaced by the link and "Cannot show this report here"). The identity home's own origin stands in for the report server
// that answers (`/health/live`), as `apps-policy.spec.ts` does. Needs the built Gateway with the views runtime next to it (`Shared/web/view-app` built and copied to `apps-views`), no PostgreSQL and no Store: the dashboard reads no data.
// It enrols a person at the home with an invitation, so it sorts after `first-run` and `identity-home` (`views-` does). Written for plan 26 AP17/AP27; not run where it was written (no browser, no Gateway build there).

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')

const root = path.join(os.tmpdir(), 'dbdatakit-e2e-views-apps-embed')
const gatewayDirectory = path.join(root, 'gateway')
const homePort = 8661
const appsPort = 8662
const managementPort = 8663
const silentPort = 8664
const homeUrl = `https://localhost:${homePort}`
const appsUrl = `https://localhost:${appsPort}`
const silentUrl = `https://localhost:${silentPort}`
const unapprovedUrl = 'https://reports.unapproved.example'

const running: ChildProcess[] = []
let silent: net.Server | undefined

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

const manifest = [
  'schemaVersion: 1', 'name: reports', 'title: Reports', 'version: 1.0.0', 'roles: [viewer]', 'trust: trusted',
  'views: [views/home.yaml]',
  'models:', '  - model: territories', '    entities: {territory: [read]}',
  'egress:', `  frame: ["${homeUrl}", "${silentUrl}", "${unapprovedUrl}"]`, '',
].join('\n')
const dashboard = [
  'schemaVersion: 1', 'kind: dashboard', 'title: Reports', 'model: territories', 'widgets:',
  `  - {kind: embed, title: Health, url: '${homeUrl}/health/live', link_text: Open the health report}`,
  `  - {kind: embed, title: Other, url: '${unapprovedUrl}/r', link_text: Open the other report}`,
  `  - {kind: embed, title: Silent, url: '${silentUrl}/r', link_text: Open the silent report}`, '',
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

  // an approver, as `apps-policy.spec.ts` makes one
  expect(() => run(['apps', 'approve', '--hash', '0'.repeat(64), '--app', 'plain', '--by', 'grace'])).toThrow()
  const serverId = /instanceId: (\S+)/.exec(fs.readFileSync(settings, 'utf8'))![1]
  const grace = run(['identity', 'service-account-add', '--name', 'grace'])
  run(['identity', 'assign', '--user', grace.id, '--server', serverId, '--roles', 'approver'])

  // the app of views: the manifest asks for three origins, the approval covers two
  const directory = path.join(root, 'reports')
  fs.mkdirSync(path.join(directory, 'views'), { recursive: true })
  fs.writeFileSync(path.join(directory, 'app.yaml'), manifest)
  fs.writeFileSync(path.join(directory, 'views', 'home.yaml'), dashboard)
  run(['apps', 'approve', '--path', directory, '--by', 'grace', '--egress', `frame=${homeUrl},frame=${silentUrl}`])
  run(['apps', 'publish', '--dir', directory])
  run(['apps', 'instance-save', '--name', 'reports', '--app', 'reports'])

  // a report server that takes the connection and never answers
  silent = net.createServer((socket) => { socket.on('error', () => {}) })
  await new Promise<void>((resolve) => silent!.listen(silentPort, '127.0.0.1', resolve))

  const gateway = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`])
  await waitFor(`${homeUrl}/health/live`, gateway.output)
  await waitFor(`${appsUrl}/health/live`, gateway.output)
})

test.afterAll(async () => {
  for (const child of running) child.kill()
  silent?.close()
})

test('an approved report origin is framed, one nobody approved is only a link and not in the policy, and a report that does not answer shows the link', async ({ page, context }) => {
  await addAuthenticator(context, page)
  const invitation = run(['identity', 'invite', '--email', 'ada@example.com', '--role', 'admin', '--url', homeUrl])
  await page.goto(invitation.url)
  await page.getByTestId('identity-invite-name').fill('Ada Lovelace')
  await page.getByTestId('identity-invite-enrol').click()
  await expect(page.getByTestId('identity-invite-done')).toHaveText('Signed in as Ada Lovelace.')
  const ada = run(['identity', 'user-list']).users.find((u: any) => u.name === 'Ada Lovelace')
  run(['identity', 'assign', '--user', ada.id, '--app', 'reports', '--roles', 'viewer'])
  await context.clearCookies({ domain: 'localhost' })
  await page.goto(`${homeUrl}/identity/sign-in`)
  await page.getByTestId('identity-sign-in-passkey').click()
  await expect(page.getByTestId('identity-signed-in')).toHaveText('Signed in as Ada Lovelace.')

  await page.goto(`${appsUrl}/apps/reports/`)
  // the policy is the app document's (`_app/`, the one in the shell's frame), not the shell page's, which frames only itself; the request carries the person's session
  const policy = (await page.request.get(`${appsUrl}/apps/reports/_app/`)).headers()['content-security-policy']
  // the document's policy lists the approved origins and not the one nobody approved
  expect(policy).toContain(`frame-src ${homeUrl} ${silentUrl}`)
  expect(policy).not.toContain('unapproved.example')

  const app = page.frameLocator('#app-frame')
  const embeds = app.getByTestId('vz-embed')
  await expect(embeds).toHaveCount(3)

  // 1. approved and answering: a frame, with the link under it
  const health = embeds.nth(0)
  await expect(health).toHaveAttribute('data-mode', 'frame')
  await expect(health.locator('iframe.vz-embed-frame')).toHaveAttribute('src', `${homeUrl}/health/live`)
  await expect(health).toHaveAttribute('data-state', 'loaded', { timeout: 15_000 })
  await expect(health.getByTestId('vz-embed-open').locator('a')).toHaveText('Open the health report')

  // 2. asked for and not approved: the link alone, never a frame
  const other = embeds.nth(1)
  await expect(other).toHaveAttribute('data-mode', 'link')
  await expect(other.locator('iframe')).toHaveCount(0)
  await expect(other.locator('a')).toHaveAttribute('href', `${unapprovedUrl}/r`)

  // 3. approved and silent: the frame is replaced by the link and the words after 15 seconds
  const quiet = embeds.nth(2)
  await expect(quiet.getByTestId('vz-embed-failed')).toContainText('Cannot show this report here', { timeout: 30_000 })
  await expect(quiet).toHaveAttribute('data-mode', 'link')
  await expect(quiet.locator('a')).toHaveAttribute('href', `${silentUrl}/r`)
})
