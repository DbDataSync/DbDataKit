import { describe, expect, it } from 'vitest'
import { validateText } from '../client/text'
import { sampleBundle } from '../testing/sampleBundle'
import type { ReflowLayoutDoc, ViewsDoc } from '../views/model'
import { checkReflowSizes, layoutReflow, sizesFor, TILE_GUTTER, type ReflowResult } from './reflow'

const dashboard = validateText(sampleBundle().documents['dashboard/retail-sales-today']!).document as ViewsDoc
const desk = dashboard.layouts.find((l) => l.name === 'desk') as ReflowLayoutDoc
const at = (width: number, height: number, layout: Pick<ReflowLayoutDoc, 'grid' | 'tiles' | 'widget_options'> = desk, widgets = dashboard.widgets) => layoutReflow(layout, widgets, { width, height })

/** Whether the tiles of a grid result partition the box exactly: no overlap, no gap, nothing outside. */
function expectExactFill(result: ReflowResult, label: string) {
  const { width, height } = result.box
  let area = 0
  for (const t of result.tiles) {
    expect(t.x >= 0 && t.y >= 0 && t.x + t.width <= width && t.y + t.height <= height, `${label}: ${t.widget} is inside the box`).toBe(true)
    expect(Number.isInteger(t.x) && Number.isInteger(t.y) && Number.isInteger(t.width) && Number.isInteger(t.height), `${label}: ${t.widget} has whole pixels`).toBe(true)
    area += t.width * t.height
  }
  for (const [i, a] of result.tiles.entries())
    for (const b of result.tiles.slice(i + 1))
      expect(a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height, `${label}: ${a.widget} and ${b.widget} overlap`).toBe(false)
  expect(area, `${label}: the tiles fill the box`).toBe(width * height)
}

describe('layoutReflow, the sample dashboard\'s desk layout', () => {
  it.each([[1920, 1080], [1366, 768]])('at %i x %i no tile is below its minimum and the tiles fill the box exactly', (w, h) => {
    const result = at(w, h)
    expect(result.mode).toBe('grid')
    expect(result.violations).toEqual([])
    expect(result.scrolls).toBe(false)
    expect(result.contentHeight).toBe(h)
    for (const t of result.tiles) {
      expect(t.width, `${t.widget} width`).toBeGreaterThanOrEqual(t.min.width)
      expect(t.height, `${t.widget} height`).toBeGreaterThanOrEqual(t.min.height)
    }
    expectExactFill(result, `${w} x ${h}`)
  })

  it('fills the box exactly at every size from 800 x 600 up, whole pixels shared by neighbours (a sweep, no timing)', () => {
    let grids = 0
    for (let w = 800; w <= 2600; w += 37)
      for (let h = 600; h <= 1500; h += 41) {
        const result = at(w, h)
        if (result.mode === 'grid') { grids++; expectExactFill(result, `${w} x ${h}`) }
      }
    expect(grids).toBeGreaterThan(1000)
  })

  it('places a tile by its cells: the control spans the row, the chart seven columns by five rows', () => {
    const result = at(1920, 1080)
    const by = Object.fromEntries(result.tiles.map((t) => [t.widget, t]))
    expect(by.regions).toMatchObject({ x: 0, y: 0, width: 1920, height: 135, cell: { column: 1, row: 1, columns: 12, rows: 1 } })
    expect(by.by_region).toMatchObject({ x: 0, y: 405, width: 1120, height: 675, cell: { column: 1, row: 4, columns: 7, rows: 5 } })
    expect(by.top_products).toMatchObject({ x: 1120, y: 405, width: 800, height: 675 })
    expect(result.columns).toBe(12)
    expect(result.rows).toBe(8)
  })

  it('takes the rows of the grid from the tiles when the layout names none', () => {
    const result = at(1200, 800, { grid: { columns: 2 }, tiles: [{ widget: 'net', x: 0, y: 0, w: 1, h: 2 }, { widget: 'margin', x: 1, y: 0, w: 1, h: 1 }, { widget: 'note', x: 1, y: 1, w: 1, h: 1 }] })
    expect(result.rows).toBe(2)
    expectExactFill(result, 'derived rows')
  })

  it('changes from the grid to the compact stack exactly where a tile gets below its minimum', () => {
    // the table is five of twelve columns and may not be narrower than 320: seven columns end at round(7 w / 12)
    const wide = at(767, 800)
    expect(wide.mode).toBe('grid')
    expect(wide.tiles.find((t) => t.widget === 'top_products')!.width).toBe(320)
    const narrow = at(766, 800)
    expect(narrow.mode).toBe('stack')
    expect(narrow.violations.map((v) => v.widget)).toEqual(['top_products'])
    expect(narrow.violations[0]).toMatchObject({ width: 319, height: 500, min: { width: 320, height: 200 } })
  })

  it('a box too small gives the compact stack: one column in document order at the minimum heights, and the page scrolls', () => {
    const result = at(390, 844)
    expect(result.mode).toBe('stack')
    expect(result.tiles.map((t) => t.widget)).toEqual(desk.tiles.map((t) => t.widget))
    expect(result.tiles.every((t) => t.x === 0 && t.width === 390 && t.cell === null)).toBe(true)
    expect(result.tiles.map((t) => t.height)).toEqual([56, 88, 88, 56, 180, 200])
    let y = 0
    for (const t of result.tiles) { expect(t.y).toBe(y); y += t.height }
    expect(result.contentHeight).toBe(668)
    expect(result.scrolls).toBe(false)
    expect(at(390, 500).scrolls).toBe(true)
    expect(result.violations.length).toBeGreaterThan(0)
  })

  it('a tile may raise its minimum, never lower it', () => {
    const tiles = [{ widget: 'net', x: 0, y: 0, w: 1, h: 1, min: { w: 600 } }, { widget: 'margin', x: 1, y: 0, w: 1, h: 1, min: { w: 10, h: 10 } }]
    const result = at(1000, 400, { grid: { columns: 2, rows: 1 }, tiles })
    expect(result.mode).toBe('stack')
    expect(result.violations.map((v) => v.widget)).toEqual(['net'])
    expect(result.tiles[0]!.min).toEqual({ width: 600, height: 88 })
    expect(result.tiles[1]!.min).toEqual({ width: 160, height: 88 })
  })

  it('tells each tile its density from the box less the gutter, and a layout can pin it', () => {
    const result = at(1920, 1080)
    const net = result.tiles.find((t) => t.widget === 'net')!
    expect(net.density).toBe('l')
    expect(net.width).toBe(480)
    const pinned = at(1920, 1080, { ...desk, widget_options: { net: { density: 'compact' } } }).tiles.find((t) => t.widget === 'net')!
    expect(pinned.density).toBe('s')
    expect(at(1366, 768).tiles.find((t) => t.widget === 'net')!.density).toBe('l')
    expect(TILE_GUTTER).toBe(8)
  })

  it('goldens: the placed tiles at six sizes', async () => {
    const sizes: Array<[number, number]> = [[1920, 1080], [1366, 768], [1280, 720], [1024, 768], [800, 600], [390, 844]]
    const golden = Object.fromEntries(sizes.map(([w, h]) => {
      const r = at(w, h)
      return [`${w}x${h}`, { mode: r.mode, violations: r.violations.map((v) => v.widget), contentHeight: r.contentHeight, tiles: r.tiles.map((t) => `${t.widget} ${t.x},${t.y} ${t.width}x${t.height} ${t.density}${t.cell ? ` cell ${t.cell.column},${t.cell.row} span ${t.cell.columns}x${t.cell.rows}` : ''}`) }]
    }))
    await expect(`${JSON.stringify(golden, null, 1)}\n`).toMatchFileSnapshot('./__snapshots__/reflow-goldens.json')
  })
})

