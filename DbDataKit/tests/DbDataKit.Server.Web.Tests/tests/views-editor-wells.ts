import { expect, type Page } from '@playwright/test'

const STORAGE_KEY = 'dbdatakit.views.documents'
const by = (page: Page, id: string) => page.getByTestId(id)

async function storedText(page: Page, key: string): Promise<string | null> {
  return page.evaluate(([storageKey, k]) => {
    const raw = window.localStorage.getItem(storageKey!)
    return raw ? ((JSON.parse(raw) as Record<string, string | null>)[k!] ?? null) : null
  }, [STORAGE_KEY, key] as const)
}

/**
 * The widget editor of the Views page (plan 32 DBR25, M2 step 2): a chart from the palette opens its editor with the retail model's catalog; a field is dragged from the list into a well (and the request it asks for
 * changes in the same step, each part with the place of the document it comes from), picked from a well's list, or typed; a dimension dragged into a measure well is refused in the engine's words and nothing changes; a field the
 * catalog lacks is placed and flagged `unknown_member`; every well change is one undo step; the type chooser turns a table into a chart; a written query shows the binding message when an output misses an encoded field; a
 * filter the browser cannot check (a misspelt attribute typed in the YAML view) is answered by the source through `ViewsClient.validate`; a layout's `widget_options` are set; the saved file holds all of it.
 */
