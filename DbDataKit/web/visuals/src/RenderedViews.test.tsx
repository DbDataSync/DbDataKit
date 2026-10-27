import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { chartToSvg } from './chartSvg'
import { DataTable } from './DataTable'
import { RenderedViews } from './RenderedViews'
import { viewFixtures, type ViewFixture } from './testing/fixtures'

const fixtures = viewFixtures()
const complete = fixtures.filter((f) => f.rendered.status === 'complete')
const failed = fixtures.filter((f) => f.rendered.status === 'failed')
const html = (document: unknown, theme: 'light' | 'dark' = 'light', showData = false) => renderToStaticMarkup(<RenderedViews document={document} theme={theme} showData={showData} />)
const textOf = (fragment: string) => fragment.replace(/<[^>]+>/g, '').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#x27;/g, "'")

/** The tbody of a rendered grid as rows of {labels, texts}, read back from the markup. */
function gridRows(markup: string) {
  const body = /<tbody>(.*?)<\/tbody>/s.exec(markup)![1]!
  return [...body.matchAll(/<tr([^>]*)>(.*?)<\/tr>/gs)].map((m) => ({
    total: /vz-total/.test(m[1]!),
    labels: [...m[2]!.matchAll(/<th[^>]*>(.*?)<\/th>/gs)].map((c) => textOf(c[1]!)),
    texts: [...m[2]!.matchAll(/<td[^>]*>(.*?)<\/td>/gs)].map((c) => textOf(c[1]!)),
  }))
}

