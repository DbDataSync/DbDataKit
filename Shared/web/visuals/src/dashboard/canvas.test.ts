import { describe, expect, it } from 'vitest'
import { validateText } from '../client/text'
import { sampleBundle } from '../testing/sampleBundle'
import type { CanvasLayoutDoc, ViewsDoc } from '../views/model'
import { densityClass } from '../widgets/density'
import { layoutCanvas } from './canvas'

const dashboard = validateText(sampleBundle().documents['dashboard/retail-sales-today']!).document as ViewsDoc
const layoutOf = (name: string) => dashboard.layouts.find((l) => l.name === name) as CanvasLayoutDoc
const wall = layoutOf('wall')
const phone = layoutOf('phone')
const at = (width: number, height: number, layout: CanvasLayoutDoc = wall) => layoutCanvas(layout, dashboard.widgets, { width, height })
const withOptions = (over: Partial<CanvasLayoutDoc>): CanvasLayoutDoc => ({ ...wall, ...over })

describe('layoutCanvas, scale mode', () => {
  it('a 1920 x 1080 canvas in 1280 x 720 scales 2/3 and fills the box, centred (offset 0)', () => {
    const r = at(1280, 720)
    expect(r.mode).toBe('scale')
    expect(r.scale).toBeCloseTo(2 / 3, 10)
    expect(r.scaled.width).toBeCloseTo(1280, 6)
    expect(r.scaled.height).toBeCloseTo(720, 6)
    expect(r.offset).toEqual({ x: 0, y: 0 })
    expect(r.scrolls).toBe(false)
    expect(r.content).toEqual({ width: 1280, height: 720 })
    expect(r.atMinimum || r.atMaximum).toBe(false)
  })

  it('letterboxes in a box of another shape: uniform scale, centred on the loose axis, no scroll', () => {
    const r = at(1000, 1000)
    expect(r.scale).toBeCloseTo(1000 / 1920, 10)
    expect(r.scaled.width).toBeCloseTo(1000, 6)
    expect(r.scaled.height).toBeCloseTo(562.5, 6)
    expect(r.offset).toEqual({ x: 0, y: 219 })
    expect(r.scrolls).toBe(false)
    const wide = at(2400, 800)
    expect(wide.scale).toBeCloseTo(800 / 1080, 10)
    expect(wide.offset.y).toBe(0)
    expect(wide.offset.x).toBe(Math.round((2400 - 1920 * (800 / 1080)) / 2))
  })

  it('never scales above max_scale (default 4): a larger box letterboxes the picture', () => {
    const r = at(10000, 6000)
    expect(r.scale).toBe(4)
    expect(r.atMaximum).toBe(true)
    expect(r.offset).toEqual({ x: Math.round((10000 - 7680) / 2), y: Math.round((6000 - 4320) / 2) })
    expect(at(4000, 3000, withOptions({ max_scale: 1.5 })).scale).toBe(1.5)
  })

  it('below min_scale (default 0.5) it holds min_scale and reports the scrolled size', () => {
    const r = at(800, 450)
    expect(r.fit).toBeCloseTo(800 / 1920, 10)
    expect(r.scale).toBe(0.5)
    expect(r.atMinimum).toBe(true)
    expect(r.scaled).toEqual({ width: 960, height: 540 })
    expect(r.content).toEqual({ width: 960, height: 540 })
    expect(r.scrolls).toBe(true)
    expect(r.offset).toEqual({ x: 0, y: 0 })
    // a box wide enough on one axis only: scrolls on the other, centred on the first
    const tall = at(1200, 400)
    expect(tall.scale).toBe(0.5)
    expect(tall.content).toEqual({ width: 1200, height: 540 })
    expect(tall.offset).toEqual({ x: 120, y: 0 })
    expect(tall.scrolls).toBe(true)
    expect(at(1200, 400, withOptions({ min_scale: 0.25 })).scale).toBeCloseTo(400 / 1080, 10)
  })

  it('the phone canvas (390 x 900) in 390 x 844 scales just under 1 and does not scroll; in 320 x 568 it holds 0.5 only if it must', () => {
    const r = at(390, 844, phone)
    expect(r.scale).toBeCloseTo(844 / 900, 10)
    expect(r.scrolls).toBe(false)
    expect(r.offset.x).toBe(Math.round((390 - 390 * (844 / 900)) / 2))
    const small = at(320, 568, phone)
    expect(small.scale).toBeCloseTo(568 / 900, 10)
    expect(small.scrolls).toBe(false)
    const tiny = at(320, 400, phone)
    expect(tiny.scale).toBe(0.5)
    expect(tiny.scrolls).toBe(true)
  })

  it('whole pixels for the box and the offset, whatever the box', () => {
    const r = at(1279.6, 719.4)
    expect(r.box).toEqual({ width: 1280, height: 719 })
    expect(Number.isInteger(r.offset.x) && Number.isInteger(r.offset.y)).toBe(true)
  })
})