export async function editorWellsStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  await page.setViewportSize({ width: 1440, height: 1400 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await by(page, 'views-new').click()
  await by(page, 'ed-file-name').fill('wells-board')
  await by(page, 'ed-title').fill('Wells board')

  // 1. A chart from the palette opens its editor, with the catalog of its model and a well for each channel.
  await by(page, 'ed-add-chart').click()
  await expect(by(page, 'ed-widget-editor-chart')).toBeVisible()
  await expect(by(page, 'ed-catalog')).toContainText('retail_sales')
  for (const well of ['x', 'y', 'color', 'column', 'row', 'tooltip']) await expect(by(page, `ed-well-chart-${well}`)).toBeVisible()
  await expect(by(page, 'ed-well-chart-x-entry-0')).toContainText('customers.region')
  await expect(by(page, 'ed-derived-chart')).toHaveAttribute('data-state', 'request')
  await expect(by(page, 'ed-derived-request-chart')).toContainText('"sales.net_amount"')
  await expect(by(page, 'ed-derived-sources-chart')).toContainText('widgets.chart.encodings.y')

  // 2. Dropping a field changes the request in one step; the sources say where each part comes from; the whole change is one undo step.
  await by(page, 'ed-well-chart-y-remove-0').click()
  await expect(by(page, 'ed-derived-issues-chart')).toContainText('The encodings name no measure')
  await by(page, 'ed-field-sales.units').dragTo(by(page, 'ed-well-chart-y'))
  await expect(by(page, 'ed-well-chart-y-entry-0')).toContainText('sales.units')
  await expect(by(page, 'ed-derived-request-chart')).toContainText('"sales.units"')
  await expect(by(page, 'ed-derived-request-chart')).not.toContainText('"sales.net_amount"')
  await by(page, 'ed-undo').click()
  await expect(by(page, 'ed-derived-issues-chart')).toBeVisible()
  await by(page, 'ed-undo').click()
  await expect(by(page, 'ed-well-chart-y-entry-0')).toContainText('sales.net_amount')

  // 3. Picking from a well's own list offers only what the well takes: a measure well lists no attribute.
  await by(page, 'ed-well-chart-color-pick').selectOption('products.category')
  await expect(by(page, 'ed-derived-sources-chart')).toContainText('widgets.chart.encodings.color')
  await by(page, 'ed-well-chart-tooltip-pick').selectOption('sales.units')
  await expect(by(page, 'ed-well-chart-tooltip-entry-0')).toContainText('sales.units')

  // 4. A metric: a dimension dragged into its measure well is refused with the engine's words, and the well is as it was.
  await by(page, 'ed-add-metric').click()
  await expect(by(page, 'ed-widget-editor-metric')).toBeVisible()
  expect(await by(page, 'ed-well-metric-value-pick').locator('option[value="customers.region"]').count()).toBe(0)
  await by(page, 'ed-field-customers.region').dragTo(by(page, 'ed-well-metric-value'))
  await expect(by(page, 'ed-status')).toContainText('customers.region is a attribute, not a measure.')
  await expect(by(page, 'ed-well-metric-value-entry-0')).toContainText('sales.net_amount')

  // 5. A field the catalog lacks is placed and flagged unknown_member, in the well and in the derivation; removing it repairs both.
  // the metric's editor is open: reopen the chart's
  await by(page, 'ed-widget-edit-chart').click()
  await by(page, 'ed-well-chart-row-type').fill('products.colour')
  await by(page, 'ed-well-chart-row-type').press('Enter')
  await expect(by(page, 'ed-well-chart-row-flag-0')).toContainText('unknown_member: products.colour is not a catalog member.')
  await expect(by(page, 'ed-derived-issues-chart')).toContainText('widgets.chart.encodings.row')
  await by(page, 'ed-well-chart-row-remove-0').click()
  await expect(by(page, 'ed-derived-chart')).toHaveAttribute('data-state', 'request')

  // 6. A table, then the type chooser turns it into a chart: the attribute goes to x, the measure to y, the title stays.
  await by(page, 'ed-add-table').click()
  await by(page, 'ed-well-table-columns-pick').selectOption('stores.store')
  await expect(by(page, 'ed-columns-table')).toContainText('stores.store')
  await by(page, 'ed-column-label-table-0').fill('Shop')
  await by(page, 'ed-type-table').selectOption('chart')
  await expect(by(page, 'ed-widget-editor-table')).toBeVisible()
  await expect(by(page, 'ed-well-table-x-entry-0')).toContainText('products.product')
  await expect(by(page, 'ed-well-table-y-entry-0')).toContainText('sales.units')
  await expect(by(page, 'ed-type-table')).toHaveValue('chart')
  await by(page, 'ed-undo').click()
  await expect(by(page, 'ed-type-table')).toHaveValue('table')

  // 7. A written query: the derived request is kept to be edited; an output that misses an encoded field shows the binding message at the field; derive again.
  await by(page, 'ed-widget-edit-chart').click()
  await by(page, 'ed-derived-explicit-chart').click()
  await expect(by(page, 'ed-derived-chart')).toHaveAttribute('data-state', 'explicit')
  await expect(by(page, 'ed-mode-chart')).toHaveText('query written out')
  const text = by(page, 'ed-explicit-text-chart')
  await text.fill((await text.inputValue()).replace('"customers.region",', '').replace('"products.category",', ''))
  await expect(by(page, 'ed-explicit-binding-chart')).toContainText('is encoded as')
  await expect(by(page, 'ed-explicit-binding-chart')).toContainText('is not an output of the query')
  await text.fill((await text.inputValue()) + ' oops')
  await expect(by(page, 'ed-explicit-error-chart')).toContainText('Not JSON')
  await by(page, 'ed-derived-back-chart').click()
  await expect(by(page, 'ed-derived-chart')).toHaveAttribute('data-state', 'request')

  // 8. widget_options of the layout, set beside the widget: density and a hidden title; the saved file holds them.
  await by(page, 'ed-opt-desk-chart-density').selectOption('compact')
  await by(page, 'ed-opt-desk-chart-show_title').selectOption('false')
  await by(page, 'ed-opt-desk-chart-legend').selectOption('hide')
  await by(page, 'ed-opt-desk-chart-show_title').selectOption('')

  // 9. Place the three widgets and save.
  for (const id of ['chart', 'metric', 'table']) await by(page, `ed-place-desk-${id}`).click()
  await expect(by(page, 'ed-validity')).toHaveText('Valid')
  await by(page, 'ed-save').click()
  await expect(by(page, 'ed-name')).toHaveText('wells-board.yaml')

  const saved = (await storedText(page, 'dashboard/wells-board'))!
  expect(saved).toContain('type: chart')
  expect(saved).toContain('widget_options:')
  expect(saved).toMatch(/chart: \{density: compact, legend: hide\}/)
  expect(saved).toContain('x: customers.region')
  expect(saved).toContain('color: products.category')
  expect(saved).not.toContain('products.colour')

  // 10. The sample dashboard: its filter misspelt in the YAML view is answered by the source, at the filter's path.
  await page.goto('/views/dashboards/retail-sales-today?edit=1')
  await by(page, 'ed-widget-edit-net').click()
  await expect(by(page, 'ed-widget-editor-net')).toBeVisible()
  await expect(by(page, 'ed-server-net')).toHaveCount(0)
  await by(page, 'ed-tab-yaml').click()
  const yaml = by(page, 'ed-yaml')
  await expect(yaml.locator('.view-line').filter({ hasText: 'schemaVersion' })).toBeVisible()
  await yaml.locator('.view-line').filter({ hasText: 'operator: equals' }).first().click()
  await page.keyboard.press('End')
  await page.keyboard.press('Shift+Home')
  await page.keyboard.type('filters: [{attribute: customers.regoin, operator: equals, values: {param: region}}]')
  await by(page, 'ed-tab-form').click()
  await expect(by(page, 'ed-server-net')).toContainText('widgets.net.query.filters[0].attribute')
  await expect(by(page, 'ed-server-net')).toContainText('customers.regoin is not a catalog member.')

  expect(outside, 'the editor contacts no other host').toEqual([])
}
