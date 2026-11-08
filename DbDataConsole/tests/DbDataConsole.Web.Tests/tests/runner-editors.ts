import { expect, type Page, type Route } from '@playwright/test'
import { createHash } from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

// The Runner screens' editors (plan 33, UE08, UE10, UE11) against a runner whose management routes are mocked in the page, the way `gateway-apps-editors-screen.ts` does for the Gateway: the console's relay is its own route, so the story needs the
// signed-in console only and takes no port (the runner's side is DbDataRunner.Tests: ScreenRoutesTests and PublishApiTests, and the console's own side is ConsoleRunnerTests and EmbeddedRunnerTests). The mock keeps state, answers the routes the
// screens call with the words of the runner's own refusals, and keeps every body it is sent, so a story checks what the screen SENDS as well as what it shows.

export const RUNNER = 'runner-1'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

export interface MockBinding { channel: string; slot: string; bound_to: string; revision: number }

export interface MockRunner {
  /** Every body the screen sent, by `METHOD route`. */
  sent: Record<string, unknown[]>
  bindings: MockBinding[]
  requireSignatures: boolean
  /** The next bind call finds the slot changed by someone else (answered 409 as the runner does). */
  conflictNext: boolean
  /** Makes every write answer 403 as a runner does for a person without the role. */
  deny: boolean
  /** The hashes of the files the runner holds, and the order of the calls that asked for or sent them. */
  artifacts: Set<string>
  fileCalls: string[]
  /** What was published from files, by package name: the versions in the order they were made. */
  published: Map<string, { version: number; channel: string; mode: string; manifest: string; content: string }[]>
  lastBody: (key: string) => unknown | undefined
}

const SLOTS = [
  { channel: 'prod', slot: 'source', kind: 'sqlserver', tasks: ['check', 'load'], versions: [1, 2] },
  { channel: 'prod', slot: 'target', kind: 'postgres', tasks: ['load'], versions: [2] },
  { channel: 'test', slot: 'source', kind: 'sqlserver', tasks: ['load'], versions: [1] },
]

