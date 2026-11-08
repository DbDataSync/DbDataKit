import { expect, type Page, type Route } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '../../../../')
const sample = fs.readFileSync(path.join(root, 'DbDataBuild/templates/retail/reports/retail-sales-monthly.yaml'), 'utf8')
const results = path.join(root, 'DbDataKit/conformance/views/widget-results/report/retail-sales-monthly')
const recorded = (id: string) => JSON.parse(fs.readFileSync(path.join(results, `${id}.json`), 'utf8')) as unknown

const SERVER = 'gateway-1'
const NAME = 'retail-sales-monthly'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

interface PreviewBody { kind?: string; text?: string; layout?: string; parameters?: Record<string, unknown>; only?: string[]; widget?: unknown }

/**
 * A Gateway that runs the views module as the console's relay shows it: the report as published, and the module's preview route, which gives the recorded results of the sample report for the widgets the draft's layout
 * places and refuses (400 `invalid`, in a publish's words) a draft that holds the text `REFUSE-ME`. The route is the module's `POST /api/v1/views/preview`; here it is mocked, and every body it gets is kept.
 */
async function mockGateway(page: Page) {
  const calls = { preview: [] as PreviewBody[], other: [] as string[] }
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Retail gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const tail = new URL(request.url()).pathname.slice(`/relay/${SERVER}/api/v1/`.length)
    calls.other.push(`${request.method()} ${tail}`)
    if (tail === 'views/status') return json(route, { enabled: true, state: 'ok', dashboards: 0, reports: 1 })
    if (tail === 'dashboards') return json(route, { items: [] })
    if (tail === 'reports') return json(route, { items: [{ name: NAME, title: 'Retail sales by month', version: '3' }] })
    if (tail === `reports/${NAME}`) return json(route, { kind: 'report', name: NAME, title: 'Retail sales by month', version: '3', as_of: '2026-11-08T12:00:00Z', text: sample, document: {}, layouts: [{ name: 'paged', type: 'flow', default: true }] })
    if (tail === 'views/preview' && request.method() === 'POST') {
      const body = request.postDataJSON() as PreviewBody
      calls.preview.push(body)
      if (body.text?.includes('REFUSE-ME')) return json(route, { code: 'invalid', message: 'The report is refused: report/draft.yaml:3:1: error DBD-VIEW-043: The document queries the model `gone` and does not depend on it. [widgets.net.query]', diagnostics: [] }, 400)
      const widgets: Record<string, unknown> = { intro: { type: 'text', text: 'Net revenue by month.' } }
      for (const id of ['net', 'margin', 'units', 'by_month', 'products']) widgets[id] = recorded(id)
      return json(route, { kind: 'rendered_widgets', layout: body.layout ?? 'paged', as_of: '2026-11-08T12:00:00Z', widgets })
    }
    return json(route, { code: 'not_found', message: 'There is no such call.' }, 404)
  })
  return calls
}

/**
 * The live preview of an unsaved draft through the views module (plan 32 DBR28, M2): the report editor of a Gateway's published report sends the whole draft (its text and the layout) to the module in one call and draws the
 * answer as the person; each edit sends the new text and the preview follows; a draft the module refuses is said in the module's words (no picture of it is made up); nothing is saved (no PUT), and no other host is contacted.
 */
export async function editorPreviewStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  await page.setViewportSize({ width: 1440, height: 1400 })
  const calls = await mockGateway(page)

  await page.goto(`/views/reports/${NAME}?edit=1`)
  const preview = page.getByTestId('ed-flow-paged-preview')
  await expect(page.getByTestId('ed-flow-paged-editor')).toBeVisible()
  await expect(page.getByTestId('views-source-current').or(page.getByTestId('ed-root'))).toBeVisible()

  // the draft goes to the module in one call, as a report, with its layout and its text; the answer is drawn
  await expect(preview).toHaveAttribute('data-via', 'document')
  await expect(preview).toHaveAttribute('data-ready', 'true')
  await expect(page.getByTestId('vz-chart-by_month').locator('svg')).toBeVisible()
  await expect(page.getByTestId('vz-table-products').locator('tbody tr')).toHaveCount(24)
  expect(calls.preview.length).toBeGreaterThan(0)
  expect(calls.preview[0]).toMatchObject({ kind: 'report', layout: 'paged' })
  expect(calls.preview[0]!.text).toContain('Retail sales by month')
  expect(calls.preview[0]!.widget).toBeUndefined()

  // an edit sends the new text, and the preview follows it before anything is saved
  await page.getByTestId('ed-flow-paged-section-0-title').fill('Overview')
  await expect(page.locator('.vz-flow-title').first()).toHaveText('Overview')
  await expect.poll(() => calls.preview.at(-1)!.text).toContain('title: Overview')
  await expect(page.getByTestId('ed-dirty')).toHaveText('Unsaved changes')

  // a draft the module refuses is said in its words, and each widget says the preview failed; fixing it brings the picture back
  await page.getByTestId('ed-flow-paged-header-left').fill('REFUSE-ME')
  await expect(page.getByTestId('ed-flow-paged-preview-refused')).toContainText('DBD-VIEW-043')
  await expect(page.getByTestId('vz-widget-error').first()).toBeVisible()
  await page.getByTestId('ed-flow-paged-header-left').fill('{title}')
  await expect(page.getByTestId('ed-flow-paged-preview-refused')).toHaveCount(0)
  await expect(page.getByTestId('vz-chart-by_month').locator('svg')).toBeVisible()

  // nothing was saved, and nothing but the module was asked
  expect(calls.other.filter((c) => c.startsWith('PUT'))).toEqual([])
  expect(outside).toEqual([])
}
