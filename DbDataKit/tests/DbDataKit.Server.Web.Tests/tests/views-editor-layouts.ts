import { expect, type Locator, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
/** The file the story must save: canonical order, the tiles the drags made, the phone canvas with its selector, one parameter and the refresh. */
const golden = fs.readFileSync(path.resolve(here, 'views-editor-layouts.golden.yaml'), 'utf8').replace(/\r\n/g, '\n')
const STORAGE_KEY = 'dbdatakit.views.documents'

async function storedText(page: Page, key: string): Promise<string | null> {
  return page.evaluate(([storageKey, k]) => {
    const raw = window.localStorage.getItem(storageKey!)
    return raw ? ((JSON.parse(raw) as Record<string, string | null>)[k!] ?? null) : null
  }, [STORAGE_KEY, key] as const)
}

/** A drag with explicit coordinates: the pointer goes down at the middle of the element, moves by (dx, dy) screen pixels in a few steps, and goes up. */
async function drag(page: Page, element: Locator, dx: number, dy: number) {
  await element.scrollIntoViewIfNeeded()
  const box = (await element.boundingBox())!
  const x = box.x + box.width / 2
  const y = box.y + box.height / 2
  await page.mouse.move(x, y)
  await page.mouse.down()
  await page.mouse.move(x + dx / 2, y + dy / 2, { steps: 4 })
  await page.mouse.move(x + dx, y + dy, { steps: 4 })
  await page.mouse.up()
}

const input = (page: Page, id: string) => page.getByTestId(id)
const tile = (layout: string, widget: string, field: string) => `ed-tile-${layout}-${widget}-${field}`
const values = async (page: Page, layout: string, widget: string) => ({
  x: await input(page, tile(layout, widget, 'x')).inputValue(), y: await input(page, tile(layout, widget, 'y')).inputValue(),
  w: await input(page, tile(layout, widget, 'w')).inputValue(), h: await input(page, tile(layout, widget, 'h')).inputValue(),
})

/**
 * The layout editors of the Views page (plan 32 DBR26, M2 steps 4 and 5): on the reflow grid a tile is dragged by whole cells and pulled by its edges, and a move into another tile is refused with the neighbour's name;
 * on a phone canvas (390 x 900, width at most 600 and portrait) tiles snap to 10 px, a tile pulled below its widget's minimum is outlined and the loader's text appears, an overlap is outlined, and undo takes each back;
 * "test at 390 x 844" says the phone layout is chosen and "test at 1366 x 768" the desk; a parameter and the refresh are set; the file saved is the golden. The drags use explicit coordinates taken from the page's own boxes.
 */
export async function editorLayoutsStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1200 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await page.getByTestId('views-new').click()
  // a new document starts with one text widget (`note`) so that it is valid as it stands; the story builds on an empty one
  await page.getByTestId('ed-widget-remove-note').click()
  await input(page, 'ed-file-name').fill('layout-board')
  await input(page, 'ed-title').fill('Layout board')
  for (const type of ['metric', 'chart']) await page.getByTestId(`ed-add-${type}`).click()
  for (const id of ['metric', 'chart']) await page.getByTestId(`ed-place-desk-${id}`).click()
  await expect(input(page, 'ed-validity')).toHaveText('Valid')
  expect(await values(page, 'desk', 'metric')).toEqual({ x: '0', y: '0', w: '6', h: '2' })
  expect(await values(page, 'desk', 'chart')).toEqual({ x: '0', y: '2', w: '6', h: '2' })

  // The reflow grid: 720 px for 12 columns is 60 px a cell, 36 px a row. Two cells right, then pulled two cells wider by the east edge.
  const desk = page.getByTestId('ed-gridtile-desk-metric')
  await drag(page, desk, 125, 4)
  expect(await values(page, 'desk', 'metric')).toEqual({ x: '2', y: '0', w: '6', h: '2' })
  await drag(page, page.getByTestId('ed-gridhandle-desk-metric-e'), 119, 0)
  expect(await values(page, 'desk', 'metric')).toEqual({ x: '2', y: '0', w: '8', h: '2' })
  // pulled down into the chart: refused, said with the neighbour's name, nothing changes; and a drag out of the grid stops at its edge
  await drag(page, page.getByTestId('ed-gridhandle-desk-metric-s'), 0, 40)
  await expect(input(page, 'ed-status')).toContainText('overlap `chart`')
  expect(await values(page, 'desk', 'metric')).toEqual({ x: '2', y: '0', w: '8', h: '2' })
  await drag(page, desk, 900, 0)
  expect(await values(page, 'desk', 'metric')).toEqual({ x: '4', y: '0', w: '8', h: '2' })
  await expect(input(page, 'ed-validity')).toHaveText('Valid')
  // the whole drag is one undo step
  await input(page, 'ed-undo').click()
  expect(await values(page, 'desk', 'metric')).toEqual({ x: '2', y: '0', w: '8', h: '2' })

  // The layout list: a copy is named, moved up and taken out again; the default stays one.
  await page.getByTestId('ed-layout-duplicate-desk').click()
  await input(page, 'ed-layout-rename-desk-copy').fill('spare')
  await page.getByTestId('ed-layout-rename-go-desk-copy').click()
  await expect(page.getByTestId('ed-layout-spare')).toBeVisible()
  await page.getByTestId('ed-layout-up-spare').click()
  await expect(page.getByTestId('ed-layout-up-spare')).toBeDisabled()
  await page.getByTestId('ed-layout-delete-spare').click()
  await expect(page.getByTestId('ed-layout-spare')).toHaveCount(0)
  await expect(page.getByTestId('ed-layout-default-desk')).toBeChecked()

  // A phone canvas: a selector, tiles placed and snapped, a tile too small and one overlapping.
  await input(page, 'ed-layout-new-name').fill('phone')
  await page.getByTestId('ed-layout-add-canvas').click()
  await expect(page.getByTestId('ed-layout-phone')).toBeVisible()
  for (const id of ['metric', 'chart']) await page.getByTestId(`ed-place-phone-${id}`).click()
  await input(page, 'ed-selector-widthMax-phone').fill('600')
  await input(page, 'ed-selector-orientation-phone').selectOption('portrait')
  await expect(input(page, 'ed-validity')).toHaveText('Valid')
  expect(await values(page, 'phone', 'metric')).toEqual({ x: '10', y: '10', w: '300', h: '200' })
  expect(await values(page, 'phone', 'chart')).toEqual({ x: '10', y: '220', w: '300', h: '200' })
  // 720 px for 390 units: 1.846 px a unit; 92 px is 49.8 units, snapped from 10 to 60
  await drag(page, page.getByTestId('ed-gridtile-phone-metric'), 92, 0)
  expect(await values(page, 'phone', 'metric')).toEqual({ x: '60', y: '10', w: '300', h: '200' })
  await drag(page, page.getByTestId('ed-gridtile-phone-metric'), -200, 0)
  expect(await values(page, 'phone', 'metric')).toEqual({ x: '0', y: '10', w: '300', h: '200' })
  // the chart pulled up by its north edge: 170 units tall, below the 180 a chart needs: outlined, and the loader says so
  await drag(page, page.getByTestId('ed-gridhandle-phone-chart-s'), 0, -50)
  expect(await values(page, 'phone', 'chart')).toEqual({ x: '10', y: '220', w: '300', h: '170' })
  await expect(page.getByTestId('ed-gridtile-phone-chart')).toHaveAttribute('data-below-minimum', 'true')
  await expect(page.getByTestId('ed-tile-problems-phone-chart')).toContainText('needs at least 280 by 180')
  await input(page, 'ed-save').click()
  await expect(input(page, 'ed-status')).toContainText('is not saved')
  await input(page, 'ed-undo').click()
  await expect(page.getByTestId('ed-gridtile-phone-chart')).toHaveAttribute('data-below-minimum', 'false')
  // the metric dragged onto the chart overlaps it on the layer: both outlined, and the loader refuses it
  await drag(page, page.getByTestId('ed-gridtile-phone-metric'), 0, 300)
  await expect(page.getByTestId('ed-gridtile-phone-metric')).toHaveAttribute('data-overlap', 'true')
  await expect(page.getByTestId('ed-gridtile-phone-chart')).toHaveAttribute('data-overlap', 'true')
  await expect(input(page, 'ed-validity')).not.toHaveText('Valid')
  await input(page, 'ed-undo').click()
  await expect(page.getByTestId('ed-gridtile-phone-metric')).toHaveAttribute('data-overlap', 'false')
  // a layer of its own takes the overlap away
  await drag(page, page.getByTestId('ed-gridtile-phone-metric'), 0, 300)
  await input(page, tile('phone', 'metric', 'layer')).fill('1')
  await expect(input(page, 'ed-validity')).toHaveText('Valid')
  await input(page, 'ed-undo').click()
  await input(page, 'ed-undo').click()
  expect(await values(page, 'phone', 'metric')).toEqual({ x: '0', y: '10', w: '300', h: '200' })
  await expect(input(page, 'ed-validity')).toHaveText('Valid')

  // "Test at W x H": the phone at 390 x 844, the default desk at a laptop's size, the phone not in landscape.
  await expect(input(page, 'ed-test-result')).toHaveAttribute('data-layout', 'phone')
  await input(page, 'ed-test-width').fill('1366')
  await input(page, 'ed-test-height').fill('768')
  await expect(input(page, 'ed-test-result')).toHaveAttribute('data-layout', 'desk')
  await input(page, 'ed-test-width').fill('560')
  await input(page, 'ed-test-height').fill('400')
  await expect(input(page, 'ed-test-result')).toHaveAttribute('data-layout', 'desk')
  await input(page, 'ed-test-width').fill('390')
  await input(page, 'ed-test-height').fill('844')
  await expect(input(page, 'ed-test-result')).toHaveAttribute('data-layout', 'phone')
  // half a number is kept out of the document and said
  await input(page, 'ed-selector-widthMin-phone').fill('12x')
  await expect(input(page, 'ed-selector-bad-phone')).toBeVisible()
  await input(page, 'ed-selector-widthMin-phone').fill('')

  // Parameter, refresh, widget properties.
  await input(page, 'ed-refresh').fill('5m')
  await input(page, 'ed-parameter-name').fill('region')
  await page.getByTestId('ed-parameter-add').click()
  await input(page, 'ed-parameter-default-region').fill('West')
  await input(page, 'ed-prop-label-metric').fill('Net sales')
  await expect(input(page, 'ed-validity')).toHaveText('Valid')

  await input(page, 'ed-save').click()
  await expect(page).toHaveURL(/\/views\/dashboards\/layout-board\?edit=1/)
  await expect(input(page, 'ed-dirty')).toHaveText('Saved')
  expect(await storedText(page, 'dashboard/layout-board')).toBe(golden)
}
