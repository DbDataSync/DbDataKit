import { expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
/** The file the story must save: canonical order, one parameter, report options, a flow layout with page setup, running text, a cover, two sections (a row of spans, a break, a table that repeats its head). */
const golden = fs.readFileSync(path.resolve(here, 'views-editor-report.golden.yaml'), 'utf8').replace(/\r\n/g, '\n')
const STORAGE_KEY = 'dbdatakit.views.documents'
const L = 'paged'

async function storedText(page: Page, key: string): Promise<string | null> {
  return page.evaluate(([storageKey, k]) => {
    const raw = window.localStorage.getItem(storageKey!)
    return raw ? ((JSON.parse(raw) as Record<string, string | null>)[k!] ?? null) : null
  }, [STORAGE_KEY, key] as const)
}

const t = (rest: string) => `ed-flow-${L}-${rest}`
const pagesOf = async (page: Page) => Number(await page.getByTestId('vz-flow').getAttribute('data-pages'))
const guidesOf = (page: Page) => page.getByTestId('vz-flow-guide').count()

/**
 * The report editor of the Views page (plan 32 DBR27, M2 steps 1 to 3 for a report): a new report starts with one flow layout and an empty section; widgets are added and placed as blocks (a text, a row of two metrics
 * with spans 6 and 6, a table in a second section that breaks before and repeats its head); the page is set (Letter, landscape, a margin), the header and footer are written with tokens and an unknown token is refused at its
 * field and fixed; strict and a page limit are set; the paged preview follows the draft (its widgets, its page size: a taller page holds fewer guides) before anything is saved; the file saved is the golden, and the
 * sample report opens in the editor with its preview drawn from the recorded results.
 */
export async function editorReportStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1400 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-reports').click()
  await page.getByTestId('views-new').click()
  // a new document starts with one text widget (`note`) so that it is valid as it stands; the story builds on an empty one
  await page.getByTestId('ed-widget-remove-note').click()
  await page.getByTestId('ed-file-name').fill('quarterly')
  await page.getByTestId('ed-title').fill('Quarterly report')

  // A new report: one flow layout (A4 portrait, 15 mm margins), one empty section, which the loader does not accept yet; the form shows the layout's editor, not a note about YAML.
  await expect(page.getByTestId(t('editor'))).toBeVisible()
  await expect(page.getByTestId(t('section-0'))).toBeVisible()
  await expect(page.getByTestId('ed-validity')).not.toHaveText('Valid')
  await expect(page.getByTestId(t('printable'))).toContainText('210 x 297 mm portrait')
  await expect(page.getByTestId('ed-report-settings')).toBeVisible()
  await expect(page.getByTestId('ed-refresh')).toHaveCount(0)

  // A parameter, then widgets: a note, two metrics and a table.
  await page.getByTestId('ed-parameter-name').fill('quarter')
  await page.getByTestId('ed-parameter-add').click()
  await page.getByTestId('ed-parameter-default-quarter').fill('FY27-Q1')
  for (const type of ['text', 'metric', 'metric', 'table']) await page.getByTestId(`ed-add-${type}`).click()
  await expect(page.getByTestId('ed-widget-metric2')).toBeVisible()

  // Section 1 (Summary): the note as a block, then a row of the two metrics with spans 6 and 6.
  await page.getByTestId(t('section-0-pick')).selectOption('text')
  await page.getByTestId(t('section-0-add-widget')).click()
  await page.getByTestId(t('section-0-pick')).selectOption('metric')
  await page.getByTestId(t('section-0-add-row')).click()
  await page.getByTestId(t('cell-0-1-add')).click()
  await expect(page.getByTestId(t('cell-0-1-1-widget'))).toHaveValue('metric2')
  await page.getByTestId(t('cell-0-1-0-span')).fill('6')
  await page.getByTestId(t('cell-0-1-1-span')).fill('6')
  await expect(page.getByTestId(t('block-0-1'))).toContainText('Row of 2 (6 + 6 of 12)')
  // spans past twelve: the loader's sentence at the row, and the file is not valid
  await page.getByTestId(t('cell-0-1-0-span')).fill('9')
  await expect(page.getByTestId(t('block-0-1-problems'))).toContainText('add up to 15')
  await page.getByTestId(t('cell-0-1-0-span')).fill('6')
  await expect(page.getByTestId(t('block-0-1-problems'))).toHaveCount(0)

  // Section 2 (Detail): a title, a break before, the table with a repeated head; the title of section 1 is renamed.
  await page.getByTestId(t('section-add')).click()
  await page.getByTestId(t('section-0-title')).fill('Summary')
  await page.getByTestId(t('section-1-title')).fill('Detail')
  await page.getByTestId(t('section-1-break')).selectOption('before')
  await page.getByTestId(t('section-1-pick')).selectOption('table')
  await page.getByTestId(t('section-1-add-widget')).click()
  await page.getByTestId(t('block-1-0-repeat')).selectOption('true')
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')

  // Header and footer with tokens; an unknown one is refused at its field, by the loader, and fixed.
  await page.getByTestId(t('header-left')).fill('{title}')
  await page.getByTestId(t('header-center')).fill('Run {nope}')
  await expect(page.getByTestId(t('header-center-problems'))).toContainText('{nope}')
  await expect(page.getByTestId('ed-validity')).not.toHaveText('Valid')
  await page.getByTestId(t('header-center')).fill('')
  await expect(page.getByTestId(t('header-center-problems'))).toHaveCount(0)
  await page.getByTestId(t('header-right')).fill('{params.quarter}')
  await page.getByTestId(t('footer-center')).fill('Page {page} of {pages}')
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')
  await expect(page.getByTestId(t('tokens'))).toContainText('{params.quarter}')

  // Page setup: Letter, landscape, a wider left margin; the printable area is said.
  await page.getByTestId(t('size')).selectOption('Letter')
  await page.getByTestId(t('orientation')).selectOption('landscape')
  await page.getByTestId(t('margin-left')).fill('20')
  await expect(page.getByTestId(t('printable'))).toContainText('279.4 x 215.9 mm landscape')

  // Options and the cover.
  await page.getByTestId('ed-report-strict').check()
  await page.getByTestId('ed-report-limit-pages').fill('50')
  await page.getByTestId('ed-report-cover').check()
  await page.getByTestId(t('cover')).check()

  // The preview is of the draft, unsaved: the running text with its tokens, the section titles, the two metrics in their cells, the table, a cover; the widgets are the draft's (no recorded result: each says so).
  const flow = page.getByTestId('vz-flow')
  await expect(page.getByTestId(t('preview'))).toHaveAttribute('data-ready', 'true')
  await expect(flow).toHaveAttribute('data-layout', L)
  await expect(page.locator('.vz-flow-title')).toHaveText(['Summary', 'Detail'])
  await expect(page.getByTestId('vz-flow-header')).toContainText('Quarterly report')
  await expect(page.getByTestId('vz-flow-header')).toContainText('FY27-Q1')
  await expect(page.getByTestId('vz-flow-cover')).toBeVisible()
  await expect(page.getByTestId('vz-flow-cell-metric')).toBeVisible()
  await expect(page.getByTestId('vz-flow-cell-metric2')).toBeVisible()

  // The page size drives the guides: with blocks of a fixed height the same content is on fewer pages of a taller sheet.
  await page.getByTestId(t('block-0-0-height')).fill('120')
  await page.getByTestId(t('block-1-0-height')).fill('300')
  await page.getByTestId(t('size')).selectOption('A5')
  await page.getByTestId(t('orientation')).selectOption('portrait')
  await expect(page.getByTestId(t('printable'))).toContainText('148 x 210 mm portrait')
  await expect(page.getByTestId(t('preview'))).toHaveAttribute('data-ready', 'true')
  const small = await pagesOf(page)
  expect(small).toBeGreaterThan(2)
  await expect.poll(() => guidesOf(page)).toBe(small - 1)
  await page.getByTestId(t('size')).selectOption('A3')
  await expect(page.getByTestId(t('printable'))).toContainText('297 x 420 mm portrait')
  await expect.poll(() => pagesOf(page)).toBeLessThan(small)
  const tall = await pagesOf(page)
  await expect.poll(() => guidesOf(page)).toBe(tall - 1)
  // take the heights and the size back to what the golden says
  await page.getByTestId(t('block-0-0-height')).fill('')
  await page.getByTestId(t('block-1-0-height')).fill('')
  await page.getByTestId(t('size')).selectOption('Letter')
  await page.getByTestId(t('orientation')).selectOption('landscape')
  await expect(page.getByTestId(t('printable'))).toContainText('279.4 x 215.9 mm landscape')
  // the browser's Print is there; the exact print preview waits for the exporter and says why
  await expect(page.getByTestId('vz-flow-print')).toBeEnabled()
  await expect(page.getByTestId('vz-flow-preview')).toBeDisabled()

  // Undo takes the last change back (the orientation), redo restores it.
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')
  await page.getByTestId('ed-undo').click()
  await expect(page.getByTestId(t('orientation'))).toHaveValue('portrait')
  await page.getByTestId('ed-redo').click()
  await expect(page.getByTestId(t('orientation'))).toHaveValue('landscape')

  // Save: the file is the golden, and it reopens in the editor with the same form.
  await page.getByTestId('ed-save').click()
  await expect(page).toHaveURL(/\/views\/reports\/quarterly\?edit=1/)
  await expect(page.getByTestId('ed-dirty')).toHaveText('Saved')
  expect(await storedText(page, 'report/quarterly')).toBe(golden)
  await page.reload()
  await expect(page.getByTestId(t('section-1-break'))).toHaveValue('before')
  await expect(page.getByTestId(t('header-right'))).toHaveValue('{params.quarter}')
  await expect(page.getByTestId('ed-report-strict')).toBeChecked()
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')

  // The sample report opens in the editor with its own forms, and the preview draws its recorded results (the chart and the 24-row table).
  await page.goto('/views/reports/retail-sales-monthly?edit=1')
  await expect(page.getByTestId(t('editor'))).toBeVisible()
  await expect(page.getByTestId(t('preview'))).toHaveAttribute('data-ready', 'true')
  await expect(page.getByTestId('vz-chart-by_month').locator('svg')).toBeVisible()
  await expect(page.getByTestId('vz-table-products').locator('tbody tr')).toHaveCount(24)
  await expect(page.locator('.vz-flow-title')).toHaveText(['Summary', 'Trend', 'Products'])
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')
  // a form edit keeps the file's own order and comments; the page setup form reads what the file says
  await expect(page.getByTestId(t('size'))).toHaveValue('A4')
}
