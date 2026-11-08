import { expect, type Page, type Route } from '@playwright/test'
import { createHash } from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

// The Apps screen's editors (plan 33, UE01 to UE03) against a Gateway whose management routes are mocked in the page: the same words and test ids as `gateway-apps.spec.ts`, which runs the real Gateway. The mock keeps
// state and answers the routes of `GatewayApps` the screen calls (what it refuses, it refuses in the words of the store: `AppInstanceStore`), and keeps every body it is sent, so the story checks what the screen SENDS as well as
// what it shows. What this cannot show is the host serving the result: that is `gateway-apps-editors.spec.ts`, with a real Gateway.

const SERVER = 'gw-1'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

export interface MockCaller { can_publish: boolean; can_approve: boolean; you: string }

interface MockVersion {
  version: number; manifest: string; files: Record<string, string>; manifest_version: string; trust: 'sandbox' | 'trusted'; models: string[]; egress: Record<string, string[]>
  published_by: string; note: string | null; hash: string
}
interface MockApproval { id: string; app: string; hash: string; approved_by: string; note: string | null; egress: Record<string, string[]>; revoked: boolean }
interface MockNavigationSet { name: string; title: string; entries: { instance: string; label: string | null; icon: string | null }[] }
interface MockInstance { name: string; app: string; pin: number | null; title: string | null; settings: Record<string, string>; bindings: Record<string, string>; version: number }

export interface MockGateway {
  caller: MockCaller
  /** Every body the screen sent, by `METHOD route`. */
  sent: Record<string, unknown[]>
  instances: Map<string, MockInstance>
  apps: Map<string, { versions: MockVersion[]; serving: number }>
  approvals: MockApproval[]
  navigation: Map<string, MockNavigationSet>
  lastBody: (key: string) => unknown | undefined
}

/** What the mock reads of a manifest (the real Gateway reads it with `AppManifestLoader`): the name, version, trust, entry, models and the origins it asks for. */
function readManifest(text: string) {
  const line = (key: string) => new RegExp(`^${key}:\\s*(\\S+)`, 'm').exec(text)?.[1]
  const egress: Record<string, string[]> = {}
  for (const m of text.matchAll(/^ {2}(connect|script|style|img|font|media|frame|form): \[([^\]]*)\]/gm)) egress[m[1]!] = m[2]!.split(',').map((o) => o.trim()).filter(Boolean)
  return { name: line('name'), version: line('version'), trust: line('trust') === 'trusted' ? 'trusted' as const : 'sandbox' as const, entry: line('entry'), models: [...text.matchAll(/^ {2}- model: (\S+)/gm)].map((m) => m[1]!), egress }
}

const hashOf = (manifest: string, files: Record<string, string>) =>
  createHash('sha256').update(manifest).update(JSON.stringify(Object.keys(files).sort().map((k) => [k, files[k]]))).digest('hex')

const notesManifest = (version: string, models: string[]) => `schemaVersion: 1\nname: notes\nversion: ${version}\nentry: index.html\nmodels:\n${models.map((m) => `  - model: ${m}\n`).join('')}`

