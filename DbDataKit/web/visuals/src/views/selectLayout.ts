/**
 * `selectLayout(doc, context)` (plan 32 section 3.2, DBR02a): a pure function, the same as `LayoutSelector.Select` in `DbDataKit.Views` and run against the same goldens
 * (`conformance/views/select-layout`). (1) A named layout wins; an unknown name is refused naming the layouts the document has. (2) Otherwise the layouts whose `when` matches are the
 * candidates; a condition on a dimension the context lacks (an export has no viewport width) does not match. (3) The most specific candidate wins (the most conditions); a tie goes to document order.
 * (4) With no candidate, the default layout.
 */

export type Medium = 'screen' | 'print' | 'export'

export interface LayoutContext {
  /** The container's size in CSS pixels; absent when there is none (an export). */
  width?: number
  height?: number
  /** Where it is shown; `screen` when absent. */
  medium?: Medium
  /** A layout named by a URL (`?layout=print`), an export option or a subscription. */
  layout?: string
}

interface Range { min?: number; max?: number }
export interface SelectableLayout {
  name: string
  default?: boolean
  when?: { width?: Range; height?: Range; orientation?: 'portrait' | 'landscape'; aspect?: Range; medium?: Medium | Medium[] }
}
export interface SelectableDocument { layouts: SelectableLayout[] }

export type LayoutChoice = 'named' | 'matched' | 'default'
export type LayoutSelection = { ok: true; layout: SelectableLayout; choice: LayoutChoice } | { ok: false; refusal: string }

const inRange = (r: Range, v: number) => (r.min === undefined || v >= r.min) && (r.max === undefined || v <= r.max)

/** The number of conditions of a selector: the layout with the most wins. */
export function specificity(when: NonNullable<SelectableLayout['when']>): number {
  return (['width', 'height', 'orientation', 'aspect', 'medium'] as const).filter((k) => when[k] !== undefined).length
}

/** Whether every condition of a selector holds in a context. An omitted condition matches anything; a condition on a dimension the context lacks does not match. */
export function matches(when: NonNullable<SelectableLayout['when']>, context: LayoutContext): boolean {
  const { width, height } = context
  if (when.width && (width === undefined || !inRange(when.width, width))) return false
  if (when.height && (height === undefined || !inRange(when.height, height))) return false
  if (when.orientation) {
    if (width === undefined || height === undefined) return false
    if ((height > width ? 'portrait' : 'landscape') !== when.orientation) return false
  }
  if (when.aspect && (width === undefined || height === undefined || height <= 0 || !inRange(when.aspect, width / height))) return false
  if (when.medium !== undefined && !(Array.isArray(when.medium) ? when.medium : [when.medium]).includes(context.medium ?? 'screen')) return false
  return true
}

export function selectLayout(document: SelectableDocument, context: LayoutContext = {}): LayoutSelection {
  const layouts = document.layouts
  if (context.layout !== undefined) {
    const named = layouts.find((l) => l.name === context.layout)
    return named
      ? { ok: true, layout: named, choice: 'named' }
      : { ok: false, refusal: `The document has no layout named \`${context.layout}\`; its layouts are ${layouts.map((l) => `\`${l.name}\``).join(', ')}.` }
  }
  let best: SelectableLayout | undefined
  for (const layout of layouts)
    if (layout.when && matches(layout.when, context) && (best === undefined || specificity(layout.when) > specificity(best.when!))) best = layout
  if (best) return { ok: true, layout: best, choice: 'matched' }
  const def = layouts.find((l) => l.default === true)
  return def ? { ok: true, layout: def, choice: 'default' } : { ok: false, refusal: 'The document has no default layout.' }
}
