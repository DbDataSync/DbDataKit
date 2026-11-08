import { expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const sample = fs.readFileSync(path.resolve(here, '../../../../DbDataBuild/templates/retail/dashboards/retail-sales-today.yaml'), 'utf8')

const NAME = 'retail-sales-today'
const OPEN = `/views/dashboards/${NAME}`
const DEPENDENTS = ['margin', 'net', 'note', 'top_products']

// The sample with a drill on the chart (Alt+click), one on the table (a drill that forgets a parameter its target requires) and a kiosk layout: the wall.
const drilling = sample
  .replace("    on_select: {set: region, from: customers.region}\n", "    on_select: {set: region, from: customers.region}\n    drill: {to: {dashboard: region-detail}, with: {region: $customers.region}}\n")
  .replace("      limit: 100\n", "      limit: 100\n    drill: {to: {dashboard: needs-year}, with: {product: $products.product}}\n")
  .replace("refresh: {every: 5m}\n", "refresh: {every: 5m}\nkiosk: {layout: wall}\n")

const regionDetail = `schemaVersion: 1
kind: dashboard
title: Region detail
parameters:
  - {name: region, type: string, required: true, label: Region}
widgets:
  who: {type: control, parameter: region, control: text, label: Region}
  note: {type: text, text: "Detail for **{{params.region}}**."}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 4}
    tiles:
      - {widget: who, x: 0, y: 0, w: 12, h: 1}
      - {widget: note, x: 0, y: 1, w: 12, h: 3}
`
const needsYear = `schemaVersion: 1
kind: dashboard
title: Needs a year
parameters:
  - {name: product, type: string, required: true}
  - {name: year, type: integer, required: true}
widgets:
  product: {type: control, parameter: product, control: text, label: Product}
  year: {type: control, parameter: year, control: number, label: Year}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 4}
    tiles:
      - {widget: product, x: 0, y: 0, w: 6, h: 1}
      - {widget: year, x: 6, y: 0, w: 6, h: 1}
`

/** What the page's fixture source was asked: how many requests so far, and for which widgets the last one. */
async function resolved(page: Page) {
  const line = page.getByTestId('views-resolved')
  return { count: Number(await line.getAttribute('data-count')), widgets: ((await line.getAttribute('data-widgets')) ?? '').split(',').filter(Boolean).sort() }
}

async function upload(page: Page, name: string, text: string) {
  await page.goto('/views/dashboards')
  await page.getByTestId('views-open-file').setInputFiles({ name: `${name}.yaml`, mimeType: 'application/yaml', buffer: Buffer.from(text) })
  await expect(page).toHaveURL(new RegExp(`/views/dashboards/${name}$`))
}

/**
 * A bar of the chart, clicked until `after` holds. The chart draws again once its tile has its final size (the first draw may be replaced under the pointer), and a click on a bar that is going away is lost;
 * a click that lands changes the page at once, so `after` is short and a second click never follows one that worked.
 */
async function clickBar(page: Page, after: () => Promise<void>, init: { modifiers?: Array<'Alt' | 'Shift' | 'Control'>; nth?: number } = {}) {
  await expect(async () => {
    await page.getByTestId('vz-chart-by_region').locator('svg .mark-rect path').nth(init.nth ?? 0).click({ modifiers: init.modifiers, timeout: 2000 })
    await after()
  }).toPass({ timeout: 20_000, intervals: [300] })
}
const chip = (page: Page) => page.getByTestId('vz-selection-by_region')
const chosen = (page: Page) => () => expect(chip(page)).toBeVisible({ timeout: 1500 })
const cleared = (page: Page) => () => expect(chip(page)).toHaveCount(0, { timeout: 1500 })

/**
 * Parameters, cross-filter, drill, refresh and kiosk (plan 32 DBR08): a click on a bar of the chart sets the `region` parameter as the control would (the address says `p.region`, only the widgets that use it resolve
 * again, the chip names it, the control shows it) and a second click clears it; the selection survives a switch from the reflow layout to the canvas; Alt+click on a bar drills to another dashboard with the
 * region in the address and a way back that restores the board as it was; a drill that forgets a required parameter of its target is refused in words; the clock, driven by the page's own fake clock, refreshes the board
 * every five minutes and the tiles say "as of"; `?mode=kiosk` shows the document's wall layout alone, scaled to the window, with no chrome and a way out. Needs a signed-in page and nothing else.
 */
export async function interactStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => {
    if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url())
  })
  await page.setViewportSize({ width: 1366, height: 768 })

  // ---- cross-filter, on the reflow layout and on the canvas ----
  await page.goto(OPEN)
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'desk')
  await expect(page.getByTestId('vz-chart-by_region').locator('svg')).toBeVisible()
  await expect(page.getByTestId('vz-selection-by_region')).toHaveCount(0)
  const first = await resolved(page)
  await clickBar(page, chosen(page))
  const region = ((await page.getByTestId('vz-selection-by_region').textContent()) ?? '').replace(/^Region:\s*/, '').replace(/Clear$/, '').trim()
  expect(region).toMatch(/^\w+$/)
  await expect(page).toHaveURL(new RegExp(`p\\.region=${region}`))
  await expect.poll(async () => (await resolved(page)).count).toBe(first.count + 1)
  expect((await resolved(page)).widgets).toEqual(DEPENDENTS)
  // the control shows what the bar chose, the text says so
  await expect(page.getByTestId('vz-text')).toContainText(region)
  await expect(page.getByTestId('vz-control-regions').getByRole('checkbox', { name: region })).toBeChecked()
  // a second click on the same bar clears it
  await clickBar(page, cleared(page))
  await expect(page).not.toHaveURL(/p\.region=/)
  await expect.poll(async () => (await resolved(page)).count).toBe(first.count + 2)
  expect((await resolved(page)).widgets).toEqual(DEPENDENTS)

  // the selection survives a switch to the canvas layout, and clearing it there resolves the same dependents
  await clickBar(page, chosen(page))
  const afterSelect = (await resolved(page)).count
  await page.getByTestId('views-layout-select').selectOption('wall')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'wall')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-mode', 'canvas')
  await expect(page.getByTestId('vz-selection-by_region')).toContainText(region)
  await expect(page).toHaveURL(new RegExp(`p\\.region=${region}`))
  await expect(page.getByTestId('vz-chart-by_region').locator('svg')).toBeVisible()
  // the canvas places no widget the desk did not resolve: nothing is asked for the switch
  expect((await resolved(page)).count).toBe(afterSelect)
  await page.getByTestId('vz-selection-clear-by_region').click()
  await expect(page.getByTestId('vz-selection-by_region')).toHaveCount(0)
  await expect.poll(async () => (await resolved(page)).count).toBe(afterSelect + 1)
  expect((await resolved(page)).widgets).toEqual(DEPENDENTS)
  await page.getByTestId('views-layout-select').selectOption('')

  // ---- drill and the way back ----
  await upload(page, 'region-detail', regionDetail)
  await upload(page, 'needs-year', needsYear)
  await upload(page, NAME, drilling)
  await page.goto(OPEN)
  await expect(page.getByTestId('vz-chart-by_region').locator('svg')).toBeVisible()
  await clickBar(page, chosen(page), { nth: 0 })
  const before = page.url()
  await clickBar(page, () => expect(page).toHaveURL(/\/views\/dashboards\/region-detail\?/, { timeout: 1500 }), { nth: 5, modifiers: ['Alt'] })
  const there = new URL(page.url())
  expect(there.searchParams.get('p.region')).toMatch(/^\w+$/)
  await expect(page.getByTestId('vz-text')).toContainText(`Detail for ${there.searchParams.get('p.region')}`)
  await expect(page.getByTestId('views-trail')).toBeVisible()
  await expect(page.getByTestId('views-trail-0')).toHaveText(NAME)
  await expect(page.getByTestId('views-trail-here')).toHaveText('Region detail')
  // back: the board is as it was left, the selection included
  await page.getByTestId('views-trail-back').click()
  await expect(page).toHaveURL(before)
  await expect(page.getByTestId('vz-selection-by_region')).toBeVisible()
  await expect(page.getByTestId('views-trail')).toHaveCount(0)

  // a drill that leaves out a parameter its target requires is refused, and the board stays
  await page.getByTestId('vz-table-top_products').locator('tbody tr').first().click()
  await expect(page.getByTestId('views-drill-refused')).toContainText('does not give the required parameter `year`')
  await expect(page).toHaveURL(before)

  // ---- refresh: the page's own fake clock, five minutes at a time ----
  await page.clock.install()
  await page.goto(OPEN)
  await expect(page.getByTestId('vz-chart-by_region').locator('svg')).toBeVisible()
  await expect(page.getByTestId('vz-asof-net')).toHaveText(/^as of \d\d:\d\d$/)
  const idle = await resolved(page)
  await page.clock.runFor('04:00')
  expect((await resolved(page)).count).toBe(idle.count)
  await page.clock.runFor('01:40')
  await expect.poll(async () => (await resolved(page)).count).toBe(idle.count + 1)
  expect((await resolved(page)).widgets).toEqual(['by_region', 'margin', 'net', 'note', 'regions', 'top_products'])
  await expect(page.getByTestId('vz-dashboard-host')).toHaveAttribute('data-refresh', 'on')

  // ---- kiosk: the wall, alone, scaled to the window ----
  await page.setViewportSize({ width: 1280, height: 720 })
  await page.goto(`${OPEN}?mode=kiosk`)
  await expect(page.getByTestId('views-kiosk-page')).toBeVisible()
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'wall')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-mode', 'canvas')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-scale', '0.6667')
  await expect(page.getByTestId('vz-chart-by_region').locator('svg')).toBeVisible()
  // no chrome of the console: no rail, no toolbar, no maximize; the document does not scroll
  await expect(page.getByTestId('rail-views')).toHaveCount(0)
  await expect(page.getByTestId('views-toolbar')).toHaveCount(0)
  await expect(page.getByRole('button', { name: /^Maximize/ })).toHaveCount(0)
  const scroll = await page.evaluate(() => ({ x: document.documentElement.scrollWidth - document.documentElement.clientWidth, y: document.documentElement.scrollHeight - document.documentElement.clientHeight }))
  expect(scroll).toEqual({ x: 0, y: 0 })
  const picture = await page.getByTestId('vz-canvas').boundingBox()
  expect(Math.round(picture!.width)).toBe(1280)
  expect(Math.round(picture!.height)).toBe(720)
  // it still cross-filters, and the refresh goes on
  await clickBar(page, chosen(page))
  await expect(page).toHaveURL(/mode=kiosk/)
  // a layout named in the address wins over the kiosk layout
  await page.goto(`${OPEN}?mode=kiosk&layout=desk`)
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'desk')
  await page.goto(`${OPEN}?mode=kiosk`)
  // the way out: the button, and Escape
  await page.getByTestId('views-kiosk-exit').click()
  await expect(page.getByTestId('views-toolbar')).toBeVisible()
  await expect(page).not.toHaveURL(/mode=kiosk/)
  await page.getByTestId('views-kiosk').click()
  await expect(page.getByTestId('views-kiosk-page')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('views-toolbar')).toBeVisible()

  expect(outside, 'the browser contacted no other host').toEqual([])
}
