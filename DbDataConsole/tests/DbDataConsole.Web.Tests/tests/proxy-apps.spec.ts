import { expect, test } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { signInNewAdmin } from './people'

// Plan 26, AP24: the Apps screen of the Proxy's screens, shown by the console through its relay. A Proxy that serves the identity home and the apps host (started here, with its own directory and ports 8516 to 8519) has
// published apps, instances, a portal, a navigation set and a trusted app with an approval. An administrator who also holds the Approver role (the pairing's ceiling) sees them all with their versions and addresses, rolls
// the app back and pins an instance from the screen, sees why a mounted instance cannot be removed, sees what a version can reach, revokes an approval as a person of the identity home who holds the Approver role, and
// approves the same files again, refused as long as the approver is the author. Needs no PostgreSQL and no Store (nothing here reads data).
// It invites a console administrator (`user invite`), so it must sort after `identity-home.spec.ts`: the specs run one at a time in the order of their file names, and `proxy-apps` does (`pending-servers`, `proxy-apps`, `runner`).
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const proxyDll = path.join(repoRoot, 'DbDataProxy/src/DbDataProxy.Server/bin/Debug/net10.0/dbdataproxy.dll')

const proxyDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-proxy-apps')
// 8516 is reserved for this Proxy's Flight SQL listener, which its modules (identity, apps) do not start
const managementPort = 8517
const homePort = 8518
const appsPort = 8519
const proxyUrl = `https://localhost:${managementPort}`
const homeUrl = `https://localhost:${homePort}`
const appsUrl = `https://localhost:${appsPort}`
const author = os.userInfo().username   // the local command line records the operating-system user as the author

let proxy: ChildProcess | undefined

/** A command of the Proxy's command line on its directory, as an operator runs it; the JSON document's data. */
function run(args: string[]): Record<string, any> {
  const text = execFileSync('dotnet', ['exec', proxyDll, ...args, '--repo', proxyDirectory, '--format', 'json'], { encoding: 'utf8' })
  return JSON.parse(text).data
}

function answers(url: string) {
  return new Promise<boolean>((resolve) => {
    https.get(url, { rejectUnauthorized: false }, (response) => { response.resume(); resolve(response.statusCode === 200) }).on('error', () => resolve(false))
  })
}

/** A folder holding one app, written from its files, published with the command line. */
function publish(name: string, files: Record<string, string>, note?: string) {
  const folder = path.join(os.tmpdir(), `dbdatakit-console-e2e-proxy-apps-${name}`)
  fs.rmSync(folder, { recursive: true, force: true })
  fs.mkdirSync(folder, { recursive: true })
  for (const [file, text] of Object.entries(files)) fs.writeFileSync(path.join(folder, file), text)
  return run(['apps', 'publish', '--dir', folder, ...(note ? ['--note', note] : [])])
}

const manifest = (version: string) => [
  'schemaVersion: 1', 'name: notes', 'title: Notes', `version: ${version}`, 'entry: index.html', 'roles: [viewer]',
  'models:', '  - model: territories', '    entities: {territory: [read, write]}', '',
].join('\n')
const page = (text: string) => `<!doctype html><html><head><meta charset="utf-8"><title>Notes</title></head><body>${text}</body></html>\n`

