import { useState } from 'react'
import type { DescribedColumn } from '@dbdatakit/workspace'
import { QueryDescribePanel } from '../components/ChannelReads'
import type { ColumnMetadata } from '../types'

/**
 * The columns a described query gives, as a mapping records its source's columns (`sourceColumns`): name, type and whether it may be null. A query
 * reports no key, so none is a key column; the mapping's key is stated on the Primary key card.
 */
export const describedColumns = (columns: readonly DescribedColumn[]): ColumnMetadata[] =>
  columns.map((c) => ({ name: c.name, nativeType: c.type, isNullable: c.nullable, isPrimaryKey: false, isIdentity: false }))

/** What the card says of the columns the mapping records of its query. */
export function queryColumnsLine(columns: readonly ColumnMetadata[] | undefined): string {
  if (!columns || columns.length === 0) return 'No column of the query is recorded yet: describe it to map its columns here.'
  return `${columns.length} ${columns.length === 1 ? 'column' : 'columns'} recorded: ${columns.map((c) => c.name).join(', ')}`
}

/**
 * The columns of a mapping whose source is a query. The catalog cache holds tables only, so the mapping keeps the query's columns itself
 * (`sourceColumns`), as it keeps a table's; Column Mapping and the Runner read them from there. **Describe columns** asks the Runner to describe the query
 * in a channel picked here, and **Use these columns** writes them into the mapping as an unsaved edit. A query that changed is described again.
 */
export function QueryColumnsCard({ connection, query, columns, onColumns, editable }: {
  /** The connection the query runs on (the mapping's, else the replication's). */
  connection: string
  query: string
  columns: ColumnMetadata[] | undefined
  onColumns: (columns: ColumnMetadata[]) => void
  editable: boolean
}) {
  const [describing, setDescribing] = useState(false)
  const ready = connection !== '' && query.trim() !== ''
  return (
    <div className="card" data-testid="query-columns-card" data-count={columns?.length ?? 0}>
      <div className="card-head tight">
        <span className="card-title sm">Query columns</span>
        <span className="card-note">the columns this mapping records of its source query</span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <span className="hint" data-testid="query-columns-line">{queryColumnsLine(columns)}</span>
        {!ready && <span className="hint" data-testid="query-columns-not-ready">Write the query and pick its connection first.</span>}
        {ready && editable && !describing && (
          <div><button type="button" className="btn btn-sm" onClick={() => setDescribing(true)} data-testid="query-columns-describe-button">Describe columns…</button></div>
        )}
        {ready && describing && (
          <>
            <QueryDescribePanel key={connection} connection={connection} sql={query} onColumns={(described) => onColumns(describedColumns(described))} testId="query-columns-describe" />
            <div><button type="button" className="btn-link quiet" style={{ padding: 0 }} onClick={() => setDescribing(false)} data-testid="query-columns-describe-close">Close</button></div>
          </>
        )}
      </div>
    </div>
  )
}
