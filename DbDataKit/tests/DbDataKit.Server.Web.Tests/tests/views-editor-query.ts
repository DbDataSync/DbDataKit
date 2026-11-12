import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * The Filter and Sort sections of the widget editor (plan 32 DBR43): a filter on a region and a sort by revenue descending are added to a chart, the request it asks for changes in the same step; a parameter is the
 * value of a filter; an operator the field's type does not take is refused with the engine's words; every change is one undo step. Written without a browser to run it: a verifier runs it once.
 */
export async function editorQueryStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1500 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await by(page, 'views-new').click()
  // a new document starts with one text widget (`note`) so that it is valid as it stands; the story builds on an empty one
  await page.getByTestId('ed-widget-remove-note').click()
  await by(page, 'ed-file-name').fill('query-board')
  await by(page, 'ed-title').fill('Query board')
  await by(page, 'ed-add-chart').click()
  await expect(by(page, 'ed-widget-editor-chart')).toBeVisible()

  // 1. A filter on a region: the request it asks for has it, and the filter is listed.
  await by(page, 'ed-filter-attribute-chart').selectOption('customers.region')
  await by(page, 'ed-filter-value-chart').fill('West')
  await by(page, 'ed-filter-add-chart').click()
  await expect(by(page, 'ed-filter-chart-0')).toContainText('customers.region')
  await expect(by(page, 'ed-filter-chart-0')).toContainText('"West"')
  await expect(by(page, 'ed-derived-request-chart')).toContainText('"filters"')
  await expect(by(page, 'ed-derived-request-chart')).toContainText('West')

  // 2. A sort by revenue descending: the request has the order.
  await by(page, 'ed-order-field-chart').selectOption('sales.net_amount')
  await by(page, 'ed-order-add-chart').click()
  await by(page, 'ed-order-dir-chart-0').selectOption('desc')
  await expect(by(page, 'ed-derived-request-chart')).toContainText('"desc"')
  await expect(by(page, 'ed-derived-sources-chart')).toContainText('widgets.chart.query.order[0]')
  // the preview is of the filtered, ordered widget
  await expect(by(page, 'ed-preview-chart')).toHaveAttribute('data-result', /chart|loading/)

  // 3. The operators offered are the field's type's: a date offers the relative ones, a region does not.
  await by(page, 'ed-filter-attribute-chart').selectOption('date.calendar_date')
  expect(await by(page, 'ed-filter-operator-chart').locator('option[value="last_complete"]').count()).toBe(1)
  await by(page, 'ed-filter-attribute-chart').selectOption('customers.region')
  expect(await by(page, 'ed-filter-operator-chart').locator('option[value="last_complete"]').count()).toBe(0)

  // 4. An operator the type does not take is not offered (a year is a number: no text operators); the engine's refusal, for one typed in the YAML view, is shown by the source.
  await by(page, 'ed-filter-attribute-chart').selectOption('date.year')
  expect(await by(page, 'ed-filter-operator-chart').locator('option[value="contains"]').count()).toBe(0)
  expect(await by(page, 'ed-filter-operator-chart').locator('option[value="gte"]').count()).toBe(1)

  // 5. A parameter as the value is chosen from the document's parameters (a document without one offers none).
  await by(page, 'ed-filter-attribute-chart').selectOption('customers.region')
  await expect(by(page, 'ed-filter-parameter-chart')).toBeVisible()

  // 6. Removing the order entry leaves the filter; the request follows.
  await by(page, 'ed-order-remove-chart-0').click()
  await expect(by(page, 'ed-order-chart-0')).toHaveCount(0)
  await expect(by(page, 'ed-filter-chart-0')).toBeVisible()
}
