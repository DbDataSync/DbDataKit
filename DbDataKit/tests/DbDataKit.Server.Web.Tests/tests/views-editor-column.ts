import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * The table column controls of the widget editor (plan 32 DBR44): a column's label, format (as a structured currency with a sample), alignment and width are set through the forms, and the saved file holds them.
 * Written without a browser to run it. That the file reads back equal is the unit test `axisParts.test.ts` (the commands applied to a document equal the golden widget).
 */
export async function editorColumnStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1500 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await by(page, 'views-new').click()
  await by(page, 'ed-file-name').fill('column-board')
  await by(page, 'ed-title').fill('Column board')
  await by(page, 'ed-add-table').click()
  await expect(by(page, 'ed-widget-editor-table')).toBeVisible()
  await by(page, 'ed-column-label-table-0').fill('Shop')
  await by(page, 'ed-column-format-table-1-editor').locator('summary').click()
  await by(page, 'ed-column-format-table-1-kind').selectOption('number')
  await by(page, 'ed-column-format-table-1-style').selectOption('currency')
  await expect(by(page, 'ed-column-format-table-1-problem')).toContainText('currency')
  await by(page, 'ed-column-format-table-1-currency').fill('eur')
  await by(page, 'ed-column-format-table-1-decimals').fill('0')
  await expect(by(page, 'ed-column-format-table-1-sample')).toContainText('1,234,568')
  await by(page, 'ed-column-align-table-1').selectOption('right')
  await by(page, 'ed-column-width-table-1').fill('120')
  await by(page, 'ed-save').click()
  const text = await page.evaluate(() => JSON.parse(window.localStorage.getItem('dbdatakit.views.documents') ?? '{}')['dashboard/column-board'] as string)
  expect(text).toContain('currency: EUR')
  expect(text).toContain('align: right')
  expect(text).toContain('width: 120')
}