/** A runner with the package `orders`: version 1 serves on `prod` and `test`, version 2 is staged on `prod` and wants a second slot (`target`) that nothing binds yet. */
export async function mockRunner(page: Page): Promise<MockRunner> {
  const mock: MockRunner = {
    sent: {}, requireSignatures: false, conflictNext: false, deny: false, artifacts: new Set(), fileCalls: [], published: new Map(),
    bindings: [{ channel: 'prod', slot: 'source', bound_to: 'erp', revision: 1 }, { channel: 'test', slot: 'source', bound_to: 'erp-test', revision: 1 }],
    lastBody: (key) => mock.sent[key]?.at(-1),
  }
  const heads = [{ channel: 'prod', active: 1, previous: null, staged: 2 }, { channel: 'test', active: 1, previous: null, staged: null }]
  const detail = () => ({
    kind: 'integration', name: 'orders', owner: null, latest_version: 2,
    versions: [2, 1].map((v) => ({ version: v, hash: 'h' + v, published_by: 'ada', published_at: '2026-11-08T10:00:00Z', note: null, depends_on: [], files: 3 })),
    channels: heads,
    staging: [{ channel: 'prod', version: 2, summary: '0 of 1 runners ready', all_ready: false, failed: [], losses: [], needs_confirmation: false }],
    bindings: mock.bindings, slots: SLOTS, instances: [], events: [],
  })

  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: RUNNER, name: 'Orders runner', url: 'https://runner.invalid', product: 'dbdatarunner', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${RUNNER}/api/v1/**`, async (route) => {
    const request = route.request()
    const method = request.method()
    const tail = new URL(request.url()).pathname.slice(`/relay/${RUNNER}/api/v1/`.length)
    const parts = tail.split('/').map(decodeURIComponent)
    const body = method === 'GET' || method === 'HEAD' || parts[0] === 'artifacts' ? undefined : request.postDataJSON() as Record<string, any>
    if (method !== 'GET' && method !== 'HEAD' && parts[0] !== 'artifacts') (mock.sent[`${method} ${tail}`] ??= []).push(body ?? null)
    const refuse = (status: number, message: string) => json(route, { error: message }, status)
    const needsAdmin = () => refuse(403, 'This needs the admin role.')

    if (method === 'GET' && tail === 'status') return json(route, { product: 'dbdatarunner', version: '1', group: 'main', running: true, state_engine: 'sqlite', channels: ['prod', 'test'], capabilities: {}, tasks: [], instances: [], problems: [] })
    if (method === 'GET' && tail === 'connections') {
      return json(route, { connections: ['erp', 'erp-test', 'warehouse'].map((name) => ({ name, kind: 'sqlserver', secret_ref: null, roles: ['run'], used_by: [] })) })
    }
    if (method === 'GET' && tail === 'settings') return json(route, { require_signatures: mock.requireSignatures, max_artifact_bytes: 536870912 })
    if (method === 'PUT' && tail === 'settings/require_signatures') {
      if (mock.deny) return needsAdmin()
      mock.requireSignatures = body!.value
      return json(route, { require_signatures: mock.requireSignatures })
    }
    if (method === 'GET' && tail === 'definitions/integration') {
      const made = [...mock.published].map(([name, versions]) => {
        const last = versions.at(-1)!
        return { name, latest_version: last.version, owner: null, channels: [{ channel: last.channel, active: last.mode === 'serve' ? last.version : null, previous: null, staged: last.mode === 'stage' ? last.version : null }] }
      })
      return json(route, { kind: 'integration', definitions: [{ name: 'orders', latest_version: 2, owner: null, channels: heads }, ...made] })
    }
    if (method === 'GET' && tail === 'definitions/integration/orders') return json(route, detail())
    if (method === 'HEAD' && parts[0] === 'artifacts') {
      mock.fileCalls.push(`HEAD ${parts[1]!.slice(0, 8)}`)
      return route.fulfill({ status: mock.artifacts.has(parts[1]!) ? 200 : 404 })
    }
    if (method === 'PUT' && parts[0] === 'artifacts') {
      if (mock.deny) return needsAdmin()
      const named = parts[1]!
      const actual = createHash('sha256').update(request.postDataBuffer() ?? Buffer.alloc(0)).digest('hex')
      if (actual !== named) return refuse(400, `The bytes sent for ${named} hash to ${actual}.`)
      const already = mock.artifacts.has(named)
      mock.artifacts.add(named)
      mock.fileCalls.push(`PUT ${named.slice(0, 8)}`)
      return json(route, { sha256: named, size: request.postDataBuffer()?.length ?? 0, already_stored: already })
    }
    if (method === 'PUT' && parts[0] === 'definitions' && parts[1] === 'integration') {
      if (mock.deny) return needsAdmin()
      const name = parts[2]!
      if (mock.requireSignatures) return refuse(403, `This runner requires signed versions (require_signatures is on) and the integration '${name}' has no valid signature (the version is unsigned). Publish it signed, or have an administrator turn require_signatures off.`)
      const missing = (body!.artifacts as { sha256: string }[]).find((a) => !mock.artifacts.has(a.sha256))
      if (missing) return refuse(400, `The file ${missing.sha256} has not been uploaded.`)
      const versions = mock.published.get(name) ?? []
      const content = JSON.stringify([body!.manifest, body!.artifacts])
      const same = versions.at(-1)?.content === content
      const version = same ? versions.at(-1)!.version : versions.length + 1
      if (!same) { versions.push({ version, channel: body!.channel, mode: body!.mode, manifest: body!.manifest, content }); mock.published.set(name, versions) }
      const tasks = [...(body!.manifest as string).matchAll(/^ {2}- name: (\S+)/gm)].map((m) => m[1])
      return json(route, { kind: 'integration', name, channel: body!.channel, version, changed: !same, mode: body!.mode, signature: 'unsigned', tasks })
    }
    if (method === 'PUT' && parts[0] === 'bindings' && parts[2] === 'integration') {
      const channel = parts[1]!
      if (mock.deny) return needsAdmin()
      const slots = body!.slots as Record<string, string>
      if (mock.conflictNext) {
        mock.conflictNext = false
        const [slot] = Object.keys(slots)
        const row = mock.bindings.find((b) => b.channel === channel && b.slot === slot)!
        row.revision += 1
        return refuse(409, `The slot '${slot}' of integration '${parts[3]}' in channel '${channel}' is at revision ${row.revision}, not ${body!.expected_revision}: it was bound since you looked.`)
      }
      for (const [slot, to] of Object.entries(slots)) {
        if (!to.trim()) return refuse(400, `The slot '${slot}' must be bound to something.`)
        const row = mock.bindings.find((b) => b.channel === channel && b.slot === slot)
        if (row) { row.bound_to = to; row.revision += 1 } else mock.bindings.push({ channel, slot, bound_to: to, revision: 1 })
      }
      return json(route, { name: parts[3], channel, bindings: mock.bindings.filter((b) => b.channel === channel) })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return mock
}

export const bindingRow = (page: Page, channel: string, slot: string) =>
  page.locator(`[data-testid="runner-bindings"][data-channel="${channel}"] [data-testid="runner-binding-row"][data-slot="${slot}"]`)

/** UE08: bind the slots of a package per channel, and the require-signatures switch, with the runner's refusals shown in its words. */
export async function bindingsStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  const runner = await mockRunner(page)

  // ---- the publishing setting: off; turning it on says what it does while signatures are not built, and sends the value
  await page.goto(`/servers/${RUNNER}/packages`)
  await expect(page.getByTestId('runner-require-signatures')).toHaveAttribute('data-value', 'false')
  await page.getByTestId('runner-require-signatures-toggle').click()
  await expect(page.getByTestId('runner-confirm-dialog')).toContainText('every new publish is refused')
  await page.getByTestId('runner-confirm-cancel').click()
  expect(runner.sent['PUT settings/require_signatures']).toBeUndefined()
  await page.getByTestId('runner-require-signatures-toggle').click()
  await page.getByTestId('runner-confirm-go').click()
  await expect(page.getByTestId('runner-require-signatures')).toHaveAttribute('data-value', 'true')
  expect(runner.lastBody('PUT settings/require_signatures')).toEqual({ value: true })
  await page.getByTestId('runner-require-signatures-toggle').click()
  await page.getByTestId('runner-confirm-go').click()
  await expect(page.getByTestId('runner-require-signatures')).toHaveAttribute('data-value', 'false')

  // a refusal by role is shown in the runner's words and the switch stays as it was
  runner.deny = true
  await page.getByTestId('runner-require-signatures-toggle').click()
  await page.getByTestId('runner-confirm-go').click()
  await expect(page.getByTestId('runner-confirm-dialog')).toContainText('This needs the admin role.')
  await expect(page.getByTestId('runner-require-signatures')).toHaveAttribute('data-value', 'false')
  await page.getByTestId('runner-confirm-cancel').click()
  runner.deny = false

  // ---- the package: each channel lists the slots of the served and the staged version with what they are bound to
  await page.getByTestId('runner-package-link').click()
  const prod = page.locator('[data-testid="runner-bindings"][data-channel="prod"]')
  const test = page.locator('[data-testid="runner-bindings"][data-channel="test"]')
  await expect(prod.getByTestId('runner-binding-row')).toHaveCount(2)
  await expect(test.getByTestId('runner-binding-row')).toHaveCount(1)
  await expect(bindingRow(page, 'prod', 'source')).toHaveAttribute('data-bound', 'erp')
  await expect(bindingRow(page, 'prod', 'source')).toContainText('sqlserver')
  await expect(bindingRow(page, 'prod', 'source')).toContainText('check, load')
  await expect(bindingRow(page, 'prod', 'target')).toHaveAttribute('data-bound', '')
  await expect(prod.getByTestId('runner-bindings-unbound')).toContainText('Not bound yet: target')
  await expect(test.getByTestId('runner-bindings-unbound')).toHaveCount(0)   // a channel binds its own

  // ---- Save waits for a change and for a value it can send, and says why
  const save = prod.getByTestId('runner-bindings-save')
  await expect(save).toBeDisabled()
  await expect(prod.getByTestId('runner-bindings-why')).toContainText('Change a connection first.')
  await bindingRow(page, 'prod', 'target').getByTestId('runner-binding-input').fill('a b')
  await expect(prod.getByTestId('runner-bindings-why')).toContainText('without spaces')
  await expect(save).toBeDisabled()
  await bindingRow(page, 'prod', 'target').getByTestId('runner-binding-input').fill('warehouse')
  await expect(save).toBeEnabled()
  await save.click()
  await expect(prod.getByTestId('runner-bindings-saved')).toContainText('Bound in prod: target.')
  await expect(bindingRow(page, 'prod', 'target')).toHaveAttribute('data-bound', 'warehouse')
  await expect(prod.getByTestId('runner-bindings-unbound')).toHaveCount(0)
  expect(runner.lastBody('PUT bindings/prod/integration/orders')).toEqual({ slots: { target: 'warehouse' } })   // a slot with no binding carries no revision

  // ---- one bound slot changed carries its revision; a change made meanwhile is refused in the runner's words and what was typed stays
  runner.conflictNext = true
  await bindingRow(page, 'prod', 'source').getByTestId('runner-binding-input').fill('erp2')
  await save.click()
  await expect(prod).toContainText("The slot 'source' of integration 'orders' in channel 'prod' is at revision 2, not 1: it was bound since you looked.")
  expect(runner.lastBody('PUT bindings/prod/integration/orders')).toEqual({ slots: { source: 'erp2' }, expected_revision: 1 })
  await expect(bindingRow(page, 'prod', 'source').getByTestId('runner-binding-input')).toHaveValue('erp2')
  await expect(bindingRow(page, 'prod', 'source')).toHaveAttribute('data-revision', '2')   // the screen read the channel again after the refusal
  await bindingRow(page, 'prod', 'source').getByTestId('runner-binding-input').fill('erp3')
  await save.click()
  await expect(bindingRow(page, 'prod', 'source')).toHaveAttribute('data-bound', 'erp3')
  expect(runner.lastBody('PUT bindings/prod/integration/orders')).toEqual({ slots: { source: 'erp3' }, expected_revision: 2 })   // the revision read again after the refusal

  // ---- the known connections are offered, and the test channel is untouched
  await expect(page.locator('#runner-known-connections-prod option')).toHaveCount(3)
  await expect(bindingRow(page, 'test', 'source')).toHaveAttribute('data-bound', 'erp-test')

  // ---- a refusal by role is shown in the runner's words
  runner.deny = true
  await bindingRow(page, 'test', 'source').getByTestId('runner-binding-input').fill('other')
  await test.getByTestId('runner-bindings-save').click()
  await expect(test).toContainText('This needs the admin role.')
  expect(outside).toEqual([])   // nothing went to another origin
}