/** A Gateway that serves the app `notes` (two versions: 1.0.0 uses `territories`, 1.1.0 uses `territories` and `people`) with one instance, `acme`, which follows it, and a trusted app `vault` whose only version has no approval. */
export async function mockGateway(page: Page, caller: MockCaller = { can_publish: true, can_approve: true, you: 'Ada' }): Promise<MockGateway> {
  const version = (v: Partial<MockVersion> & { version: number; manifest: string }): MockVersion => {
    const read = readManifest(v.manifest)
    return { files: {}, manifest_version: read.version!, trust: read.trust, models: read.models, egress: read.egress, published_by: 'ada-laptop', note: null, hash: hashOf(v.manifest, v.files ?? {}), ...v }
  }
  const apps = new Map<string, { versions: MockVersion[]; serving: number }>([
    ['notes', { serving: 2, versions: [
      version({ version: 1, manifest: notesManifest('1.0.0', ['territories']), note: 'first build' }),
      version({ version: 2, manifest: notesManifest('1.1.0', ['territories', 'people']), note: 'second build' }),
    ] }],
    ['vault', { serving: 1, versions: [version({ version: 1, manifest: 'schemaVersion: 1\nname: vault\nversion: 1.0.0\nentry: index.html\ntrust: trusted\negress:\n  connect: [https://vault.example.com]\n  img: [https://cdn.example.com]\n' })] }],
  ])
  const instances = new Map<string, MockInstance>([
    ['acme', { name: 'acme', app: 'notes', pin: null, title: 'Acme notes', settings: {}, bindings: {}, version: 1 }],
    ['pinned', { name: 'pinned', app: 'notes', pin: 1, title: null, settings: {}, bindings: {}, version: 1 }],
  ])
  const navigation = new Map<string, MockNavigationSet>([['main', { name: 'main', title: 'Main menu', entries: [{ instance: 'acme', label: 'Acme', icon: null }, { instance: 'pinned', label: null, icon: null }] }]])
  const approvals: MockApproval[] = []
  const sent: Record<string, unknown[]> = {}
  const gateway: MockGateway = { caller, sent, instances, apps, approvals, navigation, lastBody: (key) => sent[key]?.at(-1) }

  const standing = (app: string, hash: string) => approvals.find((a) => a.app === app && a.hash === hash && !a.revoked)
  const asked = (egress: Record<string, string[]>) => Object.entries(egress).filter(([, o]) => o.length > 0).map(([directive, origins]) => ({ directive, origins }))
  const about = (v: MockVersion) => ({
    title: 'Notes', description: null, kind: 'app', trust: v.trust, roles: ['viewer'], models: v.models.map((m) => ({ model: m, entity: 'row', operations: 'read and write' })), semantic_models: [], egress: asked(v.egress), storage: null,
    published_by: v.published_by, published_at: '2026-11-08T10:00:00Z', approval: null,
  })
  const instanceRow = (i: MockInstance) => {
    const app = apps.get(i.app)!
    const running = i.pin ?? app.serving
    const v = app.versions.find((x) => x.version === running)!
    return {
      name: i.name, app: i.app, title: i.title, pin: i.pin, follows: i.pin === null, instance_version: i.version, published_by: 'Ada', published_at: '2026-11-08T10:00:00Z', path: `/apps/${i.name}/`, address: null,
      bindings: i.bindings, settings: i.settings, navigation: [...navigation.values()].filter((n) => n.entries.some((e) => e.instance === i.name)).map((n) => n.name), mounted_by: [], app_version: running, serving_version: app.serving, manifest_version: v.manifest_version, kind: 'app', trust: v.trust, runs_as_asked: v.trust === 'sandbox' || !!standing(i.app, v.hash), approval_id: null,
    }
  }
  const refusalForTrust = (name: string, v: string, hash: string) =>
    `'${name}' ${v} asks to be trusted and has no approval for these exact files and manifest (hash ${hash}). An approver has to review and approve that hash: dbdatagateway apps approve --path <folder> --by <approver>.`
  const decode = (files: Record<string, string> | undefined) => files ?? {}

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
    const needsPublisher = () => refuse(403, "This call needs the publisher role (or admin). Pair this client with a code made by 'dbdatagateway server pair --max-role publisher' on the gateway.")

    if (method === 'GET' && tail === 'apps') {
      return json(route, { apps: [...apps].map(([name, a]) => {
        const serving = a.versions.find((v) => v.version === a.serving)!
        return { name, version: a.serving, versions: a.versions.map((v) => v.version), title: name === 'notes' ? 'Notes' : name, manifest_version: serving.manifest_version, description: null, kind: 'app', trust: serving.trust, published_by: serving.published_by, published_at: '2026-11-08T10:00:00Z', instances: [...instances.values()].filter((i) => i.app === name).map((i) => i.name) }
      }), caller })
    }
    if (method === 'GET' && parts[0] === 'apps' && parts.length === 2) {
      const app = apps.get(parts[1]!)
      if (!app) return refuse(404, `The app '${parts[1]}' has no published version.`)
      return json(route, {
        name: parts[1], serving: app.serving, caller,
        versions: [...app.versions].reverse().map((v) => {
          const approval = standing(parts[1]!, v.hash)
          return { version: v.version, manifest_version: v.manifest_version, trust: v.trust, published_by: v.published_by, published_at: '2026-11-08T10:00:00Z', note: v.note, serving: v.version === app.serving, hash: v.hash, approval_id: approval?.id ?? null, files: Object.keys(v.files).length, about: about(v) }
        }),
      })
    }
    if (method === 'GET' && tail === 'app-instances') return json(route, { instances: [...instances.values()].map(instanceRow), apps_address: null, caller })
    if (method === 'GET' && tail === 'app-navigation') return json(route, { navigation: [...navigation.values()], caller })
    if (method === 'GET' && tail === 'app-approvals') {
      return json(route, { approvals: approvals.map((a) => ({ id: a.id, app: a.app, version: '1.0.0', hash: a.hash, approved_by: a.approved_by, approved_at: '2026-11-08T11:00:00Z', note: a.note, published_version: null, revoked: a.revoked, revoked_at: null, operations: [], egress: asked(a.egress) })), caller })
    }

    if (method === 'POST' && tail === 'apps/check') {
      if (!caller.can_publish) return needsPublisher()
      const files = decode(body?.files)
      for (const [path, content] of Object.entries(files)) if (!/^[A-Za-z0-9+/]*={0,2}$/.test(content)) return refuse(400, `The file '${path}' is not base64.`)
      const read = readManifest(body!.manifest)
      if (!read.name) return json(route, { ok: false, files: Object.keys(files).length, name: null, manifest_version: null, trust: null, hash: null, approval_id: null, next_version: null, about: null, diagnostics: [{ code: 'DBD-APP-001', severity: 'error', file: 'app.yaml', line: 1, column: 1, message: '`name` is missing: an app has a name.', fix: null }] })
      const diagnostics = read.entry && !(read.entry in files) ? [{ code: 'DBD-APP-006', severity: 'error', file: 'app.yaml', line: 1, column: 1, message: `The manifest names \`${read.entry}\`, which is not in the app's folder.`, fix: 'Create the file, or take it out of the manifest.' }] : []
      const hash = hashOf(body!.manifest, files)
      const existing = apps.get(read.name)
      const v = version({ version: 0, manifest: body!.manifest, files })
      return json(route, {
        ok: diagnostics.length === 0, files: Object.keys(files).length, diagnostics, name: read.name, manifest_version: read.version, trust: read.trust, hash, approval_id: standing(read.name, hash)?.id ?? null,
        next_version: Math.max(0, ...(existing?.versions.map((x) => x.version) ?? [])) + 1, about: about(v),
      })
    }
    if (method === 'PUT' && parts[0] === 'apps' && parts.length === 2) {
      if (!caller.can_publish) return needsPublisher()
      const read = readManifest(body!.manifest)
      if (!read.name) return refuse(400, 'The manifest is not valid: `name` is missing.')
      if (read.name !== parts[1]) return refuse(400, `The manifest is named '${read.name}' and the call names '${parts[1]}'; they are the same name.`)
      const files = decode(body!.files)
      const hash = hashOf(body!.manifest, files)
      if (read.trust === 'trusted' && !standing(read.name, hash)) return refuse(400, refusalForTrust(read.name, read.version!, hash))
      const app = apps.get(read.name) ?? { versions: [], serving: 0 }
      const next = Math.max(0, ...app.versions.map((x) => x.version)) + 1
      app.versions.push(version({ version: next, manifest: body!.manifest, files, published_by: caller.you, note: body!.note ?? null }))
      app.serving = next
      apps.set(read.name, app)
      return json(route, { name: read.name, version: next, files: Object.keys(files).length, published_by: caller.you })
    }
    if (method === 'POST' && tail === 'app-approvals') {
      if (!caller.can_approve) return refuse(403, 'Approving an app needs the approver role, which this client\'s pairing does not allow.')
      const approver = body!.by as string
      if (approver !== 'Grace Hopper') return refuse(400, `'${approver}' is not a user of the identity home who holds the Approver role on this server.`)
      let app: string, manifest: string, files: Record<string, string>, author: string
      if (body!.manifest) {
        const read = readManifest(body!.manifest)
        app = read.name!; manifest = body!.manifest; files = decode(body!.files); author = caller.you
      } else {
        const v = apps.get(body!.app)?.versions.find((x) => x.version === body!.version)
        if (!v) return refuse(400, `The app '${body!.app}' has no version ${body!.version}.`)
        app = body!.app; manifest = v.manifest; files = v.files; author = v.published_by
      }
      if (author.toLowerCase() === approver.toLowerCase()) return refuse(400, `'${approver}' is the author of '${app}' and cannot approve it.`)
      const read = readManifest(manifest)
      const egress: Record<string, string[]> = {}
      for (const part of String(body!.egress ?? '').split(',').filter(Boolean)) {
        const at = part.indexOf('=')
        const directive = part.slice(0, at), origin = part.slice(at + 1)
        if (!(read.egress[directive] ?? []).includes(origin)) return refuse(400, `The approval would allow ${directive} to ${origin}, which '${app}' does not ask for.`)
        ;(egress[directive] ??= []).push(origin)
      }
      const approval = { id: `apr_${approvals.length + 1}`, app, hash: hashOf(manifest, files), approved_by: approver, note: body!.note ?? null, egress, revoked: false }
      approvals.push(approval)
      return json(route, { id: approval.id, app, version: read.version, hash: approval.hash, approved_by: approver, author })
    }

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
      const instanceVersion = (instances.get(name)?.version ?? 0) + 1
      instances.set(name, { name, app: body!.app, pin: body!.pin ?? null, title: body!.title ?? null, settings: body!.settings ?? {}, bindings: body!.bindings ?? {}, version: instanceVersion })
      return json(route, { name, app: body!.app, version: instanceVersion })
    }
    if (method === 'PUT' && parts[0] === 'app-navigation' && parts.length === 2) {
      if (!caller.can_publish) return needsPublisher()
      const name = parts[1]!
      if (body?.title == null || body.entries == null) return refuse(400, 'The body is a JSON object: {"title": "<text>", "entries": [{"instance": "<instance>", "label": "<text>", "icon": "<name>"}]}, and "rename_from": "<set>" to take the name from another set.')
      const from: string | null = body.rename_from && body.rename_from !== name ? body.rename_from : null
      if (from !== null && !navigation.has(from)) return refuse(400, `The navigation set '${from}' is not served, so it cannot be renamed to '${name}'.`)
      if (from !== null && navigation.has(name)) return refuse(400, `There is already a navigation set named '${name}'; rename to another name, or change that one.`)
      if (!/^[a-z0-9][a-z0-9_-]{0,63}$/.test(name)) return refuse(400, `A navigation set's name is lower-case letters, digits, '-' and '_', starting with a letter or digit; '${name}' is not.`)
      if (String(body.title).trim() === '') return refuse(400, 'A navigation set needs a title.')
      if (body.entries.length === 0) return refuse(400, 'A navigation set lists at least one instance.')
      const seen = new Set<string>()
      for (const entry of body.entries) {
        if (seen.has(entry.instance)) return refuse(400, `'${entry.instance}' is listed twice in the set '${name}'.`)
        seen.add(entry.instance)
        if (!instances.has(entry.instance)) return refuse(400, `'${entry.instance}' is not an instance published to production; save it before listing it in a set.`)
      }
      navigation.set(name, { name, title: body.title, entries: body.entries })
      if (from !== null) navigation.delete(from)
      return json(route, { name, version: 1, entries: body.entries.length, renamed_from: from })
    }
    if (method === 'DELETE' && parts[0] === 'app-navigation' && parts.length === 2) {
      if (!caller.can_publish) return needsPublisher()
      if (!navigation.delete(parts[1]!)) return refuse(404, `The navigation set '${parts[1]}' is not served.`)
      return json(route, { name: parts[1], removed: true })
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

/** A folder of files, under a folder of its own name (the browser gives each file's path with that name first). */
function writeFolder(name: string, files: Record<string, string>) {
  const dir = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'dbdatakit-gateway-apps-editors-')), name)
  for (const [file, text] of Object.entries(files)) {
    fs.mkdirSync(path.dirname(path.join(dir, file)), { recursive: true })
    fs.writeFileSync(path.join(dir, file), text)
  }
  return dir
}

