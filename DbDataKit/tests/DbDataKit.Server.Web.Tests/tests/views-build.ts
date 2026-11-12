import { expect, type BrowserContext, type Page } from '@playwright/test'

/**
 * Dashboards and reports as files of a Build project (plan 32 DBR29), from the Views tab of the project (`/servers/<build>/projects/<project>/views`). The project is the retail template (its semantic model `retail_sales`,
 * its sample dashboard and report), a git repository the server hosts. In a workspace a person: starts a new dashboard, whose wells list the project's own model (the catalog comes from the workspace), adds a chart and a
 * metric, saves it (the file `dashboards/<name>.yaml` appears in the workspace's list and in the Edit tab); types a field the model does not have and the project check, which reads the file with the views loader and the project's
 * models, names it at the file and line; fixes it; a second page saves the same file first and this page's save is a version conflict that offers a reload; the Edit tab opens the file and links back to the editor; commits and merges
 * the workspace, and the project's documents are offered for publishing, where the button says why it is off (no Gateway is paired with this server). The browser contacts no other host.
 * Publishing itself (a paired Gateway) is the server's own tests (`BuildServerProxyTests`) and the mocked story that ran with this slice; a Gateway to publish to is not part of this fixture.
 */
export async function buildViewsStory(page: Page, context: BrowserContext, project: string) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  const watch = (p: Page) => p.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  watch(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  const check = expect.configure({ timeout: 60_000 })

  // 1. The tab: no workspace yet, so nothing to edit; what can be published is the project as merged (the sample documents), and the button says why it is off.
  await page.getByTestId('build-tab-views').click()
  await expect(page.getByTestId('build-views-no-workspace')).toBeVisible()
  await check(page.getByTestId('build-views-publish-row-dashboard-retail-sales-today')).toBeVisible()
  await expect(page.getByTestId('build-views-publish-row-report-retail-sales-monthly')).toBeVisible()
  await expect(page.getByTestId('build-views-publish-button-dashboard-retail-sales-today')).toBeDisabled()
  await expect(page.getByTestId('build-views-publish-why')).toContainText('not paired with a Gateway')

  // 2. A workspace: the sample documents are in it, and a new dashboard is offered.
  await page.getByTestId('build-views-workspace-name').fill('dash')
  await page.getByTestId('build-views-workspace-make').click()
  await check(page.getByTestId('build-views-doc-dashboard-retail-sales-today')).toBeVisible()
  await expect(page.getByTestId('build-views-doc-report-retail-sales-monthly')).toBeVisible()
  await page.getByTestId('build-views-new-dashboard').click()
  await expect(page.getByTestId('ed-root')).toBeVisible()
  await expect(page.getByTestId('ed-file-name')).toBeVisible()
  await page.getByTestId('ed-widget-remove-note').click()   // a new document starts with one text widget; the story builds on an empty one

  // 3. The model is the project's own: its measures and attributes are what the wells offer; a chart and a metric, saved as dashboards/my-sales.yaml.
  await page.getByTestId('ed-file-name').fill('my-sales')
  await page.getByTestId('ed-title').fill('My sales')
  await page.getByTestId('ed-add-chart').click()
  await check(page.getByTestId('ed-field-sales.net_amount')).toBeVisible()
  await expect(page.getByTestId('ed-well-chart-x-entry-0')).toContainText('customers.region')
  await page.getByTestId('ed-add-metric').click()
  await page.getByTestId('ed-place-desk-chart').click()
  await page.getByTestId('ed-place-desk-metric').click()
  await expect(page.getByTestId('ed-validity')).toHaveText('Valid')
  await expect(page.getByTestId('build-views-editor-note')).toContainText('name it and Save')
  await page.getByTestId('ed-save').click()
  // a new document moves to its own address when it is saved, and the editor opens again on the file (its message is gone with the old one): the file's name is what shows it was written
  await expect(page.getByTestId('ed-name')).toHaveText('dashboards/my-sales.yaml')
  await expect(page.getByTestId('build-views-editor-note')).toContainText('Saved in the workspace')

  // 4. A field the model does not have: the browser's validator cannot know it, so the file is saved, and the project check names it at the file and line.
  await page.getByTestId('ed-widget-edit-chart').click()      // the editor opened again on the saved file (a new document moves to its own address), with the widget closed
  // the pick list of the well is there once the project's catalog is known; the editor is drawn again when it arrives (and may close the widget), so the name is typed after it, and once more if the box went with the redraw
  await expect(page.getByTestId('ed-well-chart-color-pick')).toBeVisible({ timeout: 60_000 })
  await expect(async () => {
    if (!(await page.getByTestId('ed-well-chart-color-type').isVisible())) await page.getByTestId('ed-widget-edit-chart').click()
    await page.getByTestId('ed-well-chart-color-type').fill('products.colour', { timeout: 3_000 })
    await page.getByTestId('ed-well-chart-color-type').press('Enter', { timeout: 3_000 })
  }).toPass({ timeout: 60_000 })
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-status')).toContainText('Saved my-sales')
  await page.getByTestId('ed-back').click()
  await expect(page.getByTestId('build-views-doc-dashboard-my-sales')).toBeVisible()
  await expect(page.getByTestId('build-views-doc-dashboard-my-sales')).toContainText('dashboards/my-sales.yaml')
  await page.getByTestId('build-views-check-button').click()
  const finding = page.getByTestId('build-views-check-finding').first()
  await check(finding).toBeVisible()
  await expect(finding).toContainText('unknown_member')
  await expect(finding).toContainText('products.colour')
  await expect(finding).toContainText('dashboards/my-sales.yaml:')
  await expect(page.getByTestId('build-views-check-finding')).toHaveCount(1)   // the sample documents of the template are clean

  // 5. The same file in a second page, saved first: this page's save is a version conflict that offers a reload.
  await page.getByTestId('build-views-edit-dashboard-my-sales').click()
  await check(page.getByTestId('ed-root')).toBeVisible()
  const other = await context.newPage()
  watch(other)
  await other.goto(page.url())
  await other.getByTestId('ed-title').fill('My sales, other tab')
  await other.getByTestId('ed-save').click()
  await expect(other.getByTestId('ed-status')).toContainText('Saved my-sales')
  await page.getByTestId('ed-title').fill('My sales, this tab')
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-conflict')).toContainText('changed elsewhere')
  await expect(page.getByTestId('ed-conflict')).toContainText('stored version')
  await page.getByTestId('ed-conflict-reload').click()
  await expect(page.getByTestId('ed-title')).toHaveValue('My sales, other tab')
  await other.close()

  // 6. Fix the field (remove it from the well); saved, and the check is clean.
  await page.getByTestId('ed-widget-edit-chart').click()
  await check(page.getByTestId('ed-well-chart-color-remove-0')).toBeVisible()
  await page.getByTestId('ed-well-chart-color-remove-0').click()
  await page.getByTestId('ed-save').click()
  await expect(page.getByTestId('ed-status')).toContainText('Saved my-sales')
  await page.getByTestId('ed-back').click()
  await page.getByTestId('build-views-check-button').click()
  await check(page.getByTestId('build-views-check-clean')).toBeVisible()

  // 7. The Edit tab lists the file with the others and opens it; from there the editor is one link away. (The tab compiles the draft as it opens, and the server runs a workspace's commands one at a time, so the file is read when that ends.)
  await page.getByTestId('build-tab-edit').click()
  await page.getByTestId('build-edit-workspace').selectOption({ label: 'dash' })
  await page.getByTestId('build-edit-file-dashboards/my-sales.yaml').click()
  await check(page.getByTestId('build-edit-open')).toHaveText('dashboards/my-sales.yaml')
  await page.getByTestId('build-edit-open-editor').click()
  await check(page.getByTestId('ed-title')).toHaveValue('My sales, other tab')

  // 8. Commit and merge the workspace (the approver's merge); the project as merged now has the new dashboard, ready to publish, and the button still says why it is off.
  await page.getByTestId('build-tab-edit').click()
  await page.getByTestId('build-edit-workspace').selectOption({ label: 'dash' })
  await page.getByTestId('build-git-message').fill('A dashboard of my own')
  await page.getByTestId('build-git-commit').click()
  await check(page.getByTestId('build-git-merge')).toBeEnabled()
  await page.getByTestId('build-git-merge').click()
  await check(page.getByTestId('build-edit-none')).toBeVisible()   // the merge has ended (the workspace is gone): only then does the project as merged hold the file
  await page.getByTestId('build-tab-views').click()
  await check(page.getByTestId('build-views-publish-row-dashboard-my-sales')).toBeVisible()
  await expect(page.getByTestId('build-views-publish-button-dashboard-my-sales')).toBeDisabled()
  expect(outside).toEqual([])
  void project
}
