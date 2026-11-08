import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { validateText } from '../client/text'
import { sampleBundle } from '../testing/sampleBundle'
import type { Box, CanvasLayoutDoc, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { DashboardFrame } from './DashboardFrame'

const bundle = sampleBundle()
const dashboard = validateText(bundle.documents['dashboard/retail-sales-today']!).document as ViewsDoc
const results: Record<string, RenderedWidget> = { ...bundle.results!['dashboard/retail-sales-today']!, note: { type: 'text' } }
const layout = (name: string) => dashboard.layouts.find((l) => l.name === name)!
const frame = (box: Box, name = 'wall', over: Partial<Parameters<typeof DashboardFrame>[0]> = {}) =>
  renderToStaticMarkup(<DashboardFrame document={dashboard} layout={layout(name)} box={box} theme="light" results={results} parameters={{ region: ['West'] }} {...over} />)
const text = (markup: string) => markup.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim()

describe('DashboardFrame, the canvas layout as markup', () => {
  it('the wall in 1280 x 720: one canvas at its own size, scaled by a transform, tiles at their canvas pixels, nothing scrolls', () => {
    const markup = frame({ width: 1280, height: 720 })
    expect(markup).toContain('data-layout="wall" data-layout-type="canvas" data-mode="canvas"')
    expect(markup).toContain('data-scale="0.6667"')
    expect(markup).not.toContain('data-scrolls')
    expect(markup).toContain('class="vz-canvas-sizer" data-testid="vz-canvas-sizer" style="width:1280px;height:720px"')
    expect(markup).toContain('style="left:0;top:0;width:1920px;height:1080px;transform:scale(0.6666666666666666)"')
    expect(markup).toContain('data-testid="vz-tile-by_region" data-layer="0" style="left:40px;top:340px;width:1100px;height:700px;z-index:0"')
    for (const id of ['regions', 'net', 'margin', 'note', 'by_region', 'top_products']) expect(markup).toContain(`data-testid="vz-widget-${id}"`)
    expect(markup).not.toContain('vz-engine-missing')
  })

  it('a chart on the canvas is drawn for its canvas box, not the screen: the same SVG at every scale', () => {
    // the chart is given the box of its tile on the canvas (minus the tile's chrome); the SVG itself is drawn by the browser (vega-embed, renderer svg) and is then scaled by the transform as vectors
    const chart = (markup: string) => markup.match(/data-testid="vz-chart-by_region" style="[^"]*"/)![0]
    expect(chart(frame({ width: 1280, height: 720 }))).toBe('data-testid="vz-chart-by_region" style="width:1082px;height:654px"')
    for (const [w, h] of [[3000, 2000], [800, 450], [1920, 1080]]) expect(chart(frame({ width: w!, height: h! }))).toBe(chart(frame({ width: 1280, height: 720 })))
  })

  it('a box smaller than min_scale allows holds min_scale and the container scrolls', () => {
    const markup = frame({ width: 800, height: 450 })
    expect(markup).toContain('data-scale="0.5000"')
    expect(markup).toContain('data-scrolls=""')
    expect(markup).toContain('style="width:960px;height:540px"')
  })

  it('overflow: scroll is scale 1 with scrollbars', () => {
    const scroll: CanvasLayoutDoc = { ...(layout('wall') as CanvasLayoutDoc), overflow: 'scroll' }
    const markup = frame({ width: 1280, height: 720 }, 'wall', { layout: scroll })
    expect(markup).toContain('data-overflow="scroll"')
    expect(markup).toContain('data-scale="1.0000"')
    expect(markup).toContain('data-scrolls=""')
    expect(markup).toContain('style="width:1920px;height:1080px"')
  })

  it('layers: a higher layer is drawn later and above (z-index)', () => {
    const layered: CanvasLayoutDoc = { ...(layout('wall') as CanvasLayoutDoc), tiles: [{ widget: 'net', x: 0, y: 0, w: 440, h: 200, layer: 1 }, { widget: 'margin', x: 0, y: 0, w: 440, h: 200 }] }
    const markup = frame({ width: 1280, height: 720 }, 'wall', { layout: layered })
    expect(markup.indexOf('vz-tile-margin')).toBeLessThan(markup.indexOf('vz-tile-net'))
    expect(markup).toContain('data-testid="vz-tile-net" data-layer="1" style="left:0;top:0;width:440px;height:200px;z-index:1"')
  })

  it('maximized: the tile leaves the canvas and fills the container, whole', () => {
    const markup = frame({ width: 1280, height: 720 }, 'wall', { maximized: 'top_products', onMaximize: () => {} })
    expect(markup).toContain('data-mode="maximized"')
    expect(markup).not.toContain('vz-canvas"')
    expect(markup.match(/<tr>/g)!.length - 1).toBe(24)
    expect(text(markup)).not.toContain('of 24 rows')
  })

  it('the phone canvas draws the five widgets it places and not the sixth', () => {
    const markup = frame({ width: 390, height: 844 }, 'phone')
    expect(markup).toContain('data-layout="phone"')
    expect(markup).toContain('data-scale="0.9378"')
    expect(markup).not.toContain('data-testid="vz-widget-note"')
    expect(markup).toContain('data-testid="vz-widget-top_products"')
  })

  it('a flow layout still says its engine is not here', () => {
    expect(frame({ width: 1366, height: 768 }, 'print')).toContain('data-testid="vz-engine-missing"')
  })

  it('snapshots: the wall at three sizes (light), dark, scroll mode, and the phone', async () => {
    await expect(frame({ width: 1920, height: 1080 })).toMatchFileSnapshot('./__snapshots__/canvas-wall-1920x1080.html')
    await expect(frame({ width: 1280, height: 720 })).toMatchFileSnapshot('./__snapshots__/canvas-wall-1280x720.html')
    await expect(frame({ width: 1000, height: 1000 })).toMatchFileSnapshot('./__snapshots__/canvas-wall-1000x1000-letterbox.html')
    await expect(frame({ width: 800, height: 450 }, 'wall', { theme: 'dark' })).toMatchFileSnapshot('./__snapshots__/canvas-wall-800x450-dark-min-scale.html')
    await expect(frame({ width: 1280, height: 720 }, 'wall', { layout: { ...(layout('wall') as CanvasLayoutDoc), overflow: 'scroll' } })).toMatchFileSnapshot('./__snapshots__/canvas-wall-1280x720-scroll.html')
    await expect(frame({ width: 390, height: 844 }, 'phone')).toMatchFileSnapshot('./__snapshots__/canvas-phone-390x844.html')
  })
})
