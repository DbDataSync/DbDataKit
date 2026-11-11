import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * The nested axis in the widget editor (plan 32 DBR42, the wells; DBR44 adds the level controls to this story): a chart's `x` well takes up to three levels, outermost first; the request it asks for lists the levels in
 * order with an `order` per level; a fourth level and a `column` beside a nested axis are refused with the loaders' words; a level moves up and down; every change is one undo step. Written without a browser to run it:
 * a verifier runs it once (the drawing of the group headers is checked here only as far as the preview's state).
 */
export async function editorAxisStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1500 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await by(page, 'views-new').click()
  await by(page, 'ed-file-name').fill('axis-board')
  await by(page, 'ed-title').fill('Axis board')
  await by(page, 'ed-add-chart').click()
  await expect(by(page, 'ed-widget-editor-chart')).toBeVisible()
  await expect(by(page, 'ed-well-chart-x-entry-0')).toContainText('customers.region')

  // 1. A second level: the well lists both, outermost first; the request asks for both attributes and an order per level.
  await by(page, 'ed-well-chart-x-type').fill('products.category')
  await by(page, 'ed-well-chart-x-type').press('Enter')
  await expect(by(page, 'ed-well-chart-x-entry-1')).toContainText('products.category')
  await expect(by(page, 'ed-derived-request-chart')).toContainText('"order"')
  await expect(by(page, 'ed-derived-sources-chart')).toContainText('widgets.chart.encodings.x[1]')
  await expect(by(page, 'ed-preview-chart')).toHaveAttribute('data-result', /chart|loading/)

  // 2. Reordering swaps the levels, and the request's order follows (the first attribute asked for is the outermost level).
  await by(page, 'ed-well-chart-x-up-1').click()
  await expect(by(page, 'ed-well-chart-x-entry-0')).toContainText('products.category')

  // 3. A third level is taken, a fourth is refused with the well's words and nothing changes.
  await by(page, 'ed-well-chart-x-type').fill('date.year_month')
  await by(page, 'ed-well-chart-x-type').press('Enter')
  await expect(by(page, 'ed-well-chart-x-entry-2')).toContainText('date.year_month')
  await by(page, 'ed-well-chart-x-type').fill('products.product')
  await by(page, 'ed-well-chart-x-type').press('Enter')
  await expect(by(page, 'ed-status')).toContainText('at most 3')
  await expect(by(page, 'ed-well-chart-x-entry-3')).toHaveCount(0)

  // 4. A column beside a nested axis is refused in the loader's words.
  await by(page, 'ed-well-chart-column-type').fill('customers.region')
  await by(page, 'ed-well-chart-column-type').press('Enter')
  await expect(by(page, 'ed-status')).toContainText('has no `column`')
  await expect(by(page, 'ed-well-chart-column-entry-0')).toHaveCount(0)

  // 5. Removing levels back to one gives the plain channel again; each step is one undo step.
  await by(page, 'ed-well-chart-x-remove-2').click()
  await by(page, 'ed-well-chart-x-remove-1').click()
  await expect(by(page, 'ed-well-chart-x-entry-1')).toHaveCount(0)
  await expect(by(page, 'ed-derived-request-chart')).not.toContainText('"order"')
  await by(page, 'ed-undo').click()
  await expect(by(page, 'ed-well-chart-x-entry-1')).toBeVisible()
}
