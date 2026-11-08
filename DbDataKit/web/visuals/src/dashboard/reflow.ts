import type { ViewsIssue } from '../views/common'
import type { Box, ReflowLayoutDoc, ViewsDoc } from '../views/model'
import { densityClass, minimumOf, type DensityClass } from '../widgets/density'

/**
 * `layoutReflow` (plan 32 section 3.3, DBR06): the reflow layout, pure. A grid of `columns` equal tracks (`minmax(0, 1fr)` in CSS) and `rows` of them (omitted: as many as the tiles reach, equal shares of the
 * height) fills the **box** exactly: the edges of the tracks are whole pixels (`round(i * size / count)`), so neighbouring tiles share an edge and a layout whose tiles cover every cell leaves no gap and no overlap.
 * Each tile is held to the smallest box its widget type is drawn in (`MINIMUMS`; a tile's own `min` may raise it). If **any** tile would be below its minimum in this box, the layout is the **compact stack**:
 * one column, document order, each tile at its minimum height, the page scrolls (the only place a reflow dashboard scrolls; phones). `violations` lists the tiles that caused it. A layout was already chosen
 * by its selector, so there are no breakpoints inside it: a narrow viewport gets a layout of its own.
 *
 * The plan writes `layoutReflow(layout, box)`; the minimums depend on each tile's widget type, so the widgets of the document come too.
 */

export interface ReflowPlacement {
  widget: string
  /** The widget's type, which sets its minimum and its density. */
  type: string
  /** The tile's rectangle in the box, in whole CSS pixels. */
  x: number
  y: number
  width: number
  height: number
  /** The grid cells the tile covers (1-based, as CSS grid lines count): the stack has none. */
  cell: { column: number; row: number; columns: number; rows: number } | null
  /** The smallest box the tile may have: its type's minimum raised by the tile's own `min`. */
  min: Box
  /** The widget's density in this tile (its box is the rectangle less the gutter); a layout's `widget_options` may pin it. */
  density: DensityClass
}

export interface ReflowViolation { widget: string; width: number; height: number; min: Box }

export interface ReflowResult {
  mode: 'grid' | 'stack'
  box: Box
  columns: number
  /** The tracks of the grid: the layout's `rows`, else the rows its tiles reach. */
  rows: number
  tiles: ReflowPlacement[]
  /** The tiles below their minimum in the grid at this box (the reason for the stack); empty when the grid fits. */
  violations: ReflowViolation[]
  /** The height the content needs: the box's in a grid, the stack's own when it is a stack. */
  contentHeight: number
  /** Whether the page scrolls: only a stack that is taller than the box. */
  scrolls: boolean
}

/** The gutter around a tile in the page: each tile's widget is drawn this much smaller than its cell (4 pixels on every side), so neighbours have a gap while the cells themselves fill the box exactly. */
export const TILE_GUTTER = 8

const edge = (i: number, count: number, size: number) => Math.round((i * size) / count)

export function layoutReflow(layout: Pick<ReflowLayoutDoc, 'grid' | 'tiles' | 'widget_options'>, widgets: Record<string, { type: string }>, box: Box): ReflowResult {
  const columns = layout.grid.columns
  const rows = layout.grid.rows ?? Math.max(1, ...layout.tiles.map((t) => t.y + t.h))
  const width = Math.max(0, Math.round(box.width))
  const height = Math.max(0, Math.round(box.height))
  const place = (widget: string, rect: Pick<ReflowPlacement, 'x' | 'y' | 'width' | 'height' | 'cell'>, tileMin?: { w?: number; h?: number }): ReflowPlacement => {
    const type = widgets[widget]?.type ?? 'text'
    const base = minimumOf(type)
    const min = { width: Math.max(base.width, tileMin?.w ?? 0), height: Math.max(base.height, tileMin?.h ?? 0) }
    return { widget, type, ...rect, min, density: densityClass(type, { width: rect.width - TILE_GUTTER, height: rect.height - TILE_GUTTER }, layout.widget_options?.[widget]?.density) }
  }

  const grid = layout.tiles.map((t) => {
    const x = edge(t.x, columns, width)
    const y = edge(t.y, rows, height)
    return place(t.widget, { x, y, width: edge(t.x + t.w, columns, width) - x, height: edge(t.y + t.h, rows, height) - y, cell: { column: t.x + 1, row: t.y + 1, columns: t.w, rows: t.h } }, t.min)
  })
  const violations = grid.filter((t) => t.width < t.min.width || t.height < t.min.height).map((t) => ({ widget: t.widget, width: t.width, height: t.height, min: t.min }))
  if (violations.length === 0) return { mode: 'grid', box: { width, height }, columns, rows, tiles: grid, violations, contentHeight: height, scrolls: false }

  let y = 0
  const stack = layout.tiles.map((t) => {
    const placed = place(t.widget, { x: 0, y, width, height: 0, cell: null }, t.min)
    const tall = { ...placed, height: placed.min.height, density: densityClass(placed.type, { width: width - TILE_GUTTER, height: placed.min.height - TILE_GUTTER }, layout.widget_options?.[t.widget]?.density) }
    y += tall.height
    return tall
  })
  return { mode: 'stack', box: { width, height }, columns, rows, tiles: stack, violations, contentHeight: y, scrolls: y > height }
}

