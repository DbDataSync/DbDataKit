import { MINIMUMS } from '../views/limits'
import type { Box, WidgetOptionsDoc, WidgetType } from '../views/model'

/**
 * Density (plan 32 section 3.3 item 3): the renderer's decision, not the document's. A tile is told its pixel box and picks a class `xs`, `s`, `m` or `l`, by how many times it is larger than the smallest
 * box its widget type is allowed (`MINIMUMS`, the same numbers the layout engines and the validator use): `l` from twice the minimum on the tighter side, `m` from one and a half, `s` from one and a fifth, `xs` below.
 * What each class drops is the renderer's: a metric its label, then its comparison; a chart its legend, then its axis titles, then thins its ticks; a table and a matrix show what fits.
 * `density: compact | comfortable` on a layout's `widget_options` pins it (`compact` to `s`, `comfortable` to `l`).
 */
export type DensityClass = 'xs' | 's' | 'm' | 'l'

/** The size of one tile's chrome: its padding (8) and border (1) on each side, and its title bar. */
export const TILE_PADDING = 9
export const TITLE_HEIGHT = 28
/** The height of a table's head row and of each of its rows, in CSS pixels (the stylesheet's, `vz-table-widget`). */
export const TABLE_HEAD_HEIGHT = 30
export const TABLE_ROW_HEIGHT = 28
/** What a matrix's grid needs per column and per row-label cell, to cut columns that do not fit. */
export const MATRIX_COLUMN_WIDTH = 96
export const MATRIX_LABEL_WIDTH = 132

/** The smallest box a widget type is drawn in (a layout's tile may raise it). */
export function minimumOf(type: WidgetType | string): Box {
  const [width, height] = MINIMUMS[type] ?? [160, 88]
  return { width, height }
}

export function densityClass(type: WidgetType | string, box: Box, pin?: WidgetOptionsDoc['density']): DensityClass {
  if (pin === 'compact') return 's'
  if (pin === 'comfortable') return 'l'
  const min = minimumOf(type)
  const ratio = Math.min(box.width / min.width, box.height / min.height)
  return ratio >= 2 ? 'l' : ratio >= 1.5 ? 'm' : ratio >= 1.2 ? 's' : 'xs'
}

/** The box a widget's body has inside its tile: the tile's padding and, when it shows one, its title bar taken off. */
export function bodyBox(box: Box, titled: boolean): Box {
  return { width: Math.max(0, box.width - 2 * TILE_PADDING), height: Math.max(0, box.height - 2 * TILE_PADDING - (titled ? TITLE_HEIGHT : 0)) }
}

/** How many body rows a table of this height shows, besides its head row: at least one. */
export function rowsThatFit(height: number, reserved = 0): number {
  return Math.max(1, Math.floor((height - TABLE_HEAD_HEIGHT - reserved) / TABLE_ROW_HEIGHT))
}