const sha = (bytes: string | Buffer) => createHash('sha256').update(bytes).digest('hex')

/** A package folder in the temporary directory: a manifest (`package.yaml`), a launch file, notes and a file in a sub folder. */
export function makePackageFolder(name: string, notes = 'notes v1'): { directory: string; manifest: string; files: Record<string, string> } {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), `ddk-pkg-${name}-`))
  const manifest = `schema: package/1\nname: ${name}\nlaunch: { assembly: app.dll }\ntasks:\n  - name: load\n  - name: check\n`
  const files: Record<string, string> = { 'app.dll': 'dll bytes', 'notes.txt': notes, 'sub/data.json': '{"a":1}' }
  fs.writeFileSync(path.join(directory, 'package.yaml'), manifest)
  for (const [file, text] of Object.entries(files)) {
    fs.mkdirSync(path.dirname(path.join(directory, file)), { recursive: true })
    fs.writeFileSync(path.join(directory, file), text)
  }
  return { directory, manifest, files }
}

/** UE10: publish a package from a folder chosen in the browser: read and hashed there, only the files the runner lacks sent, staged or served, the runner's refusal in its words. */
export async function publishFilesStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  const runner = await mockRunner(page)
  const pkg = makePackageFolder('invoices')
  const card = page.getByTestId('runner-pub')

  await page.goto(`/servers/${RUNNER}/packages`)
  await expect(page.getByTestId('runner-package-row')).toHaveCount(1)
  await expect(card.getByTestId('runner-pub-button')).toBeDisabled()
  await expect(card.getByTestId('runner-pub-why')).toContainText('Choose the folder of the package.')

  // ---- a folder: its files are listed (the manifest is not one of them), the package is named by the manifest, the channel is asked for
  await card.getByTestId('runner-pub-folder').setInputFiles(pkg.directory)
  await expect(card.getByTestId('runner-pub-files')).toHaveAttribute('data-count', '3')
  await expect(card.getByTestId('runner-pub-file')).toHaveText(['app.dll', 'notes.txt', 'sub/data.json'])
  await expect(card.getByTestId('runner-pub-manifest-read')).toHaveAttribute('data-package', 'invoices')
  await expect(card.getByTestId('runner-pub-manifest-read')).toContainText('package.yaml in the folder')
  await expect(card.getByTestId('runner-pub-button')).toBeDisabled()
  await expect(card.getByTestId('runner-pub-why')).toContainText('Name the channel.')
  await card.getByTestId('runner-pub-channel').fill('Dev')
  await expect(card.getByTestId('runner-pub-why')).toContainText('not a channel name')
  await card.getByTestId('runner-pub-channel').fill('dev')
  await expect(card.getByTestId('runner-pub-button')).toBeEnabled()

  // ---- staged: every file is asked about and sent, then the version is published naming each by its hash
  await card.getByTestId('runner-pub-button').click()
  await expect(card.getByTestId('runner-pub-folder-summary')).toContainText('3 file(s)')
  await expect(card.getByTestId('runner-pub-done')).toBeVisible({ timeout: 30_000 })
  await expect(card.getByTestId('runner-pub-summary')).toContainText('Version 1 of invoices is staged on dev')
  await expect(card.getByTestId('runner-pub-counts')).toContainText('3 file(s) sent, 0 already on the runner.')
  await expect(card.getByTestId('runner-pub-task')).toHaveCount(2)
  expect(runner.fileCalls.filter((c) => c.startsWith('HEAD'))).toHaveLength(3)
  expect(runner.fileCalls.filter((c) => c.startsWith('PUT'))).toHaveLength(3)
  expect(runner.lastBody('PUT definitions/integration/invoices')).toEqual({
    manifest: pkg.manifest, channel: 'dev', mode: 'stage',
    artifacts: Object.entries(pkg.files).map(([file, text]) => ({ path: file, sha256: sha(text), size: Buffer.byteLength(text) })),
  })
  await expect(page.getByTestId('runner-package-row')).toHaveCount(2, { timeout: 30_000 })
  await expect(page.getByTestId('runner-package-row').filter({ hasText: 'invoices' })).toContainText('v1 staged')

  // ---- a change in one file: only that file is sent again, and the version is the next
  fs.writeFileSync(path.join(pkg.directory, 'notes.txt'), 'notes v2')
  runner.fileCalls.length = 0
  await card.getByTestId('runner-pub-folder').setInputFiles(pkg.directory)
  await expect(card.getByTestId('runner-pub-button')).toBeEnabled()
  await expect(card.getByTestId('runner-pub-done')).toHaveCount(0)   // choosing again clears the last answer
  await card.getByTestId('runner-pub-button').click()
  await expect(card.getByTestId('runner-pub-counts')).toContainText('1 file(s) sent, 2 already on the runner.', { timeout: 30_000 })
  await expect(card.getByTestId('runner-pub-summary')).toContainText('Version 2 of invoices is staged on dev')
  expect(runner.fileCalls.filter((c) => c.startsWith('PUT'))).toEqual([`PUT ${sha('notes v2').slice(0, 8)}`])

  // ---- served at once, with a note
  await card.getByTestId('runner-pub-serve').check()
  await card.getByTestId('runner-pub-note').fill('hotfix')
  await card.getByTestId('runner-pub-button').click()
  await expect(card.getByTestId('runner-pub-summary')).toContainText('is served on dev', { timeout: 30_000 })
  expect(runner.lastBody('PUT definitions/integration/invoices')).toMatchObject({ mode: 'serve', note: 'hotfix' })
  await card.getByTestId('runner-pub-serve').uncheck()

  // ---- a manifest chosen on its own that is not package/1 is named, and nothing is sent
  const wrong = path.join(pkg.directory, '..', `ddk-wrong-${Date.now()}.yaml`)
  fs.writeFileSync(wrong, 'schema: build/1\nname: invoices\n')
  await card.getByTestId('runner-pub-manifest').setInputFiles(wrong)
  await expect(card.getByTestId('runner-pub-manifest-problem')).toContainText('not package/1')
  await expect(card.getByTestId('runner-pub-button')).toBeDisabled()
  await card.getByTestId('runner-pub-manifest').setInputFiles([])
  await expect(card.getByTestId('runner-pub-manifest-read')).toContainText('package.yaml in the folder')

  // ---- the runner's refusal of the publish is shown in its words, with the step; the files it already holds are not sent again
  runner.requireSignatures = true
  runner.fileCalls.length = 0
  await card.getByTestId('runner-pub-button').click()
  await expect(card.getByTestId('runner-pub-failed')).toContainText("The runner refused the package invoices (403): This runner requires signed versions (require_signatures is on)", { timeout: 30_000 })
  expect(runner.fileCalls.filter((c) => c.startsWith('PUT'))).toEqual([])
  runner.requireSignatures = false

  // ---- a refusal of a file (a person without the role) names the file
  runner.deny = true
  fs.writeFileSync(path.join(pkg.directory, 'notes.txt'), 'notes v3')
  await card.getByTestId('runner-pub-folder').setInputFiles(pkg.directory)
  await expect(card.getByTestId('runner-pub-button')).toBeEnabled()
  await card.getByTestId('runner-pub-button').click()
  await expect(card.getByTestId('runner-pub-failed')).toContainText('The runner refused the file notes.txt (403): This needs the admin role.', { timeout: 30_000 })
  expect(outside).toEqual([])
  fs.rmSync(pkg.directory, { recursive: true, force: true })
  fs.rmSync(wrong, { force: true })
}
