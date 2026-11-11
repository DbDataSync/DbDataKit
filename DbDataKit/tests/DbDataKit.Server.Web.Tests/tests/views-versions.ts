import { expect, type Page, type Route } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Plan 33, UE04: the versions of a published dashboard on the Views page, against a Gateway whose relay is mocked in the page (the Gateway side, with the registry, the roles and the audit, is DbDataGateway.Tests'
// ViewsModuleTests). The mock keeps the versions and what is served, answers the routes of `GatewayViewsManagement`, and keeps every body it is sent, so the story checks what the screen SENDS as well as what it shows.

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '../../../../')
const sample = fs.readFileSync(path.join(root, 'DbDataBuild/templates/retail/dashboards/retail-sales-today.yaml'), 'utf8')
const results = path.join(root, 'Shared/conformance/views/widget-results/dashboard/retail-sales-today')
const recorded = (id: string) => JSON.parse(fs.readFileSync(path.join(results, `${id}.json`), 'utf8')) as unknown

const SERVER = 'gateway-1'
const NAME = 'retail-sales-today'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

interface MockVersion { version: number; title: string; published_by: string; published_at: string; note: string | null }

export interface VersionsMock {
  /** Every body the screen sent, by `METHOD route`. */
  sent: Record<string, unknown[]>
  /** The version served now, or null once retired. */
  active: () => number | null
  /** The next switch is refused as stale, as if someone else had changed it. */
  refuseNextAsStale: () => void
}

export async function mockVersions(page: Page): Promise<VersionsMock> {
  const versions: MockVersion[] = [
    { version: 1, title: 'Retail sales today (first)', published_by: 'Bob', published_at: '2026-11-07T09:30:00Z', note: 'first cut' },
    { version: 2, title: 'Retail sales today', published_by: 'Ada', published_at: '2026-11-08T10:00:00Z', note: 'added the margin tile' },
  ]
  let active: number | null = 2
  let previous: number | null = 1
  let stale = false
  const sent: Record<string, unknown[]> = {}
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Retail gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const tail = url.pathname.slice(`/relay/${SERVER}/api/v1/`.length)
    const key = `${request.method()} ${tail}`
    const body = request.postData() ? request.postDataJSON() as unknown : undefined
    if (request.method() !== 'GET' && !tail.endsWith('/render')) (sent[key + url.search] ??= []).push(body)
    const current = versions.find((v) => v.version === active)
    if (tail === 'views/status') return json(route, { enabled: true, state: 'ok', dashboards: active === null ? 0 : 1, reports: 0 })
    if (tail === 'dashboards') return json(route, { items: current ? [{ name: NAME, title: current.title, version: String(active) }] : [] })
    if (tail === 'reports') return json(route, { items: [] })
    if (tail === `dashboards/${NAME}` && request.method() === 'GET') {
      if (!current) return json(route, { code: 'not_found', message: `There is no dashboard '${NAME}'.` }, 404)
      return json(route, { kind: 'dashboard', name: NAME, title: current.title, version: String(active), as_of: '2026-11-08T12:00:00Z', text: sample.replace('title: Retail sales today', `title: ${current.title}`), document: {}, layouts: [{ name: 'desk', type: 'reflow', default: true }] })
    }
    if (tail === `dashboards/${NAME}/render`) {
      const wanted = request.postDataJSON() as { layout?: string; only?: string[] }
      const widgets: Record<string, unknown> = {}
      for (const id of wanted.only ?? ['regions', 'net', 'margin', 'note', 'by_region', 'top_products']) widgets[id] = id === 'note' ? { type: 'text', text: 'Showing **all** regions.' } : recorded(id)
      return json(route, { kind: 'rendered_widgets', layout: wanted.layout ?? 'desk', as_of: '2026-11-08T12:00:00Z', widgets })
    }
    if (tail === `dashboards/${NAME}/versions`) {
      return json(route, { kind: 'dashboard', name: NAME, active, previous, versions: [...versions].reverse().map((v) => ({ ...v, active: v.version === active, depends_on: [] })) })
    }
    const expected = (value: unknown) => (value === undefined || value === null || value === (active ?? 0))
    const conflict = () => json(route, { error: `The dashboard '${NAME}' has version ${active ?? 0} served, not ${stale ? 'another' : 'that one'}: it changed since you looked. Read it again and decide.`, code: 'version_conflict', message: `The dashboard '${NAME}' has version ${active ?? 0} served, not ${stale ? 'another' : 'that one'}: it changed since you looked. Read it again and decide.`, current_version: active }, 409)
    if (tail === `dashboards/${NAME}/switch` && request.method() === 'POST') {
      const asked = body as { version: number; expected_version?: number }
      if (stale) { stale = false; return conflict() }
      if (!expected(asked.expected_version)) return conflict()
      if (!versions.some((v) => v.version === asked.version)) return json(route, { code: 'not_found', message: `'${NAME}' has no version ${asked.version}.` }, 404)
      previous = active
      active = asked.version
      return json(route, { kind: 'dashboard', name: NAME, version: active, moved: [`dashboard:${NAME}@${active}`] })
    }
    if (tail === `dashboards/${NAME}/rollback` && request.method() === 'POST') {
      const asked = body as { expected_version?: number }
      if (!expected(asked.expected_version)) return conflict()
      const back = previous
      previous = active
      active = back
      return json(route, { kind: 'dashboard', name: NAME, version: active, moved: [`dashboard:${NAME}@${active}`] })
    }
    if (tail === `dashboards/${NAME}` && request.method() === 'DELETE') {
      if (!expected(url.searchParams.get('expected_version') ? Number(url.searchParams.get('expected_version')) : null)) return conflict()
      active = null
      return json(route, { kind: 'dashboard', name: NAME, retired: true })
    }
    return json(route, { code: 'not_found', message: 'There is no such call.' }, 404)
  })
  return { sent, active: () => active, refuseNextAsStale: () => { stale = true } }
}

