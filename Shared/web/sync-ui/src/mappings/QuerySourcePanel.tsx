import { useEffect } from 'react'
import { CodeEditor } from '@dbdatakit/shell'

/** What the source card says of a query in a line: its length, or that none is written. */
export const queryLine = (query: string): string => (query.trim() ? `${query.trim().split('\n').length} line query` : 'No query written yet')

/**
 * The source of a mapping whose source is a query rather than a table: a button that opens the statement in a dialog, in place of the schema and
 * table pickers a query has no answer for. The connection and database stay on the card, since a query still runs somewhere. The dialog holds the
 * editor and whether the query may be wrapped as a subquery. No row of the query is read here.
 * The dialog's open state is the caller's.
 */
export function QuerySourcePanel({ query, onChange, allowSubquery, onAllowSubqueryChange, open, onOpenChange, testIdPrefix, editable = true }: {
  query: string
  onChange: (next: string) => void
  /** Whether the query may be wrapped as a subquery: segmenting, relationships and column transforms need it. */
  allowSubquery: boolean
  onAllowSubqueryChange: (next: boolean) => void
  open: boolean
  onOpenChange: (open: boolean) => void
  testIdPrefix: string
  editable?: boolean
}) {
  return (
    <div className="row" style={{ gap: 8, alignItems: 'center' }}>
      <button type="button" className="btn btn-sm" onClick={() => onOpenChange(true)} data-testid={`${testIdPrefix}-query-open-button`}>Source Query</button>
      <span className="hint" data-testid={`${testIdPrefix}-query-line`}>{queryLine(query)}</span>
      {open && (
        <QueryEditorDialog query={query} onChange={onChange} allowSubquery={allowSubquery} onAllowSubqueryChange={onAllowSubqueryChange}
          onClose={() => onOpenChange(false)} testIdPrefix={testIdPrefix} editable={editable} />
      )}
    </div>
  )
}

function QueryEditorDialog({ query, onChange, allowSubquery, onAllowSubqueryChange, onClose, testIdPrefix, editable }: {
  query: string
  onChange: (next: string) => void
  allowSubquery: boolean
  onAllowSubqueryChange: (next: boolean) => void
  onClose: () => void
  testIdPrefix: string
  editable: boolean
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Source query" data-testid={`${testIdPrefix}-query-dialog`} style={{ width: 'min(920px, 92vw)' }}>
        <div className="card-head">
          <span className="card-title">Source query</span>
          <button type="button" className="btn btn-sm" onClick={onClose} data-testid={`${testIdPrefix}-query-close-button`}>Close</button>
        </div>
        <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <span className="hint">Every row this returns is an insert: a scan sees what exists, and the writer reconciles what does not.</span>
          <CodeEditor value={query} language="sql" readOnly={!editable} onChange={onChange} minLines={10} testId={`${testIdPrefix}-query-editor`} />
          <div className="row" style={{ gap: 7 }}>
            <button type="button" className={`toggle ${allowSubquery ? 'on' : ''}`} disabled={!editable} onClick={() => onAllowSubqueryChange(!allowSubquery)} aria-pressed={allowSubquery}
              data-testid={`${testIdPrefix}-query-allow-subquery-toggle`} />
            <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Allow wrapping as a subquery (segmenting, relationships and column transforms need this)</span>
          </div>
        </div>
      </div>
    </div>
  )
}
