import { describe, expect, it } from 'vitest'
import { sampleBundle } from '../testing/sampleBundle'
import { validateText } from '../client/text'
import type { CanvasLayoutDoc, ReflowLayoutDoc, ViewsDoc } from '../views/model'
import { dragTile, pointerToUnits, rectChanges, testSelector, tileMarks, type Rect } from './geometry'

const doc = validateText(sampleBundle().documents['dashboard/retail-sales-today']!, { kind: 'dashboard' }).document as ViewsDoc
const desk = doc.layouts[0] as ReflowLayoutDoc
const wall = doc.layouts[1] as CanvasLayoutDoc
const phone = doc.layouts[2] as CanvasLayoutDoc
const rectOf = (l: { tiles: Array<Rect & { widget: string }> }, w: string): Rect => { const t = l.tiles.find((x) => x.widget === w)!; return { x: t.x, y: t.y, w: t.w, h: t.h } }

describe('dragging a reflow tile', () => {
  it('moves by whole cells and keeps its size: net (0,1,3,2) one cell right is refused (margin is there), and a free place works', () => {
    const net = rectOf(desk, 'net')
    expect(dragTile(desk, 'net', net, 'move', 1, 0).refused).toContain('`margin`')
    // a layout with room: the table is 5 wide at x 7 on a 12 grid
    const table = rectOf(desk, 'top_products')
    const moved = dragTile({ ...desk, tiles: desk.tiles.filter((t) => t.widget !== 'by_region') }, 'top_products', table, 'move', -2.4, 0)
    expect(moved.rect).toEqual({ x: 5, y: 3, w: 5, h: 5 })
    expect(moved.refused).toBeUndefined()
  })

  it('rounds the distance to cells, never leaves the grid, and never moves a tile above row 0', () => {
    const alone = { ...desk, tiles: [{ widget: 'net', x: 2, y: 1, w: 3, h: 2 }] }
    const start = rectOf(alone, 'net')
    expect(dragTile(alone, 'net', start, 'move', 0.49, 0.49).rect).toEqual(start)
    expect(dragTile(alone, 'net', start, 'move', 0.5, 0.6).rect).toEqual({ x: 3, y: 2, w: 3, h: 2 })
    expect(dragTile(alone, 'net', start, 'move', 100, 0).rect.x).toBe(9)
    expect(dragTile(alone, 'net', start, 'move', -100, -100).rect).toEqual({ x: 0, y: 0, w: 3, h: 2 })
    // a grid with `rows` holds the tile inside them; one without grows downward
    expect(dragTile({ ...alone, grid: { columns: 12 } }, 'net', start, 'move', 0, 100).rect.y).toBe(1 + 100)
    expect(dragTile({ ...alone, grid: { columns: 12, rows: 8 } }, 'net', start, 'move', 0, 100).rect.y).toBe(6)
  })

  it('resizes from each edge by cells, at least one cell, inside the grid, and refuses an overlap naming the neighbour', () => {
    const alone = { ...desk, tiles: [{ widget: 'net', x: 2, y: 1, w: 3, h: 2 }, { widget: 'other', x: 8, y: 1, w: 2, h: 2 }] }
    const s = rectOf(alone, 'net')
    expect(dragTile(alone, 'net', s, 'e', 2, 0).rect).toEqual({ x: 2, y: 1, w: 5, h: 2 })
    expect(dragTile(alone, 'net', s, 'e', 4, 0).refused).toContain('`other`')
    expect(dragTile(alone, 'net', s, 'w', -5, 0).rect).toEqual({ x: 0, y: 1, w: 5, h: 2 })
    expect(dragTile(alone, 'net', s, 'e', -10, 0).rect).toEqual({ x: 2, y: 1, w: 1, h: 2 })
    expect(dragTile(alone, 'net', s, 'n', 0, 10).rect).toEqual({ x: 2, y: 2, w: 3, h: 1 })
    expect(dragTile(alone, 'net', s, 'se', 1, 2).rect).toEqual({ x: 2, y: 1, w: 4, h: 4 })
    expect(dragTile(alone, 'net', s, 'nw', -1, -1).rect).toEqual({ x: 1, y: 0, w: 4, h: 3 })
    expect(dragTile({ ...alone, grid: { columns: 12 } }, 'net', s, 'e', 50, 0).refused).toContain('`other`')
  })

  it('a refused drag gives back the starting rectangle, so the last good one is never lost', () => {
    const s = rectOf(desk, 'net')
    const r = dragTile(desk, 'net', s, 'move', 1, 0)
    expect(r.rect).toEqual(s)
    expect(rectChanges(s, r.rect)).toEqual({})
  })

  it('the answer depends on the start and the total distance, not on the moves in between', () => {
    const alone = { ...desk, tiles: [{ widget: 'net', x: 2, y: 1, w: 3, h: 2 }] }
    const s = rectOf(alone, 'net')
    const direct = dragTile(alone, 'net', s, 'se', 2.2, 1.4).rect
    let last = s
    for (const f of [0.1, 0.5, 0.9, 1]) last = dragTile(alone, 'net', s, 'se', 2.2 * f, 1.4 * f).rect
    expect(last).toEqual(direct)
  })
})