describe('layoutCanvas, scroll mode and units', () => {
  const scroll = withOptions({ overflow: 'scroll' })
  it('overflow: scroll is scale 1 whatever the box, with scrollbars when the picture is larger', () => {
    const big = at(1280, 720, scroll)
    expect(big.mode).toBe('scroll')
    expect(big.scale).toBe(1)
    expect(big.scaled).toEqual({ width: 1920, height: 1080 })
    expect(big.content).toEqual({ width: 1920, height: 1080 })
    expect(big.scrolls).toBe(true)
    expect(big.offset).toEqual({ x: 0, y: 0 })
    const small = at(2560, 1440, scroll)
    expect(small.scale).toBe(1)
    expect(small.scrolls).toBe(false)
    expect(small.offset).toEqual({ x: 320, y: 180 })
    expect(small.content).toEqual({ width: 2560, height: 1440 })
  })

  it('mm and in are converted at 96 px per inch, for the canvas and the tiles', () => {
    const a4: CanvasLayoutDoc = { ...wall, canvas: { width: 297, height: 210, unit: 'mm' }, overflow: 'scroll', tiles: [{ widget: 'net', x: 10, y: 10, w: 100, h: 50 }] }
    const r = at(1600, 1200, a4)
    expect(r.canvas.width).toBeCloseTo(1122.52, 2)
    expect(r.canvas.height).toBeCloseTo(793.7, 2)
    expect(r.tiles[0]).toMatchObject({ x: 37.8, y: 37.8, width: 377.95, height: 188.98 })
    const inches: CanvasLayoutDoc = { ...a4, canvas: { width: 10, height: 5, unit: 'in' }, tiles: [{ widget: 'net', x: 1, y: 1, w: 2, h: 1 }] }
    expect(at(1600, 1200, inches).canvas).toEqual({ width: 960, height: 480 })
    expect(at(1600, 1200, inches).tiles[0]).toMatchObject({ x: 96, width: 192, height: 96 })
  })
})

describe('layoutCanvas, the tiles', () => {
  it('are placed in canvas pixels, unchanged by the scale', () => {
    const tiles = (r: ReturnType<typeof at>) => r.tiles.map((t) => [t.widget, t.x, t.y, t.width, t.height, t.layer])
    const base = tiles(at(1920, 1080))
    expect(base[0]).toEqual(['regions', 40, 30, 1840, 70, 0])
    expect(tiles(at(1280, 720))).toEqual(base)
    expect(tiles(at(800, 450))).toEqual(base)
    expect(tiles(at(5000, 3000))).toEqual(base)
  })

  it('are in ascending layer order, document order within a layer', () => {
    const layers = withOptions({ tiles: [{ widget: 'net', x: 0, y: 0, w: 300, h: 200, layer: 2 }, { widget: 'margin', x: 400, y: 0, w: 300, h: 200 }, { widget: 'note', x: 0, y: 0, w: 300, h: 200, layer: 1 }, { widget: 'regions', x: 800, y: 0, w: 400, h: 100, layer: 1 }] })
    expect(at(1920, 1080, layers).tiles.map((t) => `${t.widget}:${t.layer}`)).toEqual(['margin:0', 'note:1', 'regions:1', 'net:2'])
  })

  it('density is the same at every scale: it comes from the canvas box (a fake box included)', () => {
    const densities = (r: ReturnType<typeof at>) => Object.fromEntries(r.tiles.map((t) => [t.widget, t.density]))
    const base = densities(at(1920, 1080))
    for (const [w, h] of [[1280, 720], [800, 450], [3840, 2160], [10000, 6000], [390, 844]]) expect(densities(at(w!, h!)), `${w} x ${h}`).toEqual(base)
    for (const t of at(1920, 1080).tiles) expect(t.density).toBe(densityClass(t.type, { width: t.width, height: t.height }))
    // a pinned density wins, as in a reflow layout
    const pinned = at(1920, 1080, withOptions({ widget_options: { net: { density: 'compact' } } }))
    expect(pinned.tiles.find((t) => t.widget === 'net')!.density).toBe('s')
  })

  it('lists the tiles below their minimum at scale 1, and none in the sample layouts', () => {
    expect(at(1920, 1080).violations).toEqual([])
    expect(at(390, 844, phone).violations).toEqual([])
    const small = at(1920, 1080, withOptions({ tiles: [{ widget: 'by_region', x: 0, y: 0, w: 100, h: 60 }, { widget: 'net', x: 200, y: 0, w: 300, h: 200 }] }))
    expect(small.violations.map((v) => v.widget)).toEqual(['by_region'])
    expect(small.violations[0]).toMatchObject({ width: 100, height: 60 })
    // a tile's own min raises the type's
    const raised = at(1920, 1080, withOptions({ tiles: [{ widget: 'net', x: 0, y: 0, w: 300, h: 200, min: { w: 400 } }] }))
    expect(raised.violations).toHaveLength(1)
    expect(raised.tiles[0]!.min.width).toBe(400)
  })

  it('a sweep of boxes: the picture is always inside its scrolled area and the offset is never negative', () => {
    for (let w = 200; w <= 6000; w += 173)
      for (let h = 200; h <= 4000; h += 157)
        for (const layout of [wall, phone, withOptions({ overflow: 'scroll' })]) {
          const r = at(w, h, layout)
          expect(r.offset.x).toBeGreaterThanOrEqual(0)
          expect(r.offset.y).toBeGreaterThanOrEqual(0)
          expect(r.offset.x + r.scaled.width).toBeLessThanOrEqual(r.content.width + 0.5)
          expect(r.offset.y + r.scaled.height).toBeLessThanOrEqual(r.content.height + 0.5)
          expect(r.scrolls).toBe(r.content.width > r.box.width || r.content.height > r.box.height)
        }
  })
})
