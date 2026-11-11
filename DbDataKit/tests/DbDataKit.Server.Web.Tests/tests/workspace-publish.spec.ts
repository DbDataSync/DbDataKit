import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { scratchDirectory, workspacePublish } from '../playwright.config'

// Plan 22a GP35 (the done-when of the row): a person makes an app from the template in a workspace, edits one of its spec files, builds it, previews the build, commits, merges and publishes it to a Gateway, and then opens the
// instance's address on that Gateway and sees the change. The whole workflow is on the console's pages as the signed-in administrator: the Workspace server (`dbdataworkspace`, port 8631) hosts the project `sales`, which is the app
// template; the build runs Bun from the vendored package store (made here with `app-store/build.mjs`: `npm ci` in Shared/web first) and the server's `apps:` section; the Gateway (identity home 8632, apps host 8633, management 8634) is
// started by the story, paired with the Workspace server on the publisher keys page with a single-use code, and the instance is made with the Gateway's own command. It also shows what the screens must show on the way: a refused import
// with its file and line, whether the build was isolated, the preview with no network, the refusal of a publish in the server's words.
// It invites an administrator, so it sorts after identity-home (`workspace-files` before it, `workspaces` after, the order of the file names). `DBDATAKIT_TEST_E2E_GATEWAY_DLL` names a Gateway build outside its own bin folder.

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')
const gatewayDll = process.env.DBDATAKIT_TEST_E2E_GATEWAY_DLL ?? path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')
const serverName = 'Workspace publish e2e'
const { gateway } = workspacePublish
const homeUrl = `https://localhost:${gateway.homePort}`
const appsUrl = `https://localhost:${gateway.appsPort}`
const managementUrl = `https://localhost:${gateway.managementPort}`
const title = 'Sales territories'

const running: ChildProcess[] = []

function start(args: string[]) {
  const child = spawn('dotnet', ['exec', ...args], { stdio: ['ignore', 'pipe', 'pipe'] })
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
function gatewayCommand(args: string[]): Record<string, any> {
  const text = execFileSync('dotnet', ['exec', gatewayDll, ...args, '--repo', gateway.directory, '--format', 'json'], { encoding: 'utf8' })
  return JSON.parse(text).data
}

async function addAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
}

async function signInAsAdmin(page: Page, context: BrowserContext, baseURL: string) {
  await addAuthenticator(context, page)
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `workspace-publish-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Workspace Publisher')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

/** Saves what is in the editor. */
async function saveEditor(page: Page) {
  await expect(page.getByTestId('wsp-save')).toBeEnabled()
  await page.getByTestId('wsp-save').click()
  await expect(page.getByTestId('wsp-dirty')).toHaveCount(0)
}

/** Puts a file's whole text in the editor (select all, then type over it) and saves it. For text with no indentation: the editor indents what is typed after a line that is indented. */
async function writeInEditor(page: Page, text: string) {
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.press('Control+A')
  await page.keyboard.insertText(text)
  await saveEditor(page)
}

/** Replaces the whole of one line (1-based) of the open file: the line is selected and typed over, so the lines around it are as they were. */
async function replaceLine(page: Page, line: number, text: string) {
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.press('Control+Home')
  for (let i = 1; i < line; i++) await page.keyboard.press('ArrowDown')
  await page.keyboard.press('Home')
  await page.keyboard.press('Shift+End')
  await page.keyboard.insertText(text)
  await saveEditor(page)
}

test.describe.configure({ mode: 'serial' })
test.setTimeout(420_000)

test.beforeAll(async () => {
  // the vendored package store the Workspace server builds from (the server's `apps:` section names this folder)
  execFileSync('node', ['app-store/build.mjs', '--out', workspacePublish.packageStore], { cwd: path.join(repoRoot, 'Shared/web'), stdio: 'inherit' })

  // the Gateway the app is published to: the identity home and the apps host, the apps host's client registered at the home
  fs.mkdirSync(path.join(gateway.directory, 'config'), { recursive: true })
  const settings = path.join(gateway.directory, 'config', 'server.yaml')
  fs.writeFileSync(settings, [
    'schemaVersion: 2', 'tls: { mode: selfSigned, hostnames: [localhost] }', 'modules: [identity, apps]', 'definitionPollSeconds: 1',
    `management: { enabled: true, address: 127.0.0.1, port: ${gateway.managementPort} }`,
    `identity: { address: 127.0.0.1, port: ${gateway.homePort}, publicUrl: '${homeUrl}' }`,
    `apps: { address: 127.0.0.1, port: ${gateway.appsPort}, home: { clientId: '__CLIENT__' } }`, '',
  ].join('\n'))
  const registered = gatewayCommand(['identity', 'client-add', '--name', 'Apps host', '--public', '--redirect', `${appsUrl}/apps/-/callback`])
  fs.writeFileSync(settings, fs.readFileSync(settings, 'utf8').replace('__CLIENT__', registered.client_id))
  const server = start([gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gateway.directory}`])
  await waitFor(`${homeUrl}/health/live`, server.output)
  await waitFor(`${appsUrl}/health/live`, server.output)
  await waitFor(`${managementUrl}/health/live`, server.output)
})

