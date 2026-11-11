import { expect, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const retail = path.resolve(here, '../../../../DbDataBuild/templates/retail')
const sampleReport = fs.readFileSync(path.join(retail, 'reports/retail-sales-monthly.yaml'), 'utf8')

const REPORT = '/views/reports/retail-sales-monthly'

/** Overflow of the document (a report scrolls inside its own frame, never the page). */
const documentOverflow = (page: Page) => page.evaluate(() => ({ x: document.documentElement.scrollWidth - document.documentElement.clientWidth, y: document.documentElement.scrollHeight - document.documentElement.clientHeight }))

/** Where each guide is, in pixels from the top of the sheet's content, in order. */
const guides = (page: Page) => page.getByTestId('vz-flow-guide').evaluateAll((all) => all.map((g) => Number((g as HTMLElement).dataset.y)))

/** The number of pages of a PDF: the page objects (`/Type /Page`, not `/Pages`). */
const pagesOf = (pdf: Buffer) => (pdf.toString('latin1').match(/\/Type\s*\/Page[^s]/g) ?? []).length

/**
 * The report on screen (plan 32 DBR14, M1 and M3): the sample report scrolls in a sheet the width of its page with dashed page-break guides, the table keeps one head and one total, the print stylesheet
 * is the paged-media rules, the same dashboard shows as a one-page dashboard and, with `?layout=print`, as a paginated report, and a block wider than the page is reported. Called from the dashboards
 * story (one signed-in page, no server but the console). No pixel comparison: it asserts structure, order and counts; the PDF is the browser's own (`page.pdf()`), in this test only.
 */
export async function reportsStory(page: Page) {
  await page.setViewportSize({ width: 1280, height: 800 })
  await page.goto('/views/reports')
  await expect(page.getByTestId('views-row-retail-sales-monthly')).toContainText('Retail sales by month')
  await page.getByTestId('views-open-retail-sales-monthly').click()
  await expect(page).toHaveURL(new RegExp(`${REPORT}$`))

  // The report is drawn: its flow layout, in the report's order, from the recorded results.
  const flow = page.getByTestId('vz-flow')
  await expect(flow).toHaveAttribute('data-layout', 'paged')
  await expect(flow).toHaveAttribute('data-layout-type', 'flow')
  await expect(page.getByTestId('views-layout-current')).toHaveAttribute('data-layout', 'paged')
  await expect(page.locator('.vz-flow-title')).toHaveText(['Summary', 'Trend', 'Products'])
  await expect(page.getByTestId('vz-flow-header')).toContainText('Retail sales by month')
  await expect(page.getByTestId('vz-flow-footer')).toHaveText(/^Page 2 of 2$/)
  for (const id of ['net', 'margin', 'units']) await expect(page.getByTestId(`vz-flow-cell-${id}`)).toBeVisible()
  await expect(page.getByTestId('vz-chart-by_month').locator('svg')).toBeVisible()
  await expect(page.getByTestId('vz-loading')).toHaveCount(0)
  await expect(page.getByTestId('vz-widget-error')).toHaveCount(0)

  // The table: one head and the 24 rows the resolver gave (the sample report asks for no total row; a total as the last row, once, is the unit test's of `PagedFrame`), never a footer group.
  const table = page.getByTestId('vz-table-products')
  await expect(table.locator('thead')).toHaveCount(1)
  await expect(table.locator('tfoot')).toHaveCount(0)
  await expect(table.locator('tbody tr')).toHaveCount(24)
  await expect(table.locator('tr.vz-total')).toHaveCount(0)

  // Page-break guides: the content is about 1,500 pixels and a page holds 971 (A4, 20 mm margins), so there are two pages and one guide, where the second starts: inside the table, between two of its rows, at most a page down.
  await expect(flow).toHaveAttribute('data-pages', '2')
  await expect(page.getByTestId('vz-flow-guide')).toHaveCount(1)
  const ys = await guides(page)
  expect(ys[0]).toBeLessThanOrEqual(972)
  expect(ys[0]).toBeGreaterThan(800)
  const productsTop = await page.getByTestId('vz-flow-block-products').evaluate((e) => (e.getBoundingClientRect().top - (e.closest('[data-testid="vz-flow-content"]') as HTMLElement).getBoundingClientRect().top))
  expect(ys[0]).toBeGreaterThan(productsTop)
  await expect(page.getByTestId('vz-flow-note')).toContainText('the PDF is exact')
  expect(await documentOverflow(page)).toEqual({ x: 0, y: 0 })
  const scroll = page.getByTestId('vz-flow-scroll')
  expect(await scroll.evaluate((e) => e.scrollHeight > e.clientHeight * 2)).toBe(true)
  await scroll.evaluate((e) => { e.scrollTop = e.scrollHeight })
  expect(await scroll.evaluate((e) => e.scrollTop)).toBeGreaterThan(0)
  await page.getByTestId('vz-flow-guide').last().scrollIntoViewIfNeeded()
  const inside = await page.evaluate(() => {
    const g = document.querySelector('[data-testid="vz-flow-guide"]')!.getBoundingClientRect()
    const f = document.querySelector('[data-testid="vz-flow-scroll"]')!.getBoundingClientRect()
    return g.top >= f.top && g.top <= f.bottom
  })
  expect(inside).toBe(true)
  expect(await documentOverflow(page)).toEqual({ x: 0, y: 0 })
  await expect(page.getByTestId('vz-flow-overwidth')).toHaveCount(0)

  // Print preview is the exporter's (DBR15): disabled, with the reason, until there is one. The browser's own Print is there.
  await expect(page.getByTestId('vz-flow-preview')).toBeDisabled()
  await expect(page.getByTestId('vz-flow-preview')).toHaveAttribute('title', /exporter/)
  await expect(page.getByTestId('vz-flow-print')).toBeEnabled()

  // The print stylesheet: the paged-media rules of section 4.1 hold in print, and the browser's own PDF of this HTML has the page the report asks for.
  await page.emulateMedia({ media: 'print' })
  expect(await page.evaluate(() => {
    const style = (selector: string) => getComputedStyle(document.querySelector(selector)!)
    return {
      head: style('[data-testid="vz-table-products"] thead').display,
      foot: document.querySelectorAll('[data-testid="vz-table-products"] tfoot').length,
      row: style('[data-testid="vz-table-products"] tbody tr').breakInside,
      chart: style('[data-testid="vz-flow-block-by_month"]').breakInside,
      title: style('.vz-flow-title').breakAfter,
      guide: style('[data-testid="vz-flow-guide"]').display,
      bar: style('[data-testid="vz-flow-bar"], .vz-flow-bar').display,
      sheetPadding: style('[data-testid="vz-flow-sheet"]').paddingTop,
    }
  })).toMatchObject({ head: 'table-header-group', foot: 0, row: 'avoid', chart: 'avoid', title: 'avoid', guide: 'none', bar: 'none', sheetPadding: '0px' })
  const pdf = await page.pdf({ preferCSSPageSize: true, printBackground: true })
  expect(pagesOf(pdf)).toBeGreaterThanOrEqual(2)
  expect(pagesOf(pdf)).toBeLessThanOrEqual(3)
  const media = /\/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]/.exec(pdf.toString('latin1'))
  expect([Number(media![1]), Number(media![2])].map(Math.round)).toEqual([595, 842]) // A4 in points
  // The repeated head on the second page of the long table, when a PDF reader is on the machine (poppler's `pdftotext`; CI's Linux job has it, Windows may not).
  const text = pdfText(pdf)
  if (text !== null) {
    const pages = text.split('\f').filter((p) => p.trim() !== '')
    expect(pages.length).toBe(pagesOf(pdf))
    const heads = pages.map((p) => (p.match(/Category\s+Product\s+Units\s+Net sales/g) ?? []).length)
    expect(heads).toEqual([1, 1]) // the table's head is on both pages: repeated on the second
    expect(heads.every((n) => n <= 1)).toBe(true)
    expect(text.match(/\bTotal\b/g)?.length ?? 0).toBe(0) // the sample report has no total row
    expect(pages[0]).toContain('Net sales by month') // the chart is not split off its title
  }
  await page.emulateMedia({ media: 'screen' })

  // The same dashboard shows as a one-page dashboard and, with ?layout=print, as a paginated report (the sample's print layout is a flow layout for the print medium).
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.goto('/views/dashboards/retail-sales-today')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'desk')
  await expect(page.getByTestId('vz-flow')).toHaveCount(0)
  await page.goto('/views/dashboards/retail-sales-today?layout=print')
  await expect(page.getByTestId('vz-flow')).toHaveAttribute('data-layout', 'print')
  await expect(page.getByTestId('vz-dashboard')).toHaveCount(0)
  await expect(page.getByTestId('views-layout-select')).toHaveValue('print')
  await expect(page.getByTestId('vz-flow-note')).toContainText('A4 landscape')
  await expect(page.getByTestId('vz-table-top_products').locator('tbody tr')).toHaveCount(24)
  expect((await guides(page)).length).toBeGreaterThanOrEqual(1)
  expect(await documentOverflow(page)).toEqual({ x: 0, y: 0 })
  await page.getByTestId('views-layout-select').selectOption('')
  await expect(page.getByTestId('vz-dashboard')).toHaveAttribute('data-layout', 'desk')

  // A block wider than the page is reported after layout: the same report with its three metrics squeezed into one column each of the twelve (a file of the same name keeps the recorded results).
  const row = '- {widget: intro}\n          - row: [{widget: net, span: 4}, {widget: margin, span: 4}, {widget: units, span: 4}]'
  expect(sampleReport).toContain(row)
  const wide = sampleReport.replace(row, '- row: [{widget: net, span: 1}, {widget: margin, span: 1}, {widget: units, span: 1}, {widget: intro, span: 9}]')
  await page.goto('/views/reports')
  await page.getByTestId('views-open-file').setInputFiles({ name: 'retail-sales-monthly.yaml', mimeType: 'application/yaml', buffer: Buffer.from(wide) })
  await expect(page).toHaveURL(new RegExp(`${REPORT}$`))
  await expect(page.getByTestId('vz-flow')).toBeVisible()
  await expect(page.getByTestId('vz-flow-overwidth')).toContainText('Wider than the page')
  const over = ((await page.getByTestId('vz-flow-overwidth').getAttribute('data-widgets')) ?? '').split(' ')
  expect(over).toEqual(expect.arrayContaining(['net']))
  expect(over).not.toContain('products')
}

/** The text of a PDF by poppler's `pdftotext`, pages separated by a form feed; null when it is not installed. */
function pdfText(pdf: Buffer): string | null {
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'dbdatakit-report-')), 'report.pdf')
  try {
    fs.writeFileSync(file, pdf)
    return execFileSync('pdftotext', ['-layout', file, '-'], { encoding: 'utf8' })
  } catch {
    return null
  } finally {
    fs.rmSync(path.dirname(file), { recursive: true, force: true })
  }
}
