import type { Box, CanvasLayoutDoc } from '../views/model'
import { densityClass, minimumOf, type DensityClass } from '../widgets/density'

/**
 * `layoutCanvas` (plan 32 section 3.4, DBR07): the canvas layout, pure. The author's picture has a size (`canvas`, in px, mm or in at 96 px per inch) and the host shows it:
 * `overflow: scale` (the default) scales the whole picture **uniformly** to fit the box, centred and letterboxed, never above `max_scale` (4) and never below `min_scale` (0.5): below it the picture holds
 * `min_scale` and scrolls, so text stays readable on a small screen; `overflow: scroll` is scale 1 with scrollbars. Tiles are placed in **canvas pixels** (the unit converted, unchanged by the scale) and
 * drawn in ascending `layer` order. A tile's density is decided from its canvas box, never from the screen, because the whole picture is scaled as one: the same picture at every size.
 *
 * Pure: the box is given, nothing is measured; the same numbers in .NET are not needed (the plan puts layout code in TypeScript only).
 */

export const CANVAS_MIN_SCALE = 0.5
export const CANVAS_MAX_SCALE = 4
/** CSS pixels per canvas unit: `mm` and `in` are converted at 96 px per inch. */
export const PIXELS_PER_UNIT = { px: 1, mm: 96 / 25.4, in: 96 } as const

export interface CanvasPlacement {
  widget: string
  type: string
  /** The tile's rectangle on the canvas, in canvas pixels (scale 1). */
  x: number
  y: number
  width: number
  height: number
  /** Drawn in ascending order; 0 when the tile names none. */
  layer: number
  /** The smallest box the tile may have at scale 1: its type's minimum raised by the tile's own `min`. */
  min: Box
  /** The widget's density, from the canvas box (a layout's `widget_options` may pin it). */
  density: DensityClass
}

export interface CanvasViolation { widget: string; width: number; height: number; min: Box }

export interface CanvasResult {
  mode: 'scale' | 'scroll'
  box: Box
  /** The canvas at scale 1, in pixels. */
  canvas: Box
  /** The scale that fits the box before `min_scale` and `max_scale` are applied (`scale` mode; 1 in `scroll`). */
  fit: number
  /** The scale the picture is drawn at. */
  scale: number
  /** The picture's size on screen: the canvas times the scale. */
  scaled: Box
  /** Where the picture's top left is in the scrolled area: whole pixels, centred on an axis where the picture is smaller than the box (the letterbox), 0 where it is larger. */
  offset: { x: number; y: number }
  /** The size of the scrolled area: the box or, on an axis where the picture is larger, the picture. */
  content: Box
  /** Whether the container scrolls (a picture larger than the box in either axis). */
  scrolls: boolean
  /** Whether the picture was held at `min_scale` (the box is smaller than the picture can be shown). */
  atMinimum: boolean
  /** Whether it was held at `max_scale`. */
  atMaximum: boolean
  /** Tiles in drawing order: ascending layer, then document order. */
  tiles: CanvasPlacement[]
  /** Tiles below their minimum at scale 1 (the validator refuses these at load; a layout from elsewhere may still have them). */
  violations: CanvasViolation[]
}

const hundredth = (n: number) => Math.round(n * 100) / 100

export function layoutCanvas(layout: Pick<CanvasLayoutDoc, 'canvas' | 'overflow' | 'min_scale' | 'max_scale' | 'tiles' | 'widget_options'>, widgets: Record<string, { type: string }>, box: Box): CanvasResult {
  const ppu = PIXELS_PER_UNIT[layout.canvas.unit ?? 'px']
  const canvas = { width: hundredth(layout.canvas.width * ppu), height: hundredth(layout.canvas.height * ppu) }
  const width = Math.max(0, Math.round(box.width))
  const height = Math.max(0, Math.round(box.height))
  const mode = layout.overflow === 'scroll' ? 'scroll' : 'scale'
  const minScale = layout.min_scale ?? CANVAS_MIN_SCALE
  const maxScale = Math.max(minScale, layout.max_scale ?? CANVAS_MAX_SCALE)

  const fit = mode === 'scroll' ? 1 : Math.min(width / canvas.width, height / canvas.height)
  const scale = mode === 'scroll' ? 1 : Math.min(maxScale, Math.max(minScale, fit))
  const scaled = { width: canvas.width * scale, height: canvas.height * scale }
  const content = { width: Math.max(width, Math.ceil(hundredth(scaled.width))), height: Math.max(height, Math.ceil(hundredth(scaled.height))) }
  const offset = { x: Math.max(0, Math.round((width - scaled.width) / 2)), y: Math.max(0, Math.round((height - scaled.height) / 2)) }

  const tiles = layout.tiles
    .map((t, index) => {
      const type = widgets[t.widget]?.type ?? 'text'
      const base = minimumOf(type)
      const min = { width: Math.max(base.width, t.min?.w ?? 0), height: Math.max(base.height, t.min?.h ?? 0) }
      const tileWidth = hundredth(t.w * ppu)
      const tileHeight = hundredth(t.h * ppu)
      const placement: CanvasPlacement = {
        widget: t.widget, type, x: hundredth(t.x * ppu), y: hundredth(t.y * ppu), width: tileWidth, height: tileHeight, layer: t.layer ?? 0, min,
        density: densityClass(type, { width: tileWidth, height: tileHeight }, layout.widget_options?.[t.widget]?.density),
      }
      return { placement, index }
    })
    .sort((a, b) => a.placement.layer - b.placement.layer || a.index - b.index)
    .map((t) => t.placement)
  const violations = tiles.filter((t) => t.width < t.min.width || t.height < t.min.height).map((t) => ({ widget: t.widget, width: t.width, height: t.height, min: t.min }))

  return {
    mode, box: { width, height }, canvas, fit, scale, scaled, offset, content,
    scrolls: content.width > width || content.height > height,
    atMinimum: mode === 'scale' && fit < minScale,
    atMaximum: mode === 'scale' && fit > maxScale,
    tiles, violations,
  }
}
