import { expect, type BrowserContext, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
/** The file the story must save: what the editor writes for the steps below (canonical key order, short leaves on one line). */
const golden = fs.readFileSync(path.resolve(here, 'views-editor.golden.yaml'), 'utf8').replace(/\r\n/g, '\n')

const STORAGE_KEY = 'dbdatakit.views.documents'
const EDIT = '/views/dashboards/editor-board?edit=1'

/** The text the fixture source holds for a document, read from the browser's storage (what a reload opens). */
async function storedText(page: Page, key: string): Promise<string | null> {
  return page.evaluate(([storageKey, k]) => {
    const raw = window.localStorage.getItem(storageKey!)
    return raw ? ((JSON.parse(raw) as Record<string, string | null>)[k!] ?? null) : null
  }, [STORAGE_KEY, key] as const)
}

const tile = (layout: string, widget: string, field: 'x' | 'y' | 'w' | 'h') => `ed-tile-${layout}-${widget}-${field}`

/**
 * The editor of the Views page (plan 32 DBR24, M2 steps 1, 3, 6 to 10): a new dashboard from the Dashboards tab starts with an empty `desk` layout and says it lacks a widget; widgets from the palette and their tiles make it
 * valid; a tile moved outside the grid is marked at the tile and Save is refused with the validator's text; undo repairs it; Save, reload, and the file is the golden; a second page saves the same file and the first page's
 * save is a version conflict that offers a reload; the YAML view's change is followed by the form; duplicate and delete. The browser contacts no other host.
 * Dragging on the grid and the field wells are later slices (DBR25, DBR26); coordinates here are typed.
 */
export async function editorStory(page: Page, context: BrowserContext) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  const watch = (p: Page) => p.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  watch(page)
  await page.setViewportSize({ width: 1440, height: 900 })

  // 1. New dashboard: an empty desk layout, and what it lacks.
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await page.getByTestId('views-new').click()
  await expect(page.getByTestId('ed-root')).toBeVisible()
  await expect(page.getByTestId('ed-layout-desk')).toBeVisible()
  await expect(page.getByTestId('ed-validity')).toHaveAttribute('data-errors', '2')
  await expect(page.getByTestId('ed-problems')).toContainText('defines at least one widget')
  await expect(page.getByTestId('ed-save')).toBeEnabled()
  await page.getByTestId('ed-file-name').fill('editor-board')
  await page.getByTestId('ed-title').fill('Editor board')

  // Save is refused while it is invalid, with the validator's text; nothing is stored.
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-status')).toContainText('is not saved')
  expect(await storedText(page, 'dashboard/editor-board')).toBeNull()

  // 2./3. Widgets from the palette, placed on the desk layout: the problems go.
  for (const type of ['metric', 'chart', 'table', 'text']) await page.getByTestId(`ed-add-${type}`).click()
  await expect(page.getByTestId('ed-widget-metric')).toBeVisible()
  await expect(page.getByTestId('ed-widget-chart')).toBeVisible()
  for (const id of ['metric', 'chart', 'table', 'text']) await page.getByTestId(`ed-place-desk-${id}`).click()
  await expect(page.getByTestId('ed-validity')).toHaveAttribute('data-errors', '0')
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')

  // 4. Typed coordinates: the metric is made narrower and the text note a thin strip.
  await page.getByTestId(tile('desk', 'metric', 'w')).fill('4')
  await page.getByTestId(tile('desk', 'chart', 'w')).fill('8')
  await page.getByTestId(tile('desk', 'chart', 'x')).fill('4')
  await page.getByTestId(tile('desk', 'chart', 'y')).fill('0')
  await page.getByTestId(tile('desk', 'chart', 'h')).fill('4')
  await page.getByTestId(tile('desk', 'metric', 'h')).fill('4')
  await page.getByTestId(tile('desk', 'table', 'y')).fill('4')
  await page.getByTestId(tile('desk', 'table', 'w')).fill('12')
  await page.getByTestId(tile('desk', 'text', 'y')).fill('6')
  await page.getByTestId(tile('desk', 'text', 'w')).fill('12')
  await page.getByTestId(tile('desk', 'text', 'h')).fill('1')
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')

  // 5. A second layout (a copy of desk): a tile outside its grid is marked at the tile; Save is refused with the loader's text; undo repairs it.
  await page.getByTestId('ed-layout-duplicate-desk').click()
  await expect(page.getByTestId('ed-layout-desk-copy')).toBeVisible()
  await page.getByTestId(tile('desk-copy', 'metric', 'x')).fill('11')
  await expect(page.getByTestId('ed-tile-problems-desk-copy-metric')).toBeVisible()
  await expect(page.getByTestId('ed-validity')).not.toHaveText('Valid')
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-status')).toContainText('is not saved')
  await page.getByTestId('ed-undo').click()
  await expect(page.getByTestId('ed-tile-problems-desk-copy-metric')).toHaveCount(0)
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')
  // the copy is taken out again, so the golden is the board of the steps above
  await page.getByTestId('ed-layout-delete-desk-copy').click()
  await expect(page.getByTestId('ed-layout-desk-copy')).toHaveCount(0)

  // 6./7. Save; reload; the file is the same.
  await page.getByTestId('ed-save').click()
  // the first save names the file: the editor reopens it at its own address (a reload would too)
  await expect(page).toHaveURL(/\/views\/dashboards\/editor-board\?edit=1/)
  await expect(page.getByTestId('ed-name')).toHaveText('editor-board.yaml')
  await expect(page.getByTestId('ed-dirty')).toHaveText('Saved')
  expect(await storedText(page, 'dashboard/editor-board')).toBe(golden)
  await page.goto(EDIT)
  await expect(page.getByTestId('ed-title')).toHaveValue('Editor board')
  await expect(page.getByTestId('ed-widget-chart')).toBeVisible()
  await expect(page.getByTestId('ed-dirty')).toHaveText('Saved')
  await expect(page.getByTestId(tile('desk', 'chart', 'x'))).toHaveValue('4')

  // 8. The same file in a second page, saved first: this page's save is a version conflict, which offers a reload.
  const other = await context.newPage()
  watch(other)
  await other.goto(EDIT)
  await other.getByTestId('ed-title').fill('Editor board, other tab')
  await other.getByTestId('ed-save').click()
  await expect(other.getByTestId('ed-status')).toContainText('Saved editor-board')
  await page.getByTestId('ed-title').fill('Editor board, this tab')
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-conflict')).toContainText('changed elsewhere')
  await expect(page.getByTestId('ed-conflict')).toContainText('stored version')
  await expect(page.getByTestId('ed-dirty')).toHaveText('Unsaved changes')
  await page.getByTestId('ed-conflict-reload').click()
  await expect(page.getByTestId('ed-conflict')).toHaveCount(0)
  await expect(page.getByTestId('ed-title')).toHaveValue('Editor board, other tab')
  await other.close()

  // 10. The YAML view is the document an agent would write: change the title there and the form follows (the YAML view's editor is Monaco: keys, no pasting).
  await page.getByTestId('ed-tab-yaml').click()
  const yaml = page.getByTestId('ed-yaml')
  await expect(yaml).toBeVisible()
  // wait for the editor to hold the text, click the title line (the third of the canonical order), select it and type
  await expect(yaml.locator('.view-line').first()).toContainText('schemaVersion')
  await yaml.locator('.view-line').nth(2).click()
  await page.keyboard.press('End')
  await page.keyboard.press('Shift+Home')
  await page.keyboard.type('title: Typed in YAML')
  await page.getByTestId('ed-tab-form').click()
  await expect(page.getByTestId('ed-title')).toHaveValue('Typed in YAML')
  await expect(page.getByTestId('ed-dirty')).toHaveText('Unsaved changes')
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-status')).toContainText('Saved editor-board')
  expect(await storedText(page, 'dashboard/editor-board')).toBe(golden.replace('title: Editor board', 'title: Typed in YAML'))

  // 9. Duplicate, then delete the copy.
  await page.getByTestId('ed-duplicate-name').fill('editor-board-copy')
  await page.getByTestId('ed-duplicate').click()
  await expect(page).toHaveURL(/\/views\/dashboards\/editor-board-copy\?edit=1/)
  await expect(page.getByTestId('ed-name')).toHaveText('editor-board-copy.yaml')
  await expect(page.getByTestId('ed-title')).toHaveValue('Typed in YAML')
  await page.getByTestId('ed-delete').click()
  await page.getByTestId('ed-delete-confirm').click()
  await expect(page.getByTestId('views-list')).toBeVisible()
  await expect(page.getByTestId('views-row-editor-board')).toBeVisible()
  await expect(page.getByTestId('views-row-editor-board-copy')).toHaveCount(0)
  expect(await storedText(page, 'dashboard/editor-board-copy')).toBeNull()

  // The saved board opens in the viewer, drawn.
  await page.getByTestId('views-open-editor-board').click()
  await expect(page.getByTestId('views-title')).toHaveText('Typed in YAML')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'desk')

  expect(outside, 'the browser contacted no other host').toEqual([])
}
