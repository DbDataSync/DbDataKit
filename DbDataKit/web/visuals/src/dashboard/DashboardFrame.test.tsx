import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { validateText } from '../client/text'
import { sampleBundle } from '../testing/sampleBundle'
import type { Box, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { DashboardFrame } from './DashboardFrame'

const bundle = sampleBundle()
const dashboard = validateText(bundle.documents['dashboard/retail-sales-today']!).document as ViewsDoc
const results: Record<string, RenderedWidget> = { ...bundle.results!['dashboard/retail-sales-today']!, note: { type: 'text' } }
const layout = (name: string) => dashboard.layouts.find((l) => l.name === name)!
const frame = (box: Box, over: Partial<Parameters<typeof DashboardFrame>[0]> = {}) =>
  renderToStaticMarkup(<DashboardFrame document={dashboard} layout={layout('desk')} box={box} theme="light" results={results} parameters={{ region: ['West'] }} {...over} />)
const text = (markup: string) => markup.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim()

describe('DashboardFrame, the reflow layout as markup', () => {
  it.each([[1920, 1080], [1366, 768]])('at %i x %i it is a grid of fr tracks with every tile in its cells, and nothing scrolls', (width, height) => {
    const markup = frame({ width, height })
    expect(markup).toContain('data-mode="grid"')
    expect(markup).toContain(`style="width:${width}px;height:${height}px"`)
    expect(markup).toContain('grid-template-columns:repeat(12, minmax(0, 1fr));grid-template-rows:repeat(8, minmax(0, 1fr))')
    expect(markup).toContain('data-testid="vz-tile-by_region" style="grid-column:1 / span 7;grid-row:4 / span 5"')
    expect(markup).toContain('data-testid="vz-tile-regions" style="grid-column:1 / span 12;grid-row:1 / span 1"')
    for (const id of ['regions', 'net', 'margin', 'note', 'by_region', 'top_products']) expect(markup).toContain(`data-testid="vz-widget-${id}"`)
    expect(markup).not.toContain('data-mode="stack"')
  })

  it('a table that does not fit its tile shows what fits, says so, and offers the whole container', () => {
    const markup = frame({ width: 1366, height: 768 }, { onMaximize: () => {} })
    expect(text(markup)).toContain('12 of 24 rows')
    expect(markup).toContain('aria-label="Maximize Best-selling products"')
    // at 1920 x 1080 the tile is tall enough for more rows and still says how many
    expect(text(frame({ width: 1920, height: 1080 }))).toMatch(/\d+ of 24 rows/)
  })

  it('maximized: one widget fills the container, whole, and the action closes it', () => {
    const markup = frame({ width: 1366, height: 768 }, { maximized: 'top_products', onMaximize: () => {} })
    expect(markup).toContain('data-mode="maximized"')
    expect(markup).toContain('vz-is-maximized')
    expect(markup).not.toContain('data-testid="vz-widget-net"')
    expect(markup.match(/<tr>/g)!.length - 1).toBe(24)
    expect(text(markup)).not.toContain('of 24 rows')
    expect(markup).toContain('aria-label="Close the full view of Best-selling products"')
    expect(frame({ width: 1366, height: 768 }, { maximized: 'nope' })).toContain('data-mode="grid"')
  })

  it('a box too small is the compact stack: one column, document order, the page scrolls', () => {
    const markup = frame({ width: 390, height: 844 })
    expect(markup).toContain('data-mode="stack"')
    const order = [...markup.matchAll(/data-testid="vz-tile-(\w+)"/g)].map((m) => m[1])
    expect(order).toEqual(['regions', 'net', 'margin', 'note', 'by_region', 'top_products'])
    expect(markup).toContain('data-testid="vz-tile-net" style="height:88px;flex:none"')
    expect(markup).toContain('class="vz-dashboard-stack" style="height:668px"')
    expect(markup).not.toContain('grid-template-columns')
  })

  it('a widget that failed shows its own error and the others draw', () => {
    const markup = frame({ width: 1366, height: 768 }, { results: { ...results, by_region: { type: 'error', code: 'denied', message: 'You may not read retail_sales.' } } })
    expect(markup.match(/role="alert"/g)!.length).toBe(1)
    expect(text(markup)).toContain('denied : You may not read retail_sales.')
    expect(text(markup)).toContain('$3.30M')
  })

  it('a widget with no result is loading, the document-only ones still draw, and the parameters reach text and controls', () => {
    const markup = frame({ width: 1366, height: 768 }, { results: {}, loading: new Set(['net', 'margin', 'by_region', 'top_products']) })
    expect(markup.match(/role="status"/g)!.length).toBe(4)
    expect(text(markup)).toContain('Showing West (no region chosen means all regions).')
    expect(markup).toContain('data-testid="vz-control-regions"')
  })

  it('a canvas or flow layout says its engine is not here, and draws nothing else', () => {
    for (const name of ['wall', 'print']) {
      const markup = frame({ width: 1366, height: 768 }, { layout: layout(name) })
      expect(markup).toContain('data-testid="vz-engine-missing"')
      expect(markup).toContain(`data-layout="${name}"`)
      expect(markup).not.toContain('vz-widget-net')
    }
  })

  it('a layout\'s widget options reach the widget (a pinned density)', () => {
    const pinned = { ...layout('desk'), widget_options: { net: { density: 'compact' as const } } }
    const markup = frame({ width: 1920, height: 1080 }, { layout: pinned })
    expect(markup).toMatch(/vz-w-metric vz-d-s"[^>]*data-density="s"/)
  })

  it('snapshots: the desk at three sizes (light), dark, and the stack', async () => {
    await expect(frame({ width: 1920, height: 1080 })).toMatchFileSnapshot('./__snapshots__/dashboard-1920x1080.html')
    await expect(frame({ width: 1366, height: 768 }, { onMaximize: () => {} })).toMatchFileSnapshot('./__snapshots__/dashboard-1366x768.html')
    await expect(frame({ width: 1366, height: 768 }, { theme: 'dark' })).toMatchFileSnapshot('./__snapshots__/dashboard-1366x768-dark.html')
    await expect(frame({ width: 390, height: 844 })).toMatchFileSnapshot('./__snapshots__/dashboard-390x844-stack.html')
    await expect(frame({ width: 1366, height: 768 }, { maximized: 'top_products', onMaximize: () => {} })).toMatchFileSnapshot('./__snapshots__/dashboard-maximized.html')
  })
})