const base64 = (text: string) => Buffer.from(text, 'utf8').toString('base64')

/**
 * UE02: a version is published from a folder. A folder with no app.yaml is refused before anything is sent, naming it; one whose manifest names a file it does not have shows the Gateway's findings and cannot be sent; a good folder
 * is checked (the version it would be), sent as the manifest text and the other files in base64 under their paths, and served at once. A trusted app is sent only after an approver approves exactly those files, choosing which of the
 * origins the manifest asks for the app may use (the author may not approve); when the Gateway refuses what the screen allowed (the approval was revoked meanwhile) the refusal is shown in its words. A published version is approved
 * with origins chosen too. A person without the role sees why Publish is off.
 */
export async function publishStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  await page.setViewportSize({ width: 1440, height: 1700 })
  const gateway = await mockGateway(page)
  await openApps(page)

  await page.getByTestId('publish-open').click()
  await expect(page.getByTestId('publish-send')).toBeDisabled()
  await expect(page.getByTestId('publish-why')).toContainText('Choose the folder')

  // a folder with no manifest at its top: said before anything is sent, naming the folder
  await page.getByTestId('publish-folder').setInputFiles(writeFolder('bare', { 'index.html': '<html>x</html>' }))
  await expect(page.getByTestId('publish-problem')).toContainText('There is no app.yaml in bare')
  await expect(page.getByTestId('publish-send')).toBeDisabled()
  expect(gateway.sent['POST apps/check']).toBeUndefined()

  // a manifest that names a file the folder does not have: the Gateway's finding, in the Gateway's words; nothing can be published
  const manifest = (name: string, version: string, extra = '') => `schemaVersion: 1\nname: ${name}\nversion: ${version}\nentry: index.html\n${extra}`
  await page.getByTestId('publish-folder').setInputFiles(writeFolder('broken', { 'app.yaml': manifest('notes', '1.2.0') }))
  await expect(page.getByTestId('publish-check')).toHaveAttribute('data-ok', 'false')
  await expect(page.getByTestId('publish-diagnostic')).toContainText('The manifest names `index.html`, which is not in the app\'s folder.')
  await expect(page.getByTestId('publish-send')).toBeDisabled()
  await expect(page.getByTestId('publish-why')).toContainText('errors in these files')

  // a good folder: checked, then sent as the manifest text and the other files in base64 under their paths
  const notesManifest = manifest('notes', '1.2.0', 'models:\n  - model: territories\n')
  await page.getByTestId('publish-folder').setInputFiles(writeFolder('notes', { 'app.yaml': notesManifest, 'index.html': '<html>notes three</html>', 'assets/logo.txt': 'logo' }))
  await expect(page.getByTestId('publish-summary')).toContainText('notes: app.yaml and 2 files')
  await expect(page.getByTestId('publish-would')).toContainText('notes 1.2.0 would be version 3, sandboxed')
  await expect(page.getByTestId('publish-diagnostics-none')).toBeVisible()
  await page.getByTestId('publish-note').fill('from a folder')
  await expect(page.getByTestId('publish-send')).toHaveText('Publish notes as version 3')
  await page.getByTestId('publish-send').click()
  await expect(page.getByTestId('apps-notice')).toContainText('notes is published as version 3 (2 files) and served at once')
  expect(gateway.lastBody('PUT apps/notes')).toEqual({ manifest: notesManifest, files: { 'assets/logo.txt': base64('logo'), 'index.html': base64('<html>notes three</html>') }, note: 'from a folder' })
  await expect(page.getByTestId('version-row-3')).toHaveAttribute('data-serving', 'true')
  await expect(page.getByTestId('instance-row-acme').getByTestId('instance-runs')).toHaveText('follows the app: version 3')
  await expect(page.getByTestId('publish-panel')).toHaveCount(0)

  // a trusted app: Publish stays off until an approver has approved these exact files; the author may not, and the approver chooses the origins
  const ledgerManifest = manifest('ledger', '1.0.0', 'trust: trusted\negress:\n  connect: [https://api.example.com]\n  img: [https://cdn.example.com]\n')
  await page.getByTestId('publish-open').click()
  await page.getByTestId('publish-folder').setInputFiles(writeFolder('ledger', { 'app.yaml': ledgerManifest, 'index.html': '<html>ledger</html>' }))
  await expect(page.getByTestId('publish-would')).toContainText('ledger 1.0.0 would be version 1, asking to be trusted')
  await expect(page.getByTestId('publish-send')).toBeDisabled()
  await expect(page.getByTestId('publish-why')).toContainText('asks to be trusted and no approval stands for these exact files')
  await expect(page.getByTestId('publish-approve')).toBeVisible()
  await expect(page.getByTestId('publish-approve-send')).toBeDisabled()
  await expect(page.getByTestId('publish-approve-why')).toContainText('two people')           // the person signed in is the author
  await page.getByTestId('publish-approve-by').fill('Grace Hopper')
  await expect(page.getByTestId('publish-approve-send')).toBeEnabled()
  await expect(page.getByTestId('publish-egress-connect=https://api.example.com')).not.toBeChecked()
  await expect(page.getByTestId('publish-egress-img=https://cdn.example.com')).not.toBeChecked()
  await page.getByTestId('publish-egress-connect=https://api.example.com').check()
  await page.getByTestId('publish-approve-note').fill('reviewed the origins')
  await page.getByTestId('publish-approve-send').click()
  await expect(page.getByTestId('apps-notice')).toContainText('Approved ledger (apr_1)')
  expect(gateway.lastBody('POST app-approvals')).toEqual({ manifest: ledgerManifest, files: { 'index.html': base64('<html>ledger</html>') }, by: 'Grace Hopper', note: 'reviewed the origins', egress: 'connect=https://api.example.com' })
  await expect(page.getByTestId('publish-approve')).toHaveCount(0)
  await expect(page.getByTestId('publish-send')).toBeEnabled()
  await expect(page.getByTestId('approval-row-apr_1')).toContainText('for connect https://api.example.com')

  // the Gateway refuses what the screen allowed (the approval was revoked meanwhile): its words are shown and nothing is published
  gateway.approvals[0]!.revoked = true
  await page.getByTestId('publish-send').click()
  await expect(page.getByTestId('publish-refusal')).toContainText('asks to be trusted and has no approval for these exact files and manifest')
  expect(gateway.apps.has('ledger')).toBe(false)
  gateway.approvals[0]!.revoked = false
  await page.getByTestId('publish-send').click()
  await expect(page.getByTestId('apps-notice')).toContainText('ledger is published as version 1 (1 file) and served at once')
  expect(gateway.apps.get('ledger')!.versions).toHaveLength(1)

  // the approval of a version that is already published chooses origins too
  await page.getByTestId('app-open-vault').click()
  await page.getByTestId('version-approve-1').click()
  await page.getByTestId('approver-name').fill('Grace Hopper')
  await page.getByTestId('approve-egress-img=https://cdn.example.com').check()
  await page.getByTestId('approve-confirm-1').click()
  await expect(page.getByTestId('apps-notice')).toContainText('Approved (apr_2)')
  expect(gateway.lastBody('POST app-approvals')).toEqual({ app: 'vault', version: 1, by: 'Grace Hopper', egress: 'img=https://cdn.example.com' })
  await expect(page.getByTestId('approval-row-apr_2')).toContainText('for img https://cdn.example.com')

  // a person who may only look sees why Publish is off
  await mockGateway(page, { can_publish: false, can_approve: false, you: 'Bob' })
  await page.reload()
  await expect(page.getByTestId('instance-row-acme')).toBeVisible()
  await expect(page.getByTestId('publish-open')).toBeDisabled()
  await expect(page.getByTestId('publish-open-why')).toContainText('publisher or administrator role')
  expect(outside).toEqual([])
}

