import { expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
// What the semantic library's renderer gives for its view fixtures (DbDataKit.Semantic.Tests, conformance/view-rendered): real rendered views, the input of the shared visuals.
const rendered = (name: string) => fs.readFileSync(path.resolve(here, '../../../../DbDataKit/tests/DbDataKit.Semantic.Tests/conformance/view-rendered', `${name}.json`), 'utf8')

/**
 * The views page (plan 19, Y03), part of the signed-in story: a fixture's matrix and a fixture's chart are opened from pasted documents and drawn; the colours switch to dark;
 * a document that would load data from a URL is refused and the browser asks nobody for it. Run by the first-run story, which holds the session.
 */
export async function viewsStory(page: Page) {
  // Anything the page asks of another host is a failure of the point of the page: a view loads nothing.
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => {
    if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url())
  })

  await page.getByTestId('rail-views').click()
  await expect(page.getByTestId('views-page')).toBeVisible()

  // A matrix whose totals are the result's own: the grand total reads as the server formatted it.
  await page.getByTestId('views-input').fill(rendered('02_totals_come_from_the_result'))
  await page.getByTestId('views-show').click()
  const grid = page.getByTestId('vz-matrix-m1')
  await expect(grid).toBeVisible()
  await expect(grid.getByRole('columnheader', { name: 'FY27-Q1' })).toBeVisible()
  await expect(grid.getByRole('row').filter({ hasText: 'Total' }).last()).toContainText('22.9M')

  // A chart, drawn by Vega-Lite into an SVG: three regions, three lines; its data is under it as a table.
  await page.getByTestId('views-input').fill(rendered('17_line_chart'))
  await page.getByTestId('views-show').click()
  const chart = page.getByTestId('vz-chart-c1')
  await expect(chart.locator('svg .mark-line')).toHaveCount(3)
  await page.getByText('Data (6 rows)').click()
  await expect(page.getByTestId('vz-table-c1').getByRole('row')).toHaveCount(7)

  // Dark: the page's views take the dark theme and the chart is drawn again in its colours.
  const before = await chart.locator('svg .mark-line path').first().getAttribute('stroke')
  await page.getByTestId('views-theme').selectOption('dark')
  await expect(page.getByTestId('vz-views')).toHaveAttribute('data-vz-theme', 'dark')
  await expect(chart.locator('svg .mark-line path').first()).not.toHaveAttribute('stroke', before!)

  // A chart that names a URL is refused and nothing is drawn; a view document is explained; text that is not JSON is said not to be.
  const unsafe = JSON.parse(rendered('17_line_chart'))
  unsafe.views[0].vega_lite.data = { url: 'https://example.com/data.csv' }
  await page.getByTestId('views-input').fill(JSON.stringify(unsafe))
  await page.getByTestId('views-show').click()
  await expect(page.getByTestId('vz-problems')).toContainText('This is not a view that can be shown.')
  await expect(page.getByTestId('vz-chart-c1')).toHaveCount(0)

  await page.getByTestId('views-input').fill(JSON.stringify({ spec_version: '0.3', kind: 'view', result: { ref: 'result.x' }, views: [{ type: 'matrix', id: 'm1', values: [{ field: 'orders.net_revenue' }] }] }))
  await page.getByTestId('views-show').click()
  await expect(page.getByTestId('views-message')).toContainText('it needs that result')

  await page.getByTestId('views-input').fill('not json')
  await page.getByTestId('views-show').click()
  await expect(page.getByTestId('views-message')).toContainText('not JSON')

  expect(outside).toEqual([])
}
