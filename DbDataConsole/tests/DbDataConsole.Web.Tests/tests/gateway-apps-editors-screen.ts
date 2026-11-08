import { expect, type Page, type Route } from '@playwright/test'

// The Apps screen's editors (plan 33, UE01 to UE03) against a Gateway whose management routes are mocked in the page: the same words and test ids as `gateway-apps.spec.ts`, which runs the real Gateway. The mock keeps
// state and answers the routes of `GatewayApps` the screen calls (what it refuses, it refuses in the words of the store: `AppInstanceStore`), and keeps every body it is sent, so the story checks what the screen SENDS as well as
// what it shows. What this cannot show is the host serving the result: that is `gateway-apps-editors.spec.ts`, with a real Gateway.

const SERVER = 'gw-1'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

export interface MockCaller { can_publish: boolean; can_approve: boolean; you: string }

interface MockVersion { version: number; manifest_version: string; models: string[]; published_by: string; note: string | null }
interface MockInstance { name: string; app: string; pin: number | null; title: string | null; settings: Record<string, string>; bindings: Record<string, string>; version: number }

export interface MockGateway {
  caller: MockCaller
  /** Every body the screen sent, by `METHOD route`. */
  sent: Record<string, unknown[]>
  instances: Map<string, MockInstance>
  lastBody: (key: string) => unknown | undefined
}

/** A Gateway that serves the app `notes` (two versions: 1.0.0 uses `territories`, 1.1.0 uses `territories` and `people`) and one instance of it, `acme`, which follows the app. */
export async function mockGateway(page: Page, caller: MockCaller = { can_publish: true, can_approve: true, you: 'Ada' }): Promise<MockGateway> {
  const apps = new Map<string, { versions: MockVersion[]; serving: number }>([
    ['notes', { serving: 2, versions: [
      { version: 1, manifest_version: '1.0.0', models: ['territories'], published_by: 'ada-laptop', note: 'first build' },
      { version: 2, manifest_version: '1.1.0', models: ['territories', 'people'], published_by: 'ada-laptop', note: 'second build' },
    ] }],
  ])
  const instances = new Map<string, MockInstance>([['acme', { name: 'acme', app: 'notes', pin: null, title: 'Acme notes', settings: {}, bindings: {}, version: 1 }]])
  const sent: Record<string, unknown[]> = {}
  const gateway: MockGateway = { caller, sent, instances, lastBody: (key) => sent[key]?.at(-1) }

  const about = (v: MockVersion) => ({
    title: 'Notes', description: null, kind: 'app', trust: 'sandbox', roles: ['viewer'], models: v.models.map((m) => ({ model: m, entity: 'row', operations: 'read and write' })), semantic_models: [], egress: [], storage: null,
    published_by: v.published_by, published_at: '2026-11-08T10:00:00Z', approval: null,
  })
  const instanceRow = (i: MockInstance) => {
    const app = apps.get(i.app)!
    const running = i.pin ?? app.serving
    return {
      name: i.name, app: i.app, title: i.title, pin: i.pin, follows: i.pin === null, instance_version: i.version, published_by: 'Ada', published_at: '2026-11-08T10:00:00Z', path: `/apps/${i.name}/`, address: null,
      bindings: i.bindings, settings: i.settings, navigation: [], mounted_by: [], app_version: running, serving_version: app.serving, manifest_version: app.versions.find((v) => v.version === running)!.manifest_version, kind: 'app', trust: 'sandbox', runs_as_asked: true, approval_id: null,
    }
  }

  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Apps gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const method = request.method()
    const tail = new URL(request.url()).pathname.slice(`/relay/${SERVER}/api/v1/`.length)
    const parts = tail.split('/').map(decodeURIComponent)
    const body = method === 'GET' || method === 'DELETE' ? undefined : request.postDataJSON() as Record<string, any>
    if (method !== 'GET') (sent[`${method} ${tail}`] ??= []).push(body ?? null)
    const refuse = (status: number, message: string) => json(route, { error: message }, status)
    const needsPublisher = () => json(route, { error: 'This call needs the publisher role (or admin). Pair this client with a code made by \'dbdatagateway server pair --max-role publisher\' on the gateway.' }, 403)

    if (method === 'GET' && tail === 'apps') {
      return json(route, { apps: [...apps].map(([name, a]) => ({ name, version: a.serving, versions: a.versions.map((v) => v.version), title: 'Notes', manifest_version: a.versions.find((v) => v.version === a.serving)!.manifest_version, description: null, kind: 'app', trust: 'sandbox', published_by: 'ada-laptop', published_at: '2026-11-08T10:00:00Z', instances: [...instances.values()].filter((i) => i.app === name).map((i) => i.name) })), caller })
    }
    if (method === 'GET' && parts[0] === 'apps' && parts.length === 2) {
      const app = apps.get(parts[1]!)
      if (!app) return refuse(404, `The app '${parts[1]}' has no published version.`)
      return json(route, {
        name: parts[1], serving: app.serving, caller,
        versions: [...app.versions].reverse().map((v) => ({ version: v.version, manifest_version: v.manifest_version, trust: 'sandbox', published_by: v.published_by, published_at: '2026-11-08T10:00:00Z', note: v.note, serving: v.version === app.serving, hash: 'ab'.repeat(32), approval_id: null, files: 1, about: about(v) })),
      })
    }
    if (method === 'GET' && tail === 'app-instances') return json(route, { instances: [...instances.values()].map(instanceRow), apps_address: null, caller })
    if (method === 'GET' && tail === 'app-navigation') return json(route, { navigation: [], caller })
    if (method === 'GET' && tail === 'app-approvals') return json(route, { approvals: [], caller })

    if (method === 'PUT' && parts[0] === 'app-instances' && parts.length === 2) {
      if (!caller.can_publish) return needsPublisher()
      const name = parts[1]!
      const app = apps.get(body?.app)
      if (!app) return refuse(400, `The app '${body?.app}' has not been published to production.`)
      if (body!.pin != null && !app.versions.some((v) => v.version === body!.pin)) return refuse(400, `The app '${body!.app}' has no version ${body!.pin}.`)
      const running = app.versions.find((v) => v.version === (body!.pin ?? app.serving))!
      for (const model of Object.keys(body!.bindings ?? {})) {
        if (!running.models.includes(model)) return refuse(400, `'${model}' is not a model the app '${body!.app}' uses, so the instance cannot bind it. It uses: ${[...running.models].sort().join(', ')}.`)
      }
      const embed = body!.settings?.['embed.allowed_origins']
      if (embed && !/^https?:\/\/[^/]+$/.test(embed)) return refuse(400, `'${embed}' is not an origin for embed.allowed_origins: use scheme, host and optional port, such as https://portal.example.com.`)
      const version = (instances.get(name)?.version ?? 0) + 1
      instances.set(name, { name, app: body!.app, pin: body!.pin ?? null, title: body!.title ?? null, settings: body!.settings ?? {}, bindings: body!.bindings ?? {}, version })
      return json(route, { name, app: body!.app, version })
    }
    return refuse(404, 'There is no such call.')
  })
  return gateway
}

