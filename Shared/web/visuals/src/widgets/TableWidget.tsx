import type { TableDoc } from '../views/model'
import type { RenderedTable } from '../views/rendered'
import type { Box } from '../views/model'
import { useState, type KeyboardEvent, type MouseEvent } from 'react'
import { ariaSort, nextSort, sortedOrder, type SortDirection, type ViewerSort } from './clickSort'
import { rowsThatFit } from './density'
import { CellContent } from '../CellContent'

/** The "12 of 40" line of a table or matrix that does not fit its tile, and the action that gives it the whole container (plan 32 section 3.3 item 1): nothing is hidden silently. */
export function FitNotice({ text, onMaximize, label }: { text: string; onMaximize?: () => void; label: string }) {
  return (
    <p className="vz-notice vz-fit" role="note" data-testid="vz-fit-notice">
      {text}
      {onMaximize ? <button type="button" className="vz-button" onClick={onMaximize} aria-label={label}>Show all</button> : null}
    </p>
  )
}

/**
 * A table widget: the rows of its result as the server formatted them, a total row where the result has one. Given the box of its body it shows **the rows that fit** and says "12 of 40 rows" with an action
 * that maximizes the tile (`onMaximize`); `maximized` shows every row and the widget scrolls. A cut at the server's limit (`total_rows`) is told the same way.
 */
export function TableWidget({ id, widget, result, box, maximized = false, onMaximize, onRow, sortable = false, onServerSort, onLink }: {
  id: string
  widget: TableDoc
  result: RenderedTable
  box?: Box
  maximized?: boolean
  onMaximize?: () => void
  /** A row was pointed at (a click, or Enter or Space on it: a table's `drill`); the index is into `result.rows`. Rows then take focus and say so to the pointer. */
  onRow?: (index: number, modifiers: { shiftKey: boolean; ctrlKey: boolean; metaKey: boolean; altKey: boolean }) => void
  /** Headers sort the rows on screen (plan 32 DBR45; never stored: print and export use the document's `sort`). Off by default. */
  sortable?: boolean
  /** When the server cut the result at its limit, the host runs the query again with `order` for this column (null, null: the document's order) and the notice says the sort is the server's. Without it the loaded rows are sorted. */
  onServerSort?: (field: string | null, dir: SortDirection | null) => void
  /** A cell that names a link of the document (plan 32 DBR51) was clicked: the link's name and the row's index in `result.rows`. Without it such a cell is plain text. */
  onLink?: (link: string, row: number) => void
}) {
  const [sort, setSort] = useState<ViewerSort | null>(null)
  const total = result.rows.length
  const last = result.rows[total - 1]
  const hasTotalRow = last?.total === true
  const loaded = result.rows.filter((r) => !r.total).length
  // the server cut the result: a click asks it for the rows in the new order (the loaded ones are then already in it), else the loaded rows are sorted here
  const byServer = (result.total_rows ?? loaded) > loaded && onServerSort !== undefined
  let shown = sortedOrder(result.rows, sortable && !byServer ? sort : null).map((index) => ({ row: result.rows[index]!, index }))
  if (box && !maximized) {
    // one line of the body goes to the notice that says rows are cut
    const fit = rowsThatFit(box.height)
    if (total > fit) {
      const room = Math.max(1, rowsThatFit(box.height, 36))
      shown = hasTotalRow && room > 1 ? [...shown.slice(0, room - 1), shown[total - 1]!] : shown.slice(0, room)
    }
  }
  const dataRows = result.rows.filter((r) => !r.total).length
  const shownData = shown.filter((r) => !r.row.total).length
  const known = result.total_rows ?? dataRows
  const cut = shownData < dataRows
  const serverCut = !cut && known > dataRows
  const click = (i: number) => {
    const next = nextSort(sort, i)
    setSort(next)
    if (byServer && onServerSort) onServerSort(next === null ? null : result.columns[i]!.field, next?.dir ?? null)
  }
  const align = (i: number) => {
    const a = result.columns[i]?.align ?? widget.encodings.columns[i]?.align
    return a === 'right' || a === 'center' ? `vz-${a}` : undefined
  }
  return (
    <div className={maximized ? 'vz-table-scroll vz-maximized' : 'vz-table-fit'}>
      <table className="vz-table vz-table-widget" data-testid={`vz-table-${id}`}>
        <caption className="vz-sr">{widget.title ?? `Table ${id}`}</caption>
        <thead>
          <tr>
            {result.columns.map((c, i) => (
              <th key={c.field} scope="col" className={align(i)} aria-sort={sortable ? ariaSort(sort, i) : undefined}>
                {sortable
                  ? <button type="button" className="vz-sort" data-testid={`vz-sort-${id}-${i}`} onClick={() => click(i)}>{c.label}{sort?.index === i ? (sort.dir === 'asc' ? ' \u25B2' : ' \u25BC') : ''}</button>
                  : c.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {shown.map(({ row, index }) => {
            const pointable = onRow !== undefined && !row.total
            return (
              <tr
                key={index}
                className={[row.total ? 'vz-total' : '', pointable ? 'vz-pointable' : ''].filter(Boolean).join(' ') || undefined}
                tabIndex={pointable ? 0 : undefined}
                onClick={pointable ? (e: MouseEvent) => onRow(index, e) : undefined}
                onKeyDown={pointable ? (e: KeyboardEvent) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onRow(index, e) } } : undefined}
              >
                {row.cells.map((cell, c) => <td key={c} className={align(c)}><CellContent cell={cell} onLink={onLink && !row.total ? (name) => onLink(name, index) : undefined} /></td>)}
              </tr>
            )
          })}
        </tbody>
      </table>
      {cut ? <FitNotice text={`${shownData} of ${known} rows`} onMaximize={onMaximize} label={`Show all ${known} rows of ${widget.title ?? id}`} /> : null}
      {serverCut ? <p className="vz-notice" role="note">{`${dataRows} of ${known} rows`}{sortable && sort !== null ? (onServerSort ? ', sorted by the server' : `. Sorted over the ${dataRows} rows loaded, not all ${known}.`) : ''}</p> : null}
    </div>
  )
}
