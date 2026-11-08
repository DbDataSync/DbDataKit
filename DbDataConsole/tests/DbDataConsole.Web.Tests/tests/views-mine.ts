import { expect, type Page, type Route } from '@playwright/test'
import { mockViews } from './views-published'

const SERVER = 'gateway-1'
const MINE = `mine:${SERVER}`
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

interface Stored { owner: string; ownerName: string; kind: 'dashboard' | 'report'; name: string; version: number; versions: Array<{ version: number; text: string; by: string; at: string }>; shares: string[] }

// the people of the story: a person's id owns what they make (u-<name>), their name is what lists show and what shares name
const PEOPLE: Record<string, { name: string; roles: string[]; net: string }> = {
  wendy: { name: 'Wendy', roles: ['analyst'], net: '900' },
  erin: { name: 'Erin', roles: ['analyst'], net: '700' },
  bob: { name: 'Bob', roles: ['clerk'], net: '1,600' },
}

const current = (d: Stored) => d.versions.find((v) => v.version === d.version)!
const titleOf = (text: string) => /^title: (.*)$/m.exec(text)?.[1]?.trim() ?? /"title"\s*:\s*"([^"]*)"/.exec(text)?.[1] ?? 'Untitled'

/**
 * The module's routes for a person's own documents as the console's relay shows them: a **fake of the module's rules** (the real ones are `ViewsUserDocumentsTests`, through the real host on SQLite, SQL Server and PostgreSQL): who owns
 * what, who may open what (own, or shared to them by name, role or everyone), a stale save is a 409, every save is a version, restore is a new version. The person is the one the story names (`as`), so one page shows two people;
 * the rows follow the person (the fake gives each its own number for the same widget). `bump` is a save made behind the page's back, for the conflict.
 */
