import { layoutReflow } from '../dashboard/reflow'
import { PIXELS_PER_UNIT } from '../dashboard/canvas'
import { minimumOf } from '../widgets/density'
import { selectLayout, type LayoutContext } from '../views/selectLayout'
import type { Box, CanvasLayoutDoc, LayoutDoc, ReflowLayoutDoc, TileDoc, ViewsDoc } from '../views/model'

/**
 * The geometry of the layout editors (plan 32 DBR26), pure: what a drag or a resize of a tile does to `x`, `y`, `w` and `h`, in the unit of its layout (grid cells in a reflow layout; the canvas's unit in a canvas),
 * with no DOM and no pointer. A page turns pointer movement into a distance in units (`pointerToUnits`) and asks `dragTile` for the tile the drag makes; it asks from the tile's **starting** rectangle and the
 * total distance, so the answer does not depend on how many moves it took. A reflow tile snaps to whole cells and may not overlap another (the move is refused, naming the other widget); a canvas tile snaps
 * to `snap` units, stays inside the canvas, and an overlap on its layer is reported (the loader refuses it on save; the editor shows it first).
 */

export interface Rect { x: number; y: number; w: number; h: number }
/** `move`, or the edge or corner being pulled: `n`, `s`, `e`, `w`, `ne`, `nw`, `se`, `sw`. */
export type Handle = 'move' | 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw'

export interface DragResult {
  /** The rectangle after the drag (the starting one when `refused`). */
  rect: Rect
  /** Why the drag is not applied (a reflow tile that would overlap another): the page keeps the last good rectangle and says this. */
  refused?: string
  /** The widgets this rectangle overlaps on a canvas (empty in a reflow layout, where it is refused). */
  overlaps: string[]
}

export interface DragOptions {
  /** Canvas only: the grid tiles snap to, in canvas units. Default 10 for px, 1 for mm and in; 0.0001 or less turns snapping off. */
  snap?: number
}

/** Distance on the screen in pixels to distance in units, given how many pixels one unit takes on the screen (the cell width of the drawn grid, or the canvas's scale times its pixels per unit). */
export const pointerToUnits = (pixels: number, pixelsPerUnit: number): number => (pixelsPerUnit > 0 ? pixels / pixelsPerUnit : 0)

export const defaultSnap = (unit: 'px' | 'mm' | 'in' | undefined) => (unit === undefined || unit === 'px' ? 10 : 1)

const round = (n: number) => Math.round(n * 1e6) / 1e6
const snapTo = (v: number, step: number) => (step > 1e-4 ? Math.round(v / step) * step : v)
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))
const overlap = (a: Rect, b: Rect) => a.x < b.x + b.w - 1e-9 && b.x < a.x + a.w - 1e-9 && a.y < b.y + b.h - 1e-9 && b.y < a.y + a.h - 1e-9

/**
 * The tile a drag makes. `start` is the tile's rectangle when the pointer went down, `dx` and `dy` the distance moved since in units. The bounds are the grid's columns (and rows when the grid names them,
 * otherwise a reflow grid grows downward without end) or the canvas's size. Resizing never makes a tile smaller than one snap step.
 */
export function dragTile(
  layout: Pick<ReflowLayoutDoc, 'type' | 'grid' | 'tiles'> | Pick<CanvasLayoutDoc, 'type' | 'canvas' | 'tiles'>,
  widget: string,
  start: Rect,
  handle: Handle,
  dx: number,
  dy: number,
  options: DragOptions = {},
): DragResult {
  const canvas = layout.type === 'canvas' ? layout.canvas : null
  const step = canvas ? (options.snap ?? defaultSnap(canvas.unit)) : 1
  const maxX = canvas ? canvas.width : (layout as ReflowLayoutDoc).grid.columns
  const maxY = canvas ? canvas.height : ((layout as ReflowLayoutDoc).grid.rows ?? Number.POSITIVE_INFINITY)
  const moves = handle === 'move'
  const left = start.x, top = start.y, right = start.x + start.w, bottom = start.y + start.h
  let l = left, t = top, r = right, b = bottom
  if (moves) {
    // the whole tile moves by a snapped distance, so its size never changes and it stays on the grid it was on
    const mx = clamp(snapTo(left + dx, step), 0, Math.max(0, maxX - start.w))
    const my = clamp(snapTo(top + dy, step), 0, Math.max(0, maxY - start.h))
    l = mx; t = my; r = mx + start.w; b = my + start.h
  } else {
    if (handle.includes('w')) l = clamp(snapTo(left + dx, step), 0, right - step)
    if (handle.includes('e')) r = clamp(snapTo(right + dx, step), left + step, maxX)
    if (handle.includes('n')) t = clamp(snapTo(top + dy, step), 0, bottom - step)
    if (handle.includes('s')) b = clamp(snapTo(bottom + dy, step), top + step, maxY)
  }
  const rect: Rect = { x: round(l), y: round(t), w: round(r - l), h: round(b - t) }
  const others = layout.tiles.filter((o) => o.widget !== widget)
  const sameLayer = (o: TileDoc) => !canvas || (o.layer ?? 0) === ((layout.tiles.find((x) => x.widget === widget)?.layer) ?? 0)
  const hit = others.filter((o) => sameLayer(o) && overlap(rect, o))
  if (!canvas && hit.length > 0) return { rect: start, refused: `The tile would overlap \`${hit[0]!.widget}\`; a reflow grid places a widget in cells no other tile has.`, overlaps: [] }
  return { rect, overlaps: hit.map((o) => o.widget) }
}

