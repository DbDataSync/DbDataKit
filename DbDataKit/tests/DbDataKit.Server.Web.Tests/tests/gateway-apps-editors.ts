import { expect, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// The fixture of `gateway-apps-editors.spec.ts` (plan 33, UE01 to UE03): a real Gateway with its identity home and its apps host, in a directory and on ports of its own (8520 is reserved for its Flight SQL listener, which its
// modules do not start; 8521 is the management API, 8522 the identity home, 8523 the apps host), and the pairing of the console with it as a person who may publish and approve.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
export const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')
export const gatewayDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-gateway-apps-editors')
export const managementPort = 8521
export const homePort = 8522
export const appsPort = 8523
export const gatewayUrl = `https://localhost:${managementPort}`
export const appsUrl = `https://localhost:${appsPort}`

let gateway: ChildProcess | undefined

/** A command of the Gateway's command line on its directory, as an operator runs it; the JSON document's data. */
export function run(args: string[]): Record<string, any> {
  const text = execFileSync('dotnet', ['exec', gatewayDll, ...args, '--repo', gatewayDirectory, '--format', 'json'], { encoding: 'utf8' })
  return JSON.parse(text).data
}

/** The status of a GET without following a redirect (the apps host redirects a person who has no session to the identity home, and answers 404 for an instance it does not serve). */
export function statusOf(url: string) {
  return new Promise<number>((resolve) => {
    https.get(url, { rejectUnauthorized: false }, (response) => { response.resume(); resolve(response.statusCode ?? 0) }).on('error', () => resolve(0))
  })
}

/** A folder holding one app, written from its files. */
export function writeFolder(name: string, files: Record<string, string>) {
  const folder = path.join(os.tmpdir(), `dbdatakit-console-e2e-gateway-apps-editors-${name}`)
  fs.rmSync(folder, { recursive: true, force: true })
  for (const [file, text] of Object.entries(files)) {
    fs.mkdirSync(path.dirname(path.join(folder, file)), { recursive: true })
    fs.writeFileSync(path.join(folder, file), text)
  }
  return folder
}

export const manifest = (version: string, models = ['territories']) => [
  'schemaVersion: 1', 'name: notes', 'title: Notes', `version: ${version}`, 'entry: index.html', 'roles: [viewer]',
  'models:', ...models.flatMap((m) => [`  - model: ${m}`, '    entities: {row: [read, write]}']), '',
].join('\n')
export const page = (text: string) => `<!doctype html><html><head><meta charset="utf-8"><title>Notes</title></head><body>${text}</body></html>\n`

/** Starts the Gateway with the app `notes` published twice (1.0.0 uses `territories`, 1.1.0 also `people`) and no instance. */
export async function startGateway() {
  fs.rmSync(gatewayDirectory, { recursive: true, force: true })
  fs.mkdirSync(path.join(gatewayDirectory, 'config'), { recursive: true })
  const config = path.join(gatewayDirectory, 'config', 'server.yaml')
  fs.writeFileSync(config, [
    'schemaVersion: 2', 'tls: { mode: selfSigned, hostnames: [localhost] }', 'modules: [identity, apps]', 'definitionPollSeconds: 1',
    `management: { enabled: true, address: 127.0.0.1, port: ${managementPort} }`,
    `identity: { address: 127.0.0.1, port: ${homePort}, publicUrl: 'https://localhost:${homePort}' }`,
    `apps: { address: 127.0.0.1, port: ${appsPort}, home: { clientId: '__CLIENT__' } }`, '',
  ].join('\n'))
  const registered = run(['identity', 'client-add', '--name', 'Apps host', '--public', '--redirect', `${appsUrl}/apps/-/callback`])
  fs.writeFileSync(config, fs.readFileSync(config, 'utf8').replace('__CLIENT__', registered.client_id))
  run(['apps', 'publish', '--dir', writeFolder('notes-1', { 'app.yaml': manifest('1.0.0'), 'index.html': page('notes one') }), '--note', 'first build'])
  run(['apps', 'publish', '--dir', writeFolder('notes-2', { 'app.yaml': manifest('1.1.0', ['territories', 'people']), 'index.html': page('notes two') }), '--note', 'second build'])
  gateway = spawn('dotnet', ['exec', gatewayDll, 'server', 'run', `--Gateway:DataDirectory=${gatewayDirectory}`], { stdio: 'ignore' })
  for (let i = 0; i < 120 && !(await statusOf(`${appsUrl}/health/live`) === 200 && await statusOf(`https://localhost:${homePort}/health/live`) === 200); i++) await new Promise((r) => setTimeout(r, 500))
}

export function stopGateway() { gateway?.kill() }

/** Pairs the signed-in console with the Gateway as a person who is an administrator and may approve, and opens its Apps screen. */
export async function pairAndOpenApps(screen: Page, name: string) {
  const made = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--max-role', 'admin,approver', '--repo', gatewayDirectory], { encoding: 'utf8' })
  await screen.goto('/instances')
  await screen.getByTestId('pair-url-input').fill(gatewayUrl)
  await screen.getByTestId('pair-code-input').fill(/Pairing code: (\S+)/.exec(made)![1])
  await screen.getByTestId('pair-name-input').fill(name)
  await screen.getByTestId('pair-button').click()
  const row = screen.getByTestId(`instance-row-${name}`)
  await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await row.getByTestId('instance-open-link').click()
  await screen.getByTestId('rail-apps').click()
  await expect(screen.getByTestId('gateway-apps')).toBeVisible()
}

/**
 * UE01 against the real Gateway: an administrator makes an instance of `notes` in the form (a binding, a setting, a title), the apps host serves it (404 before, a redirect to the identity home after, for a person with no
 * session), the form changes the setting, and the pin control in the list keeps what the form saved because the Gateway stored it: the row is read back from the Gateway each time.
 */
export async function instanceStory(screen: Page) {
  expect(await statusOf(`${appsUrl}/apps/sales/`)).toBe(404)
  await screen.getByTestId('instance-new').click()
  await screen.getByTestId('instance-editor-name').fill('sales')
  await screen.getByTestId('instance-editor-app').selectOption('notes')
  await screen.getByTestId('instance-editor-title').fill('Sales notes')
  await screen.getByTestId('instance-editor-binding-territories').fill('default')
  await screen.getByTestId('instance-editor-setting-add').click()
  await screen.getByTestId('instance-editor-setting-key-0').fill('embed.allowed_origins')
  await screen.getByTestId('instance-editor-setting-value-0').fill('https://portal.example.com/not-an-origin')
  await screen.getByTestId('instance-editor-save').click()
  await expect(screen.getByTestId('error-banner')).toContainText('is not an origin for embed.allowed_origins')   // the store's own words

  await screen.getByTestId('instance-editor-setting-value-0').fill('https://portal.example.com')
  await screen.getByTestId('instance-editor-save').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('sales is saved as instance version 1')
  await expect(screen.getByTestId('instance-config-sales')).toHaveText('embed.allowed_origins=https://portal.example.com, territories → default')
  await expect(screen.getByTestId('instance-row-sales').getByTestId('instance-runs')).toHaveText('follows the app: version 2')
  await expect(screen.getByTestId('instance-open-sales')).toHaveAttribute('href', `${appsUrl}/apps/sales/`)
  // the apps host serves it within its poll interval: no longer "No such app"
  await expect.poll(() => statusOf(`${appsUrl}/apps/sales/`), { timeout: 20_000 }).not.toBe(404)
  expect(run(['apps', 'instance-list']).instances.find((i: any) => i.name === 'sales')).toMatchObject({ app: 'notes', title: 'Sales notes' })

  // changing the setting in the form: the other things stay
  await screen.getByTestId('instance-edit-sales').click()
  await expect(screen.getByTestId('instance-editor-binding-territories')).toHaveValue('default')
  await screen.getByTestId('instance-editor-setting-value-0').fill('https://portal.example.com:8443')
  await screen.getByTestId('instance-editor-save').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('sales is saved as instance version 2')
  await expect(screen.getByTestId('instance-config-sales')).toHaveText('embed.allowed_origins=https://portal.example.com:8443, territories → default')

  // pinning from the list keeps the setting and the binding (the Gateway replaced the instance, and the screen sent them back)
  await screen.getByTestId('instance-pin-sales').selectOption('1')
  await screen.getByTestId('instance-pin-save-sales').click()
  await expect(screen.getByTestId('instance-row-sales').getByTestId('instance-runs')).toHaveText('pinned to version 1 (the app serves 2)')
  await expect(screen.getByTestId('instance-config-sales')).toHaveText('embed.allowed_origins=https://portal.example.com:8443, territories → default')
}

/**
 * UE02 against the real Gateway: the `territory-views` sample is published from its folder (version 1, served by an instance), published again with a change (version 2, which the instance follows); a trusted app is not
 * publishable until an approver of the identity home (a service account that holds the Approver role on this server) approves exactly its files, with one of the two origins it asks for; then it is published and served.
 */
export async function publishStory(screen: Page) {
  const sample = path.join(repoRoot, 'DbDataGateway/templates/apps/territory-views')
  await screen.getByTestId('publish-open').click()
  await screen.getByTestId('publish-folder').setInputFiles(sample)
  await expect(screen.getByTestId('publish-would')).toContainText('territory-views 1.0.0 would be version 1, sandboxed')
  await expect(screen.getByTestId('publish-diagnostics-none')).toBeVisible()
  await screen.getByTestId('publish-note').fill('first build, from the screen')
  await screen.getByTestId('publish-send').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('territory-views is published as version 1')
  await expect(screen.getByTestId('app-row-territory-views').getByTestId('app-serves')).toContainText('version 1 (1.0.0)')
  // what was sent is the folder: the hash the command line prints for it is the hash of the version the screen shows
  await expect(screen.getByTestId('version-row-1')).toContainText('first build, from the screen')
  run(['apps', 'instance-save', '--name', 'views', '--app', 'territory-views'])
  await expect.poll(() => statusOf(`${appsUrl}/apps/views/`), { timeout: 20_000 }).not.toBe(404)
  await expect(screen.getByTestId('instance-row-views').getByTestId('instance-runs')).toHaveText('follows the app: version 1', { timeout: 20_000 })   // made at the command line: the screen reads the instances every ten seconds

  // a change to the folder is the next version, and the instance that follows moves
  const changed = writeFolder('territory-views', {})
  fs.cpSync(sample, path.join(changed, 'territory-views'), { recursive: true })
  const manifestPath = path.join(changed, 'territory-views', 'app.yaml')
  fs.writeFileSync(manifestPath, fs.readFileSync(manifestPath, 'utf8').replace('version: 1.0.0', 'version: 1.1.0'))
  await screen.getByTestId('publish-open').click()
  await screen.getByTestId('publish-folder').setInputFiles(path.join(changed, 'territory-views'))
  await expect(screen.getByTestId('publish-would')).toContainText('territory-views 1.1.0 would be version 2')
  await screen.getByTestId('publish-send').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('territory-views is published as version 2')
  await expect(screen.getByTestId('instance-row-views').getByTestId('instance-runs')).toHaveText('follows the app: version 2')
  expect(run(['apps', 'list', '--app', 'territory-views']).apps.find((a: any) => a.name === 'territory-views').version).toBe(2)

  // a trusted app: grace is a person of the identity home with the Approver role on this server (its id is written when the server first pairs)
  const serverId = /instanceId: (\S+)/.exec(fs.readFileSync(path.join(gatewayDirectory, 'config', 'server.yaml'), 'utf8'))![1]
  const grace = run(['identity', 'service-account-add', '--name', 'grace'])
  run(['identity', 'assign', '--user', grace.id, '--server', serverId, '--roles', 'approver'])
  const vault = writeFolder('vault', {
    'app.yaml': ['schemaVersion: 1', 'name: vault', 'title: Vault', 'version: 1.0.0', 'entry: index.html', 'trust: trusted', 'roles: [viewer]', 'egress:', '  connect: [https://api.example.com]', '  img: [https://cdn.example.com]', ''].join('\n'),
    'index.html': page('vault'),
  })
  expect(() => run(['apps', 'publish', '--dir', vault])).toThrow()                      // the command line is refused the same way
  await screen.getByTestId('publish-open').click()
  await screen.getByTestId('publish-folder').setInputFiles(vault)
  await expect(screen.getByTestId('publish-would')).toContainText('vault 1.0.0 would be version 1, asking to be trusted')
  await expect(screen.getByTestId('publish-send')).toBeDisabled()
  await expect(screen.getByTestId('publish-why')).toContainText('asks to be trusted and no approval stands for these exact files')
  await screen.getByTestId('publish-approve-by').fill('grace')
  await screen.getByTestId('publish-egress-connect=https://api.example.com').check()
  await screen.getByTestId('publish-approve-note').fill('reviewed the origin')
  await screen.getByTestId('publish-approve-send').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('Approved vault')
  await expect(screen.getByTestId('publish-send')).toBeEnabled()
  expect(run(['apps', 'approval-list']).approvals.length).toBeGreaterThan(0)
  await screen.getByTestId('publish-send').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('vault is published as version 1')
  await expect(screen.getByTestId('app-row-vault')).toContainText('trusted')
  // the approval names the one origin that was chosen, not the other the manifest asked for
  await expect(screen.locator('[data-testid^="approval-row-"]').filter({ hasText: 'vault' }).getByTestId('approval-for')).toHaveText('for connect https://api.example.com')
  run(['apps', 'instance-save', '--name', 'safe', '--app', 'vault'])
  await expect.poll(() => statusOf(`${appsUrl}/apps/safe/`), { timeout: 20_000 }).not.toBe(404)
  await expect(screen.getByTestId('instance-row-safe').getByTestId('instance-trust')).toContainText('trusted, approved', { timeout: 20_000 })   // made at the command line, as above
}

/**
 * UE03 against the real Gateway: a navigation set of two instances is made in the editor (members added from a list, one moved up), renamed (the old name stops being served in the same call), refused by the store in its own
 * words when a member was removed meanwhile (at the command line), changed again and removed. The command line reads each result from the Gateway's state, so the screen and the verbs agree.
 */
export async function navigationStory(screen: Page) {
  run(['apps', 'instance-save', '--name', 'nav-a', '--app', 'notes', '--title', 'First'])
  run(['apps', 'instance-save', '--name', 'nav-b', '--app', 'notes', '--title', 'Second'])
  // the screen reads the instances every ten seconds
  await expect(screen.getByTestId('instance-row-nav-b')).toBeVisible({ timeout: 20_000 })
  const sets = () => run(['apps', 'nav-list']).sets as { name: string; title: string; instances: string[] }[]

  await screen.getByTestId('nav-new').click()
  await screen.getByTestId('nav-editor-name').fill('menu')
  await screen.getByTestId('nav-editor-title').fill('Menu')
  await screen.getByTestId('nav-editor-add').selectOption('nav-b')
  await screen.getByTestId('nav-editor-add').selectOption('nav-a')
  await screen.getByTestId('nav-editor-label-nav-b').fill('Second one')
  await screen.getByTestId('nav-editor-up-nav-a').click()
  await screen.getByTestId('nav-editor-save').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('Navigation set menu is saved (version 1)')
  await expect(screen.getByTestId('nav-row-menu').getByTestId('nav-entries')).toHaveText('nav-a → nav-b (Second one)')
  expect(sets().find((s) => s.name === 'menu')).toMatchObject({ title: 'Menu', instances: ['nav-a', 'nav-b'] })
  await expect(screen.getByTestId('instance-row-nav-a')).toContainText('in the menu menu')

  // renamed: the new name is served and the old one is not
  await screen.getByTestId('nav-edit-menu').click()
  await screen.getByTestId('nav-editor-name').fill('menu2')
  await screen.getByTestId('nav-editor-save').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('Navigation set menu2 is saved (version 1, renamed from menu)')
  await expect(screen.getByTestId('nav-row-menu')).toHaveCount(0)
  expect(sets().map((s) => s.name)).toContain('menu2')
  expect(sets().map((s) => s.name)).not.toContain('menu')

  // a member removed at the command line while the editor is open: the store refuses the set, in its words, and the editor stays
  await screen.getByTestId('nav-edit-menu2').click()
  run(['apps', 'instance-remove', '--name', 'nav-b'])
  await screen.getByTestId('nav-editor-save').click()
  await expect(screen.getByTestId('error-banner')).toContainText("'nav-b' is not an instance published to prod")
  await expect(screen.getByTestId('nav-editor')).toBeVisible()
  await screen.getByTestId('nav-editor-remove-nav-b').click()
  await screen.getByTestId('nav-editor-save').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('Navigation set menu2 is saved')
  expect(sets().find((s) => s.name === 'menu2')!.instances).toEqual(['nav-a'])

  // removed after a second click; the instance it listed is still served
  await screen.getByTestId('nav-remove-menu2').click()
  await screen.getByTestId('nav-remove-confirm-menu2').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('Navigation set menu2 is no longer served')
  expect(sets().map((s) => s.name)).not.toContain('menu2')
  expect(run(['apps', 'instance-list']).instances.map((i: any) => i.name)).toContain('nav-a')
}
