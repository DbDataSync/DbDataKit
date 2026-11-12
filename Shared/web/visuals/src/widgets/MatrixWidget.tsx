import { useState } from 'react'
import { MatrixGrid } from '../MatrixGrid'
import type { RenderedMatrix } from '../types'
import type { Box } from '../views/model'
import { MATRIX_COLUMN_WIDTH, MATRIX_LABEL_WIDTH, TABLE_ROW_HEIGHT } from './density'
import { FitNotice } from './TableWidget'
import type { Modifiers } from '../MatrixGrid'
import { nextSort, sortedOrder, type ViewerSort } from './clickSort'

/**
 * A matrix widget: the existing `MatrixGrid`, cut to its tile. A matrix **truncates columns first** (a column the grid has no room for goes, a total column is kept), then rows (a total row is kept), and says
 * "6 of 9 columns, 12 of 40 rows" with the action that maximizes the tile. `maximized` draws it whole and the widget scrolls.
 */
export function MatrixWidget({ view: given, box, maximized = false, onMaximize, title, onCell, sortable = false, onLink, onExpand }: { view: RenderedMatrix; box?: Box; maximized?: boolean; onMaximize?: () => void; title?: string; onCell?: (row: number, column: number | undefined, modifiers: Modifiers) => void; sortable?: boolean; onLink?: (link: string, row: number, column: number) => void; onExpand?: (row: number, expanded: boolean) => void }) {
  // a click on a column's innermost header sorts the rows by that column on screen (plan 32 DBR45; never stored); a total row stays last, and a cell's row is still the row of the result
  const [sort, setSort] = useState<ViewerSort | null>(null)
  const order = sortedOrder(given.rows, sortable ? sort : null)
  const view: RenderedMatrix = sortable && sort !== null ? { ...given, rows: order.map((i) => given.rows[i]!) } : given
  let shown = view
  const told: string[] = []
  // what a cell of the shown (cut) matrix is in the whole one
  let rowIndex = view.rows.map((_, i) => i)
  let columnIndex = view.columns.map((_, i) => i)
  if (box && !maximized) {
    const labels = Math.max(1, ...view.rows.map((r) => r.labels.length))
    const levels = Math.max(1, ...view.columns.map((c) => c.labels.length))
    const keepColumns = Math.max(1, Math.floor((box.width - labels * MATRIX_LABEL_WIDTH) / MATRIX_COLUMN_WIDTH))
    let columns = view.columns.map((_, i) => i)
    if (view.columns.length > keepColumns) {
      const totals = columns.filter((i) => view.columns[i]!.total)
      const room = Math.max(1, keepColumns - totals.length)
      columns = [...columns.filter((i) => !view.columns[i]!.total).slice(0, room), ...totals].sort((a, b) => a - b)
      told.push(`${columns.length} of ${view.columns.length} columns`)
    }
    const keepRows = Math.max(1, Math.floor((box.height - (levels + 0) * TABLE_ROW_HEIGHT - 36) / TABLE_ROW_HEIGHT))
    let rows = view.rows.map((_, i) => i)
    if (view.rows.length > keepRows) {
      const totals = rows.filter((i) => view.rows[i]!.total)
      const room = Math.max(1, keepRows - totals.length)
      rows = [...rows.filter((i) => !view.rows[i]!.total).slice(0, room), ...totals].sort((a, b) => a - b)
      told.push(`${rows.length} of ${view.rows.length} rows`)
    }
    if (told.length > 0)
      shown = { ...view, columns: columns.map((i) => view.columns[i]!), rows: rows.map((r) => ({ ...view.rows[r]!, cells: columns.map((c) => view.rows[r]!.cells[c]!) })) }
    rowIndex = rows
    columnIndex = columns
  }
  return (
    <div className={maximized ? 'vz-matrix-fit vz-maximized' : 'vz-matrix-fit'}>
      <MatrixGrid view={shown} onCell={onCell ? (r, c, m) => onCell(order[rowIndex[r]!]!, c === undefined ? undefined : columnIndex[c]!, m) : undefined}
        sort={sort === null ? null : { column: columnIndex.indexOf(sort.index), dir: sort.dir }}
        onSortColumn={sortable ? (c) => setSort(nextSort(sort, columnIndex[c]!)) : undefined}
        onLink={onLink ? (name, r, c) => onLink(name, order[rowIndex[r]!]!, columnIndex[c]!) : undefined}
        onExpand={onExpand ? (r, expanded) => onExpand(order[rowIndex[r]!]!, expanded) : undefined} />
      {told.length > 0 ? <FitNotice text={told.join(', ')} onMaximize={onMaximize} label={`Show the whole matrix${title ? ` ${title}` : ''}`} /> : null}
    </div>
  )
}
