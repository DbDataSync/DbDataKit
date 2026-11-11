import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * The link editor and the channel formats of the widget editor (plan 32 DBR38 and DBR49). A form link is added, shown as a dialog, given a prefilled field that is locked and a print treatment; the chart follows it on a
 * click; `print: link` on a dialog is refused in the validator's words; the chart's `y` gets a format through the format editor. The saved file holds all of it; that the commands write
 * what the golden says is the unit tests `linkCommands.test.ts` and `axisParts.test.ts`. Written without a browser to run it: a verifier runs it once.
 */
export async function editorLinksStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1700 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await by(page, 'views-new').click()
  await by(page, 'ed-file-name').fill('links-board')
  await by(page, 'ed-title').fill('Links board')
  await by(page, 'ed-add-chart').click()
  await expect(by(page, 'ed-widget-editor-chart')).toBeVisible()

  // 1. A form link: shown as a dialog, with a prefilled field that is read-only in the form.
  await by(page, 'ed-link-name').fill('order-form')
  await by(page, 'ed-link-new-kind').selectOption('form')
  await by(page, 'ed-link-new-target').fill('orders')
  await by(page, 'ed-link-add').click()
  await expect(by(page, 'ed-link-order-form')).toBeVisible()
  await by(page, 'ed-link-as-order-form').selectOption('dialog')
  await by(page, 'ed-link-value-name-order-form').fill('region')
  await by(page, 'ed-link-value-new-order-form').fill('$customers.region')
  await by(page, 'ed-link-value-add-order-form').click()
  await by(page, 'ed-link-lock-order-form-region').check()
  await by(page, 'ed-link-print-order-form').selectOption('hidden')

  // 2. A print: link on a link shown in a dialog has no address: the validator says so beside the link.
  await by(page, 'ed-link-print-order-form').selectOption('link')
  await expect(by(page, 'ed-link-problems-order-form')).toContainText('DBD-VIEW-046')
  await by(page, 'ed-link-print-order-form').selectOption('hidden')
  await expect(by(page, 'ed-link-problems-order-form')).toHaveCount(0)

  // 3. The chart follows the link on a click.
  await by(page, 'ed-prop-onclick-chart').selectOption('order-form')

  // 4. (DBR49) The chart's measure and colour channels take a format through the format editor.
  await by(page, 'ed-channel-format-chart-y-format-editor').locator('summary').click()
  await by(page, 'ed-channel-format-chart-y-format-kind').selectOption('number')
  await by(page, 'ed-channel-format-chart-y-format-style').selectOption('currency')
  await by(page, 'ed-channel-format-chart-y-format-currency').fill('eur')
  await by(page, 'ed-channel-format-chart-y-format-decimals').fill('0')
  await expect(by(page, 'ed-channel-format-chart-y-format-sample')).toContainText('1,234,568')

  await by(page, 'ed-place-desk-chart').click()
  await by(page, 'ed-save').click()
  await expect(by(page, 'ed-name')).toContainText('links-board')
  const text = await page.evaluate(() => JSON.parse(window.localStorage.getItem('dbdatakit.views.documents') ?? '{}')['dashboard/links-board'] as string)
  expect(text).toContain('order-form:')
  expect(text).toContain('form: orders')
  expect(text).toContain('as: dialog')
  expect(text).toContain('lock: [region]')
  expect(text).toContain('on_click: order-form')
  expect(text).toContain('currency: EUR')
}