export function mockMine(page: Page) {
  const docs: Stored[] = []
  const puts: Array<{ name: string; text: string; expected?: string | number }> = []
  const state = { who: 'wendy', clock: 0 }
  const stamp = () => `2026-11-08T12:${String(state.clock++).padStart(2, '0')}:00Z`
  const id = () => `u-${state.who}`
  const grantees = () => ['*', `user:${id()}`, `user:${state.who}`, ...PEOPLE[state.who]!.roles.map((r) => `group:${r}`)]
  const find = (owner: string, kind: string, name: string) => docs.find((d) => d.owner === owner && d.kind === kind && d.name === name)
  const visible = (d: Stored) => d.owner === id() || d.shares.some((g) => grantees().includes(g))
  const save = (owner: string, kind: 'dashboard' | 'report', name: string, text: string, by: string, expected?: string | number) => {
    const d = find(owner, kind, name)
    if (!d) { docs.push({ owner, ownerName: PEOPLE[state.who]!.name, kind, name, version: 1, versions: [{ version: 1, text, by, at: stamp() }], shares: [] }); return 1 }
    d.version += 1
    d.versions.push({ version: d.version, text, by, at: stamp() })
    void expected
    return d.version
  }

  const handler = async (route: Route) => {
    const request = route.request()
    const url = new URL(request.url())
    const tail = url.pathname.slice(`/relay/${SERVER}/api/v1/my/`.length)
    const method = request.method()
    const parts = tail.split('/')
    if (tail === 'views') {
      const kind = url.searchParams.get('kind')
      const items = docs.filter((d) => (!kind || d.kind === kind) && visible(d)).sort((a, b) => a.owner.localeCompare(b.owner) || a.name.localeCompare(b.name))
      const mine = items.filter((d) => d.owner === id()), others = items.filter((d) => d.owner !== id())
      return json(route, { items: [...mine, ...others].map((d) => ({ kind: d.kind, owner: d.owner, owner_name: d.ownerName, name: d.name, title: titleOf(current(d).text), version: String(d.version), updated_at: current(d).at, own: d.owner === id(), shared_with: d.owner === id() ? d.shares : [] })) })
    }
    const kind = parts[0] === 'reports' ? 'report' : 'dashboard'
    const name = decodeURIComponent(parts[1] ?? '')
    const action = parts[2]
    const owner = url.searchParams.get('owner') ?? id()
    const target = find(owner, kind, name)
    const notFound = () => json(route, { code: 'not_found', message: `There is no ${kind} '${name}' of ${owner} that you may open.` }, 404)
    if (method === 'PUT' && !action) {
      const body = request.postDataJSON() as { text: string; expected_version?: string | number }
      puts.push({ name, text: body.text, expected: body.expected_version })
      const mine = find(id(), kind, name)
      if (!mine && body.expected_version !== undefined) return json(route, { code: 'not_found', message: 'There is no document of yours to change.' }, 404)
      if (mine && String(mine.version) !== String(body.expected_version)) return json(route, { code: 'version_conflict', message: `The ${kind} '${name}' is at version ${mine.version}, not ${body.expected_version ?? 'none'}; open it again and save on that version.`, current_version: String(mine.version) }, 409)
      return json(route, { owner: id(), name, version: String(save(id(), kind, name, body.text, state.who)) })
    }
    if (!target || !visible(target)) return notFound()
    const own = target.owner === id()
    if (method === 'GET' && !action) return json(route, { kind, owner: target.owner, owner_name: target.ownerName, name, title: titleOf(current(target).text), version: String(target.version), as_of: current(target).at, own, text: current(target).text, document: {}, layouts: [{ name: 'desk', type: 'reflow', default: true }] })
    if (method === 'POST' && action === 'render') {
      const body = (request.postDataJSON() ?? {}) as { only?: string[] }
      const text = current(target).text
      const placed = [...text.matchAll(/widget: ([A-Za-z0-9_-]+)/g)].map((m) => m[1]!)
      const widgets: Record<string, unknown> = {}
      for (const id of body.only ?? placed)
        widgets[id] = /^(net|margin|query)/.test(id) ? { type: 'metric', value: { v: 1, text: PEOPLE[state.who]!.net } } : { type: 'text', text: `${id}` }
      return json(route, { kind: 'rendered_widgets', layout: 'desk', as_of: current(target).at, widgets })
    }
    if (!own) return notFound()   // only the owner changes, shares, restores, promotes or deletes: a call on that name is a call on the caller's own
    if (method === 'DELETE' && !action) { docs.splice(docs.indexOf(target), 1); return json(route, { deleted: true }) }
    if (action === 'shares') {
      if (method === 'PUT') target.shares = [...new Set((request.postDataJSON() as { grantees: string[] }).grantees.map((g) => g.toLowerCase()))]
      return json(route, { grantees: target.shares })
    }
    if (action === 'history' && method === 'GET') {
      const version = url.searchParams.get('version')
      if (version) return json(route, { version, text: target.versions.find((v) => String(v.version) === version)?.text ?? '' })
      return json(route, { items: [...target.versions].reverse().map((v) => ({ version: String(v.version), saved_by: v.by, saved_at: v.at, current: v.version === target.version })) })
    }
    if (action === 'restore' && method === 'POST') {
      const body = request.postDataJSON() as { version: number; expected_version: number }
      if (body.expected_version !== target.version) return json(route, { code: 'version_conflict', message: `The ${kind} '${name}' is at version ${target.version}, not ${body.expected_version}.`, current_version: String(target.version) }, 409)
      const earlier = target.versions.find((v) => v.version === body.version)!
      return json(route, { owner: id(), name, version: String(save(id(), kind, name, earlier.text, state.who)), restored: String(body.version) })
    }
    if (action === 'promote' && method === 'GET') return json(route, { path: `${kind === 'report' ? 'reports' : 'dashboards'}/${name}.yaml`, text: current(target).text })
    return json(route, { code: 'not_found', message: 'There is no such call.' }, 404)
  }

  return {
    docs, puts, state,
    install: () => page.route(`**/relay/${SERVER}/api/v1/my/**`, handler),
    as: (person: string) => { state.who = person },
    /** A save made behind the page's back (another tab, a second device): the page's version is stale. */
    bump: (name: string, text?: string) => { const d = find(id(), 'dashboard', name)!; save(d.owner, 'dashboard', name, text ?? current(d).text.replace(/^title: .*$/m, 'title: Changed elsewhere'), state.who) },
  }
}

/**
 * Your own dashboards (plan 32 DBR12) on the Views page: pin a widget of a published dashboard into a new dashboard of your own and into an existing one, see it with your own rows, edit it (and meet a stale edit as a conflict), share it to a
 * group, read its history and bring an earlier version back, promote it to a file, and see what another person sees (nothing before the share, and after it the document with THEIR rows, owned by you, not editable by them, copyable);
 * pin a semantic request kept as a widget; delete. The module is a **fake of its rules** in the page (the real ones are `ViewsUserDocumentsTests`, through the real host); what the story proves is the screens.
 */