/** Opens the Apps screen of the mocked Gateway. */
export async function openApps(page: Page) {
  await page.goto(`/servers/${SERVER}/apps`)
  await expect(page.getByTestId('gateway-apps')).toBeVisible()
  await expect(page.getByTestId('instance-row-acme')).toBeVisible()
}

/**
 * UE01: an instance is made from the screen. The form offers a binding for each model the version it would run uses (and none for another), saves what is typed (and sends nothing the person did not type), shows the Gateway's
 * refusal in its words, edits the instance without losing its settings, and the pin control in the list still sends the settings back. A person without the role sees why every button is off.
 */
export async function instanceEditorStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  await page.setViewportSize({ width: 1440, height: 1500 })
  const gateway = await mockGateway(page)
  await openApps(page)

  // the form starts empty and says what is missing
  await page.getByTestId('instance-new').click()
  const editor = page.getByTestId('instance-editor')
  await expect(editor).toHaveAttribute('data-mode', 'new')
  await expect(page.getByTestId('instance-editor-save')).toBeDisabled()
  await expect(page.getByTestId('instance-editor-why')).toContainText('Name the instance')
  await page.getByTestId('instance-editor-name').fill('acme')
  await expect(page.getByTestId('instance-editor-why')).toContainText('already an instance named acme')
  await page.getByTestId('instance-editor-name').fill('sales')
  await expect(page.getByTestId('instance-editor-why')).toContainText('Choose the app')
  await page.getByTestId('instance-editor-app').selectOption('notes')
  await expect(page.getByTestId('instance-editor-why')).toHaveCount(0)

  // the bindings are the models of the version the instance would run: following, that is version 2; pinned to version 1, one fewer
  await expect(page.getByTestId('instance-editor-binding-territories')).toBeVisible()
  await expect(page.getByTestId('instance-editor-binding-people')).toBeVisible()
  await page.getByTestId('instance-editor-pin').selectOption('1')
  await expect(page.getByTestId('instance-editor-binding-people')).toHaveCount(0)
  await expect(page.getByTestId('instance-editor-binding-territories')).toBeVisible()
  await page.getByTestId('instance-editor-pin').selectOption('')
  await page.getByTestId('instance-editor-binding-territories').fill('live')
  await page.getByTestId('instance-editor-title').fill('Sales notes')
  await page.getByTestId('instance-editor-setting-add').click()
  await page.getByTestId('instance-editor-setting-key-0').fill('embed.allowed_origins')
  await expect(page.getByTestId('instance-editor-setting-hint')).toContainText('Origins that may frame this instance')
  await page.getByTestId('instance-editor-setting-value-0').fill('https://portal.example.com/path')

  // the Gateway's refusal is shown in its own words, the form stays open with what was typed, and nothing was saved
  await page.getByTestId('instance-editor-save').click()
  await expect(page.getByTestId('error-banner')).toContainText('is not an origin for embed.allowed_origins')
  await expect(editor).toBeVisible()
  await expect(page.getByTestId('instance-editor-binding-territories')).toHaveValue('live')
  expect(gateway.instances.has('sales')).toBe(false)

  // fixed, it is saved: what was sent is exactly what was typed (the empty binding of `people` is not sent)
  await page.getByTestId('instance-editor-setting-value-0').fill('https://portal.example.com')
  await page.getByTestId('instance-editor-save').click()
  await expect(page.getByTestId('apps-notice')).toContainText('sales is saved as instance version 1')
  await expect(editor).toHaveCount(0)
  expect(gateway.lastBody('PUT app-instances/sales')).toEqual({
    app: 'notes', pin: null, title: 'Sales notes', settings: { 'embed.allowed_origins': 'https://portal.example.com' }, bindings: { territories: 'live' },
  })
  await expect(page.getByTestId('instance-row-sales').getByTestId('instance-runs')).toHaveText('follows the app: version 2')
  await expect(page.getByTestId('instance-config-sales')).toHaveText('embed.allowed_origins=https://portal.example.com, territories → live')

  // changing it: the form is filled from the instance, its name and app are fixed, and a change to one setting keeps the rest
  await page.getByTestId('instance-edit-sales').click()
  await expect(editor).toHaveAttribute('data-mode', 'edit')
  await expect(page.getByTestId('instance-editor-name')).toBeDisabled()
  await expect(page.getByTestId('instance-editor-app')).toBeDisabled()
  await expect(page.getByTestId('instance-editor-title')).toHaveValue('Sales notes')
  await expect(page.getByTestId('instance-editor-binding-territories')).toHaveValue('live')
  await page.getByTestId('instance-editor-setting-value-0').fill('https://portal.example.com:8443')
  await page.getByTestId('instance-editor-save').click()
  await expect(page.getByTestId('apps-notice')).toContainText('sales is saved as instance version 2')
  expect(gateway.instances.get('sales')).toMatchObject({ settings: { 'embed.allowed_origins': 'https://portal.example.com:8443' }, bindings: { territories: 'live' }, title: 'Sales notes' })

  // a binding the pinned version has no model for is shown, and saving says it removes it
  await page.getByTestId('instance-edit-sales').click()
  await page.getByTestId('instance-editor-binding-people').fill('everyone')
  await page.getByTestId('instance-editor-pin').selectOption('1')
  await expect(page.getByTestId('instance-editor-binding-stale-people')).toContainText('saving removes this binding')
  await page.getByTestId('instance-editor-save').click()
  await expect(page.getByTestId('apps-notice')).toContainText('sales is saved as instance version 3')
  expect(gateway.instances.get('sales')).toMatchObject({ pin: 1, bindings: { territories: 'live' } })
  expect(gateway.instances.get('sales')!.bindings).not.toHaveProperty('people')

  // the pin control of the list stays, and sends the settings, the title and the bindings back unchanged
  await page.getByTestId('instance-pin-sales').selectOption('')
  await page.getByTestId('instance-pin-save-sales').click()
  await expect(page.getByTestId('apps-notice')).toContainText('sales follows notes again')
  expect(gateway.lastBody('PUT app-instances/sales')).toEqual({
    app: 'notes', pin: null, title: 'Sales notes', settings: { 'embed.allowed_origins': 'https://portal.example.com:8443' }, bindings: { territories: 'live' },
  })

  // a person who may only look sees why: the buttons are off and the reason is on the page
  const viewer = await mockGateway(page, { can_publish: false, can_approve: false, you: 'Bob' })
  await page.reload()
  await expect(page.getByTestId('instance-row-acme')).toBeVisible()
  await expect(page.getByTestId('instance-new')).toBeDisabled()
  await expect(page.getByTestId('instance-new-why')).toContainText('publisher or administrator role')
  await expect(page.getByTestId('instance-edit-acme')).toBeDisabled()
  expect(Object.keys(viewer.sent).filter((k) => k.startsWith('PUT'))).toEqual([])
  expect(outside).toEqual([])
}
