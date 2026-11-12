import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * The entry controls and the group editor of the widget editor (plan 32 DBR54, over DBR50 to DBR52): the same measure is put in a table twice under two aliases with a filter each (the request asks `{measure, as, filters}`
 * for both); a matrix gets an attribute in its cell area with an icon map and a link; a field group is declared in the document settings and named by a chart's x well (the request asks for its levels in order), and a group a
 * widget uses is not removed. That the saved file equals the goldens is the unit test `entryParts.test.ts`; this story checks the screen. Written without a browser to run it: a verifier runs it once.
 */
export async function editorEntriesStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1800 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await by(page, 'views-new').click()
  await page.getByTestId('ed-widget-remove-note').click()
  await by(page, 'ed-file-name').fill('entries-board')
  await by(page, 'ed-title').fill('Entries board')

  // 1. The same measure twice, each with its alias and one filter of its own.
  await by(page, 'ed-add-table').click()
  await expect(by(page, 'ed-widget-editor-table')).toBeVisible()
  const measure = by(page, 'ed-well-table-columns-entry-1')
  await expect(measure).toBeVisible()
  await by(page, 'ed-entry-table-columns-1-options').locator('summary').click()
  await by(page, 'ed-entry-table-columns-1-repeat').click()
  await expect(by(page, 'ed-well-table-columns-entry-2')).toBeVisible()
  await by(page, 'ed-entry-table-columns-2-options').locator('summary').click()
  await by(page, 'ed-entry-table-columns-2-alias').fill('completed_revenue')
  await by(page, 'ed-entry-table-columns-2-filter-attribute').selectOption({ index: 1 })
  await by(page, 'ed-entry-table-columns-2-filter-value').fill('Completed')
  await by(page, 'ed-entry-table-columns-2-filter-add').click()
  await expect(by(page, 'ed-entry-table-columns-2-filter-0')).toBeVisible()
  await expect(by(page, 'ed-derived-request-table')).toContainText('"as": "completed_revenue"')
  // a bad alias is said in the loaders' words and nothing else changes
  await by(page, 'ed-entry-table-columns-2-alias').fill('Completed Revenue')
  await expect(by(page, 'ed-entry-table-columns-2-alias-problem')).toContainText('snake_case')
  await by(page, 'ed-entry-table-columns-2-alias').fill('completed_revenue')

  // 2. A field group in the document settings, then named by a chart's x well.
  await by(page, 'ed-group-name').fill('period')
  await by(page, 'ed-group-new-fields').fill('orders.ordered_at_year, orders.ordered_at_month')
  await by(page, 'ed-group-add').click()
  await expect(by(page, 'ed-group-period')).toBeVisible()
  await by(page, 'ed-group-template-period').fill('{2} {1}')
  await by(page, 'ed-add-chart').click()
  await expect(by(page, 'ed-widget-editor-chart')).toBeVisible()
  await by(page, 'ed-well-chart-x-pick').selectOption('@period')
  await expect(by(page, 'ed-well-chart-x-entry-0')).toContainText('group period')
  await expect(by(page, 'ed-derived-request-chart')).toContainText('orders.ordered_at_month')
  // a group in use is not removed
  await expect(by(page, 'ed-group-remove-period')).toBeDisabled()
  await expect(by(page, 'ed-group-uses-period')).toContainText('chart')

  // 3. An attribute in a matrix's cell area: display, icons, the many policy and a link.
  await by(page, 'ed-add-matrix').click()
  await expect(by(page, 'ed-widget-editor-matrix')).toBeVisible()
  await by(page, 'ed-well-matrix-values-type').fill('orders.status')
  await by(page, 'ed-well-matrix-values-type').press('Enter')
  await by(page, 'ed-entry-matrix-values-1-options').locator('summary').click()
  await by(page, 'ed-entry-matrix-values-1-display').selectOption('icon')
  await expect(by(page, 'ed-entry-matrix-values-1-problem-display')).toContainText('needs `icons`')
  await by(page, 'ed-entry-matrix-values-1-icon-value').fill('Open')
  await by(page, 'ed-entry-matrix-values-1-icon-new').selectOption('circle')
  await by(page, 'ed-entry-matrix-values-1-icon-add').click()
  await expect(by(page, 'ed-entry-matrix-values-1-icon-Open')).toBeVisible()
  await by(page, 'ed-entry-matrix-values-1-many').selectOption('count')
}
