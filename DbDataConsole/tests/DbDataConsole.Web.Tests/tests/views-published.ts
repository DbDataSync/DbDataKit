import { expect, type Page, type Route } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '../../../../')
const sample = fs.readFileSync(path.join(root, 'DbDataBuild/templates/retail/dashboards/retail-sales-today.yaml'), 'utf8')
const results = path.join(root, 'DbDataKit/conformance/views/widget-results/dashboard/retail-sales-today')
const recorded = (id: string) => JSON.parse(fs.readFileSync(path.join(results, `${id}.json`), 'utf8')) as unknown

const SERVER = 'proxy-1'
const NAME = 'retail-sales-today'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

export interface ModuleCalls { render: Array<{ layout?: string; only?: string[]; parameters?: Record<string, unknown> }>; other: string[] }

/**
 * A Proxy that runs the views module, as the console's relay shows it (the relay is the console's own route, mocked here so no Proxy is needed; the Proxy's side is `ViewsModuleTests`, through the real host): the console lists it, its status says the
 * module is on, it lists the retail dashboard as published (version 2) and renders it. `denied` makes every widget an error entry, as a person with no access to the data source gets.
 */
export async function mockViews(page: Page, options: { module?: boolean; denied?: boolean } = {}): Promise<ModuleCalls> {
  const calls: ModuleCalls = { render: [], other: [] }
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Retail proxy', url: 'https://proxy.invalid', product: 'dbdataproxy', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const tail = url.pathname.slice(`/relay/${SERVER}/api/v1/`.length)
    calls.other.push(`${request.method()} ${tail}`)
    if (tail === 'views/status') return options.module === false ? json(route, { error: 'There is no such call.' }, 404) : json(route, { enabled: true, state: 'ok', dashboards: 1, reports: 0 })
    if (tail === 'dashboards') return json(route, { items: [{ name: NAME, title: 'Retail sales today', version: '2' }] })
    if (tail === 'reports') return json(route, { items: [] })
    if (tail === `dashboards/${NAME}`) {
      return json(route, { kind: 'dashboard', name: NAME, title: 'Retail sales today', version: '2', as_of: '2026-11-08T12:00:00Z', text: sample, document: {}, layouts: [{ name: 'desk', type: 'reflow', default: true }] })
    }
    if (tail === `dashboards/${NAME}/render`) {
      const body = request.postDataJSON() as { layout?: string; only?: string[]; parameters?: Record<string, unknown> }
      calls.render.push(body)
      const all = ['regions', 'net', 'margin', 'note', 'by_region', 'top_products']
      const widgets: Record<string, unknown> = {}
      for (const id of body.only ?? all) {
        widgets[id] = options.denied && id !== 'note'
          ? { type: 'error', code: 'denied', message: 'Not authorized for data source \'sales-ro\': it is for the home groups analysts, and you are in none of them.' }
          : id === 'note' ? { type: 'text', text: 'Showing **all** regions.' } : recorded(id)
      }
      return json(route, { kind: 'rendered_widgets', layout: body.layout ?? 'desk', as_of: '2026-11-08T12:00:00Z', widgets })
    }
    return json(route, { code: 'not_found', message: 'There is no such call.' }, 404)
  })
  return calls
}

/**
 * The Views page over a Proxy's published documents (plan 32 DBR11b): with a Proxy that runs the module the page lists and opens the PUBLISHED dashboard (not the fixtures) through the relay, says so, resolves only what a parameter change touches,
 * draws a person's refusals as widget errors, keeps the choice of source in the address, and goes back to the fixtures by a choice; a Proxy without the module is not a source and the page shows the fixtures.
 */
export async function publishedStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })

  // a Proxy without the module (or one that answers 404, as before this slice) is not a source: the fixtures, said so
  const without = await mockViews(page, { module: false })
  await page.goto('/views/dashboards')
  await expect(page.getByTestId('views-source-current')).toHaveAttribute('data-source', 'fixtures')
  await expect(page.getByTestId('views-source')).toContainText('Fixtures')
  await expect(page.getByTestId('views-source')).not.toContainText('Retail proxy')
  expect(without.render).toEqual([])
  await page.unroute('**/api/instances')
  await page.unroute(`**/relay/${SERVER}/api/v1/**`)

  // with the module: the published dashboard is what is listed, and the first published source is chosen with no address
  const calls = await mockViews(page)
  await page.goto('/views/dashboards')
  await expect(page.getByTestId('views-source-current')).toHaveAttribute('data-published', 'true')
  await expect(page.getByTestId('views-source-current')).toContainText('read as you')
  await expect(page.getByTestId('views-open-file')).toHaveCount(0)
  await expect(page.getByTestId(`views-row-${NAME}`)).toContainText('Retail sales today')
  await page.getByTestId(`views-open-${NAME}`).click()
  await expect(page).toHaveURL(new RegExp(`/views/dashboards/${NAME}$`))
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'desk')
  for (const id of ['regions', 'net', 'margin', 'note', 'by_region', 'top_products']) await expect(page.getByTestId(`vz-tile-${id}`)).toBeVisible()
  await expect(page.getByTestId('views-source-current')).toHaveAttribute('data-published', 'true')
  expect(calls.render.length).toBeGreaterThan(0)
  expect(calls.render[0]!.only).toBeUndefined()
  expect(calls.render[0]!.layout).toBe('desk')

  // a parameter change asks the module for the widgets that use it, and only those; the value is in the address
  const before = calls.render.length
  await page.goto(`/views/dashboards/${NAME}?p.region=West`)
  await expect(page.getByTestId('vz-tile-net')).toBeVisible()
  expect(calls.render.at(-1)!.parameters).toMatchObject({ region: ['West'] })
  expect(calls.render.length).toBeGreaterThan(before)

  // the source chosen in the address is kept through the links
  await page.goto('/views/dashboards?source=fixtures')
  await expect(page.getByTestId('views-source-current')).toHaveAttribute('data-source', 'fixtures')
  await expect(page.getByTestId('views-open-file')).toHaveCount(1)
  await page.getByTestId(`views-open-${NAME}`).click()
  await expect(page).toHaveURL(new RegExp(`/views/dashboards/${NAME}\\?source=fixtures$`))
  await expect(page.getByTestId('views-source-current')).toHaveAttribute('data-published', 'false')
  await page.goto('/views/dashboards?source=gone')
  await expect(page.getByTestId('views-source-unknown')).toContainText('gone')
  await page.getByTestId('views-source').selectOption(SERVER)
  await expect(page).toHaveURL(new RegExp(`source=${SERVER}$`))
  await expect(page.getByTestId(`views-row-${NAME}`)).toBeVisible()

  // a person with no access to the data source: the page is drawn and every widget that needed it says why, in the server's words
  await page.unroute(`**/relay/${SERVER}/api/v1/**`)
  await mockViews(page, { denied: true })
  await page.goto(`/views/dashboards/${NAME}`)
  await expect(page.getByTestId('vz-tile-net').getByTestId('vz-widget-error')).toContainText('home groups analysts')
  await expect(page.getByTestId('vz-tile-by_region').getByTestId('vz-widget-error')).toBeVisible()
  await expect(page.getByTestId('vz-tile-note')).toContainText('Showing')

  expect(outside).toEqual([])
}