test.afterAll(async () => {
  for (const child of running) child.kill()
})

test('an app is made from the template, a spec edited, built, previewed, committed, merged and published, and the instance on the Gateway shows the change', async ({ page, context, baseURL, browser }) => {
  await signInAsAdmin(page, context, baseURL!)
  const project = workspacePublish.project

  // ---- the console pairs with the Workspace server and opens it
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(workspacePublish.url)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'workspace-publish-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill(serverName)
  await page.getByTestId('pair-button').click()
  const row = page.getByTestId(`instance-row-${serverName}`)
  await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await row.getByTestId('instance-open-link').click()
  await expect(page.getByTestId('ws-projects')).toBeVisible()

  // ---- the publisher key: paired with the Gateway with its single-use code; the page shows a reference, never a key
  const code = /Pairing code: (\S+)/.exec(execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--repo', gateway.directory, '--max-role', 'publisher'], { encoding: 'utf8' }))![1]
  await page.getByTestId('rail-keys').click()
  await expect(page.getByTestId('wsk-none')).toBeVisible()
  await page.getByTestId('wsk-pair-name').fill('edge')
  await page.getByTestId('wsk-pair-url').fill('http://not-https')
  await expect(page.getByTestId('wsk-pair-why')).toContainText('https://')
  await expect(page.getByTestId('wsk-pair')).toBeDisabled()
  await page.getByTestId('wsk-pair-url').fill(managementUrl)
  await page.getByTestId('wsk-pair-code').fill(code)
  await page.getByTestId('wsk-pair').click()
  await expect(page.getByTestId('wsk-paired')).toContainText('Paired edge')
  const key = page.locator('[data-testid="wsk-key"][data-key="edge"]')
  await expect(key.getByTestId('wsk-ref')).toHaveText('secret:dbdata:workspace:publisher-key:edge')
  await expect(key.getByTestId('wsk-gateway')).toHaveText(managementUrl)
  // a key typed where a reference goes is refused as it is typed
  await page.getByTestId('wsk-ref-name').fill('other')
  await page.getByTestId('wsk-ref-value').fill('workspace-1a2b3c:c2VlZA')
  await expect(page.getByTestId('wsk-ref-why')).toContainText('"secret:"')
  await expect(page.getByTestId('wsk-ref-set')).toBeDisabled()
  expect(fs.readFileSync(path.join(workspacePublish.directory, 'publish-targets.json'), 'utf8')).not.toContain('seed')
  await page.getByTestId('rail-workspaces').click()

  // ---- a workspace in the app project (the template), open
  await page.getByTestId(`ws-new-name-${project}`).fill('edit-sales')
  await page.getByTestId(`ws-new-create-${project}`).click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-edit-sales"]')
  await expect(listed).toBeVisible()
  const id = (await listed.getAttribute('data-workspace'))!
  const folder = path.join(workspacePublish.directory, 'workspaces', project, id)
  await page.getByTestId(`ws-open-${id}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
  // the template's files are there, named for the project
  for (const file of ['app.yaml', 'index.html', 'package.json']) await expect(page.locator(`[data-testid="wsp-file"][data-path="${file}"]`)).toBeVisible()
  expect(fs.readFileSync(path.join(folder, 'app.yaml'), 'utf8')).toContain('name: sales')

  // ---- edit a spec: the grid's title
  const spec = path.join(folder, 'views', 'territories.yaml')
  const original = fs.readFileSync(spec, 'utf8').replace(/\r\n/g, '\n')
  expect(original).toContain('title: Territories\n')
  await page.locator('[data-testid="wsp-file"][data-path="views/territories.yaml"]').click().catch(async () => {
    await page.locator('[data-testid="wsp-folder"][data-path="views"]').click()
  })
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'views/territories.yaml')
  await replaceLine(page, original.split('\n').indexOf('title: Territories') + 1, `title: ${title}`)
  await expect.poll(() => fs.readFileSync(spec, 'utf8').replace(/\r\n/g, '\n')).toContain(`title: ${title}\n`)

  // ---- the builds tab: the server can build, and says how
  await page.getByTestId('wsp-tab-builds').click()
  await expect(page.getByTestId('wsp-readiness')).toHaveAttribute('data-ready', 'true')
  await expect(page.getByTestId('wsp-build-store')).toContainText('package store')
  await expect(page.getByTestId('wsp-builds-none')).toBeVisible()

  // ---- a source that imports what is not allowed: the build fails and names the file and the line
  await page.getByTestId('wsp-tab-changes').click()
  await page.getByTestId('wsp-new-path').fill('src/extra.ts')
  await page.getByTestId('wsp-new-file').click()
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'src/extra.ts')
  await writeInEditor(page, "import pad from 'left-pad'\nexport default pad\n")
  await page.getByTestId('wsp-tab-builds').click()
  await page.getByTestId('wsp-build').click()
  const failed = page.locator('[data-testid="wsp-build-detail"][data-status="failed"]')
  await expect(failed).toBeVisible({ timeout: 120_000 })
  const diagnostic = failed.locator('[data-testid="wsp-build-diagnostic"][data-file="src/extra.ts"]')
  await expect(diagnostic).toHaveAttribute('data-line', '1')
  await expect(diagnostic).toContainText('left-pad')
  await expect(page.getByTestId('wsp-preview-toggle')).toHaveCount(0)      // nothing to preview, nothing to publish
  await expect(page.getByTestId('wsp-publish')).toHaveCount(0)

  // ---- take it out again, build: it works, and the page says whether the bundler was isolated
  await page.getByTestId('wsp-tab-changes').click()
  await page.locator('[data-testid="wsp-file"][data-path="src/extra.ts"]').click()
  await page.getByTestId('wsp-delete').click()
  await page.getByTestId('wsp-delete-confirm').click()
  await expect(page.locator('[data-testid="wsp-file"][data-path="src/extra.ts"]')).toHaveCount(0)
  await page.getByTestId('wsp-tab-builds').click()
  await page.getByTestId('wsp-build').click()
  const built = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(built).toBeVisible({ timeout: 180_000 })
  await expect(built.getByTestId('wsp-build-made')).toContainText('sales 1.0.0')
  await expect(built.locator('[data-testid="wsp-build-isolated"], [data-testid="wsp-build-not-isolated"]')).toBeVisible()
  await expect(built.getByTestId('wsp-build-dirty')).toBeVisible()            // built from changes that are not committed yet
  await expect(page.locator('[data-testid="wsp-build-row"]')).toHaveCount(2)

  // ---- the preview: the built app in a sandboxed frame, with the changed title; read only, no network, no data
  await built.getByTestId('wsp-preview-toggle').click()
  const frame = page.frameLocator('[data-testid="wsp-preview-frame"]')
  await expect(frame.locator('nav.va-menu a').first()).toHaveText(title, { timeout: 30_000 })
  await expect(page.getByTestId('wsp-preview-frame')).toHaveAttribute('sandbox', 'allow-scripts')
  const buildId = (await built.getAttribute('data-build'))!
  const previewPath = `/relay/${await instanceIdOf(page)}/api/v1/projects/${project}/workspaces/${id}/builds/${buildId}/preview/`
  const served = await page.evaluate(async (p) => {
    const get = await fetch(p, { credentials: 'same-origin' })
    const put = await fetch(p + 'main.js', { method: 'PUT', credentials: 'same-origin', body: 'x' })
    const post = await fetch(p, { method: 'POST', credentials: 'same-origin', body: '{}' })
    return { csp: get.headers.get('content-security-policy'), nosniff: get.headers.get('x-content-type-options'), put: put.status, post: post.status }
  }, previewPath)
  expect(served.csp).toContain("connect-src 'none'")
  expect(served.csp).toContain('sandbox allow-scripts')
  expect(served.nosniff).toBe('nosniff')
  expect([served.put, served.post]).toEqual([403, 403])                         // only GET is a preview route: the relay does not pass anything else (and the server has no route for it)
  await built.getByTestId('wsp-preview-toggle').click()

  // ---- commit, then merge: the project's own copy has the change
  await page.getByTestId('wsp-tab-changes').click()
  await page.getByTestId('wsp-commit-message').fill('Call the territories grid Sales territories')
  await page.getByTestId('wsp-commit').click()
  await expect(page.getByTestId('wsp-committed')).toContainText('Committed')
  await expect(page.getByTestId('wsp-merge')).toBeEnabled()
  await page.getByTestId('wsp-merge').click()
  await expect(page.getByTestId('wsp-merged')).toBeVisible()
  expect(fs.readFileSync(path.join(workspacePublish.directory, 'projects', project, 'views', 'territories.yaml'), 'utf8').replace(/\r\n/g, '\n')).toContain(`title: ${title}\n`)
  expect(fs.existsSync(folder)).toBe(false)

  // ---- publish: the builds of the merged workspace are still there; an unknown key is refused in the server's words, then the real one publishes
  await expect(page.locator('[data-testid="wsp-build-row"]')).toHaveCount(2)
  await expect(page.getByTestId('wsp-build')).toBeDisabled()
  await expect(page.getByTestId('wsp-build-why')).toContainText('merged')
  const done = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(done).toBeVisible()
  await done.getByTestId('wsp-publish-target').fill('nowhere')
  await done.getByTestId('wsp-publish').click()
  await expect(done.getByTestId('wsp-publish-refused')).toContainText("There is no publisher key called 'nowhere'")
  await done.getByTestId('wsp-publish-target').fill('edge')
  await done.getByTestId('wsp-publish-note').fill('The first version, from the story')
  await done.getByTestId('wsp-publish').click()
  const published = done.getByTestId('wsp-published')
  await expect(published).toBeVisible({ timeout: 60_000 })
  await expect(published).toHaveAttribute('data-version', '1')
  await expect(published).toContainText(`to ${managementUrl}`)
  await expect(done.locator('[data-testid="wsp-publish-step"]').first()).toContainText('Read the built app')
  // the record of the build holds the refusal and the publish, with who and where
  await expect(done.locator('[data-testid="wsp-publish-record"][data-ok="false"]')).toContainText("There is no publisher key called 'nowhere'")
  await expect(done.locator('[data-testid="wsp-publish-record"][data-ok="true"]')).toContainText('Workspace Publisher to edge')

  // ---- the instance on the Gateway: an instance of the published app, opened as a person of the home, shows the change
  gatewayCommand(['apps', 'instance-save', '--name', 'sales', '--app', 'sales'])
  await new Promise((resolve) => setTimeout(resolve, 3000))        // the host reads the group's definitions every second
  await openInstance(browser)
})

/** The id the console gives the Workspace server in its relay's addresses. */
async function instanceIdOf(page: Page): Promise<string> {
  const match = /\/servers\/([^/]+)/.exec(new URL(page.url()).pathname)
  if (!match) throw new Error('the page is not a server page: ' + page.url())
  return match[1]
}

/** A person of the Gateway's identity home signs in with a passkey and opens the instance `sales`: its menu has the title that was edited in the workspace. */
async function openInstance(browser: Browser) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true })
  try {
    const page = await context.newPage()
    await addAuthenticator(context, page)
    const firstRun = fs.readFileSync(path.join(gateway.directory, 'identity', 'FIRST-RUN.txt'), 'utf8')
    await page.goto(`${homeUrl}/invite#${firstRun.split('/invite#')[1].split(/\s/)[0]}`)
    await page.getByTestId('identity-invite-name').fill('Ada Lovelace')
    await page.getByTestId('identity-invite-email').fill('ada@example.com')
    await page.getByTestId('identity-invite-enrol').click()
    await expect(page.getByTestId('identity-invite-done')).toHaveText('Signed in as Ada Lovelace.')
    const ada = gatewayCommand(['identity', 'user-list']).users.find((u: any) => u.name === 'Ada Lovelace')
    gatewayCommand(['identity', 'assign', '--user', ada.id, '--app', 'sales', '--roles', 'author'])
    await context.clearCookies({ domain: 'localhost' })
    await page.goto(`${homeUrl}/identity/sign-in`)
    await page.getByTestId('identity-sign-in-passkey').click()
    await expect(page.getByTestId('identity-signed-in')).toHaveText('Signed in as Ada Lovelace.')

    await page.goto(`${appsUrl}/apps/sales/`)
    const app = page.frameLocator('#app-frame')
    await expect(app.locator('nav.va-menu a').first()).toHaveText(title, { timeout: 30_000 })
    await expect(app.locator('.va-title')).toHaveText(title)
  } finally {
    await context.close()
  }
}
