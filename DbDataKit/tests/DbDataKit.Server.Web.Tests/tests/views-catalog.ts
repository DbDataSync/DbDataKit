import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * The report browser (plan 32 RC05): the Catalog tab of Views lists two catalogs (the sample source: ours, and a report server's) with each item marked by its catalog; a folder opens; a search finds an item of each catalog together; an item with parameters opens
 * with the one parameters pane (a required parameter is named before anything is sent, a dependent one empties when its parent changes); an external item opens in a frame (the sample says its origin is approved) with the values filled in; one that is a link opens off the site
 * with no opener; an export is taken in a format the item offers. Run in a browser (the console only, the sample source). Written and not run when this was committed (no console host could be started in the session that wrote it).
 */
export async function catalogStory(page: Page) {
  await page.setViewportSize({ width: 1280, height: 1200 })
  await page.getByTestId('rail-views').click()
  await by(page, 'views-tab-catalog').click()
  await expect(by(page, 'catalog-panel')).toBeVisible()

  // 1. two catalogs side by side at the root, each marked
  await expect(by(page, 'catalog-provider-views:folder/dashboards')).toHaveText('views')
  await expect(by(page, 'catalog-provider-ssrs:f-sales')).toHaveText('ssrs')

  // 2. a search finds a report server's report and our dashboard together
  await by(page, 'catalog-search').fill('revenue')
  await by(page, 'catalog-search-go').click()
  await expect(by(page, 'catalog-row-ssrs:r-quarterly')).toBeVisible()
  await expect(by(page, 'catalog-row-views:dashboard/sales')).toBeVisible()
  await by(page, 'catalog-search-clear').click()

  // 3. a folder opens, and the way back is the crumb
  await by(page, 'catalog-open-ssrs:f-sales').click()
  await expect(by(page, 'catalog-row-ssrs:r-quarterly')).toBeVisible()
  await expect(by(page, 'catalog-crumb-/Sales')).toBeVisible()

  // 4. an item with parameters: a required one is named before anything is sent
  await by(page, 'catalog-open-ssrs:r-quarterly').click()
  await expect(by(page, 'catalog-params')).toBeVisible()
  await by(page, 'catalog-open').click()
  await expect(by(page, 'catalog-problem')).toContainText('Country')
  await expect(by(page, 'catalog-frame')).toHaveCount(0)

  // 5. a dependent parameter empties when its parent changes, and the report opens in its frame
  await by(page, 'catalog-param-Country').selectOption('US')
  await by(page, 'catalog-param-Region').selectOption(['East'])
  await by(page, 'catalog-param-Country').selectOption('DE')
  await expect(by(page, 'catalog-param-Region')).not.toHaveValue('East')
  await by(page, 'catalog-open').click()
  await expect(by(page, 'catalog-frame')).toHaveAttribute('src', /reports\.example\.com\/ReportServer/)
  await expect(by(page, 'catalog-frame')).toHaveAttribute('sandbox', /allow-scripts/)

  // 6. an export in a format the item offers is a link to the file; a format it does not offer has no button
  await by(page, 'catalog-export-pdf').click()
  await expect(by(page, 'catalog-export-link')).toHaveAttribute('href', /rs:Format=PDF/)
  await expect(by(page, 'catalog-export-pptx')).toHaveCount(0)

  // 7. a link off the domain opens in a new tab, with no opener
  await by(page, 'catalog-open-ssrs:r-board').click()
  await by(page, 'catalog-open').click()
  await expect(by(page, 'catalog-link')).toHaveAttribute('rel', /noopener/)
  await expect(by(page, 'catalog-link')).toHaveAttribute('target', '_blank')
  await expect(by(page, 'catalog-frame')).toHaveCount(0)

  // 8. our own dashboard opens in its board with the layout
  await by(page, 'catalog-crumb-/').click()
  await by(page, 'catalog-open-views:folder/dashboards').click()
  await by(page, 'catalog-open-views:dashboard/sales').click()
  await by(page, 'catalog-open').click()
  await by(page, 'catalog-document-link').click()
  await expect(page).toHaveURL(/\/views\/dashboards\/sales/)
}
