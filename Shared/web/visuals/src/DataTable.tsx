import type { DataValue } from './types'

export interface DataTableProps {
  /** Column names in order; by default the keys of the first row. */
  columns?: string[]
  rows: Array<Record<string, DataValue>>
  /** An id for tests and for the caption's link to the table. */
  id: string
  caption?: string
  /** Rows shown; the rest are counted under the table. */
  limit?: number
}

const text = (v: DataValue | undefined) => (v === null || v === undefined ? '' : String(v))

/** Rows as a plain table: what a chart is drawn from, or any result a person wants to read as it is. Values read as they are; a number is aligned to the right. */
export function DataTable({ columns, rows, id, caption, limit = 200 }: DataTableProps) {
  const names = columns ?? Object.keys(rows[0] ?? {})
  const shown = rows.slice(0, limit)
  return (
    <div className="vz-table-scroll">
      <table className="vz-table" data-testid={`vz-table-${id}`}>
        {caption ? <caption>{caption}</caption> : null}
        <thead>
          <tr>{names.map((n) => <th key={n} scope="col">{n}</th>)}</tr>
        </thead>
        <tbody>
          {shown.map((row, r) => (
            <tr key={r}>
              {names.map((n) => <td key={n} className={typeof row[n] === 'number' ? 'vz-num' : undefined}>{text(row[n])}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length > shown.length ? <p className="vz-notice" role="note">{`${rows.length - shown.length} more rows are not shown.`}</p> : null}
    </div>
  )
}
