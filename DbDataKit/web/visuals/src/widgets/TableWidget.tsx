import type { TableDoc } from '../views/model'
import type { RenderedTable } from '../views/rendered'
import type { Box } from '../views/model'
import type { KeyboardEvent, MouseEvent } from 'react'
import { rowsThatFit } from './density'

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
export function TableWidget({ id, widget, result, box, maximized = false, onMaximize, onRow }: {
  id: string
  widget: TableDoc
  result: RenderedTable
  box?: Box
  maximized?: boolean
  onMaximize?: () => void
  /** A row was pointed at (a click, or Enter or Space on it: a table's `drill`); the index is into `result.rows`. Rows then take focus and say so to the pointer. */
  onRow?: (index: number, modifiers: { shiftKey: boolean; ctrlKey: boolean; metaKey: boolean; altKey: boolean }) => void
}) {
  const total = result.rows.length
  const last = result.rows[total - 1]
  const hasTotalRow = last?.total === true
  let shown = result.rows.map((row, index) => ({ row, index }))
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
  const align = (i: number) => {
    const a = result.columns[i]?.align ?? widget.encodings.columns[i]?.align
    return a === 'right' || a === 'center' ? `vz-${a}` : undefined
  }
  return (
    <div className={maximized ? 'vz-table-scroll vz-maximized' : 'vz-table-fit'}>
      <table className="vz-table vz-table-widget" data-testid={`vz-table-${id}`}>
        <caption className="vz-sr">{widget.title ?? `Table ${id}`}</caption>
        <thead>
          <tr>{result.columns.map((c, i) => <th key={c.field} scope="col" className={align(i)}>{c.label}</th>)}</tr>
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
                {row.cells.map((cell, c) => <td key={c} className={align(c)}>{cell.text}</td>)}
              </tr>
            )
          })}
        </tbody>
      </table>
      {cut ? <FitNotice text={`${shownData} of ${known} rows`} onMaximize={onMaximize} label={`Show all ${known} rows of ${widget.title ?? id}`} /> : null}
      {serverCut ? <p className="vz-notice" role="note">{`${dataRows} of ${known} rows`}</p> : null}
    </div>
  )
}
