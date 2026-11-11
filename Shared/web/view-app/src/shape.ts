import type { StoredRow } from '@dbdatakit/store-ui'
import type { GridSpec, WidgetSpec } from './spec'

export interface SortState { attribute: string; direction: 'asc' | 'desc' }

const text = (v: unknown) => (v === null || v === undefined ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v))

const empty = (v: unknown) => v === null || v === undefined

function compare(a: unknown, b: unknown): number {
  if (empty(a)) return empty(b) ? 0 : 1   // empty values last
  if (empty(b)) return -1
  if (typeof a === 'number' && typeof b === 'number') return a - b
  return text(a).localeCompare(text(b), undefined, { numeric: true, sensitivity: 'base' })
}

/** The rows a grid shows: filtered (an attribute contains the typed text, any case), then sorted by each key in turn. The input is not changed. */
export function shapeRows(rows: StoredRow[], filters: Record<string, string>, sort: SortState[]): StoredRow[] {
  const active = Object.entries(filters).filter(([, v]) => v.trim() !== '')
  const kept = active.length === 0 ? rows.slice() : rows.filter((r) => active.every(([name, v]) => text(r.values[name]).toLowerCase().includes(v.trim().toLowerCase())))
  if (sort.length === 0) return kept
  return kept.map((row, i) => ({ row, i })).sort((x, y) => {
    for (const s of sort) {
      const [a, b] = [x.row.values[s.attribute], y.row.values[s.attribute]]
      if (empty(a) || empty(b)) { if (empty(a) && empty(b)) continue; return empty(a) ? 1 : -1 }   // empty values last in either direction
      const c = compare(a, b)
      if (c !== 0) return s.direction === 'desc' ? -c : c
    }
    return x.i - y.i
  }).map((e) => e.row)
}

/** The sort a click on a header gives: the first click ascending, the next descending, then back to the spec's own order. */
export function nextSort(current: SortState[], attribute: string, initial: GridSpec['sort']): SortState[] {
  const first = current[0]
  if (!first || first.attribute !== attribute) return [{ attribute, direction: 'asc' }]
  return first.direction === 'asc' ? [{ attribute, direction: 'desc' }] : initial
}

// ---- dashboards ----

const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v)) ? Number(v) : null)

/** One figure: the count of rows, or the sum, mean, least or greatest of an attribute's numbers (empty and non-numeric values are left out; null when none is a number). */
export function measure(rows: StoredRow[], widget: Pick<WidgetSpec, 'aggregate' | 'measure'>): number | null {
  if (widget.aggregate === 'count') return rows.length
  const values = rows.map((r) => num(r.values[widget.measure?.name ?? ''])).filter((v): v is number => v !== null)
  if (values.length === 0) return null
  switch (widget.aggregate) {
    case 'sum': return values.reduce((a, b) => a + b, 0)
    case 'avg': return values.reduce((a, b) => a + b, 0) / values.length
    case 'min': return Math.min(...values)
    default: return Math.max(...values)
  }
}

export interface Group { group: string; value: number }

/** The measure per value of the `by` attribute. A bar shows the greatest `limit` groups, a line the first `limit` in order. Groups with no number are left out. */
export function groups(rows: StoredRow[], widget: WidgetSpec): Group[] {
  const by = widget.by?.name ?? ''
  const buckets = new Map<string, StoredRow[]>()
  for (const r of rows) {
    const key = text(r.values[by]) || '(none)'
    const bucket = buckets.get(key)
    if (bucket) bucket.push(r)
    else buckets.set(key, [r])
  }
  const all = [...buckets].map(([group, members]) => ({ group, value: measure(members, widget) })).filter((g): g is Group => g.value !== null)
  if (widget.kind === 'bar') return all.sort((a, b) => b.value - a.value || a.group.localeCompare(b.group)).slice(0, widget.limit)
  return all.sort((a, b) => compare(a.group, b.group)).slice(0, widget.limit)
}

export function measureTitle(widget: WidgetSpec): string {
  if (widget.aggregate === 'count') return 'Rows'
  const name = widget.measure?.label ?? widget.measure?.name ?? ''
  return `${widget.aggregate[0].toUpperCase()}${widget.aggregate.slice(1)} of ${name}`
}

/**
 * A bar or line widget as a rendered-view chart document (the shared renderer's input, `rendered-view.schema.json`): a Vega-Lite spec inside the subset with its values inline. The renderer validates it again, so what is drawn
 * is data and a mark, never anything the app wrote.
 */
export function chartDocument(widgets: { id: string; widget: WidgetSpec; rows: StoredRow[] }[]) {
  return {
    kind: 'rendered_view' as const,
    status: 'complete' as const,
    views: widgets.map(({ id, widget, rows }) => ({
      id,
      type: 'chart' as const,
      vega_lite: {
        title: widget.title,
        mark: widget.kind === 'line' ? 'line' : 'bar',
        data: { values: groups(rows, widget).map((g) => ({ group: g.group, value: g.value })) },
        encoding: {
          x: { field: 'group', type: widget.kind === 'line' ? 'ordinal' : 'nominal', title: widget.by?.label ?? widget.by?.name ?? null, sort: null },
          y: { field: 'value', type: 'quantitative', title: measureTitle(widget) },
        },
      },
    })),
  }
}

/** A table widget's rows: the chosen attributes, under their labels, the first `limit` rows. */
export function tableRows(rows: StoredRow[], widget: WidgetSpec): { columns: string[]; rows: Record<string, string | number | boolean | null>[] } {
  const cols = widget.columns.map((c) => ({ name: c.name, label: c.label ?? c.name }))
  return {
    columns: cols.map((c) => c.label),
    rows: rows.slice(0, widget.limit).map((r) => Object.fromEntries(cols.map((c) => {
      const v = r.values[c.name]
      return [c.label, v === undefined ? null : typeof v === 'object' && v !== null ? JSON.stringify(v) : (v as string | number | boolean | null)]
    }))),
  }
}

export function formatNumber(value: number | null): string {
  if (value === null) return '–'
  return Number.isInteger(value) ? String(value) : String(Math.round(value * 100) / 100)
}