describe('every fixture view renders', () => {
  const matrixCases = complete.flatMap((f) => f.rendered.views!.filter((v) => v.type === 'matrix').map((v) => [`${f.name} ${v.id}`, f, v] as const))
  const chartCases = complete.flatMap((f) => f.rendered.views!.filter((v) => v.type === 'chart').map((v) => [`${f.name} ${v.id}`, f, v] as const))

  it('covers every kind of view the fixtures have', () => {
    expect(matrixCases.length).toBeGreaterThanOrEqual(10)
    expect(chartCases.length).toBeGreaterThanOrEqual(3)
  })

  it.each(matrixCases)('%s: the grid reads as the server wrote it', (_name, fixture: ViewFixture, view) => {
    const markup = html(fixture.rendered)
    expect(markup).toContain(`data-testid="vz-matrix-${view.id}"`)
    const rows = gridRows(markup)
    // what the server rendered, cell by cell ...
    expect(rows.map((r) => ({ labels: r.labels, texts: r.texts }))).toEqual(view.rows.map((r: { labels: string[]; cells: Array<{ text: string }> }) => ({ labels: r.labels, texts: r.cells.map((c) => c.text) })))
    // ... and, where the fixture states what the grid must read as, that
    const stated = (fixture.expect as unknown as { views?: Array<{ rows: Array<{ labels: string[]; texts: string[] }>; notice?: string }> }).views?.[0]
    if (stated) expect(rows.map((r) => ({ labels: r.labels, texts: r.texts }))).toEqual(stated.rows.map((r) => ({ labels: r.labels, texts: r.texts })))
    // a total row is marked as one, exactly where the result had it
    expect(rows.map((r) => r.total)).toEqual(view.rows.map((r: { total?: boolean }) => Boolean(r.total)))
    if (stated?.notice) expect(textOf(markup)).toContain(stated.notice)
  })

  it.each(chartCases)('%s: the chart is drawn, from its data alone', async (_name, fixture: ViewFixture, view) => {
    const markup = html(fixture.rendered)
    expect(markup).toContain(`data-testid="vz-chart-${view.id}"`)
    const svg = await chartToSvg(view.vega_lite)
    expect(svg).toMatch(/^<svg /)
    for (const channel of ['x', 'y']) {
      const field = view.vega_lite.encoding?.[channel]?.field as string | undefined
      if (field) expect(svg).toContain(field)
    }
    expect(svg).not.toMatch(/(?:href|src)="https?:/)
  })

  it.each(failed.map((f) => [f.name, f] as const))('%s: a view that could not be made shows the server\'s errors and draws nothing', (_name, fixture) => {
    const markup = html(fixture.rendered)
    expect(markup).toContain('data-testid="vz-problems"')
    expect(markup).not.toContain('vz-matrix')
    expect(markup).not.toContain('vz-chart')
    for (const error of fixture.rendered.errors!) expect(markup).toContain(error.code)
  })
})

describe('a document that is not safe to draw', () => {
  const chart = { id: 'c1', type: 'chart', vega_lite: { mark: 'bar', encoding: { x: { field: 'a', type: 'nominal' } }, data: { values: [{ a: 'x' }] } } }
  const wrap = (view: unknown) => ({ kind: 'rendered_view', status: 'complete', views: [view] })

  it('with a chart that loads data is refused, and nothing is drawn', () => {
    const markup = html(wrap({ ...chart, vega_lite: { ...chart.vega_lite, data: { url: 'https://example.com/x.csv' } } }))
    expect(markup).toContain('This is not a view that can be shown.')
    expect(markup).not.toContain('vz-chart')
  })

  it('with a chart that computes is refused', () => {
    const markup = html(wrap({ ...chart, vega_lite: { ...chart.vega_lite, transform: [{ calculate: 'datum.a', as: 'b' }] } }))
    expect(markup).toContain('vega_disallowed')
    expect(markup).not.toContain('vz-chart')
  })

  it('that is not a rendered view at all is refused', () => {
    expect(html({ spec_version: '0.3', kind: 'view' })).toContain('rendered_invalid')
    expect(html(null)).toContain('rendered_invalid')
  })
})

describe('light and dark', () => {
  const line = fixtures.find((f) => f.name === '17_line_chart')!
  const matrix = fixtures.find((f) => f.name === '02_totals_come_from_the_result')!

  it('a grid carries the theme the page asks for', () => {
    expect(html(matrix.rendered, 'light')).toContain('data-vz-theme="light"')
    expect(html(matrix.rendered, 'dark')).toContain('data-vz-theme="dark"')
  })

  it('a chart is drawn in the theme\'s own colours', async () => {
    const spec = line.rendered.views![0]!.vega_lite
    const light = await chartToSvg(spec, 'light')
    const dark = await chartToSvg(spec, 'dark')
    expect(light).toContain('#0072B2')
    expect(light).not.toContain('#56B4E9')
    expect(dark).toContain('#56B4E9')
    expect(dark).not.toContain('#0072B2')
    expect(dark).toContain('#e6edf3')
  })
})

describe('snapshots, one per kind', () => {
  const matrix = fixtures.find((f) => f.name === '02_totals_come_from_the_result')!
  const line = fixtures.find((f) => f.name === '17_line_chart')!

  it('matrix', async () => {
    await expect(html(matrix.rendered)).toMatchFileSnapshot('./__snapshots__/matrix.html')
  })

  it('matrix with a notice and an Other column', async () => {
    await expect(html(fixtures.find((f) => f.name === '10_column_limit_top_n_other')!.rendered)).toMatchFileSnapshot('./__snapshots__/matrix-other.html')
  })

  it('table', async () => {
    const view = line.rendered.views![0]!
    await expect(renderToStaticMarkup(<DataTable id={view.id} caption="Revenue by quarter and region" rows={view.vega_lite.data.values} />)).toMatchFileSnapshot('./__snapshots__/table.html')
  })

  it('chart, the host the browser fills', async () => {
    await expect(html(line.rendered, 'light', true)).toMatchFileSnapshot('./__snapshots__/chart.html')
  })

  it('chart, drawn (light)', async () => {
    await expect(await chartToSvg(line.rendered.views![0]!.vega_lite, 'light')).toMatchFileSnapshot('./__snapshots__/chart-light.svg')
  })

  it('chart, drawn (dark)', async () => {
    await expect(await chartToSvg(line.rendered.views![0]!.vega_lite, 'dark')).toMatchFileSnapshot('./__snapshots__/chart-dark.svg')
  })

  it('a failed view', async () => {
    await expect(html(fixtures.find((f) => f.name === '18_chart_outside_the_subset')!.rendered)).toMatchFileSnapshot('./__snapshots__/failed.html')
  })
})
