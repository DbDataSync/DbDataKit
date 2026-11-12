import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Plan 32 DBR33, the apps host's half: a custom page of an app is opened at /apps/<instance>/_views/<name>?<parameters>. The shell's page frames the app's own document (sandboxed, `#view=RegionalPage&mode=screen`), the
// frame says `views:hello`, the shell fetches the rows as the person from `.../_rows` and answers `views:init`; the code fetches nothing. The app is the SDK's fixture app (`npm run build:fixture-app -w @dbdatakit/visuals`: a
// page `RegionalPage` of three sections), published with a manifest that declares it as the page `regional-detail`. Ada (a role on the app) sees the page draw in its frame and the shell mark it ready; a wrong parameter
// is a notice in the shell's own page and the frame is never started; Bob (no role) is refused; the instance feed lists the page by its manifest entry. Needs the built Gateway (as `apps-home-blocks.spec.ts`) and the built
// fixture app (DBDATAKIT_TEST_FIXTURE_APP names its folder; the default is Shared/web/visuals/dist/fixture-app), ports 8701 (identity home), 8702 (apps) and 8703 (management).

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')
const fixtureApp = process.env.DBDATAKIT_TEST_FIXTURE_APP ?? path.join(repoRoot, 'Shared/web/visuals/dist/fixture-app')

const root = path.join(os.tmpdir(), 'dbdatakit-e2e-apps-pages')
const gatewayDirectory = path.join(root, 'gateway')
const homePort = 8701
const appsPort = 8702
const managementPort = 8703
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

const manifest = [
  'schemaVersion: 1', 'name: regional', 'title: Regional', 'version: 1.0.0', 'entry: index.html', 'roles: [viewer]',
  'views:',
  '  - name: regional-detail', '    kind: page', '    export: RegionalPage', '    title: Regional detail', '    formats: [pdf, png]',
  '    params:', '      - {name: region, type: string}', '      - {name: top, type: integer}', '',
].join('\n')

test.describe.configure({ mode: 'serial' })
test.setTimeout(240_000)

test.beforeAll(async () => {
  if (!fs.existsSync(path.join(fixtureApp, 'app.js'))) throw new Error(`The fixture app is not built: run \`npm run build:fixture-app -w @dbdatakit/visuals\` (looked in ${fixtureApp}).`)
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

  // the app: the fixture's built files and a manifest that declares its page
  const directory = path.join(root, 'regional')
  fs.mkdirSync(directory, { recursive: true })
  for (const file of fs.readdirSync(fixtureApp)) fs.copyFileSync(path.join(fixtureApp, file), path.join(directory, file))
  fs.writeFileSync(path.join(directory, 'app.yaml'), manifest)
  run(['apps', 'publish', '--dir', directory])
  run(['apps', 'instance-save', '--name', 'regional', '--app', 'regional'])

  const gateway = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`])
  await waitFor(`${homeUrl}/health/live`, gateway.output)
  await waitFor(`${appsUrl}/health/live`, gateway.output)
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

test('a custom page opens at its address, draws in the app\'s own sandboxed frame with the rows the shell fetched as the person, and refuses a wrong parameter in words', async ({ browser }) => {
  const adaContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const ada = await adaContext.newPage()
  const adaUser = await enrol(adaContext, ada, 'Ada Lovelace', 'ada@example.com')
  run(['identity', 'assign', '--user', adaUser.id, '--app', 'regional', '--roles', 'viewer'])
  await signIn(adaContext, ada, 'Ada Lovelace')

  const bobContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const bob = await bobContext.newPage()
  await enrol(bobContext, bob, 'Bob Barker', 'bob@example.com')
  await signIn(bobContext, bob, 'Bob Barker')

  // Ada: the shell's page, the app's document in a sandboxed frame mounted on the page's view, the page drawn by the code, and the shell marks the frame ready
  await ada.goto(`${appsUrl}/apps/regional/_views/regional-detail?region=West&top=3`)
  const frame = ada.locator('iframe[data-view-page="regional-detail"]')
  await expect(frame).toHaveAttribute('sandbox', 'allow-scripts allow-forms allow-popups')
  await expect(frame).toHaveAttribute('src', '/apps/regional/_app/#view=RegionalPage&mode=screen')
  await expect(ada.frameLocator('iframe[data-view-page]').getByRole('heading', { name: 'Section 3' })).toBeVisible()
  await expect(frame).toHaveAttribute('data-state', 'ready')
  await expect(ada.locator('[data-view-notice]')).toHaveCount(0)

  // the rows are the host's: the shell's header is needed, the parameters come back typed, and the page's own address keeps them
  const rows = await ada.evaluate(async () => {
    const r = await fetch('/apps/regional/_views/regional-detail/_rows?region=West&top=3&nobody=declared', { credentials: 'same-origin', headers: { 'X-DbData-Shell': '1' } })
    const without = await fetch('/apps/regional/_views/regional-detail/_rows', { credentials: 'same-origin' })
    return { status: r.status, body: await r.json(), without: without.status }
  })
  expect(rows).toEqual({ status: 200, body: { data: {}, params: { region: 'West', top: 3 } }, without: 400 })
  expect(new URL(ada.url()).search).toBe('?region=West&top=3')

  // a wrong parameter is the shell's own notice, in words; the frame is never started
  await ada.goto(`${appsUrl}/apps/regional/_views/regional-detail?top=ten`)
  await expect(ada.locator('[data-view-notice]')).toContainText('is an integer, not \'ten\'')
  await expect(ada.locator('iframe[data-view-page]')).not.toHaveAttribute('data-state', 'ready')
  expect((await ada.request.get(`${appsUrl}/apps/regional/_views/nothing`)).status()).toBe(404)

  // the instance feed lists the page by its manifest entry
  const feed = await (await ada.request.get(`${appsUrl}/apps/catalog`)).json()
  expect(feed.instances[0].pages).toEqual([{ name: 'regional-detail', title: 'Regional detail', address: '/apps/regional/_views/regional-detail', formats: ['pdf', 'png'], parameters: ['region', 'top'] }])

  // Bob has no role on the app: the page is refused and so are its rows
  expect((await bob.goto(`${appsUrl}/apps/regional/_views/regional-detail`))!.status()).toBe(403)
  expect((await bob.request.get(`${appsUrl}/apps/regional/_views/regional-detail/_rows`, { headers: { 'X-DbData-Shell': '1' } })).status()).toBe(403)

  await adaContext.close()
  await bobContext.close()
})
