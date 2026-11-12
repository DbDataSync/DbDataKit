import type { WidgetDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { TABLE_HEAD_HEIGHT, TABLE_ROW_HEIGHT } from '../widgets/density'
import { BLOCK_GAP } from './css'
import type { FlowBlock, FlowPlan } from './layoutFlow'
import { mmToPx } from './page'

/**
 * Where the browser will break the pages, estimated (plan 32 section 4.2): pure, for the **guides** on screen and the page count of the running text. The browser does the real paging and the exporter's PDF is exact;
 * this follows the same rules (a forced break, `keep`, a title kept with what follows, a table that splits between rows and repeats its head) over heights that are either measured in the browser
 * (`Measured`) or estimated from the results (`estimateItem`). A guide is where a page **starts**: the break moves up to the block or row boundary, so guides are not evenly spaced.
 */

export interface PaginationItem {
  key: string
  /** Its height with the gap that follows it. */
  height: number
  keep?: boolean
  keepWithNext?: boolean
  breakBefore?: boolean
  breakAfter?: boolean
  /** A table: the head's height and each row's, in order; it splits between rows. */
  table?: { headerHeight: number; rowHeights: number[]; repeatHeader: boolean }
}

export interface Piece { key: string; rowFrom?: number; /** exclusive */ rowTo?: number; continued?: boolean }
export interface PageStart { page: number; key: string; /** the table row the page starts at */ row?: number; /** pixels into a block taller than a page */ offset?: number }
export interface Pagination { pages: Piece[][]; starts: PageStart[] }

/** Lays the items on pages of `pageHeight` pixels. */
export function paginate(items: PaginationItem[], pageHeight: number): Pagination {
  const pages: Piece[][] = [[]]
  const starts: PageStart[] = []
  let used = 0
  let pending = false
  let count = 0 // pieces on this page
  const newPage = (at: { key: string; row?: number; offset?: number }) => {
    pages.push([])
    used = 0
    count = 0
    starts.push({ page: pages.length, ...at })
  }
  const add = (piece: Piece) => {
    pages[pages.length - 1]!.push(piece)
    count++
  }
  /** The least of an item that has to fit beside what is already on the page. */
  const leading = (item: PaginationItem) => (item.table ? item.table.headerHeight + (item.table.rowHeights[0] ?? 0) : Math.min(item.height, pageHeight))

  items.forEach((item, i) => {
    const forced = pending || item.breakBefore === true
    pending = false
    if (forced && count > 0) newPage({ key: item.key })
    if (item.keepWithNext) {
      const next = items[i + 1]
      if (next && count > 0 && used + item.height + leading(next) > pageHeight) newPage({ key: item.key })
    }
    if (item.table) {
      const { headerHeight, rowHeights, repeatHeader } = item.table
      // what the rows do not account for (the tile's padding and title, the gap) sits above the first row
      const overhead = Math.max(0, item.height - headerHeight - rowHeights.reduce((a, b) => a + b, 0))
      if (count > 0 && used + overhead + headerHeight + (rowHeights[0] ?? 0) > pageHeight) newPage({ key: item.key, row: 0 })
      let from = 0
      let head = headerHeight
      used += overhead + head
      rowHeights.forEach((h, r) => {
        if (used + h > pageHeight && r > from) {
          add({ key: item.key, rowFrom: from, rowTo: r, continued: from > 0 })
          newPage({ key: item.key, row: r })
          from = r
          head = repeatHeader ? headerHeight : 0
          used = head
        }
        used += h
      })
      add({ key: item.key, rowFrom: from, rowTo: rowHeights.length, continued: from > 0 })
    } else if (count > 0 && used + item.height > pageHeight && (item.keep === true || item.height <= pageHeight)) {
      newPage({ key: item.key })
      add({ key: item.key })
      used = item.height
    } else if (used + item.height > pageHeight && item.height > pageHeight) {
      // taller than a page and not kept: it runs on over as many pages as it needs
      add({ key: item.key })
      let rest = item.height - (pageHeight - used)
      let offset = pageHeight - used
      while (rest > 0) {
        newPage({ key: item.key, offset })
        add({ key: item.key, continued: true })
        const take = Math.min(rest, pageHeight)
        offset += take
        rest -= take
        used = take
      }
    } else {
      add({ key: item.key })
      used += item.height
    }
    if (item.breakAfter) pending = true
  })
  return { pages, starts }
}

// ---- the items of a plan ----

/** What the browser measured of one block: its top (relative to the content) and height, and for a table its head and rows. Zero heights mean "not measured" (no layout engine, as in a test). */
export interface Measured { top: number; height: number; header?: number; rows?: Array<{ top: number; height: number }> }

const TITLE_HEIGHT = 28
const WIDGET_CHROME = 18 // a tile's padding and border
const WIDGET_TITLE = 28
const LINE = 20

/** The default heights, in millimetres, of widgets that have a box of their own when the block says none. */
export const DEFAULT_HEIGHT_MM: Record<string, number> = { chart: 70, metric: 28, image: 60, custom: 70 }

function textLines(text: string, widthPx: number): number {
  const perLine = Math.max(10, Math.floor(widthPx / 7.5))
  return text.split('\n').reduce((n, line) => n + Math.max(1, Math.ceil(line.length / perLine)), 0)
}

/** A widget's height without a browser: a box it was given, else what its rows or lines come to. Rough on purpose; the browser's measure replaces it. */
export function estimateWidget(widget: WidgetDoc | undefined, result: RenderedWidget | undefined, heightMm: number | undefined, widthPx: number): { height: number; rows?: number[]; header?: number } {
  if (!widget) return { height: 0 }
  const title = widget.title !== undefined && widget.type !== 'metric' ? WIDGET_TITLE : 0
  const fixed = heightMm ?? DEFAULT_HEIGHT_MM[widget.type]
  if (fixed !== undefined) return { height: mmToPx(fixed) }
  if (widget.type === 'table' || widget.type === 'matrix') {
    const rows = result && (result.type === 'table' || result.type === 'matrix') ? result.rows.length : 1
    const rowHeights = Array.from({ length: rows }, () => TABLE_ROW_HEIGHT)
    return { height: WIDGET_CHROME + title + TABLE_HEAD_HEIGHT + rows * TABLE_ROW_HEIGHT, rows: rowHeights, header: TABLE_HEAD_HEIGHT }
  }
  if (widget.type === 'text') return { height: WIDGET_CHROME + textLines(widget.text, widthPx) * LINE + 12 }
  return { height: WIDGET_CHROME + title + 56 }
}

/** The items of a plan in order, over the heights of `measured` where the browser gave them and the estimate elsewhere. */
export function planItems(plan: FlowPlan, widgets: Record<string, WidgetDoc>, results: Record<string, RenderedWidget>, measured: Record<string, Measured> = {}): PaginationItem[] {
  const items: PaginationItem[] = []
  const widthPx = plan.page.contentWidthPx
  const heightOf = (key: string, estimate: number) => {
    const m = measured[key]
    return (m && m.height > 0 ? m.height : estimate) + BLOCK_GAP
  }
  if (plan.cover) items.push({ key: 'cover', height: plan.page.contentHeightPx, keep: true, breakAfter: true })
  for (const section of plan.sections) {
    const first = items.length
    if (section.title !== undefined) items.push({ key: `s${section.index}t`, height: heightOf(`s${section.index}t`, TITLE_HEIGHT), keep: true, keepWithNext: true })
    for (const block of section.blocks as FlowBlock[]) {
      if (block.kind === 'break') {
        items.push({ key: block.key, height: 0, breakAfter: true })
      } else if (block.kind === 'row') {
        const estimate = block.heightMm !== undefined ? mmToPx(block.heightMm) : Math.max(...block.cells.map((c) => estimateWidget(widgets[c.widget], results[c.widget], undefined, (widthPx * c.span) / 12).height))
        items.push({ key: block.key, height: heightOf(block.key, estimate), keep: block.keep })
      } else {
        const widget = widgets[block.widget]
        const est = estimateWidget(widget, results[block.widget], block.heightMm, widthPx)
        const m = measured[block.key]
        const rows = m?.rows && m.rows.length > 0 && m.rows.every((r) => r.height > 0) ? m.rows.map((r) => r.height) : est.rows
        const table = block.split && rows ? { headerHeight: m?.header && m.header > 0 ? m.header : (est.header ?? TABLE_HEAD_HEIGHT), rowHeights: rows, repeatHeader: block.repeatHeader } : undefined
        items.push({ key: block.key, height: heightOf(block.key, est.height), keep: block.keep, table })
      }
    }
    if (section.breakBefore && items[first]) items[first]!.breakBefore = true
    if (section.breakAfter && items.length > first) items[items.length - 1]!.breakAfter = true
  }
  return items
}

/** The y of the top of each item and the end of the last, from the estimate alone: the gap is in each item's height. */
export function estimatedTops(items: PaginationItem[]): Record<string, number> {
  const tops: Record<string, number> = {}
  let y = 0
  for (const item of items) {
    tops[item.key] = y
    y += item.height
  }
  return tops
}

/** Where on screen (pixels from the top of the content) a page starts: the browser's measure of its block (or row) where there is one, else the estimate. */
export function startY(start: PageStart, items: PaginationItem[], measured: Record<string, Measured> = {}): number {
  const m = measured[start.key]
  const item = items.find((i) => i.key === start.key)
  if (start.row !== undefined && m?.rows && m.rows[start.row] && m.rows[start.row]!.height > 0) return m.rows[start.row]!.top
  const top = m && m.height > 0 ? m.top : (estimatedTops(items)[start.key] ?? 0)
  if (start.row !== undefined && item?.table) return top + item.table.headerHeight + item.table.rowHeights.slice(0, start.row).reduce((a, b) => a + b, 0)
  return top + (start.offset ?? 0)
}