describe('dragging a canvas tile', () => {
  it('snaps to 10 px, keeps its size, stays inside the canvas', () => {
    const s = rectOf(wall, 'net')   // 40, 120, 440 x 200
    expect(dragTile(wall, 'net', s, 'move', 7, 4).rect).toEqual({ x: 50, y: 120, w: 440, h: 200 })
    expect(dragTile(wall, 'net', s, 'move', 4, 6).rect).toEqual({ x: 40, y: 130, w: 440, h: 200 })
    expect(dragTile(wall, 'net', s, 'move', -500, -500).rect).toEqual({ x: 0, y: 0, w: 440, h: 200 })
    expect(dragTile(wall, 'net', s, 'move', 5000, 5000).rect).toEqual({ x: 1480, y: 880, w: 440, h: 200 })
  })

  it('resizes by edge, snapped, never below one step, inside the canvas', () => {
    const s = rectOf(phone, 'net')   // 10, 80, 180 x 100
    expect(dragTile(phone, 'net', s, 'e', 24, 0).overlaps).toEqual(['margin'])
    expect(dragTile(phone, 'net', s, 'w', -13, 0).rect).toEqual({ x: 0, y: 80, w: 190, h: 100 })
    expect(dragTile(phone, 'net', s, 's', 0, -1000).rect.h).toBe(10)
    expect(dragTile(phone, 'by_region', rectOf(phone, 'by_region'), 'se', 1000, 1000).rect).toEqual({ x: 10, y: 190, w: 380, h: 710 })
  })

  it('reports an overlap on its layer instead of refusing, and ignores tiles on other layers', () => {
    const s = rectOf(phone, 'net')
    const over = dragTile(phone, 'net', s, 'move', 190, 0)
    expect(over.overlaps).toEqual(['margin'])
    expect(over.refused).toBeUndefined()
    const layered = { ...phone, tiles: phone.tiles.map((t) => (t.widget === 'margin' ? { ...t, layer: 1 } : t)) }
    expect(dragTile(layered, 'net', s, 'move', 190, 0).overlaps).toEqual([])
  })

  it('snaps mm canvases to 1 and honours a custom snap, or none', () => {
    const mm: CanvasLayoutDoc = { name: 'a4', type: 'canvas', canvas: { width: 297, height: 210, unit: 'mm' }, tiles: [{ widget: 'a', x: 10, y: 10, w: 100, h: 50 }] }
    const s = rectOf(mm, 'a')
    expect(dragTile(mm, 'a', s, 'move', 3.4, 0).rect.x).toBe(13)
    expect(dragTile(mm, 'a', s, 'move', 3.4, 0, { snap: 5 }).rect.x).toBe(15)
    expect(dragTile(mm, 'a', s, 'move', 3.4, 0, { snap: 0 }).rect.x).toBe(13.4)
  })

  it('turns pointer distance into units', () => {
    expect(pointerToUnits(120, 60)).toBe(2)
    expect(pointerToUnits(50, 0.5)).toBe(100)
    expect(pointerToUnits(5, 0)).toBe(0)
  })
})

describe('marking tiles below their minimum', () => {
  it('a canvas tile smaller than its type needs at scale 1 is marked (the loader refuses it), the others are not', () => {
    const small: CanvasLayoutDoc = { ...phone, tiles: phone.tiles.map((t) => (t.widget === 'by_region' ? { ...t, w: 200, h: 100 } : t)) }
    const marks = tileMarks(small, doc.widgets)
    expect(marks.filter((m) => m.belowMinimum).map((m) => m.widget)).toEqual(['by_region'])
    expect(marks.find((m) => m.widget === 'by_region')!.minimum).toEqual({ width: 280, height: 180 })
    expect(tileMarks(phone, doc.widgets).some((m) => m.belowMinimum)).toBe(false)
    expect(tileMarks(wall, doc.widgets).some((m) => m.belowMinimum)).toBe(false)
  })

  it('a tile\'s own min raises the bar', () => {
    const raised: CanvasLayoutDoc = { ...phone, tiles: phone.tiles.map((t) => (t.widget === 'net' ? { ...t, min: { w: 200 } } : t)) }
    expect(tileMarks(raised, doc.widgets).filter((m) => m.belowMinimum).map((m) => m.widget)).toEqual(['net'])
  })

  it('a reflow tile is judged in the reference window: the sample desk fits, a one-cell chart does not', () => {
    expect(tileMarks(desk, doc.widgets).some((m) => m.belowMinimum)).toBe(false)
    const tight: ReflowLayoutDoc = { ...desk, tiles: desk.tiles.map((t) => (t.widget === 'by_region' ? { ...t, w: 1 } : t)) }
    expect(tileMarks(tight, doc.widgets).filter((m) => m.belowMinimum).map((m) => m.widget)).toContain('by_region')
    expect(tileMarks(doc.layouts[3]!, doc.widgets)).toEqual([])
  })
})

describe('"test at W x H"', () => {
  it('gives selectLayout\'s answer for ten sizes of the sample', () => {
    const at = (width: number, height: number, medium?: 'screen' | 'print' | 'export') => testSelector(doc, { width, height, medium })
    expect(at(1920, 1080)).toEqual({ ok: true, layout: 'desk', choice: 'default' })
    expect(at(2560, 1440)).toEqual({ ok: true, layout: 'wall', choice: 'matched' })
    expect(at(2400, 1000)).toEqual({ ok: true, layout: 'wall', choice: 'matched' })
    expect(at(2399, 1000)).toEqual({ ok: true, layout: 'desk', choice: 'default' })
    expect(at(390, 844)).toEqual({ ok: true, layout: 'phone', choice: 'matched' })
    expect(at(600, 900)).toEqual({ ok: true, layout: 'phone', choice: 'matched' })
    expect(at(601, 900)).toEqual({ ok: true, layout: 'desk', choice: 'default' })
    expect(at(844, 390)).toEqual({ ok: true, layout: 'desk', choice: 'default' })
    expect(at(1366, 768, 'print')).toEqual({ ok: true, layout: 'print', choice: 'matched' })
    expect(at(1024, 768, 'export')).toEqual({ ok: true, layout: 'print', choice: 'matched' })
    expect(testSelector(doc, { layout: 'nope' })).toMatchObject({ ok: false, refusal: expect.stringContaining('`desk`') })
  })
})