/** The tile fields that differ between two rectangles (what `setTile` needs). */
export function rectChanges(from: Rect, to: Rect): Partial<Pick<TileDoc, 'x' | 'y' | 'w' | 'h'>> {
  const out: Partial<Pick<TileDoc, 'x' | 'y' | 'w' | 'h'>> = {}
  for (const k of ['x', 'y', 'w', 'h'] as const) if (from[k] !== to[k]) out[k] = to[k]
  return out
}

export interface TileMark {
  widget: string
  /** The tile is smaller than its widget type's minimum (or its own `min`): marked in the editor, refused by the loader on a canvas, a compact stack in a reflow grid. */
  belowMinimum: boolean
  /** The box the tile has (canvas pixels at scale 1; the reference box's pixels in a reflow grid) and the least it needs. */
  size: Box
  minimum: Box
}

/** The reference window a reflow layout is checked at: the smaller of the two sizes the plan names. */
export const REFERENCE_BOX: Box = { width: 1366, height: 768 }

/** Which tiles are too small. In a canvas, at scale 1 (what the loader checks); in a reflow grid, in the reference window (the grid becomes the compact stack where any tile is below its minimum). */
export function tileMarks(layout: LayoutDoc, widgets: ViewsDoc['widgets'], box: Box = REFERENCE_BOX): TileMark[] {
  if (layout.type === 'flow') return []
  if (layout.type === 'canvas') {
    const ppu = PIXELS_PER_UNIT[layout.canvas.unit ?? 'px']
    return layout.tiles.map((t) => {
      const base = minimumOf(widgets[t.widget]?.type ?? 'text')
      const minimum = { width: Math.max(base.width, t.min?.w ?? 0), height: Math.max(base.height, t.min?.h ?? 0) }
      const size = { width: t.w * ppu, height: t.h * ppu }
      return { widget: t.widget, belowMinimum: size.width < minimum.width - 1e-9 || size.height < minimum.height - 1e-9, size, minimum }
    })
  }
  const result = layoutReflow(layout, widgets, box)
  // the grid's placements are the ones to judge; a stack's are at their minimum by construction, so judge the grid's cells directly
  const grid = result.mode === 'grid' ? result.tiles : layoutReflow({ ...layout, tiles: layout.tiles }, widgets, { width: 100000, height: 100000 }).tiles
  const below = new Set(result.violations.map((v) => v.widget))
  return grid.map((p) => ({ widget: p.widget, belowMinimum: below.has(p.widget), size: { width: p.width, height: p.height }, minimum: p.min }))
}

export interface SelectorTest {
  ok: boolean
  /** The layout that is shown at that size, and why: `matched` (its selector), `default`, or `named`. */
  layout?: string
  choice?: 'matched' | 'default' | 'named'
  refusal?: string
}

/** "Test at W x H": which layout `selectLayout` picks for a window of that size (the answer the viewer would give). */
export function testSelector(doc: Pick<ViewsDoc, 'layouts'>, context: LayoutContext): SelectorTest {
  const picked = selectLayout(doc, context)
  return picked.ok ? { ok: true, layout: picked.layout.name, choice: picked.choice } : { ok: false, refusal: picked.refusal }
}

/** The bottom-right corner of everything the layout places: the smallest grid or canvas that holds it. */
export function extent(tiles: TileDoc[]): { x: number; y: number } {
  return { x: tiles.reduce((m, t) => Math.max(m, t.x + t.w), 0), y: tiles.reduce((m, t) => Math.max(m, t.y + t.h), 0) }
}

/** The widgets whose tiles overlap another's (on a canvas, on the same layer): the editor outlines them; a reflow layout has none that is accepted. */
export function overlapping(layout: Pick<ReflowLayoutDoc, 'tiles'> | Pick<CanvasLayoutDoc, 'tiles'>, canvas = false): Set<string> {
  const out = new Set<string>()
  const tiles = layout.tiles
  for (let i = 0; i < tiles.length; i++)
    for (let j = 0; j < i; j++) {
      const a = tiles[i]!, b = tiles[j]!
      if (canvas && (a.layer ?? 0) !== (b.layer ?? 0)) continue
      if (overlap(a, b)) { out.add(a.widget); out.add(b.widget) }
    }
  return out
}