// ---- the size check of the validator ----

/** The sizes a layout is laid out at when its selector names none: a laptop and a desktop. (The plan adds a phone, 390 x 844; a layout with no selector is not for a phone, and the compact stack is its designed fallback there, so the check would fire on every grid.) */
export const DEFAULT_CHECK_SIZES: Box[] = [{ width: 1920, height: 1080 }, { width: 1366, height: 768 }]
const PHONE: Box = { width: 390, height: 844 }

/** The container sizes a layout's selector allows: the bounds of its width and height ranges (the other side by 16:9), a phone for `portrait`; the defaults when it names none. */
export function sizesFor(when: ReflowLayoutDoc['when']): Box[] {
  if (!when) return DEFAULT_CHECK_SIZES
  const w = [when.width?.min, when.width?.max].filter((v): v is number => v !== undefined && v > 0)
  const h = [when.height?.min, when.height?.max].filter((v): v is number => v !== undefined && v > 0)
  if (w.length > 0 || h.length > 0) {
    const widths = w.length > 0 ? w : h.map((v) => Math.round((v * 16) / 9))
    return [...new Set(widths)].flatMap((width) => (h.length > 0 ? h : [Math.round((width * 9) / 16)]).map((height) => ({ width, height })))
  }
  if (when.orientation === 'portrait') return [PHONE]
  return DEFAULT_CHECK_SIZES
}

/**
 * The size check of the validator (plan 32 DBR06): every reflow layout is laid out at the sizes its selector allows, and a tile below its minimum there is a **warning** (`DBD-VIEW-028`, `TileBelowMinimum`,
 * as the canvas check's), naming the size and what the layout does there (the compact stack). It is not part of `validateViewsDocument` yet: the .NET loader (`DbDataKit.Views`) has to give the same
 * warnings at the same paths, and that twin is not built, so the two suites would disagree.
 */
export function checkReflowSizes(document: Pick<ViewsDoc, 'widgets' | 'layouts'>): ViewsIssue[] {
  const issues: ViewsIssue[] = []
  document.layouts.forEach((layout, li) => {
    if (layout.type !== 'reflow') return
    // one warning per tile, naming every size it is too small at
    const found = new Map<string, { widget: string; at: string[]; worst: ReflowViolation }>()
    for (const size of sizesFor(layout.when))
      for (const v of layoutReflow(layout, document.widgets, size).violations) {
        const entry = found.get(v.widget) ?? { widget: v.widget, at: [], worst: v }
        entry.at.push(`${size.width} x ${size.height}`)
        if (v.width < entry.worst.width) entry.worst = v
        found.set(v.widget, entry)
      }
    for (const { widget, at, worst } of found.values())
      issues.push({
        code: 'DBD-VIEW-028',
        severity: 'warning',
        path: `layouts[${li}].tiles[${layout.tiles.findIndex((t) => t.widget === widget)}]`,
        message: `The tile of \`${widget}\` is below its minimum (${worst.min.width} x ${worst.min.height}) at ${at.join(', ')}, down to ${worst.width} x ${worst.height}; the layout \`${layout.name}\` is the compact stack there.`,
      })
  })
  return issues
}
