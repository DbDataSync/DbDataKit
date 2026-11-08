import type { FlowLayoutDoc, WidgetDoc } from '../views/model'
import { pageGeometry, type PageGeometry } from './page'

/**
 * `layoutFlow(layout, widgets)` (plan 32 section 4.1, DBR14): a `flow` layout read into the blocks the browser lays out. **Pure, and not a pagination engine:** the browser's flow layout and paged media do the paging
 * (the CSS of `flowCss`); this says, for each block, what the document asked for (a section's break, `keep`, a table that splits and repeats its header, a row of spans, a forced page break) with the plan's
 * defaults applied, and the page the layout is set on. `paginate` (`paginate.ts`) estimates where the browser will break, for the guides on screen.
 */

export interface FlowCell { widget: string; span: number }

export type FlowBlock =
  | { kind: 'widget'; key: string; widget: string; heightMm?: number; keep: boolean; split: boolean; repeatHeader: boolean }
  | { kind: 'row'; key: string; cells: FlowCell[]; heightMm?: number; keep: boolean }
  | { kind: 'break'; key: string }

export interface FlowSection {
  index: number
  title?: string
  breakBefore: boolean
  breakAfter: boolean
  blocks: FlowBlock[]
}

export interface FlowPlan {
  layout: string
  page: PageGeometry
  header?: Record<string, string>
  footer?: Record<string, string>
  cover: boolean
  sections: FlowSection[]
}

interface RawBlock { widget?: string; row?: Array<{ widget: string; span: number }>; height?: number; keep?: boolean; repeat_header?: boolean; pagebreak?: boolean }
interface RawSection { title?: string; break?: 'before' | 'after'; blocks?: RawBlock[] }

/** Tables and matrices split between rows; text splits between lines (orphans and widows keep two); everything else is kept whole unless the document says `keep: false`. */
const SPLITS = new Set(['table', 'matrix'])
const FLOWS = new Set(['text'])

export function layoutFlow(layout: FlowLayoutDoc, widgets: Record<string, Pick<WidgetDoc, 'type'>>): FlowPlan {
  const sections: FlowSection[] = (layout.sections as RawSection[]).map((section, s) => ({
    index: s,
    title: section.title,
    breakBefore: section.break === 'before',
    breakAfter: section.break === 'after',
    blocks: (section.blocks ?? []).map((block, b): FlowBlock => {
      const key = `s${s}b${b}`
      if (block.pagebreak) return { kind: 'break', key }
      if (block.row) return { kind: 'row', key, cells: block.row.map((c) => ({ widget: c.widget, span: c.span })), heightMm: block.height, keep: block.keep ?? true }
      const type = widgets[block.widget ?? '']?.type ?? 'text'
      const split = SPLITS.has(type) && block.keep !== true
      return {
        kind: 'widget',
        key,
        widget: block.widget!,
        heightMm: block.height,
        keep: block.keep ?? !(SPLITS.has(type) || FLOWS.has(type)),
        split,
        repeatHeader: split && block.repeat_header !== false,
      }
    }),
  }))
  return { layout: layout.name, page: pageGeometry(layout.page), header: layout.header, footer: layout.footer, cover: (layout.cover as { parameters?: boolean } | undefined)?.parameters === true, sections }
}

/** The widget ids a plan places, in order (a widget used twice once). */
export function plannedWidgets(plan: FlowPlan): string[] {
  const ids: string[] = []
  for (const s of plan.sections) for (const b of s.blocks) for (const id of b.kind === 'widget' ? [b.widget] : b.kind === 'row' ? b.cells.map((c) => c.widget) : []) if (!ids.includes(id)) ids.push(id)
  return ids
}
