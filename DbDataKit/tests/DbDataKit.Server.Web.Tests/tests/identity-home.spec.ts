import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Plan 20 O06: the identity home served by the serving layer (a Gateway's `identity` module). A person signs in there with no console running at all, and then an administrator's console that is an OpenID
// Connect client of that home signs them in through it. The Gateway and the console are started here, not by the config's web servers, because the story starts with no console.

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')
const webRoot = path.join(repoRoot, 'Shared/web/console/dist')

const gatewayDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-identity-gateway')
const consoleDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-identity-console')
const flightPort = 8495
const homePort = 8497
const consolePort = 8498
const homeUrl = `https://localhost:${homePort}`
const consoleUrl = `https://localhost:${consolePort}`

const running: ChildProcess[] = []

function start(args: string[], env: Record<string, string> = {}) {
  const child = spawn('dotnet', ['exec', ...args], { env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] })
  let output = ''
  child.stdout!.on('data', (chunk) => { output += chunk })
  child.stderr!.on('data', (chunk) => { output += chunk })
  running.push(child)
  return { child, output: () => output }
}

/** Whether an https address answers 200, trusting its certificate whatever it is (the servers here have self-signed ones; only this check skips the verification). */
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

/** `dbdatagateway identity ...` on the Gateway's data directory, as an operator runs it; the JSON document's data. */
function identity(...args: string[]): Record<string, any> {
  const text = execFileSync('dotnet', ['exec', gatewayDll, 'identity', ...args, '--repo', gatewayDirectory, '--format', 'json'], { encoding: 'utf8' })
  return JSON.parse(text).data
}

async function addAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
}

test.describe.configure({ mode: 'serial' })

test.beforeAll(async () => {
  for (const directory of [gatewayDirectory, consoleDirectory]) {
    fs.rmSync(directory, { recursive: true, force: true })
    fs.mkdirSync(directory, { recursive: true })
  }
  fs.mkdirSync(path.join(gatewayDirectory, 'config'), { recursive: true })
  fs.writeFileSync(path.join(gatewayDirectory, 'config', 'server.yaml'), [
    'schemaVersion: 2',
    'tls: { mode: selfSigned, hostnames: [localhost] }',
    `listen: { address: 127.0.0.1, port: ${flightPort} }`,
    `identity: { enabled: true, address: 127.0.0.1, port: ${homePort}, publicUrl: '${homeUrl}' }`,
    '',
  ].join('\n'))
  const gateway = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`])
  await waitFor(`${homeUrl}/health/live`, gateway.output)
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

test('the serving layer signs a person in with no console running, and the administrator\'s console signs them in through it', async ({ page, context }) => {
  await addAuthenticator(context, page)

  // No console is running: the home is the serving layer's. Its first-run invitation is in the Gateway's data directory and opens the home's own invitation page.
  const firstRun = fs.readFileSync(path.join(gatewayDirectory, 'identity', 'FIRST-RUN.txt'), 'utf8')
  const code = firstRun.split('/invite#')[1].split(/\s/)[0]
  await page.goto(`${homeUrl}/invite#${code}`)
  await page.getByTestId('identity-invite-name').fill('Ada Lovelace')
  await page.getByTestId('identity-invite-email').fill('ada@example.com')
  await page.getByTestId('identity-invite-enrol').click()
  await expect(page.getByTestId('identity-invite-done')).toHaveText('Signed in as Ada Lovelace.')
  expect(fs.existsSync(path.join(gatewayDirectory, 'identity', 'FIRST-RUN.txt'))).toBe(false)

  // Signed out (the session is gone), she signs in again at the serving layer with the passkey her device kept.
  await context.clearCookies()
  await page.goto(`${homeUrl}/identity/sign-in`)
  await page.getByTestId('identity-sign-in-passkey').click()
  await expect(page.getByTestId('identity-signed-in')).toHaveText('Signed in as Ada Lovelace.')

  // The operator registers the administrator's console with the home and makes Ada an administrator of it, at the Gateway's command line.
  const ada = identity('user-list').users.find((u: any) => u.name === 'Ada Lovelace')
  expect(ada.email).toBe('ada@example.com')
  const client = identity('client-add', '--name', 'Administrator console', '--redirect', `${consoleUrl}/api/identity/callback`, '--logout-redirect', `${consoleUrl}/`)
  identity('assign', '--user', ada.id, '--app', 'console', '--roles', 'admin')

  // The console starts as a client of the serving layer's home, trusting the Gateway's own certificate for it.
  const certificate = fs.readdirSync(gatewayDirectory).find((name) => name.endsWith('.cer'))!
  const consoleProcess = start([
    consoleDll, 'server', 'run', '--repo', consoleDirectory, '--listen', consoleUrl, '--web-root', webRoot,
    '--identity-home', homeUrl, '--identity-client', client.client_id, '--identity-secret-env', 'DBDATAKIT_E2E_HOME_SECRET',
    '--identity-home-cert', path.join(gatewayDirectory, certificate),
  ], { DBDATAKIT_E2E_HOME_SECRET: client.secret })
  await waitFor(`${consoleUrl}/health/live`, consoleProcess.output)

  // The console's sign-in screen offers the home; she is sent there, is already signed in, and comes back signed in to the console as the home's user.
  await page.goto(consoleUrl)
  await expect(page.getByTestId('sign-in-screen')).toBeVisible()
  await page.getByTestId('sign-in-identity-home').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
  await expect(page.getByTestId('signed-in-as')).toContainText('Ada Lovelace')
})
