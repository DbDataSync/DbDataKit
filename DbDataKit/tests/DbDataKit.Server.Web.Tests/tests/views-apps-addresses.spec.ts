import { expect, test, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { signInNewAdmin } from './people'

// Plan 26 AP08 (and AP27): an instance is served at a name of its own. The Addresses section of the instance editor (the console's Gateway Apps screen, through its relay) adds a subdomain of the host's apps domain, the apps host
// routes by `Host` from then on (the same listener, no restart), a name that is not the instance's subdomain is refused in the Gateway's words, and removing the address stops the routing. The apps domain is `localhost`
// (`sales.localhost`; the requests go to 127.0.0.1 with that Host header, so no DNS is needed). A custom domain's certificate and the redirect URIs are `AppAddressesTests` and `AppAddressingTests`.
// Needs the built Gateway and the console like `gateway-apps-editors.spec.ts`, on ports 8671 (management), 8672 (identity home) and 8673 (apps). It invites a console administrator, so it sorts after `first-run` and
// `identity-home` (`views-` does). Written for plan 26 AP08/AP27; run in plan 26 AP27 (green). Its step 3 is the DNS challenge of a custom domain (the record Verify shows).

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')
const gatewayDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-views-apps-addresses')
const managementPort = 8671
const homePort = 8672
const appsPort = 8673
const gatewayUrl = `https://localhost:${managementPort}`
const fallbackWords = 'The apps host of this DbDataGateway'

let gateway: ChildProcess | undefined
let screen: Page

function run(args: string[]): Record<string, any> {
  const text = execFileSync('dotnet', ['exec', gatewayDll, ...args, '--repo', gatewayDirectory, '--format', 'json'], { encoding: 'utf8' })
  return JSON.parse(text).data
}

/** A GET to the apps host as if the person had typed `host`: the request goes to the loopback address with that Host header and name. */
function asked(host: string, requestPath = '/') {
  return new Promise<{ status: number; location?: string; body: string }>((resolve) => {
    https.get({ host: '127.0.0.1', port: appsPort, path: requestPath, headers: { Host: `${host}:${appsPort}` }, servername: host, rejectUnauthorized: false }, (response) => {
      let body = ''
      response.on('data', (chunk) => { body += chunk })
      response.on('end', () => resolve({ status: response.statusCode ?? 0, location: response.headers.location, body }))
    }).on('error', () => resolve({ status: 0, body: '' }))
  })
}

function live(url: string) {
  return new Promise<boolean>((resolve) => {
    https.get(url, { rejectUnauthorized: false }, (response) => { response.resume(); resolve(response.statusCode === 200) }).on('error', () => resolve(false))
  })
}

test.describe.configure({ mode: 'serial' })
test.setTimeout(300_000)

test.beforeAll(async ({ browser, baseURL }) => {
  fs.rmSync(gatewayDirectory, { recursive: true, force: true })
  fs.mkdirSync(path.join(gatewayDirectory, 'config'), { recursive: true })
  const config = path.join(gatewayDirectory, 'config', 'server.yaml')
  fs.writeFileSync(config, [
    'schemaVersion: 2', 'tls: { mode: selfSigned, hostnames: [localhost, "*.localhost"] }', 'modules: [identity, apps]', 'definitionPollSeconds: 1',
    `management: { enabled: true, address: 127.0.0.1, port: ${managementPort} }`,
    `identity: { address: 127.0.0.1, port: ${homePort}, publicUrl: 'https://localhost:${homePort}' }`,
    `apps: { address: 127.0.0.1, port: ${appsPort}, domain: localhost, home: { clientId: '__CLIENT__' } }`, '',
  ].join('\n'))
  const registered = run(['identity', 'client-add', '--name', 'Apps host', '--public', '--redirect', `https://localhost:${appsPort}/apps/-/callback`])
  fs.writeFileSync(config, fs.readFileSync(config, 'utf8').replace('__CLIENT__', registered.client_id))
  const folder = path.join(os.tmpdir(), 'dbdatakit-console-e2e-views-apps-addresses-notes')
  fs.rmSync(folder, { recursive: true, force: true })
  fs.mkdirSync(folder, { recursive: true })
  fs.writeFileSync(path.join(folder, 'app.yaml'), ['schemaVersion: 1', 'name: notes', 'title: Notes', 'version: 1.0.0', 'entry: index.html', 'roles: [viewer]', ''].join('\n'))
  fs.writeFileSync(path.join(folder, 'index.html'), '<!doctype html><html><head><meta charset="utf-8"><title>Notes</title></head><body>notes</body></html>\n')
  run(['apps', 'publish', '--dir', folder])
  run(['apps', 'instance-save', '--name', 'sales', '--app', 'notes'])
  gateway = spawn('dotnet', ['exec', gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`], { stdio: 'ignore' })
  for (let i = 0; i < 120 && !((await live(`https://localhost:${appsPort}/health/live`)) && (await live(`https://localhost:${homePort}/health/live`))); i++) await new Promise((r) => setTimeout(r, 500))

  const context = await browser.newContext({ baseURL, ignoreHTTPSErrors: true })
  screen = await context.newPage()
  await signInNewAdmin(screen, context, baseURL!, 'Addresses Tester', `views-apps-addresses-${Date.now()}@example.com`)
  const made = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--max-role', 'admin,approver', '--repo', gatewayDirectory], { encoding: 'utf8' })
  await screen.goto('/instances')
  await screen.getByTestId('pair-url-input').fill(gatewayUrl)
  await screen.getByTestId('pair-code-input').fill(/Pairing code: (\S+)/.exec(made)![1])
  await screen.getByTestId('pair-name-input').fill('Addresses gateway')
  await screen.getByTestId('pair-button').click()
  const row = screen.getByTestId('instance-row-Addresses gateway')
  await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await row.getByTestId('instance-open-link').click()
  await screen.getByTestId('rail-apps').click()
  await expect(screen.getByTestId('gateway-apps')).toBeVisible()
})

test.afterAll(async () => { await screen?.context().close(); gateway?.kill() })

test('a subdomain is added in the instance editor and routes by name, a name that is not the instance\'s is refused in the Gateway\'s words, and removing it stops the routing', async () => {
  // before: the name is nobody's, the apps host answers with its own words (and the path form works)
  expect((await asked('sales.localhost')).body).toContain(fallbackWords)
  await expect.poll(async () => (await asked('sales.localhost', '/apps/sales/')).status, { timeout: 20_000 }).not.toBe(404)

  // 1. the name the instance owns: added in the Addresses section, saved, routed by Host within the poll interval
  await expect(screen.getByTestId('instance-row-sales')).toBeVisible({ timeout: 20_000 })
  await screen.getByTestId('instance-edit-sales').click()
  await expect(screen.getByTestId('instance-editor-addresses-none')).toBeVisible()
  await screen.getByTestId('instance-editor-address-add').click()
  await expect(screen.getByTestId('instance-editor-address-host-0')).toHaveValue('sales.')
  await screen.getByTestId('instance-editor-address-host-0').fill('wrong.localhost')
  await screen.getByTestId('instance-editor-save').click()
  await expect(screen.getByTestId('error-banner')).toContainText("The subdomain wrong.localhost is not the instance's: a subdomain's first label is the instance name")   // the instance library's words, which come before the host's own check of the domain
  expect((await asked('wrong.localhost')).body).toContain(fallbackWords)   // nothing was saved: the name is still nobody's

  await screen.getByTestId('instance-editor-address-host-0').fill('sales.localhost')
  await screen.getByTestId('instance-editor-address-primary-0').check()
  await screen.getByTestId('instance-editor-save').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('sales is saved')
  await expect.poll(async () => (await asked('sales.localhost')).status, { timeout: 20_000 }).toBe(302)   // the instance's page: no session, so the identity home's sign-in
  const routed = await asked('sales.localhost')
  expect(routed.body).not.toContain(fallbackWords)
  expect((await asked('other.localhost')).body).toContain(fallbackWords)                                   // a name nobody bound is still nobody's

  // 2. removed: the editor shows it, the save drops it and the name goes back to nobody's
  await screen.getByTestId('instance-edit-sales').click()
  await expect(screen.getByTestId('instance-editor-address-host-0')).toHaveValue('sales.localhost')
  await screen.getByTestId('instance-editor-address-remove-0').click()
  await expect(screen.getByTestId('instance-editor-addresses-none')).toBeVisible()
  await screen.getByTestId('instance-editor-save').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('sales is saved')
  await expect.poll(async () => (await asked('sales.localhost')).body, { timeout: 20_000 }).toContain(fallbackWords)

  // 3. a custom domain is proved with a DNS record (AP08): Verify shows the TXT record to create, and a name that is nobody's here (`.invalid` never resolves) is not accepted
  await screen.getByTestId('instance-edit-sales').click()
  await screen.getByTestId('instance-editor-address-add').click()
  await screen.getByTestId('instance-editor-address-kind-0').selectOption('custom')
  await screen.getByTestId('instance-editor-address-host-0').fill('reports.invalid')
  await screen.getByTestId('instance-editor-address-verify-0').click()
  await expect(screen.getByTestId('instance-editor-address-verify-result-0')).toHaveAttribute('data-verified', 'false', { timeout: 30_000 })
  await expect(screen.getByTestId('instance-editor-address-unverified-0')).toContainText('does not resolve')
  await expect(screen.getByTestId('instance-editor-address-challenge-name-0')).toHaveText('_dbdatakit-challenge.reports.invalid')
  await expect(screen.getByTestId('instance-editor-address-challenge-value-0')).toHaveText(/^dbdatakit-domain-verification=[0-9a-f]{32}$/)
  // an edited name is a new question: the answer about the old one goes away
  await screen.getByTestId('instance-editor-address-host-0').fill('reports2.invalid')
  await expect(screen.getByTestId('instance-editor-address-verify-result-0')).toHaveCount(0)
})