test.beforeAll(async () => {
  fs.rmSync(proxyDirectory, { recursive: true, force: true })
  fs.mkdirSync(path.join(proxyDirectory, 'config'), { recursive: true })
  fs.writeFileSync(path.join(proxyDirectory, 'config', 'server.yaml'), [
    'schemaVersion: 2', 'tls: { mode: selfSigned, hostnames: [localhost] }', 'modules: [identity, apps]', 'definitionPollSeconds: 1',
    `management: { enabled: true, address: 127.0.0.1, port: ${managementPort} }`,
    `identity: { address: 127.0.0.1, port: ${homePort}, publicUrl: '${homeUrl}' }`,
    `apps: { address: 127.0.0.1, port: ${appsPort}, home: { clientId: '__CLIENT__' } }`, '',
  ].join('\n'))
  const registered = run(['identity', 'client-add', '--name', 'Apps host', '--public', '--redirect', `${appsUrl}/apps/-/callback`])
  const file = path.join(proxyDirectory, 'config', 'server.yaml')
  fs.writeFileSync(file, fs.readFileSync(file, 'utf8').replace('__CLIENT__', registered.client_id))

  // an app with two versions and two instances: `acme` follows it, `pinned` stays on the first; a portal that mounts `acme`; a menu of both
  publish('notes-1', { 'app.yaml': manifest('1.0.0'), 'index.html': page('notes one') }, 'first build')
  run(['apps', 'instance-save', '--name', 'acme', '--app', 'notes', '--title', 'Acme notes'])
  run(['apps', 'instance-save', '--name', 'pinned', '--app', 'notes', '--pin', '1'])
  publish('notes-2', { 'app.yaml': manifest('1.1.0'), 'index.html': page('notes two') }, 'second build')
  publish('front', { 'app.yaml': ['schemaVersion: 1', 'name: front', 'title: Front', 'version: 1.0.0', 'kind: portal', 'portal:', '  mounts: [{app: acme, path: /sales/}]', ''].join('\n') })
  run(['apps', 'instance-save', '--name', 'front', '--app', 'front'])
  run(['apps', 'nav-save', '--name', 'main', '--title', 'Main menu', '--item', 'acme=Acme,pinned=Pinned'])

  // a trusted app: approved by grace (a person of the identity home with the Approver role on this server) for these exact files, published, and instanced. The first attempt makes the server's own id, which the role names.
  const vault = path.join(os.tmpdir(), 'dbdatakit-console-e2e-proxy-apps-vault')
  fs.rmSync(vault, { recursive: true, force: true })
  fs.mkdirSync(vault, { recursive: true })
  fs.writeFileSync(path.join(vault, 'app.yaml'), ['schemaVersion: 1', 'name: vault', 'title: Vault', 'version: 1.0.0', 'entry: index.html', 'trust: trusted', 'roles: [viewer]', ''].join('\n'))
  fs.writeFileSync(path.join(vault, 'index.html'), page('vault'))
  expect(() => run(['apps', 'approve', '--path', vault, '--by', 'grace'])).toThrow()
  const serverId = /instanceId: (\S+)/.exec(fs.readFileSync(path.join(proxyDirectory, 'config', 'server.yaml'), 'utf8'))![1]
  const grace = run(['identity', 'service-account-add', '--name', 'grace'])
  run(['identity', 'assign', '--user', grace.id, '--server', serverId, '--roles', 'approver'])
  run(['apps', 'approve', '--path', vault, '--by', 'grace', '--note', 'reviewed'])
  run(['apps', 'publish', '--dir', vault])
  run(['apps', 'instance-save', '--name', 'safe', '--app', 'vault'])

  proxy = spawn('dotnet', ['exec', proxyDll, 'server', 'run', `--Proxy:DataDirectory=${proxyDirectory}`], { stdio: 'ignore' })
  for (let i = 0; i < 120 && !(await answers(`${appsUrl}/health/live`) && await answers(`${homeUrl}/health/live`)); i++) await new Promise((r) => setTimeout(r, 500))
})

test.afterAll(() => { proxy?.kill() })

