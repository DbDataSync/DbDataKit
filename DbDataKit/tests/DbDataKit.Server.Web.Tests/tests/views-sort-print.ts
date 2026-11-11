import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * Click-to-sort for viewers (plan 32 DBR45): on the sample dashboard a click on a table header sorts ascending, a second click descending, a third gives the document's order back; the header's cell carries `aria-sort` and the
 * keyboard works (the header is a button). Nothing is stored: a reload shows the document's order. Written without a browser to run it: a verifier runs it once. The server-run sort of a table the limit cut needs a Gateway
 * with rows over the limit (`ViewsApiTests` and `clickSort.test.tsx` check it).
 */
export async function clickSortStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/views/dashboards/retail-sales-today')
  const header = by(page, 'vz-sort-top_products-1')
  const cell = header.locator('xpath=ancestor::th')
  await expect(cell).toHaveAttribute('aria-sort', 'none')
  await header.click()
  await expect(cell).toHaveAttribute('aria-sort', 'ascending')
  await header.press('Enter')
  await expect(cell).toHaveAttribute('aria-sort', 'descending')
  await header.click()
  await expect(cell).toHaveAttribute('aria-sort', 'none')
  // a click that is not stored: after a reload the document's order is back
  await header.click()
  await page.reload()
  await expect(by(page, 'vz-sort-top_products-1').locator('xpath=ancestor::th')).toHaveAttribute('aria-sort', 'none')
}

/**
 * The print preview of a report (plan 32 DBR27, the client half): the report editor's preview has a Print preview button that asks the module for the PDF with the layout and the parameters on screen and shows it in a
 * dialog with its page count and a download; a host with no exporter says why the button is off instead. Needs a Gateway whose `views.export` points at the pinned Chromium and the bundled fonts (`scripts/fetch-fonts.sh`),
 * which the fixture console does not have: with the fixture source the button must be off with its reason, and that is what is checked here. The page-count check of a real PDF is the exporter's test in CI.
 */
export async function printPreviewStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1200 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-reports').click()
  await by(page, 'views-open-retail-sales-monthly').click()
  const preview = by(page, 'vz-flow-preview')
  await expect(preview).toBeVisible()
  await expect(preview).toBeDisabled()
  await expect(preview).toHaveAttribute('title', /.+/)
}
