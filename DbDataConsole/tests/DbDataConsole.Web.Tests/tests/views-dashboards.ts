import { expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const sample = fs.readFileSync(path.resolve(here, '../../../../DbDataBuild/templates/retail/dashboards/retail-sales-today.yaml'), 'utf8')

const OPEN = '/views/dashboards/retail-sales-today'
const DESK = ['regions', 'net', 'margin', 'note', 'by_region', 'top_products']

/** What the page's fixture source was asked: how many requests so far, and for which widgets the last one. */
async function resolved(page: Page) {
  const line = page.getByTestId('views-resolved')
  return { count: Number(await line.getAttribute('data-count')), widgets: ((await line.getAttribute('data-widgets')) ?? '').split(',').filter(Boolean) }
}

/** Overflow of the document, and of anything inside the dashboard that could scroll (an element whose overflow is auto or scroll with more content than room). */
function overflow(page: Page) {
  return page.evaluate(() => {
    const root = document.documentElement
    const scrolls = [...document.querySelectorAll('[data-testid="vz-dashboard"], [data-testid="vz-dashboard"] *')].filter((e) => {
      const s = getComputedStyle(e)
      return (s.overflowY === 'auto' || s.overflowY === 'scroll' || s.overflowX === 'auto' || s.overflowX === 'scroll') && (e.scrollHeight > e.clientHeight + 1 || e.scrollWidth > e.clientWidth + 1)
    }).map((e) => e.getAttribute('data-testid') ?? e.className)
    return { documentX: root.scrollWidth - root.clientWidth, documentY: root.scrollHeight - root.clientHeight, scrolls }
  })
}

/**
 * The Views page opening a dashboard from fixtures (plan 32 DBR23, M1): the sample dashboard fits the window at three sizes with no scrolling of the document or of a tile; a maximized tile scrolls and closes;
 * `?layout=` is honoured, the canvas layouts are drawn scaled (three sizes, `min_scale`, `overflow: scroll`) and a switch of layout resolves only the widgets not yet resolved; a parameter change (kept in the address) resolves only the widgets that use it; a file with an error is refused with its position;
 * a report is listed and said not to be drawn yet; and the browser contacts no other host. Needs a signed-in page and nothing else: the source is recorded results inside the page.
 */
export async function dashboardsStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => {
    if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url())
  })

  // The way in: the rail, the Dashboards tab, the list of the fixture source.
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await expect(page.getByTestId('views-list')).toBeVisible()
  await expect(page.getByTestId('views-row-retail-sales-today')).toContainText('Retail sales today')
  await page.getByTestId('views-open-retail-sales-today').click()
  await expect(page).toHaveURL(new RegExp(`${OPEN}$`))

  // Three window sizes: the desk layout fills the window, nothing scrolls (the document, a tile or anything in one) and every widget is drawn.
  for (const [width, height] of [[1920, 1080], [1600, 900], [1366, 768]] as const) {
    await page.setViewportSize({ width, height })
    await page.goto(OPEN)
    await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'desk')
    await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-mode', 'grid')
    for (const id of DESK) await expect(page.getByTestId(`vz-tile-${id}`)).toBeVisible()
    await expect(page.getByTestId('vz-chart-by_region').locator('svg')).toBeVisible()
    await expect(page.getByTestId('vz-loading')).toHaveCount(0)
    expect(await overflow(page), `${width} x ${height}`).toEqual({ documentX: 0, documentY: 0, scrolls: [] })
  }

  // A maximized tile shows the whole table, scrolls inside the tile and closes; the page behind does not move.
  await page.setViewportSize({ width: 1280, height: 720 })
  await page.goto(OPEN)
  await expect(page.getByTestId('vz-table-top_products')).toBeVisible()
  await expect(page.getByTestId('vz-tile-top_products')).toContainText('of 24 rows')
  await page.getByRole('button', { name: 'Maximize Best-selling products' }).click()
  await expect(page.getByTestId('vz-maximized')).toBeVisible()
  await expect(page.getByTestId('vz-table-top_products').getByRole('row')).toHaveCount(25)
  const body = page.getByTestId('vz-widget-top_products').locator('.vz-widget-body')
  expect(await body.evaluate((e) => e.scrollHeight > e.clientHeight)).toBe(true)
  await body.evaluate((e) => { e.scrollTop = e.scrollHeight })
  expect(await body.evaluate((e) => e.scrollTop)).toBeGreaterThan(0)
  expect((await overflow(page)).documentY).toBe(0)
  await page.getByRole('button', { name: 'Close the full view of Best-selling products' }).click()
  await expect(page.getByTestId('vz-maximized')).toHaveCount(0)
  await expect(page.getByTestId('vz-tile-net')).toBeVisible()

  // The layout in the address wins over the window; a layout the document lacks is said so and the window decides.
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.goto(`${OPEN}?layout=wall`)
  await expect(page.getByTestId('views-layout-current')).toHaveAttribute('data-layout', 'wall')
  await expect(page.getByTestId('views-layout-select')).toHaveValue('wall')
  await page.goto(`${OPEN}?layout=tv`)
  await expect(page.getByTestId('views-layout-unknown')).toContainText('no layout named `tv`')
  await expect(page.getByTestId('views-layout-current')).toHaveAttribute('data-layout', 'desk')

  // A canvas layout (DBR07): `wall` is a 1920 x 1080 picture, scaled as one to the room it has. At three window sizes it is uniformly scaled (the stage's aspect decides which side fits), keeps its tiles' proportions,
  // does not scroll the document or the stage, and the chart is an SVG inside the transform.
  for (const [width, height] of [[1920, 1080], [1600, 900], [1366, 768]] as const) {
    await page.setViewportSize({ width, height })
    await page.goto(`${OPEN}?layout=wall`)
    const stage = page.getByTestId('vz-dashboard')
    await expect(stage).toHaveAttribute('data-mode', 'canvas')
    await expect(stage).toHaveAttribute('data-layout-type', 'canvas')
    await expect(page.getByTestId('vz-engine-missing')).toHaveCount(0)
    for (const id of DESK) await expect(page.getByTestId(`vz-tile-${id}`)).toBeVisible()
    await expect(page.getByTestId('vz-chart-by_region').locator('svg')).toBeVisible()
    await expect(page.getByTestId('vz-loading')).toHaveCount(0)
    const room = (await stage.boundingBox())!
    const scale = Number(await stage.getAttribute('data-scale'))
    expect(scale, `${width} x ${height}`).toBeCloseTo(Math.min(room.width / 1920, room.height / 1080), 3)
    const picture = (await page.getByTestId('vz-canvas').boundingBox())!
    expect(picture.width).toBeCloseTo(1920 * scale, 0)
    expect(picture.height).toBeCloseTo(1080 * scale, 0)
    // the tiles keep their proportions: by_region is 1100 x 700 on the canvas
    const tile = (await page.getByTestId('vz-tile-by_region').boundingBox())!
    expect(tile.width / tile.height).toBeCloseTo(1100 / 700, 1)
    expect(await overflow(page), `${width} x ${height}`).toEqual({ documentX: 0, documentY: 0, scrolls: [] })
  }

  // Too small a room for `min_scale` (0.5; the stage of a 1280 x 720 window is already below it): the picture holds half size and the stage scrolls, so text stays readable.
  await page.setViewportSize({ width: 800, height: 450 })
  await page.goto(`${OPEN}?layout=wall`)
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-scale', '0.5000')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-scrolls', '')
  expect(await page.getByTestId('vz-dashboard').evaluate((e) => e.scrollHeight > e.clientHeight || e.scrollWidth > e.clientWidth)).toBe(true)
  expect((await overflow(page)).documentY).toBe(0)

  // `overflow: scroll` is scale 1 with scrollbars (a file of the sample with that one change; it has no recorded results of its own, which is not what this checks).
  const scrolling = sample.replace('overflow: scale\n    tiles:\n      - {widget: regions, x: 40', 'overflow: scroll\n    tiles:\n      - {widget: regions, x: 40')
  expect(scrolling).not.toBe(sample)
  await page.setViewportSize({ width: 1280, height: 720 })
  await page.goto('/views/dashboards')
  await page.getByTestId('views-open-file').setInputFiles({ name: 'scrolling.yaml', mimeType: 'application/yaml', buffer: Buffer.from(scrolling) })
  await expect(page).toHaveURL(/\/views\/dashboards\/scrolling$/)
  await page.getByTestId('views-layout-select').selectOption('wall')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-overflow', 'scroll')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-scale', '1.0000')
  expect(await page.getByTestId('vz-dashboard').evaluate((e) => e.scrollWidth > e.clientWidth && e.scrollHeight > e.clientHeight)).toBe(true)
  expect((await page.getByTestId('vz-canvas').boundingBox())!.width).toBeCloseTo(1920, 0)

  // A switch of layout resolves only what the new layout places and nothing has yet: phone places five widgets, desk adds the note and nothing else; back to phone asks for nothing.
  await page.goto(`${OPEN}?layout=phone`)
  await expect(page.getByTestId('views-layout-current')).toHaveAttribute('data-layout', 'phone')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-mode', 'canvas')
  await expect.poll(() => resolved(page)).toEqual({ count: 1, widgets: ['regions', 'net', 'margin', 'by_region', 'top_products'] })
  await page.getByTestId('views-layout-select').selectOption('desk')
  await expect(page).toHaveURL(/layout=desk/)
  await expect(page.getByTestId('vz-tile-note')).toBeVisible()
  await expect.poll(() => resolved(page)).toEqual({ count: 2, widgets: ['note'] })
  await page.getByTestId('views-layout-select').selectOption('phone')
  await expect(page.getByTestId('views-layout-current')).toHaveAttribute('data-layout', 'phone')
  await page.getByTestId('views-layout-select').selectOption('')
  await expect(page.getByTestId('views-layout-current')).toHaveAttribute('data-layout', 'desk')
  expect((await resolved(page)).count).toBe(2)

  // A parameter: the region control sets it, the address keeps it, and only the widgets that use it are resolved again (not the control, not the chart that the parameter does not filter).
  await page.goto(OPEN)
  await expect(page.getByTestId('vz-tile-net')).toBeVisible()
  await expect.poll(() => resolved(page)).toEqual({ count: 1, widgets: DESK })
  await page.getByTestId('vz-tile-regions').getByRole('checkbox', { name: 'West' }).check()
  await expect(page).toHaveURL(/p\.region=West/)
  await expect(page.getByTestId('vz-text')).toContainText('Showing West')
  await expect(page.getByTestId('views-parameter-region')).toContainText('West')
  await expect.poll(() => resolved(page)).toEqual({ count: 2, widgets: ['net', 'margin', 'note', 'top_products'] })
  // the same address opens the same board: West is checked and the first request is the whole board
  await page.reload()
  await expect(page.getByTestId('vz-tile-regions').getByRole('checkbox', { name: 'West' })).toBeChecked()
  await expect.poll(() => resolved(page)).toEqual({ count: 1, widgets: DESK })
  await page.getByTestId('views-parameters-reset').click()
  await expect(page).not.toHaveURL(/p\.region/)
  await expect(page.getByTestId('vz-tile-regions').getByRole('checkbox', { name: 'West' })).not.toBeChecked()
  await expect.poll(() => resolved(page)).toEqual({ count: 2, widgets: ['net', 'margin', 'note', 'top_products'] })

  // The theme follows the console (the system's setting).
  await page.emulateMedia({ colorScheme: 'dark' })
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-vz-theme', 'dark')
  await page.emulateMedia({ colorScheme: 'light' })
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-vz-theme', 'light')

  // A file with an error is refused with its position and nothing is drawn: the first tile of the grid is made wider than the grid.
  const wrong = '{widget: net, x: 0, y: 1, w: 3, h: 2}'
  expect(sample).toContain(wrong)
  const bad = sample.replace(wrong, '{widget: net, x: 0, y: 1, w: 30, h: 2}')
  const line = bad.slice(0, bad.indexOf('w: 30')).split('\n').length
  await page.goto('/views/dashboards')
  await page.getByTestId('views-open-file').setInputFiles({ name: 'bad.yaml', mimeType: 'application/yaml', buffer: Buffer.from(bad) })
  await expect(page.getByTestId('views-file-refused')).toContainText('bad.yaml is not shown')
  await expect(page.getByTestId('views-problems')).toContainText(`bad.yaml:${line}:`)
  await expect(page.getByTestId('vz-dashboard')).toHaveCount(0)
  // a good file opens: the sample again, under another name (it has no recorded results of its own, so each widget says so)
  await page.getByTestId('views-open-file').setInputFiles({ name: 'mine.yaml', mimeType: 'application/yaml', buffer: Buffer.from(sample) })
  await expect(page).toHaveURL(/\/views\/dashboards\/mine$/)
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'desk')
  await expect(page.getByTestId('vz-widget-error').first()).toContainText('no_recorded_result')

  // A flow layout is chosen and not drawn yet (DBR14): it says so.
  await page.goto(`${OPEN}?layout=print`)
  await expect(page.getByTestId('vz-engine-missing')).toContainText('flow layout')

  // A report is listed and checked; drawing it is not built, and the page says so.
  await page.getByTestId('views-tab-reports').click()
  await page.getByTestId('views-open-retail-sales-monthly').click()
  await expect(page.getByTestId('views-report-note')).toContainText('Reports are not drawn yet')
  await expect(page.getByTestId('vz-dashboard')).toHaveCount(0)

  // A name the source does not have is an error with the way back, not a blank page.
  await page.goto('/views/dashboards/nothing-here')
  await expect(page.getByTestId('views-error')).toContainText('no dashboard named `nothing-here`')

  expect(outside).toEqual([])
}