/**
 * The versions of a published dashboard (UE04): two are listed with who, when and why; the earlier one is served and is the one drawn; a stale change is refused in the Gateway's words and the list shows what is served now; rollback
 * returns to the later one; removing it from the group asks first, sends the version the person saw, and the document is gone from the list.
 */
export async function versionsStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  const mock = await mockVersions(page)

  await page.goto(`/views/dashboards/${NAME}`)
  await expect(page.getByTestId('views-title')).toHaveText('Retail sales today')
  await expect(page.getByTestId('views-versions-panel')).toHaveCount(0)
  await page.getByTestId('views-versions').click()
  await expect(page.getByTestId('views-versions-served')).toHaveAttribute('data-version', '2')
  await expect(page.getByTestId('views-version-2')).toContainText('Ada')
  await expect(page.getByTestId('views-version-2')).toContainText('added the margin tile')
  await expect(page.getByTestId('views-version-2')).toContainText('2026-11-08 10:00 UTC')
  await expect(page.getByTestId('views-version-1')).toContainText('Bob')
  await expect(page.getByTestId('views-version-served-2')).toBeVisible()
  await expect(page.getByTestId('views-version-served-1')).toHaveCount(0)
  // the served version cannot be served again, and says why; the earlier one can
  await expect(page.getByTestId('views-version-switch-2')).toBeDisabled()
  await expect(page.getByTestId('views-version-switch-2')).toHaveAttribute('title', /served now/)
  await expect(page.getByTestId('views-version-rollback')).toContainText('version 1')

  // someone else changed it while this person looked: refused in the Gateway's words, nothing moves
  mock.refuseNextAsStale()
  await page.getByTestId('views-version-switch-1').click()
  await expect(page.getByTestId('views-versions-refused')).toContainText('changed since you looked')
  expect(mock.active()).toBe(2)
  await expect(page.getByTestId('views-title')).toHaveText('Retail sales today')

  // serve the earlier version: the version this person saw is what is sent, and the earlier document is the one drawn
  await page.getByTestId('views-version-switch-1').click()
  await expect(page.getByTestId('views-versions-result')).toContainText('Version 1 of retail-sales-today is served now')
  await expect(page.getByTestId('views-title')).toHaveText('Retail sales today (first)')
  await expect(page.getByTestId('views-version-served-1')).toBeVisible()
  await expect(page.getByTestId('views-version-served-2')).toHaveCount(0)
  expect(mock.sent[`POST dashboards/${NAME}/switch`]!.at(-1)).toEqual({ version: 1, expected_version: 2 })
  await expect(page.getByTestId('vz-tile-net')).toBeVisible()

  // roll back: to the version before, which is the later one again
  await expect(page.getByTestId('views-version-rollback')).toContainText('version 2')
  await page.getByTestId('views-version-rollback').click()
  await expect(page.getByTestId('views-versions-result')).toContainText('Rolled back: version 2')
  await expect(page.getByTestId('views-title')).toHaveText('Retail sales today')
  expect(mock.sent[`POST dashboards/${NAME}/rollback`]!.at(-1)).toEqual({ expected_version: 1 })

  // remove it from the group: asked first (Keep changes nothing), then sent with the version seen, and the list no longer has it
  await page.getByTestId('views-version-retire').click()
  await page.getByTestId('views-version-retire-cancel').click()
  expect(mock.active()).toBe(2)
  await page.getByTestId('views-version-retire').click()
  await expect(page.getByTestId('views-version-retire-ask')).toContainText('versions are kept')
  await page.getByTestId('views-version-retire-confirm').click()
  await expect(page).toHaveURL(/\/views\/dashboards$/)
  await expect(page.getByTestId('views-empty')).toBeVisible()
  await expect(page.getByTestId(`views-row-${NAME}`)).toHaveCount(0)
  expect(mock.active()).toBeNull()
  expect(Object.keys(mock.sent).some((k) => k.startsWith(`DELETE dashboards/${NAME}?expected_version=2`))).toBe(true)

  expect(outside).toEqual([])
}