/**
 * UE03: a navigation set is made from the instances that exist (a member is added from a list of the ones not listed yet, so none is listed twice), reordered, labelled, renamed (saved under the new name with the old one retired in
 * the same call) and removed; the Gateway's refusal (a member removed meanwhile) is shown in its words and keeps the editor open; the instances are untouched by the set's removal; a person without the role sees why every button is off.
 */
export async function navigationStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  await page.setViewportSize({ width: 1440, height: 1600 })
  const gateway = await mockGateway(page)
  await openApps(page)
  await expect(page.getByTestId('nav-row-main').getByTestId('nav-entries')).toHaveText('acme (Acme) → pinned')
  await expect(page.getByTestId('instance-row-acme')).toContainText('in the menu main')

  // a new set says what is missing
  await page.getByTestId('nav-new').click()
  await expect(page.getByTestId('nav-editor')).toHaveAttribute('data-mode', 'new')
  await expect(page.getByTestId('nav-editor-save')).toBeDisabled()
  await expect(page.getByTestId('nav-editor-why')).toContainText('Name the set')
  await page.getByTestId('nav-editor-name').fill('main')
  await expect(page.getByTestId('nav-editor-why')).toContainText('already a navigation set named main')
  await page.getByTestId('nav-editor-name').fill('team')
  await expect(page.getByTestId('nav-editor-why')).toContainText('Give the set a title')
  await page.getByTestId('nav-editor-title').fill('Team menu')
  await expect(page.getByTestId('nav-editor-why')).toContainText('at least one instance')

  // members are added from the instances not listed yet; one cannot be listed twice
  await page.getByTestId('nav-editor-add').selectOption('pinned')
  await page.getByTestId('nav-editor-add').selectOption('acme')
  await expect(page.getByTestId('nav-editor-add-none')).toContainText('Every instance is already listed')
  await expect(page.locator('[data-testid^="nav-editor-member-"]')).toHaveText([/pinned/, /acme/])
  await page.getByTestId('nav-editor-label-pinned').fill('Pinned notes')
  await page.getByTestId('nav-editor-up-acme').click()
  await expect(page.locator('[data-testid^="nav-editor-member-"]')).toHaveText([/acme/, /pinned/])
  await expect(page.getByTestId('nav-editor-up-acme')).toBeDisabled()
  await expect(page.getByTestId('nav-editor-down-pinned')).toBeDisabled()
  await page.getByTestId('nav-editor-save').click()
  await expect(page.getByTestId('apps-notice')).toContainText('Navigation set team is saved (version 1)')
  expect(gateway.lastBody('PUT app-navigation/team')).toEqual({ title: 'Team menu', entries: [{ instance: 'acme', label: null, icon: null }, { instance: 'pinned', label: 'Pinned notes', icon: null }] })
  await expect(page.getByTestId('nav-row-team').getByTestId('nav-entries')).toHaveText('acme → pinned (Pinned notes)')
  await expect(page.getByTestId('instance-row-acme')).toContainText('in the menu main, team')

  // a member that was removed meanwhile: the Gateway's refusal is shown in its words and the editor stays open with what was typed
  await page.getByTestId('nav-edit-team').click()
  await expect(page.getByTestId('nav-editor-label-pinned')).toHaveValue('Pinned notes')
  const pinned = gateway.instances.get('pinned')!
  gateway.instances.delete('pinned')
  await page.getByTestId('nav-editor-save').click()
  await expect(page.getByTestId('error-banner')).toContainText("'pinned' is not an instance published to production")
  await expect(page.getByTestId('nav-editor')).toBeVisible()
  expect(gateway.navigation.get('team')!.entries).toHaveLength(2)
  gateway.instances.set('pinned', pinned)

  // renamed: saved under the new name and the old one retired in one call; a name that another set has is refused before a call is made
  await page.getByTestId('nav-editor-name').fill('main')
  await expect(page.getByTestId('nav-editor-why')).toContainText('already a navigation set named main')
  await expect(page.getByTestId('nav-editor-save')).toBeDisabled()
  await page.getByTestId('nav-editor-name').fill('crew')
  await expect(page.getByTestId('nav-editor-rename')).toContainText('Saving makes the set under the name crew and stops serving team')
  await expect(page.getByTestId('nav-editor-save')).toHaveText('Save under the new name')
  await page.getByTestId('nav-editor-remove-pinned').click()
  await page.getByTestId('nav-editor-save').click()
  await expect(page.getByTestId('apps-notice')).toContainText('Navigation set crew is saved (version 1, renamed from team)')
  expect(gateway.lastBody('PUT app-navigation/crew')).toEqual({ title: 'Team menu', entries: [{ instance: 'acme', label: null, icon: null }], rename_from: 'team' })
  await expect(page.getByTestId('nav-row-team')).toHaveCount(0)
  await expect(page.getByTestId('nav-row-crew').getByTestId('nav-entries')).toHaveText('acme')

  // removed after a second click; the instances are not touched
  await page.getByTestId('nav-remove-crew').click()
  await page.getByTestId('nav-remove-confirm-crew').click()
  await expect(page.getByTestId('apps-notice')).toContainText('Navigation set crew is no longer served; its instances are untouched')
  await expect(page.getByTestId('nav-row-crew')).toHaveCount(0)
  await expect(page.getByTestId('nav-row-main')).toBeVisible()
  await expect(page.getByTestId('instance-row-acme')).toBeVisible()
  await expect(page.getByTestId('instance-row-pinned')).toBeVisible()

  // a person who may only look sees why the buttons are off
  await mockGateway(page, { can_publish: false, can_approve: false, you: 'Bob' })
  await page.reload()
  await expect(page.getByTestId('nav-row-main')).toBeVisible()
  await expect(page.getByTestId('nav-new')).toBeDisabled()
  await expect(page.getByTestId('nav-new-why')).toContainText('publisher or administrator role')
  await expect(page.getByTestId('nav-edit-main')).toBeDisabled()
  await expect(page.getByTestId('nav-remove-main')).toBeDisabled()
  expect(outside).toEqual([])
}