describe('the size check of the validator', () => {
  it('the sample dashboard has no tile below its minimum at the sizes its layouts are for', () => {
    expect(checkReflowSizes(dashboard)).toEqual([])
  })

  it('knows the sizes a selector allows', () => {
    expect(sizesFor(undefined)).toEqual([{ width: 1920, height: 1080 }, { width: 1366, height: 768 }])
    expect(sizesFor({ width: { max: 600 }, orientation: 'portrait' })).toEqual([{ width: 600, height: 338 }])
    expect(sizesFor({ width: { min: 1000, max: 1400 } })).toEqual([{ width: 1000, height: 563 }, { width: 1400, height: 788 }])
    expect(sizesFor({ height: { min: 600 } })).toEqual([{ width: 1067, height: 600 }])
    expect(sizesFor({ width: { min: 800 }, height: { max: 500 } })).toEqual([{ width: 800, height: 500 }])
    expect(sizesFor({ orientation: 'portrait' })).toEqual([{ width: 390, height: 844 }])
    expect(sizesFor({ medium: 'screen' })).toEqual(sizesFor(undefined))
  })

  it('warns once per tile, with the sizes it is too small at, as DBD-VIEW-028 at the tile\'s path', () => {
    const narrow: ViewsDoc = { ...dashboard, layouts: [{ ...desk, name: 'small', default: true, when: { width: { min: 500, max: 700 } } }] }
    const issues = checkReflowSizes(narrow)
    expect(issues.length).toBeGreaterThan(0)
    expect(issues.every((i) => i.code === 'DBD-VIEW-028' && i.severity === 'warning')).toBe(true)
    const table = issues.find((i) => i.path === 'layouts[0].tiles[5]')!
    expect(table.message).toContain('`top_products`')
    expect(table.message).toContain('500 x 281, 700 x 394')
    expect(table.message).toContain('the compact stack')
    expect(new Set(issues.map((i) => i.path)).size).toBe(issues.length)
  })

  it('checks reflow layouts only', () => {
    expect(checkReflowSizes({ widgets: dashboard.widgets, layouts: dashboard.layouts.filter((l) => l.type !== 'reflow') })).toEqual([])
  })
})
