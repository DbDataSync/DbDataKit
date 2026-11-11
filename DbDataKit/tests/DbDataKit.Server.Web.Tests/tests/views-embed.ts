import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * The `embed` widget (plan 32 DBR19): the palette offers it in a dashboard, its address, link text and query pairs are set in the editor, the preview draws a link (the fixture source approves no origin to frame), an
 * address that is not https is refused in the widget's words. (A report's palette has no embed: the loader refuses one in a report: golden `report-with-embed`; the palette filter itself is not checked here.) Written without a browser to run it: a verifier runs it once. The frame itself (an approved origin) needs a module with
 * `Resolver.FrameOrigins` set and a page whose Content-Security-Policy allows it, which the console's fixture source does not have; `embedBlock.test.tsx` and `ResolveTests` check it.
 */
export async function embedStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1500 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await by(page, 'views-new').click()
  await by(page, 'ed-file-name').fill('embed-board')
  await by(page, 'ed-title').fill('Embed board')
  await by(page, 'ed-add-embed').click()
  await expect(by(page, 'ed-widget-editor-embed')).toBeVisible()

  // 1. The address, the link text and a query pair; the preview is a link, not a frame.
  await by(page, 'ed-embed-url-embed').fill('https://reports.example.com/Reports/report/Sales')
  await by(page, 'ed-embed-link-embed').fill('Open the sales report')
  await by(page, 'ed-embed-with-embed').fill('Fiscal=2026')
  await expect(by(page, 'ed-preview-embed')).toHaveAttribute('data-result', /embed|loading/)
  await expect(by(page, 'ed-preview-embed').locator('a')).toHaveAttribute('href', /reports\.example\.com/)
  await expect(by(page, 'ed-preview-embed').locator('iframe')).toHaveCount(0)

  // 2. An address that is not https is refused where it is.
  await by(page, 'ed-embed-url-embed').fill('http://reports.example.com/r')
  await expect(by(page, 'ed-validity')).toContainText('https')
}