export async function mineStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })

  await mockViews(page)
  const mine = mockMine(page)
  await mine.install()
  const list = () => page.goto(`/views/dashboards?source=${encodeURIComponent(MINE)}`)

  // the personal source is offered beside the published one; with nothing yet, the list says how to make one
  await list()
  await expect(page.getByTestId('views-source-current')).toHaveAttribute('data-source', MINE)
  await expect(page.getByTestId('views-source')).toContainText('My dashboards on Retail gateway')
  await expect(page.getByTestId('views-empty')).toContainText('You have no dashboards of your own yet')

  // PIN a widget of the published dashboard into a NEW dashboard of your own: the parameter it uses goes with it
  await page.goto('/views/dashboards/retail-sales-today')
  await expect(page.getByTestId('vz-tile-net')).toBeVisible()
  await page.getByTestId('pin-open').click()
  await page.getByTestId('pin-widget').selectOption('net')
  await page.getByTestId('pin-target').selectOption('')
  await page.getByTestId('pin-new-name').fill('my-board')
  await page.getByTestId('pin-new-title').fill('My board')
  await page.getByTestId('pin-go').click()
  await expect(page.getByTestId('pin-done')).toHaveAttribute('data-name', 'my-board')
  await expect(page.getByTestId('pin-done')).toContainText('the new dashboard')
  expect(mine.puts[0]!.text).toContain('title: My board')
  expect(mine.puts[0]!.text).toMatch(/net:\s*\n\s+type: metric/)
  expect(mine.puts[0]!.text).toContain('name: region')   // the parameter its filter uses
  expect(mine.puts[0]!.expected).toBeUndefined()

  // ... and into the EXISTING one, as a second widget, saved on the version it opened
  await page.getByTestId('pin-widget').selectOption('margin')
  await expect(page.getByTestId('pin-target')).toHaveValue('my-board')
  await page.getByTestId('pin-go').click()
  await expect(page.getByTestId('pin-done')).toHaveAttribute('data-widget', 'margin')
  expect(mine.puts[1]!.expected).toBe('1')
  expect(mine.docs[0]!.version).toBe(2)

  // my dashboards: yours, with you as the owner and nobody else it is shared with; it opens with YOUR rows
  await list()
  await expect(page.getByTestId('views-row-my-board')).toContainText('My board')
  await expect(page.getByTestId('my-owner-my-board')).toHaveText('You')
  await expect(page.getByTestId('my-shared-my-board')).toHaveText('Only you')
  await page.getByTestId('views-open-my-board').click()
  await expect(page).toHaveURL(/\/views\/dashboards\/my-board\?source=mine%3Agateway-1$/)
  await expect(page.getByTestId('vz-tile-net')).toContainText('900')
  await expect(page.getByTestId('views-edit')).toBeVisible()

  // EDIT with the existing editor against this source; a save made elsewhere meanwhile is a conflict that offers the stored one
  await page.getByTestId('views-edit').click()
  await expect(page.getByTestId('ed-root')).toBeVisible()
  await page.getByTestId('ed-title').fill('My board, edited')
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-status')).toContainText('Saved my-board (version 3)')
  expect(mine.puts.at(-1)!.expected).toBe('2')
  mine.bump('my-board')
  await page.getByTestId('ed-title').fill('My board, edited twice')
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-conflict')).toBeVisible()
  await expect(page.getByTestId('ed-status')).toContainText('is at version 4')
  await page.getByTestId('ed-conflict-reload').click()
  await expect(page.getByTestId('ed-title')).toHaveValue('Changed elsewhere')

  // SHARE it with a group
  await list()
  await page.getByTestId('my-share-my-board').click()
  await expect(page.getByTestId('my-share-list')).toContainText('Shared with nobody')
  await page.getByTestId('my-share-type').selectOption('group')
  await page.getByTestId('my-share-value').fill('analyst')
  await page.getByTestId('my-share-add').click()
  await expect(page.getByTestId('my-share-item-group:analyst')).toContainText('the analyst group')
  await expect(page.getByTestId('my-shared-my-board')).toContainText('the analyst group')
  expect(mine.docs[0]!.shares).toEqual(['group:analyst'])

  // HISTORY: every version; an earlier one is read and brought back as a NEW version
  await page.getByTestId('my-history-my-board').click()
  await expect(page.getByTestId('my-history-list').getByRole('row')).toHaveCount(5)   // the header and four versions
  await expect(page.getByTestId('my-version-4')).toHaveAttribute('data-current', 'true')
  await page.getByTestId('my-version-view-1').click()
  await expect(page.getByTestId('my-version-text')).toContainText('title: My board')
  await page.getByTestId('my-version-restore-1').click()
  await expect(page.getByTestId('my-history-message')).toContainText('Version 1 is back as version 5')
  await expect(page.getByTestId('my-version-5')).toHaveAttribute('data-current', 'true')

  // PROMOTE to a file
  await page.getByTestId('my-promote-my-board').click()
  await expect(page.getByTestId('my-promote-path')).toHaveText('dashboards/my-board.yaml')
  await expect(page.getByTestId('my-promote-text')).toContainText('kind: dashboard')

  // ANOTHER PERSON: bob is a clerk, so there is nothing shared with him; erin is an analyst, so she gets the document, owned by wendy, and sees HER rows
  mine.as('bob')
  await list()
  await expect(page.getByTestId('views-empty')).toBeVisible()
  await page.goto(`/views/dashboards/${encodeURIComponent('my-board@u-wendy')}?source=${encodeURIComponent(MINE)}`)
  await expect(page.getByTestId('views-error')).toContainText('There is no dashboard')
  mine.as('erin')
  await list()
  await expect(page.getByTestId('my-owner-my-board@u-wendy')).toHaveText('Wendy')
  await expect(page.getByTestId('my-shared-my-board@u-wendy')).toHaveText('Shared with you')
  await expect(page.getByTestId('my-edit-my-board@u-wendy')).toHaveCount(0)
  await expect(page.getByTestId('my-share-my-board@u-wendy')).toHaveCount(0)
  await page.getByTestId('views-open-my-board@u-wendy').click()
  await expect(page.getByTestId('views-shared-by')).toContainText('Shared by Wendy')
  await expect(page.getByTestId('vz-tile-net')).toContainText('700')
  await expect(page.getByTestId('vz-tile-net')).not.toContainText('900')
  await expect(page.getByTestId('views-edit')).toHaveCount(0)
  await list()
  await page.getByTestId('my-copy-my-board@u-wendy').click()
  await expect(page.getByTestId('my-notice')).toContainText('Copied to your own dashboard `my-board`')
  await expect(page.getByTestId('my-owner-my-board')).toHaveText('You')
  await expect(page.getByTestId('my-owner-my-board@u-wendy')).toHaveText('Wendy')
  expect(mine.docs.filter((d) => d.owner === 'u-erin').map((d) => d.name)).toEqual(['my-board'])

  // PIN A QUERY result: a semantic request kept as a widget (an unusable one is refused in words, nothing saved)
  mine.as('wendy')
  await list()
  await page.getByTestId('pin-query-open').click()
  await page.getByTestId('pin-query-model').fill('sales_catalog')
  await page.getByTestId('pin-query-request').fill('{"attributes": ["customers.region"]}')
  await page.getByTestId('pin-target').selectOption('')
  await page.getByTestId('pin-new-name').fill('by-region')
  await page.getByTestId('pin-query-go').click()
  await expect(page.getByTestId('pin-query-error')).toContainText('no `measures`')
  expect(mine.docs.some((d) => d.name === 'by-region')).toBe(false)
  await page.getByTestId('pin-query-request').fill('{"measures": ["orders.net_revenue"], "attributes": ["customers.region"], "limit": 100}')
  await page.getByTestId('pin-query-title').fill('Revenue by region')
  await page.getByTestId('pin-query-go').click()
  await expect(page.getByTestId('pin-query-done')).toContainText('by-region')
  const kept = mine.docs.find((d) => d.name === 'by-region')!
  expect(current(kept).text).toContain('mode: explicit')
  expect(current(kept).text).toContain('Revenue by region')
  await expect(page.getByTestId('views-row-by-region')).toBeVisible()

  // DELETE (asked twice)
  await page.getByTestId('my-delete-by-region').click()
  await page.getByTestId('my-delete-confirm-by-region').click()
  await expect(page.getByTestId('my-notice')).toContainText('Deleted `by-region`')
  await expect(page.getByTestId('views-row-by-region')).toHaveCount(0)
  expect(mine.docs.some((d) => d.name === 'by-region')).toBe(false)

  expect(outside).toEqual([])
}