test('an administrator who may approve sees the published apps in the console, rolls one back, pins an instance, revokes and approves, and the screen says why a button is off', async ({ page: screen, context, baseURL }) => {
  await signInNewAdmin(screen, context, baseURL!, 'Apps Tester', `apps-${Date.now()}@example.com`)
  const made = execFileSync('dotnet', ['exec', proxyDll, 'server', 'pair', '--max-role', 'admin,approver', '--repo', proxyDirectory], { encoding: 'utf8' })
  await screen.goto('/instances')
  await screen.getByTestId('pair-url-input').fill(proxyUrl)
  await screen.getByTestId('pair-code-input').fill(/Pairing code: (\S+)/.exec(made)![1])
  await screen.getByTestId('pair-name-input').fill('Apps proxy')
  await screen.getByTestId('pair-button').click()
  const row = screen.getByTestId('instance-row-Apps proxy')
  await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await row.getByTestId('instance-open-link').click()
  await screen.getByTestId('rail-apps').click()
  await expect(screen.getByTestId('proxy-apps')).toBeVisible()
  await expect(screen.getByTestId('apps-caller')).toContainText('You may roll back, pin and remove.')
  await expect(screen.getByTestId('apps-caller')).toContainText('You may approve and revoke.')

  // the apps with the version each serves, and the instances with the version each runs and the address to open
  await expect(screen.getByTestId('app-row-notes').getByTestId('app-serves')).toContainText('version 2 (1.1.0)')
  await expect(screen.getByTestId('app-row-vault')).toContainText('trusted')
  await expect(screen.getByTestId('instance-row-acme').getByTestId('instance-runs')).toHaveText('follows the app: version 2')
  await expect(screen.getByTestId('instance-row-pinned').getByTestId('instance-runs')).toHaveText('pinned to version 1 (the app serves 2)')
  await expect(screen.getByTestId('instance-open-acme')).toHaveAttribute('href', `${appsUrl}/apps/acme/`)
  await expect(screen.getByTestId('instance-row-acme')).toContainText('in the menu main')
  await expect(screen.getByTestId('instance-row-front').getByTestId('instance-portal')).toContainText('mounts acme')
  await expect(screen.getByTestId('instance-row-safe').getByTestId('instance-trust')).toContainText('trusted, approved')
  await expect(screen.getByTestId('nav-row-main').getByTestId('nav-entries')).toHaveText('acme (Acme) → pinned (Pinned)')

  // an instance a portal mounts cannot be removed, and the button says why; neither can an app that still has instances
  await expect(screen.getByTestId('instance-remove-acme')).toBeDisabled()
  await expect(screen.getByTestId('instance-remove-why-acme')).toContainText('The portal front mounts acme')
  await expect(screen.getByTestId('app-remove-notes')).toBeDisabled()
  await expect(screen.getByTestId('app-remove-why-notes')).toContainText('still has instances')

  // what a version can reach: the manifest's models and who published it
  await screen.getByTestId('app-open-notes').click()
  await screen.getByTestId('version-about-2').click()
  await expect(screen.getByTestId('about-models')).toContainText('territories.territory: read and write')
  await expect(screen.getByTestId('about-published-by')).toContainText(author)

  // rolling back from the screen changes what the unpinned instance serves, and the command line sees the same
  await screen.getByTestId('version-serve-1').click()
  await expect(screen.getByTestId('apps-notice')).toHaveText('notes now serves version 1.')
  await expect(screen.getByTestId('instance-row-acme').getByTestId('instance-runs')).toHaveText('follows the app: version 1')
  await expect(screen.getByTestId('instance-row-pinned').getByTestId('instance-runs')).toHaveText('pinned to version 1')
  expect(run(['apps', 'list']).apps.find((a: any) => a.name === 'notes').version).toBe(1)
  await expect(screen.getByTestId('version-serve-1')).toBeDisabled()

  // pinning an instance to version 2, which the app no longer serves
  await screen.getByTestId('instance-pin-acme').selectOption('2')
  await screen.getByTestId('instance-pin-save-acme').click()
  await expect(screen.getByTestId('instance-row-acme').getByTestId('instance-runs')).toHaveText('pinned to version 2 (the app serves 1)')

  // revoking an approval needs a person of the identity home who holds the Approver role: grace; the instance stops running as trusted
  const approval = screen.locator('[data-testid^="approval-row-"]').first()
  await expect(approval.getByTestId('approval-status')).toHaveText('standing')
  await expect(approval).toContainText('grace')
  await screen.getByTestId('approver-name').fill('grace')
  await approval.locator('[data-testid^="approval-revoke-"]').first().click()
  await approval.locator('[data-testid^="approval-revoke-confirm-"]').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('Revoked')
  await expect(screen.locator('[data-testid^="approval-row-"]').first().getByTestId('approval-status')).toContainText('revoked')
  await expect(screen.getByTestId('instance-row-safe').getByTestId('instance-trust')).toContainText('no approval stands')

  // approving the same files again: the author (the local command line's user) may not approve, grace may
  await screen.getByTestId('app-open-vault').click()
  await screen.getByTestId('version-approve-1').click()
  await screen.getByTestId('approver-name').fill(author)
  await expect(screen.getByTestId('approve-why')).toContainText('two people')
  await expect(screen.getByTestId('approve-confirm-1')).toBeDisabled()
  await screen.getByTestId('approver-name').fill('grace')
  await screen.getByTestId('approve-note').fill('looked again')
  await screen.getByTestId('approve-confirm-1').click()
  await expect(screen.getByTestId('apps-notice')).toContainText('Approved')
  await expect(screen.getByTestId('instance-row-safe').getByTestId('instance-trust')).toContainText('trusted, approved')

  // the approvals' history keeps both records: the revoked one and the new one
  await expect(screen.locator('[data-testid^="approval-row-"]')).toHaveCount(2)
  expect(run(['apps', 'approval-list']).approvals.map((a: any) => a.revoked).sort()).toEqual([false, true])
})
