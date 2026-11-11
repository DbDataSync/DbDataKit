import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Plan 26 AP27 and AP14 (with AP31): the browser enforces the egress policy of an app's document. Three instances ask for the same outside address (the identity home's own origin stands in for "another site",
// it answers `/health/live` and is a different origin from the apps host): a sandboxed app, which is never given one; a trusted app an approver approved it for; and a trusted app the approver approved for nothing.
// The first and the third are blocked by the browser, the second reaches it, and the third's about page lists the origin as asked for and not approved (AP31). No PostgreSQL and no Store are needed: the apps ask for no data.
// Needs the built Gateway (the same as `apps-host.spec.ts`). Written for plan 26 AP27; not run where it was written (no browser, no Gateway build there).

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')

const root = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-policy')
const gatewayDirectory = path.join(root, 'gateway')
const homePort = 8651
const appsPort = 8652
const managementPort = 8653
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

/** A command of the Gateway's command line on its directory, as an operator runs it; the JSON document's data. */
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

// each app: a page and a script (the policy allows no inline script) that tries to reach the other origin and says whether the browser let it
const manifest = (name: string, trust: 'sandbox' | 'trusted') => [
  'schemaVersion: 1', `name: ${name}`, `title: ${name}`, 'version: 1.0.0', 'entry: index.html', 'roles: [viewer]', ...(trust === 'trusted' ? ['trust: trusted'] : []),
  'egress:', `  connect: ["${homeUrl}"]`, '',
].join('\n')
const index = '<!doctype html><html><head><meta charset="utf-8"><title>Policy</title></head><body><pre id="outside">trying</pre><script src="app.js"></script></body></html>\n'
const script = `
const out = (text) => { document.getElementById('outside').textContent = text }
fetch('${homeUrl}/health/live', { mode: 'no-cors' }).then(() => out('reached'), () => out('blocked'))
`

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

  // an approver: a service account of the identity home that holds the Approver role on the server (publishing a trusted app needs an approval for its exact files, by someone who is not its author)
  // (a first attempt makes the server's own id, which the role names, as `apps-host.spec.ts` does)
  expect(() => run(['apps', 'approve', '--hash', '0'.repeat(64), '--app', 'plain', '--by', 'grace'])).toThrow()
  const serverId = /instanceId: (\S+)/.exec(fs.readFileSync(settings, 'utf8'))![1]
  const grace = run(['identity', 'service-account-add', '--name', 'grace'])
  run(['identity', 'assign', '--user', grace.id, '--server', serverId, '--roles', 'approver'])

  for (const [name, trust, egress] of [['plain', 'sandbox', null], ['approved', 'trusted', `connect=${homeUrl}`], ['unapproved', 'trusted', null]] as const) {
    const directory = path.join(root, name)
    fs.mkdirSync(directory, { recursive: true })
    fs.writeFileSync(path.join(directory, 'app.yaml'), manifest(name, trust))
    fs.writeFileSync(path.join(directory, 'index.html'), index)
    fs.writeFileSync(path.join(directory, 'app.js'), script)
    if (trust === 'trusted') run(['apps', 'approve', '--path', directory, '--by', 'grace', ...(egress ? ['--egress', egress] : [])])
    run(['apps', 'publish', '--dir', directory])
    run(['apps', 'instance-save', '--name', name, '--app', name])
  }

  const gateway = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`])
  await waitFor(`${homeUrl}/health/live`, gateway.output)
  await waitFor(`${appsUrl}/health/live`, gateway.output)
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

test('the browser blocks an outside origin a sandboxed or an unapproved app asks for and lets an approved one through, and the about page says which is which', async ({ page, context }) => {
  await addAuthenticator(context, page)

  // Ada enrols at the home with an invitation and is given a role on each instance (the home has an account already, the approver's service account, so it writes no FIRST-RUN.txt)
  const invitation = run(['identity', 'invite', '--email', 'ada@example.com', '--role', 'admin', '--url', homeUrl])
  await page.goto(invitation.url)
  await page.getByTestId('identity-invite-name').fill('Ada Lovelace')
  await page.getByTestId('identity-invite-enrol').click()
  await expect(page.getByTestId('identity-invite-done')).toHaveText('Signed in as Ada Lovelace.')
  const ada = run(['identity', 'user-list']).users.find((u: any) => u.name === 'Ada Lovelace')
  for (const name of ['plain', 'approved', 'unapproved']) run(['identity', 'assign', '--user', ada.id, '--app', name, '--roles', 'viewer'])
  await context.clearCookies({ domain: 'localhost' })
  await page.goto(`${homeUrl}/identity/sign-in`)
  await page.getByTestId('identity-sign-in-passkey').click()
  await expect(page.getByTestId('identity-signed-in')).toHaveText('Signed in as Ada Lovelace.')

  // a sandboxed app is given no outside address, whatever its manifest asks
  await page.goto(`${appsUrl}/apps/plain/`)
  await expect(page.frameLocator('#app-frame').locator('#outside')).toHaveText('blocked')

  // a trusted app reaches the origin its approver approved
  await page.goto(`${appsUrl}/apps/approved/`)
  await expect(page.frameLocator('#app-frame').locator('#outside')).toHaveText('reached')

  // a trusted app that asked and was approved for nothing is blocked
  await page.goto(`${appsUrl}/apps/unapproved/`)
  await expect(page.frameLocator('#app-frame').locator('#outside')).toHaveText('blocked')

  // AP31: the about pages list the approved origin in force, and the asked-for ones nobody approved apart
  await page.goto(`${appsUrl}/apps/approved/_about`)
  await expect(page.locator('ul.egress li')).toHaveText([`connect: ${homeUrl}`])
  await expect(page.locator('ul.egress-refused')).toHaveCount(0)
  await page.goto(`${appsUrl}/apps/unapproved/_about`)
  await expect(page.locator('ul.egress')).toHaveCount(0)
  await expect(page.locator('ul.egress-refused li')).toHaveText([`connect: ${homeUrl}`])
  await page.goto(`${appsUrl}/apps/plain/_about`)
  await expect(page.locator('ul.egress-refused li')).toHaveText([`connect: ${homeUrl}`])
})
